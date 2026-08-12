using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

internal sealed class DiscordCommissionInteractionService(
    DiscordCommissionOptions options,
    SqliteDiscordCollaborationStore collaboration,
    SqliteDiscordIdentityStore identities,
    SqliteProfileHostStore profiles,
    ProfileHostedTradeCompanyService companies,
    SqliteMembershipStore memberships,
    SqliteDiscordNotificationStore notifications,
    IDiscordInteractionClaimLinkIssuer claimLinks,
    IDiscordInteractionAccessResolver accessResolver,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan ClaimExpectationLifetime = TimeSpan.FromMinutes(15);

    public async Task<object> HandleAsync(
        JsonElement interaction,
        CancellationToken cancellationToken = default)
    {
        if (ReadString(interaction, "guild_id") != options.AllowedGuildId ||
            ReadString(interaction, "channel_id") != options.AllowedChannelId)
        {
            return Refusal("This action is available only in the dedicated commission channel.");
        }

        if (!interaction.TryGetProperty("data", out var data) ||
            !TryParseAction(ReadString(data, "custom_id"), out var action, out var actionToken))
        {
            return Refusal("This commission action is no longer available.");
        }

        var publication = await collaboration.LoadPublicationByActionTokenAsync(
            actionToken,
            cancellationToken);
        var contact = DiscordOriginContactCapture.FromVerifiedInteraction(interaction);
        var interactionId = ReadString(interaction, "id");
        if (publication == null ||
            contact == null ||
            !DiscordSnowflake.IsValid(interactionId) ||
            !string.Equals(
                publication.ChannelId,
                options.AllowedChannelId,
                StringComparison.Ordinal))
        {
            return Refusal("This commission action is no longer available.");
        }

        var target = new DiscordInteractionTarget(
            interactionId!,
            contact.DiscordUserId,
            publication.CompanyId,
            publication.OrderId,
            publication.PublicId);
        return action switch
        {
            DiscordCommissionComponentAction.ClaimWithDiscord =>
                await ClaimWithDiscordAsync(
                    publication,
                    target,
                    contact,
                    cancellationToken),
            DiscordCommissionComponentAction.OpenWorkspace
                when options.CrafterWorkspaceEnabled =>
                await OpenWorkspaceAsync(publication, target, cancellationToken),
            DiscordCommissionComponentAction.OpenWorkspace =>
                Refusal("This commission workspace is not available."),
            _ => Refusal("This commission action is no longer available.")
        };
    }

    private async Task<object> ClaimWithDiscordAsync(
        DiscordPublicationRecord publication,
        DiscordInteractionTarget target,
        DiscordOriginContact contact,
        CancellationToken cancellationToken)
    {
        if (publication.State != DiscordPublicationState.Open)
        {
            return Refusal("This commission is no longer open for claiming.");
        }

        var link = await identities.LoadByDiscordUserAsync(
            target.DiscordUserId,
            cancellationToken);
        if (link == null ||
            await profiles.LoadProfileAsync(
                link.ProfileId.ToString("D"),
                cancellationToken) == null)
        {
            return Refusal(
                "Link Discord in Craft Architect Options before claiming with Discord.");
        }
        var holdingProfile = await companies.ResolveProfileAccessAsync(
            link.ProfileId,
            publication.CompanyId,
            cancellationToken);
        var membership = holdingProfile == null
            ? await memberships.LoadAsync(
                publication.CompanyId,
                link.ProfileId,
                cancellationToken)
            : null;
        if (holdingProfile == null && membership is not { State: MembershipState.Active })
        {
            return Refusal("This commission is not available to your company membership.");
        }

        var issued = await claimLinks.IssueInteractionClaimLinkAsync(
            publication,
            cancellationToken);
        if (issued == null)
        {
            return Refusal("This commission is no longer open for claiming.");
        }

        var now = timeProvider.GetUtcNow();
        var recorded = await notifications.RecordPendingClaimContactAsync(
            new PendingDiscordClaimContactExpectation(
                publication.CompanyId,
                publication.OrderId,
                publication.PublicId,
                issued.CapabilityId,
                issued.CapabilityRevision,
                target.InteractionId,
                contact,
                now,
                now + ClaimExpectationLifetime),
            cancellationToken);
        return recorded
            ? DiscordCommissionMessage.CreateEphemeral(
                "Continue in Craft Architect to review and submit your claim.",
                [new DiscordEphemeralLink("Claim in Craft Architect", issued.ClaimUrl)])
            : Refusal("This commission claim action could not be issued.");
    }

    private async Task<object> OpenWorkspaceAsync(
        DiscordPublicationRecord publication,
        DiscordInteractionTarget target,
        CancellationToken cancellationToken)
    {
        if (publication.State is not (
                DiscordPublicationState.Open or DiscordPublicationState.Assigned))
        {
            return Refusal("This commission workspace is not available.");
        }

        var resolution = await accessResolver.ResolveAsync(target, cancellationToken);
        if (!resolution.Authorized)
        {
            return Refusal("This commission workspace is not available for this account.");
        }

        var actions = resolution.Actions.ToList();
        if (resolution.IsActiveParticipant)
        {
            var participant = await accessResolver.IssueParticipantEntryAsync(
                target,
                cancellationToken);
            foreach (var action in participant.Actions)
            {
                if (actions.All(existing => existing.Kind != action.Kind))
                {
                    actions.Add(action);
                }
            }
        }

        var links = actions
            .Where(action =>
                action.Delivery == DiscordInteractionActionDelivery.EphemeralOnly)
            .Select(action => new DiscordEphemeralLink(action.Label, action.Uri))
            .ToArray();
        return links.Length == 0
            ? Refusal("This commission workspace is not available for this account.")
            : DiscordCommissionMessage.CreateEphemeral(
                "Open your current Craft Architect workspace.",
                links);
    }

    private static bool TryParseAction(
        string? customId,
        out DiscordCommissionComponentAction action,
        out string actionToken)
    {
        action = default;
        actionToken = string.Empty;
        if (string.IsNullOrWhiteSpace(customId))
        {
            return false;
        }

        var separator = customId.IndexOf(':');
        if (separator <= 0 || separator == customId.Length - 1)
        {
            return false;
        }

        action = customId[..separator] switch
        {
            "claim-discord" => DiscordCommissionComponentAction.ClaimWithDiscord,
            "open-workspace" => DiscordCommissionComponentAction.OpenWorkspace,
            _ => DiscordCommissionComponentAction.Unsupported
        };
        actionToken = customId[(separator + 1)..];
        return action != DiscordCommissionComponentAction.Unsupported &&
            actionToken.StartsWith("ca:v1:", StringComparison.Ordinal) &&
            actionToken.Length <= 100;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static object Refusal(string message) =>
        DiscordCommissionMessage.CreateEphemeral(message);

    private enum DiscordCommissionComponentAction
    {
        Unsupported,
        ClaimWithDiscord,
        OpenWorkspace
    }
}

internal sealed class DiscordClaimContactCommitter(
    SqliteDiscordNotificationStore notifications,
    ICompanyCommissionDiscordDelivery discordDelivery,
    TimeProvider timeProvider)
{
    public async Task<bool> CaptureMemberAsync(
        Guid profileId,
        SqliteDiscordIdentityStore identities,
        CompanyCommissionMutationResult mutation,
        CancellationToken cancellationToken = default)
    {
        var identity = await identities.LoadByProfileAsync(profileId, cancellationToken);
        var activity = mutation.Activity;
        var commission = mutation.Order?.CompanyCommission;
        var committedClaim = commission?.ActiveClaim;
        if (!mutation.Success ||
            identity == null ||
            activity is not { Kind: CompanyCommissionActivityKind.ClaimAccepted } ||
            activity.EventId == Guid.Empty ||
            activity.CommissionRevision <= 0 ||
            committedClaim == null ||
            committedClaim.ClaimId == Guid.Empty)
        {
            return false;
        }

        await discordDelivery.CaptureDiscordClaimContactAsync(
            new CommittedDiscordClaimContact(
                commission!.CompanyId,
                commission.CommissionId,
                committedClaim.ClaimId,
                activity.EventId,
                activity.CommissionRevision,
                activity.Kind,
                activity.CreatedAtUtc,
                identity.DiscordUserId,
                new DiscordOriginContact(
                    identity.DiscordUserId,
                    identity.DisplayNameSnapshot)),
            cancellationToken);
        return true;
    }

    public async Task<bool> CaptureAsync(
        CompanyCommissionCapabilityResolution claimCapability,
        CompanyCommissionMutationResult mutation,
        CancellationToken cancellationToken = default)
    {
        var activity = mutation.Activity;
        var committedClaim = mutation.Order?.CompanyCommission?.ActiveClaim;
        if (!mutation.Success ||
            claimCapability.Kind != CompanyCommissionCapabilityKind.Claim ||
            claimCapability.CapabilityId == Guid.Empty ||
            activity is not { Kind: CompanyCommissionActivityKind.ClaimAccepted } ||
            activity.EventId == Guid.Empty ||
            activity.CommissionRevision <= 0 ||
            committedClaim == null ||
            committedClaim.ClaimId == Guid.Empty)
        {
            return false;
        }

        var expectation = await notifications.LoadPendingClaimContactAsync(
            claimCapability.CompanyId,
            claimCapability.CommissionId,
            claimCapability.PublicBriefId,
            claimCapability.CapabilityId,
            claimCapability.CapabilityRevision,
            timeProvider.GetUtcNow(),
            cancellationToken);
        if (expectation == null)
        {
            return false;
        }

        await discordDelivery.CaptureDiscordClaimContactAsync(
            new CommittedDiscordClaimContact(
                claimCapability.CompanyId,
                claimCapability.CommissionId,
                committedClaim.ClaimId,
                activity.EventId,
                activity.CommissionRevision,
                activity.Kind,
                activity.CreatedAtUtc,
                expectation.InteractionId,
                expectation.Contact),
            cancellationToken);
        return await notifications.ConsumePendingClaimContactAsync(
            expectation,
            cancellationToken);
    }
}
