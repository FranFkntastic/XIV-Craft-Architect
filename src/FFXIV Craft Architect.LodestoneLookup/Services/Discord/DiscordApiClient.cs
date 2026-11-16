using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public enum DiscordApiOutcome
{
    Succeeded,
    RetryableFailure,
    TerminalFailure,
    ReconciliationRequired
}

public sealed record DiscordApiResult(
    DiscordApiOutcome Outcome,
    string? MessageId = null,
    HttpStatusCode? StatusCode = null,
    string? Error = null,
    TimeSpan? RetryAfter = null)
{
    public bool Succeeded => Outcome == DiscordApiOutcome.Succeeded;
}

public interface IDiscordApiClient
{
    Task<DiscordApiResult> FindPublicationMessageAsync(
        string channelId,
        string actionToken,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new DiscordApiResult(
            DiscordApiOutcome.TerminalFailure,
            Error: "discord_reconciliation_search_unavailable: Channel history cannot be searched."));

    Task<DiscordApiResult> CreateMessageAsync(
        string channelId,
        object payload,
        CancellationToken cancellationToken = default);

    Task<DiscordApiResult> EditMessageAsync(
        string channelId,
        string messageId,
        object payload,
        CancellationToken cancellationToken = default);

    Task<DiscordApiResult> DeleteMessageAsync(
        string channelId,
        string messageId,
        CancellationToken cancellationToken = default);

    Task<DiscordApiResult> GetMessageAsync(
        string channelId,
        string messageId,
        CancellationToken cancellationToken = default);

    Task<DiscordApiResult> CreateNotificationMessageAsync(
        string channelId,
        object payload,
        string? allowedMentionUserId,
        CancellationToken cancellationToken = default);

    Task<DiscordApiResult> ResolveDirectMessageChannelAsync(
        string recipientUserId,
        CancellationToken cancellationToken = default);
}

public sealed class DiscordApiClient(
    HttpClient httpClient,
    DiscordCommissionOptions options,
    ILogger<DiscordApiClient> logger) : IDiscordApiClient
{
    private const int MaximumAttempts = 4;
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(30);

    public async Task<DiscordApiResult> FindPublicationMessageAsync(
        string channelId,
        string actionToken,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        if (!options.CanPublishDirectly || !IsDiscordSnowflake(channelId))
        {
            return new(DiscordApiOutcome.TerminalFailure,
                Error: "discord_reconciliation_unconfigured: Discord channel history is unavailable.");
        }
        string? before = null;
        string? found = null;
        try
        {
            for (var page = 0; page < 10; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"channels/{channelId}/messages?limit=100" + (before == null ? "" : $"&before={before}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bot", options.BotToken);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new(DiscordApiOutcome.RetryableFailure, StatusCode: response.StatusCode,
                        Error: $"discord_reconciliation_history_refused: {await ReadErrorAsync(response, cancellationToken)}");
                }
                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var messages = document.RootElement;
                var reachedCreation = false;
                foreach (var message in messages.EnumerateArray())
                {
                    var id = message.GetProperty("id").GetString()!;
                    if (message.TryGetProperty("components", out var components) &&
                        ContainsActionToken(components, actionToken))
                    {
                        if (found != null && found != id)
                        {
                            return new(DiscordApiOutcome.TerminalFailure,
                                Error: "discord_reconciliation_multiple_matches: Multiple Discord messages match; manual review is required.");
                        }
                        found = id;
                    }
                    before = id;
                    if (message.TryGetProperty("timestamp", out var timestamp) &&
                        timestamp.TryGetDateTimeOffset(out var sentAt) && sentAt < createdAt.AddMinutes(-1))
                    {
                        reachedCreation = true;
                    }
                }
                if (messages.GetArrayLength() < 100 || reachedCreation)
                {
                    return new(DiscordApiOutcome.Succeeded, found);
                }
            }
            return new(DiscordApiOutcome.TerminalFailure,
                Error: "discord_reconciliation_history_limit: Recent history did not reach publication creation; no create was retried.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException &&
            !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Discord reconciliation history check failed.");
            return new(DiscordApiOutcome.RetryableFailure,
                Error: $"discord_reconciliation_history_failed: {exception.Message}");
        }
    }

    private static bool ContainsActionToken(JsonElement element, string actionToken) =>
        element.ValueKind switch
        {
            JsonValueKind.Array => element.EnumerateArray().Any(child => ContainsActionToken(child, actionToken)),
            JsonValueKind.Object => element.EnumerateObject().Any(property =>
                property.Name == "custom_id" && property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString()!.EndsWith(":" + actionToken, StringComparison.Ordinal) ||
                property.Name == "components" && ContainsActionToken(property.Value, actionToken)),
            _ => false
        };

    public Task<DiscordApiResult> CreateMessageAsync(
        string channelId,
        object payload,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Post,
            channelId,
            messageId: null,
            payload,
            createOperation: true,
            allowedMentionUserId: null,
            cancellationToken);

    public Task<DiscordApiResult> EditMessageAsync(
        string channelId,
        string messageId,
        object payload,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Patch,
            channelId,
            messageId,
            payload,
            createOperation: false,
            allowedMentionUserId: null,
            cancellationToken);

    public Task<DiscordApiResult> DeleteMessageAsync(
        string channelId,
        string messageId,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Delete,
            channelId,
            messageId,
            payload: null,
            createOperation: false,
            allowedMentionUserId: null,
            cancellationToken);

    public Task<DiscordApiResult> GetMessageAsync(
        string channelId,
        string messageId,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Get,
            channelId,
            messageId,
            payload: null,
            createOperation: false,
            allowedMentionUserId: null,
            cancellationToken);

    public Task<DiscordApiResult> CreateNotificationMessageAsync(
        string channelId,
        object payload,
        string? allowedMentionUserId,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Post,
            channelId,
            messageId: null,
            payload,
            createOperation: true,
            allowedMentionUserId,
            cancellationToken);

    public async Task<DiscordApiResult> ResolveDirectMessageChannelAsync(
        string recipientUserId,
        CancellationToken cancellationToken = default)
    {
        if (!options.CanPublishDirectly ||
            !IsDiscordSnowflake(recipientUserId))
        {
            return new DiscordApiResult(
                DiscordApiOutcome.TerminalFailure,
                Error: "Discord DM delivery is disabled or has an invalid recipient.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "users/@me/channels")
        {
            Content = JsonContent.Create(new
            {
                recipient_id = recipientUserId
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", options.BotToken);
        request.Headers.UserAgent.ParseAdd("FFXIV-Craft-Architect/1.0");
        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var channelId = await ReadMessageIdAsync(response, cancellationToken);
                return IsDiscordSnowflake(channelId ?? string.Empty)
                    ? new DiscordApiResult(
                        DiscordApiOutcome.Succeeded,
                        channelId,
                        response.StatusCode)
                    : new DiscordApiResult(
                        DiscordApiOutcome.RetryableFailure,
                        StatusCode: response.StatusCode,
                        Error: "Discord returned a DM channel without a valid identity.");
            }

            return (int)response.StatusCode >= 500 ||
                response.StatusCode == HttpStatusCode.TooManyRequests
                    ? new DiscordApiResult(
                        DiscordApiOutcome.RetryableFailure,
                        StatusCode: response.StatusCode,
                        Error: await ReadErrorAsync(response, cancellationToken))
                    : new DiscordApiResult(
                        DiscordApiOutcome.TerminalFailure,
                        StatusCode: response.StatusCode,
                        Error: await ReadErrorAsync(response, cancellationToken));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DiscordApiResult(
                DiscordApiOutcome.RetryableFailure,
                Error: "Discord DM channel resolution timed out.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Discord DM channel resolution failed before a response.");
            return new DiscordApiResult(
                DiscordApiOutcome.RetryableFailure,
                Error: "Discord DM channel resolution failed before a response.");
        }
    }

    private async Task<DiscordApiResult> SendAsync(
        HttpMethod method,
        string channelId,
        string? messageId,
        object? payload,
        bool createOperation,
        string? allowedMentionUserId,
        CancellationToken cancellationToken)
    {
        if (!options.CanPublishDirectly ||
            !IsDiscordSnowflake(channelId) ||
            (method != HttpMethod.Post && string.IsNullOrWhiteSpace(messageId)) ||
            (payload != null &&
                !HasSafeAllowedMentions(payload, allowedMentionUserId)))
        {
            logger.LogWarning("discord_publication_preflight_failed: Discord publication configuration, channel identity, message identity, or mention suppression check failed.");
            return new DiscordApiResult(
                DiscordApiOutcome.TerminalFailure,
                Error: "Discord publication is disabled, has an invalid channel, or lacks mention suppression.");
        }

        var relativePath = messageId == null
            ? $"channels/{Uri.EscapeDataString(channelId)}/messages"
            : $"channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(messageId)}";

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(method, relativePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", options.BotToken);
            request.Headers.UserAgent.ParseAdd("FFXIV-Craft-Architect/1.0");
            if (payload != null)
            {
                request.Content = JsonContent.Create(payload);
            }

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new DiscordApiResult(
                    createOperation
                        ? DiscordApiOutcome.ReconciliationRequired
                        : DiscordApiOutcome.RetryableFailure,
                    Error: "Discord request timed out.");
            }
            catch (HttpRequestException exception)
            {
                var beforeSend = exception.HttpRequestError is
                    HttpRequestError.NameResolutionError ||
                    exception.InnerException is System.Net.Sockets.SocketException
                    {
                        SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused or
                            System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NetworkUnreachable
                    } ||
                    exception.Message.Contains("Request headers must contain only ASCII", StringComparison.OrdinalIgnoreCase);
                var outcome = createOperation && !beforeSend
                    ? DiscordApiOutcome.ReconciliationRequired
                    : DiscordApiOutcome.RetryableFailure;
                logger.LogWarning(exception, "Discord transport check failed: {Outcome}, {Method} {Path}.", outcome, method, relativePath);
                return new DiscordApiResult(
                    outcome,
                    Error: $"discord_transport_{(beforeSend ? "before_send" : "ambiguous")}: {exception.Message}");
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    if (method == HttpMethod.Delete)
                    {
                        return new DiscordApiResult(
                            DiscordApiOutcome.Succeeded,
                            messageId,
                            response.StatusCode);
                    }

                    var id = await ReadMessageIdAsync(response, cancellationToken);
                    return method == HttpMethod.Get || !string.IsNullOrWhiteSpace(id)
                        ? new DiscordApiResult(DiscordApiOutcome.Succeeded, id, response.StatusCode)
                        : new DiscordApiResult(
                            createOperation
                                ? DiscordApiOutcome.ReconciliationRequired
                                : DiscordApiOutcome.RetryableFailure,
                            StatusCode: response.StatusCode,
                            Error: "Discord returned success without a message identity.");
                }

                if (method == HttpMethod.Delete && response.StatusCode == HttpStatusCode.NotFound)
                {
                    return new DiscordApiResult(
                        DiscordApiOutcome.Succeeded,
                        messageId,
                        response.StatusCode);
                }

                var error = await ReadErrorAsync(response, cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = await ReadRetryAfterAsync(response, cancellationToken);
                    if (!retryAfter.HasValue ||
                        retryAfter.Value < TimeSpan.Zero ||
                        retryAfter.Value > MaximumRetryAfter)
                    {
                        return new DiscordApiResult(
                            DiscordApiOutcome.TerminalFailure,
                            StatusCode: response.StatusCode,
                            Error: "Discord returned an invalid retry interval.");
                    }

                    if (attempt == MaximumAttempts)
                    {
                        return new DiscordApiResult(
                            DiscordApiOutcome.RetryableFailure,
                            StatusCode: response.StatusCode,
                            Error: error,
                            RetryAfter: retryAfter);
                    }

                    await Task.Delay(retryAfter.Value, cancellationToken);
                    continue;
                }

                if ((int)response.StatusCode >= 500)
                {
                    if (createOperation)
                    {
                        return new DiscordApiResult(
                            DiscordApiOutcome.ReconciliationRequired,
                            StatusCode: response.StatusCode,
                            Error: error);
                    }

                    if (attempt == MaximumAttempts)
                    {
                        return new DiscordApiResult(
                            DiscordApiOutcome.RetryableFailure,
                            StatusCode: response.StatusCode,
                            Error: error);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << (attempt - 1))), cancellationToken);
                    continue;
                }

                return new DiscordApiResult(
                    DiscordApiOutcome.TerminalFailure,
                    StatusCode: response.StatusCode,
                    Error: error);
            }
        }

        return new DiscordApiResult(
            DiscordApiOutcome.RetryableFailure,
            Error: "Discord request exhausted its retry budget.");
    }

    private static bool IsDiscordSnowflake(string value) =>
        value.Length is >= 17 and <= 20 &&
        value.All(char.IsAsciiDigit);

    private static bool HasSafeAllowedMentions(
        object payload,
        string? allowedMentionUserId)
    {
        try
        {
            var json = JsonSerializer.SerializeToElement(payload);
            if (!json.TryGetProperty("allowed_mentions", out var allowedMentions) ||
                allowedMentions.ValueKind != JsonValueKind.Object ||
                !allowedMentions.TryGetProperty("parse", out var parse) ||
                parse.ValueKind != JsonValueKind.Array ||
                parse.GetArrayLength() != 0 ||
                allowedMentions.TryGetProperty("roles", out var roles) &&
                (roles.ValueKind != JsonValueKind.Array || roles.GetArrayLength() != 0) ||
                allowedMentions.TryGetProperty("replied_user", out var repliedUser) &&
                repliedUser.ValueKind != JsonValueKind.False ||
                allowedMentions.EnumerateObject().Any(property =>
                    property.Name is not
                        ("parse" or "users" or "roles" or "replied_user")))
            {
                return false;
            }

            if (!allowedMentions.TryGetProperty("users", out var users))
            {
                return string.IsNullOrWhiteSpace(allowedMentionUserId);
            }

            if (users.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(allowedMentionUserId))
            {
                return users.GetArrayLength() == 0;
            }

            return IsDiscordSnowflake(allowedMentionUserId) &&
                users.GetArrayLength() == 1 &&
                users[0].ValueKind == JsonValueKind.String &&
                string.Equals(
                    users[0].GetString(),
                    allowedMentionUserId,
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<string?> ReadMessageIdAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        try
        {
            using var payload = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            return payload.RootElement.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (text.Length > 512)
        {
            text = text[..512];
        }

        return string.IsNullOrWhiteSpace(text)
            ? $"Discord returned HTTP {(int)response.StatusCode}."
            : text;
    }

    private static async Task<TimeSpan?> ReadRetryAfterAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var payload = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (payload.RootElement.TryGetProperty("retry_after", out var retryAfter) &&
                retryAfter.TryGetDouble(out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }
        catch (JsonException)
        {
            // Fall through to the standard Retry-After header.
        }

        return response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } retryAt
                ? retryAt - DateTimeOffset.UtcNow
                : null);
    }
}
