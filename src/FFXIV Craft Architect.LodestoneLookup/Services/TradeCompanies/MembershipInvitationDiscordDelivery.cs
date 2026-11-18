using System.Net;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

/// <summary>Outcome codes of an invitation's Discord delivery, shared with the web client.</summary>
public static class MembershipInvitationDeliveryCodes
{
    public const string Sent = "sent";
    public const string NoRecipient = "no_recipient";
    public const string DiscordUnconfigured = "discord_unconfigured";
    public const string NoInstallation = "no_installation";
    public const string RecipientUnreachable = "recipient_unreachable";
    public const string DiscordRejected = "discord_rejected";
    public const string DiscordUnavailable = "discord_unavailable";
    public const string DiscordOutcomeUnknown = "discord_outcome_unknown";
    public const string InProgress = "delivery_in_progress";
    public const string InvitationUnavailable = "invitation_unavailable";
}

/// <summary>A host-verified Discord recipient for a company crafter record.</summary>
public sealed record MembershipInvitationRecipient(
    Guid LegacyCrafterId,
    Guid AccountProfileId,
    string DiscordUserId,
    string DisplayName);

public sealed record MembershipInvitationDeliveryResult(
    string Code,
    string? RecipientName)
{
    public bool Sent => Code == MembershipInvitationDeliveryCodes.Sent;
}

/// <summary>
/// Sends a company membership invitation to the invited person as a Discord DM from the
/// company's bot. The recipient is never chosen by the caller: it is the Discord account
/// the host linked through OAuth to the account bound to the invitation's crafter record.
/// </summary>
public sealed class MembershipInvitationDiscordDelivery(
    SqliteMembershipStore memberships,
    SqliteDiscordIdentityStore identities,
    SqliteDiscordCollaborationStore collaboration,
    IDiscordApiClient discord,
    DiscordCommissionOptions options,
    ILogger<MembershipInvitationDiscordDelivery> logger)
{
    // Discord JSON error code for "Cannot send messages to this user": DMs closed, or the
    // recipient shares no server with the bot.
    private const int CannotMessageUserCode = 50007;
    private static readonly TimeSpan StaleAttempt = TimeSpan.FromMinutes(2);

    /// <summary>Null when the company can send invitations on Discord; otherwise the reason code.</summary>
    public async Task<string?> ResolveUnavailableCodeAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        if (!options.CanPublishDirectly)
        {
            return MembershipInvitationDeliveryCodes.DiscordUnconfigured;
        }

        return await collaboration.ResolveCompanyInstallationAsync(companyId, cancellationToken) == null
            ? MembershipInvitationDeliveryCodes.NoInstallation
            : null;
    }

    public async Task<IReadOnlyList<MembershipInvitationRecipient>> LoadRecipientsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        var recipients = new List<MembershipInvitationRecipient>();
        foreach (var binding in await memberships.LoadCrafterBindingsAsync(companyId, cancellationToken))
        {
            var recipient = await ResolveBindingRecipientAsync(binding, cancellationToken);
            if (recipient != null)
            {
                recipients.Add(recipient);
            }
        }
        return recipients;
    }

    public async Task<MembershipInvitationRecipient?> ResolveRecipientAsync(
        CompanyId companyId,
        Guid? legacyCrafterId,
        CancellationToken cancellationToken = default)
    {
        if (!legacyCrafterId.HasValue)
        {
            return null;
        }

        var binding = (await memberships.LoadCrafterBindingsAsync(companyId, cancellationToken))
            .FirstOrDefault(item => item.LegacyCrafterId == legacyCrafterId.Value);
        return binding == null
            ? null
            : await ResolveBindingRecipientAsync(binding, cancellationToken);
    }

    private async Task<MembershipInvitationRecipient?> ResolveBindingRecipientAsync(
        CrafterAccountBinding binding,
        CancellationToken cancellationToken)
    {
        var identity = await identities.LoadByProfileAsync(binding.AccountProfileId, cancellationToken);
        return identity == null || !DiscordIdentityValue.IsSnowflake(identity.DiscordUserId)
            ? null
            : new MembershipInvitationRecipient(
                binding.LegacyCrafterId,
                binding.AccountProfileId,
                identity.DiscordUserId,
                string.IsNullOrWhiteSpace(identity.DisplayNameSnapshot)
                    ? "Discord account"
                    : identity.DisplayNameSnapshot.Trim());
    }

    /// <summary>
    /// Sends the invitation once. Every outcome is recorded on the invitation; the
    /// invitation itself is never changed by a failed delivery.
    /// </summary>
    public async Task<MembershipInvitationDeliveryResult> DeliverAsync(
        CompanyMembershipInvitation invitation,
        string companyName,
        string? crafterName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invitation.Token))
        {
            throw new ArgumentException("Delivery requires the invitation token.", nameof(invitation));
        }

        switch (await memberships.ClaimInvitationDeliveryAsync(
                    invitation.CompanyId,
                    invitation.InvitationId,
                    StaleAttempt,
                    cancellationToken))
        {
            case MembershipInvitationDeliveryClaimStatus.AlreadySent:
                return new MembershipInvitationDeliveryResult(
                    MembershipInvitationDeliveryCodes.Sent,
                    invitation.DiscordDelivery?.RecipientName);
            case MembershipInvitationDeliveryClaimStatus.InProgress:
                return new MembershipInvitationDeliveryResult(
                    MembershipInvitationDeliveryCodes.InProgress,
                    null);
            case MembershipInvitationDeliveryClaimStatus.Unavailable:
                return new MembershipInvitationDeliveryResult(
                    MembershipInvitationDeliveryCodes.InvitationUnavailable,
                    null);
        }

        var recipient = await ResolveRecipientAsync(
            invitation.CompanyId,
            invitation.LegacyCrafterId,
            cancellationToken);
        if (recipient == null)
        {
            return await FailAsync(invitation, null, MembershipInvitationDeliveryCodes.NoRecipient, null, cancellationToken);
        }

        var unavailable = await ResolveUnavailableCodeAsync(invitation.CompanyId, cancellationToken);
        if (unavailable != null)
        {
            return await FailAsync(invitation, recipient, unavailable, null, cancellationToken);
        }

        if (!TryBuildInvitationUrl(invitation, out var invitationUrl))
        {
            return await FailAsync(invitation, recipient, MembershipInvitationDeliveryCodes.DiscordUnconfigured, null, cancellationToken);
        }

        var channel = await discord.ResolveDirectMessageChannelAsync(recipient.DiscordUserId, cancellationToken);
        if (!channel.Succeeded || string.IsNullOrWhiteSpace(channel.MessageId))
        {
            return await FailAsync(invitation, recipient, Classify(channel), channel, cancellationToken);
        }

        var payload = CreateMessage(companyName, crafterName, invitation.ExpiresAtUtc, invitationUrl);
        var message = await discord.CreateNotificationMessageAsync(
            channel.MessageId,
            payload,
            allowedMentionUserId: null,
            cancellationToken);
        if (!message.Succeeded)
        {
            return await FailAsync(invitation, recipient, Classify(message), message, cancellationToken);
        }

        await memberships.RecordInvitationDeliveryAsync(
            invitation.CompanyId,
            invitation.InvitationId,
            sent: true,
            MembershipInvitationDeliveryCodes.Sent,
            recipient.DiscordUserId,
            recipient.DisplayName,
            message.MessageId,
            cancellationToken);
        logger.LogInformation(
            "Company {CompanyId} invitation {InvitationId} was sent on Discord to account {AccountProfileId}.",
            invitation.CompanyId,
            invitation.InvitationId,
            recipient.AccountProfileId);
        return new MembershipInvitationDeliveryResult(MembershipInvitationDeliveryCodes.Sent, recipient.DisplayName);
    }

    private async Task<MembershipInvitationDeliveryResult> FailAsync(
        CompanyMembershipInvitation invitation,
        MembershipInvitationRecipient? recipient,
        string code,
        DiscordApiResult? result,
        CancellationToken cancellationToken)
    {
        // Discord error bodies are not logged verbatim; status and Discord's own error code
        // carry the diagnosis without echoing anything from the request.
        logger.LogWarning(
            "Company {CompanyId} invitation {InvitationId} was not sent on Discord: {DeliveryCode}. Crafter record {LegacyCrafterId}, account {AccountProfileId}, Discord outcome {DiscordOutcome}, HTTP {StatusCode}, Discord error {DiscordErrorCode}.",
            invitation.CompanyId,
            invitation.InvitationId,
            code,
            invitation.LegacyCrafterId,
            recipient?.AccountProfileId,
            result?.Outcome,
            result?.StatusCode is { } status ? (int)status : null,
            ReadDiscordErrorCode(result?.Error));
        await memberships.RecordInvitationDeliveryAsync(
            invitation.CompanyId,
            invitation.InvitationId,
            sent: false,
            code,
            recipient?.DiscordUserId,
            recipient?.DisplayName,
            messageId: null,
            cancellationToken);
        return new MembershipInvitationDeliveryResult(code, recipient?.DisplayName);
    }

    private static string Classify(DiscordApiResult result)
    {
        if (ReadDiscordErrorCode(result.Error) == CannotMessageUserCode)
        {
            return MembershipInvitationDeliveryCodes.RecipientUnreachable;
        }

        return result.Outcome switch
        {
            DiscordApiOutcome.ReconciliationRequired => MembershipInvitationDeliveryCodes.DiscordOutcomeUnknown,
            DiscordApiOutcome.RetryableFailure => MembershipInvitationDeliveryCodes.DiscordUnavailable,
            _ when result.StatusCode == HttpStatusCode.Forbidden => MembershipInvitationDeliveryCodes.RecipientUnreachable,
            _ => MembershipInvitationDeliveryCodes.DiscordRejected
        };
    }

    private static int? ReadDiscordErrorCode(string? error)
    {
        if (string.IsNullOrWhiteSpace(error) || !error.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(error);
            return document.RootElement.TryGetProperty("code", out var code) &&
                code.TryGetInt32(out var value)
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The short invitation link is served from the same origin as the configured commission
    // pages and forwards into the web app's accept flow.
    private bool TryBuildInvitationUrl(CompanyMembershipInvitation invitation, out string url)
    {
        url = string.Empty;
        if (!Uri.TryCreate(options.CommissionBaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("https" or "http") ||
            baseUri.Scheme == "http" && !baseUri.IsLoopback ||
            !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            return false;
        }

        url = new UriBuilder(baseUri)
        {
            Path = $"/join/{Uri.EscapeDataString(invitation.Token!)}",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri.AbsoluteUri;
        return true;
    }

    private static object CreateMessage(
        string companyName,
        string? crafterName,
        DateTimeOffset expiresAtUtc,
        string invitationUrl)
    {
        var fields = new List<object>
        {
            new
            {
                name = "Expires",
                value = $"<t:{expiresAtUtc.ToUnixTimeSeconds()}:f>",
                inline = true
            }
        };
        if (!string.IsNullOrWhiteSpace(crafterName))
        {
            fields.Insert(0, new
            {
                name = "Connects",
                value = DiscordProjectionSanitizer.Text(crafterName, 1024),
                inline = true
            });
        }

        return new
        {
            embeds = new[]
            {
                new
                {
                    title = DiscordProjectionSanitizer.Text($"Invitation to join {companyName}", 256),
                    fields
                }
            },
            components = new[]
            {
                new
                {
                    type = 1,
                    components = new[]
                    {
                        new
                        {
                            type = 2,
                            style = 5,
                            label = "Open invitation",
                            url = invitationUrl
                        }
                    }
                }
            },
            allowed_mentions = new
            {
                parse = Array.Empty<string>(),
                users = Array.Empty<string>(),
                roles = Array.Empty<string>(),
                replied_user = false
            }
        };
    }
}
