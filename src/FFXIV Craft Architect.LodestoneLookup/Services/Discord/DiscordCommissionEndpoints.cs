using System.Text.Json;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed record DiscordVolunteerInteraction(
    string InteractionId,
    string ApplicationId,
    string GuildId,
    string ChannelId,
    string MessageId,
    string ActionToken,
    string DiscordUserId,
    string DiscordUserDisplayName);

public enum DiscordVolunteerInteractionStatus
{
    Recorded,
    Replayed,
    NoLongerOpen,
    Rejected
}

public sealed record DiscordVolunteerInteractionResult(
    DiscordVolunteerInteractionStatus Status,
    string Message);

public interface IDiscordVolunteerInteractionService
{
    Task<DiscordVolunteerInteractionResult> RecordInterestAsync(
        DiscordVolunteerInteraction interaction,
        CancellationToken cancellationToken = default);
}

public sealed class DenyDiscordVolunteerInteractionService : IDiscordVolunteerInteractionService
{
    public Task<DiscordVolunteerInteractionResult> RecordInterestAsync(
        DiscordVolunteerInteraction interaction,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new DiscordVolunteerInteractionResult(
            DiscordVolunteerInteractionStatus.Rejected,
            "Volunteer claims are not available for this installation."));
}

public static class DiscordCommissionEndpoints
{
    private const int MaximumRequestBodyBytes = 128 * 1024;
    private const int PingInteraction = 1;
    private const int ApplicationCommandInteraction = 2;
    private const int MessageComponentInteraction = 3;
    private const int PongResponse = 1;
    private const int ChannelMessageResponse = 4;

    public static void MapDiscordCommissionEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/discord/health",
            (DiscordCommissionOptions options) => Results.Ok(new
            {
                status = options.IsConfigured
                    ? "ready"
                    : options.CanVerifyInteractions
                        ? "pending-channel"
                        : "disabled",
                signingReady = options.CanVerifyInteractions,
                publishingReady = options.IsConfigured,
                directPublishingReady = options.CanPublishDirectly
            }));

        app.MapPost(
            "/discord/interactions",
            async (
                HttpContext context,
                DiscordCommissionOptions options,
                DiscordRequestVerifier verifier,
                SqliteCommissionBriefStore store,
                CancellationToken ct) =>
            {
                if (!options.CanVerifyInteractions)
                {
                    return Results.NotFound();
                }

                var body = await ReadBodyAsync(context.Request.Body, ct);
                if (body == null)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                if (!verifier.Verify(
                        context.Request.Headers["X-Signature-Timestamp"].ToString(),
                        context.Request.Headers["X-Signature-Ed25519"].ToString(),
                        body))
                {
                    return Results.Unauthorized();
                }

                JsonDocument payload;
                try
                {
                    payload = JsonDocument.Parse(body);
                }
                catch (JsonException)
                {
                    return Results.BadRequest();
                }

                using (payload)
                {
                    if (!payload.RootElement.TryGetProperty("type", out var typeElement) ||
                        typeElement.ValueKind != JsonValueKind.Number ||
                        !typeElement.TryGetInt32(out var interactionType))
                    {
                        return Results.BadRequest();
                    }

                    return interactionType switch
                    {
                        PingInteraction => Results.Json(new { type = PongResponse }),
                        ApplicationCommandInteraction => options.IsConfigured
                            ? await HandleCommandAsync(
                                payload.RootElement,
                                options,
                                store,
                                ct)
                            : InteractionError("Commission publishing has not been connected to a channel yet."),
                        MessageComponentInteraction => options.IsConfigured
                            ? await HandleMessageComponentAsync(
                                payload.RootElement,
                                options,
                                context.RequestServices.GetService<IDiscordVolunteerInteractionService>()
                                    ?? new DenyDiscordVolunteerInteractionService(),
                                ct)
                            : InteractionError("Commission collaboration has not been connected to a channel yet."),
                        _ => InteractionError("This interaction is not supported by the prototype.")
                    };
                }
            });
    }

    private static async Task<byte[]?> ReadBodyAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaximumRequestBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                return buffer[..total];
            }

            total += read;
        }

        return null;
    }

    private static async Task<IResult> HandleCommandAsync(
        JsonElement interaction,
        DiscordCommissionOptions options,
        SqliteCommissionBriefStore store,
        CancellationToken ct)
    {
        if (ReadString(interaction, "guild_id") != options.AllowedGuildId ||
            ReadString(interaction, "channel_id") != options.AllowedChannelId)
        {
            return InteractionError("This prototype is available only in its dedicated commission channel.");
        }

        if (!interaction.TryGetProperty("data", out var data) ||
            ReadString(data, "name") != "commission" ||
            !TryReadPublicId(data, out var publicId))
        {
            return InteractionError("Use `/commission post` with a published Craft Architect brief link.");
        }

        var published = await store.LoadAsync(publicId, ct);
        if (published == null)
        {
            return InteractionError("That commission brief is missing, revoked, or no longer available.");
        }

        return Results.Json(new
        {
            type = ChannelMessageResponse,
            data = DiscordCommissionMessage.Create(published, options.CommissionBaseUrl)
        });
    }

    private static async Task<IResult> HandleMessageComponentAsync(
        JsonElement interaction,
        DiscordCommissionOptions options,
        IDiscordVolunteerInteractionService volunteerInteractions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ApplicationId) ||
            ReadString(interaction, "application_id") != options.ApplicationId ||
            ReadString(interaction, "guild_id") != options.AllowedGuildId ||
            ReadString(interaction, "channel_id") != options.AllowedChannelId)
        {
            return InteractionError("This Volunteer action does not belong to this commission installation.");
        }

        if (!interaction.TryGetProperty("message", out var message) ||
            !interaction.TryGetProperty("data", out var data) ||
            !interaction.TryGetProperty("member", out var member) ||
            !member.TryGetProperty("user", out var user))
        {
            return InteractionError("This Volunteer action is incomplete.");
        }

        var interactionId = ReadString(interaction, "id");
        var messageId = ReadString(message, "id");
        var actionToken = ReadString(data, "custom_id");
        var userId = ReadString(user, "id");
        if (string.IsNullOrWhiteSpace(interactionId) ||
            string.IsNullOrWhiteSpace(messageId) ||
            string.IsNullOrWhiteSpace(actionToken) ||
            actionToken.Length > 100 ||
            string.IsNullOrWhiteSpace(userId))
        {
            return InteractionError("This Volunteer action is invalid.");
        }

        var displayName = ReadString(member, "nick") ??
            ReadString(user, "global_name") ??
            ReadString(user, "username") ??
            "Discord volunteer";
        var result = await volunteerInteractions.RecordInterestAsync(
            new DiscordVolunteerInteraction(
                interactionId,
                options.ApplicationId,
                options.AllowedGuildId,
                options.AllowedChannelId,
                messageId,
                actionToken,
                userId,
                displayName),
            ct);

        var response = result.Status switch
        {
            DiscordVolunteerInteractionStatus.Recorded =>
                "Interest recorded. A commission operator still needs to confirm assignment.",
            DiscordVolunteerInteractionStatus.Replayed =>
                "Your interest is already recorded. A commission operator still needs to confirm assignment.",
            DiscordVolunteerInteractionStatus.NoLongerOpen =>
                "This commission is no longer accepting volunteers.",
            _ => string.IsNullOrWhiteSpace(result.Message)
                ? "This Volunteer action could not be accepted."
                : result.Message
        };
        return InteractionError(response);
    }

    private static bool TryReadPublicId(JsonElement data, out string publicId)
    {
        publicId = string.Empty;
        if (!data.TryGetProperty("options", out var commandOptions) ||
            commandOptions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var post = commandOptions
            .EnumerateArray()
            .FirstOrDefault(option => ReadString(option, "name") == "post");
        if (post.ValueKind == JsonValueKind.Undefined ||
            !post.TryGetProperty("options", out var postOptions))
        {
            return false;
        }

        var brief = postOptions
            .EnumerateArray()
            .FirstOrDefault(option => ReadString(option, "name") == "brief");
        if (!brief.TryGetProperty("value", out var valueElement) ||
            valueElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = valueElement.GetString()?.Trim() ?? string.Empty;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = ParseQuery(uri.Query)
                .FirstOrDefault(pair => pair.Key.Equals("id", StringComparison.OrdinalIgnoreCase))
                .Value ?? string.Empty;
        }

        if (value.Length is < 12 or > 32 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return false;
        }

        publicId = value;
        return true;
    }

    private static IEnumerable<KeyValuePair<string, string?>> ParseQuery(string query)
    {
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = segment.Split('=', 2);
            yield return new KeyValuePair<string, string?>(
                Uri.UnescapeDataString(parts[0]),
                parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : null);
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IResult InteractionError(string message) =>
        Results.Json(new
        {
            type = ChannelMessageResponse,
            data = DiscordCommissionMessage.CreateEphemeral(message)
        });
}
