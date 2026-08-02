using System.Security.Cryptography;
using System.Text;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;

internal sealed class DiscordInteractionAccessResolver(
    DiscordIdentityOptions options,
    SqliteDiscordIdentityStore links,
    IDiscordCanonicalInteractionAuthority canonicalAuthority,
    SqliteCompanyCommissionCapabilityStore capabilities,
    TimeProvider timeProvider) :
    IDiscordInteractionAccessResolver,
    IDiscordParticipantExchangeService
{
    public async Task<DiscordInteractionAccessResolution> ResolveAsync(
        DiscordInteractionTarget target,
        CancellationToken cancellationToken = default)
    {
        var authority = await ResolveAuthorityAsync(target, cancellationToken);
        return authority == null
            ? Denied(DiscordInteractionAccessStatus.Forbidden)
            : ToResolution(authority, participantAction: null);
    }

    public async Task<DiscordInteractionAccessResolution> IssueParticipantEntryAsync(
        DiscordInteractionTarget target,
        CancellationToken cancellationToken = default)
    {
        var authority = await ResolveAuthorityAsync(target, cancellationToken);
        if (authority is not { IsActiveParticipant: true })
        {
            return authority == null
                ? Denied(DiscordInteractionAccessStatus.Forbidden)
                : ToResolution(authority, participantAction: null);
        }

        var now = timeProvider.GetUtcNow();
        var existingBinding = await links.LoadBootstrapAsync(
            target.InteractionId,
            cancellationToken);
        if (existingBinding != null &&
            (existingBinding.ProfileId != authority.ProfileId ||
             existingBinding.DiscordUserId != authority.DiscordUserId ||
             existingBinding.CompanyId != authority.CompanyId ||
             existingBinding.CommissionId != authority.CommissionId ||
             existingBinding.PublicBriefId != authority.PublicBriefId ||
             existingBinding.ParticipantGrantId != authority.ParticipantGrantId ||
             existingBinding.ParticipantCapabilityRevision !=
                authority.ParticipantCapabilityRevision))
        {
            return Denied(DiscordInteractionAccessStatus.Forbidden);
        }

        var binding = existingBinding ??
            new DiscordParticipantBootstrapBinding(
                target.InteractionId,
                authority.ProfileId,
                authority.DiscordUserId,
                authority.CompanyId,
                authority.CommissionId,
                authority.PublicBriefId,
                authority.ParticipantGrantId,
                authority.ParticipantCapabilityRevision,
                now + options.ParticipantBootstrapLifetime);
        var token = DeriveBootstrapToken(binding);
        await links.IssueBootstrapAsync(
            binding,
            token,
            now,
            cancellationToken);
        var entryUri = new Uri(
            SqliteCompanyCommissionCapabilityStore.BuildFragmentUrl(
                authority.PublicUrl.AbsoluteUri,
                "bootstrap",
                token));
        var action = new DiscordInteractionAction(
            DiscordInteractionActionKind.OpenParticipantCommission,
            "Open participant workspace",
            entryUri,
            DiscordInteractionActionDelivery.EphemeralOnly);
        return ToResolution(authority, action);
    }

    public async Task<DiscordParticipantExchangeResponse?> ExchangeAsync(
        DiscordParticipantExchangeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!SqliteCompanyCommissionCapabilityStore.IsValidCapability(
                request.ParticipantCredential))
        {
            return null;
        }

        var redemption = await links.RedeemBootstrapAsync(
            request.BootstrapToken,
            request.ParticipantCredential,
            timeProvider.GetUtcNow(),
            cancellationToken);
        if (redemption.Status is not (
                DiscordParticipantBootstrapRedemptionStatus.Redeemed or
                DiscordParticipantBootstrapRedemptionStatus.Replayed) ||
            redemption.Binding is not { } binding)
        {
            return null;
        }

        var authority = await ResolveAuthorityAsync(
            new DiscordInteractionTarget(
                binding.ProviderEventId,
                binding.DiscordUserId,
                binding.CompanyId,
                binding.CommissionId,
                binding.PublicBriefId),
            cancellationToken);
        if (authority is not { IsActiveParticipant: true } ||
            authority.ProfileId != binding.ProfileId ||
            authority.ParticipantGrantId != binding.ParticipantGrantId ||
            authority.ParticipantCapabilityRevision != binding.ParticipantCapabilityRevision)
        {
            return null;
        }

        await capabilities.InstallLinkedParticipantAsync(
            binding.CompanyId,
            binding.CommissionId,
            binding.PublicBriefId,
            binding.ParticipantGrantId,
            binding.ParticipantCapabilityRevision,
            request.ParticipantCredential,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        return new DiscordParticipantExchangeResponse(binding.PublicBriefId);
    }

    private async Task<DiscordParticipantAuthority?> ResolveAuthorityAsync(
        DiscordInteractionTarget target,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled ||
            !DiscordIdentityValue.IsSnowflake(target.InteractionId) ||
            !DiscordIdentityValue.IsSnowflake(target.DiscordUserId) ||
            target.CommissionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(target.PublicBriefId) ||
            target.PublicBriefId.Length >
                SqliteCompanyCommissionCapabilityStore.MaximumPublicBriefIdLength)
        {
            return null;
        }

        var link = await links.LoadByDiscordUserAsync(
            target.DiscordUserId,
            cancellationToken);
        return link == null
            ? null
            : await canonicalAuthority.ResolveAsync(
                link,
                target,
                cancellationToken);
    }

    private DiscordInteractionAccessResolution ToResolution(
        DiscordParticipantAuthority authority,
        DiscordInteractionAction? participantAction)
    {
        var actions = new List<DiscordInteractionAction>();
        if (authority.IsCompanyOperator)
        {
            actions.Add(new DiscordInteractionAction(
                DiscordInteractionActionKind.OpenOwnerOrder,
                "Open in Trade Architect",
                BuildOwnerUri(authority.CommissionId),
                DiscordInteractionActionDelivery.EphemeralOnly));
        }

        if (participantAction != null)
        {
            actions.Add(participantAction);
        }

        return new DiscordInteractionAccessResolution(
            DiscordInteractionAccessStatus.Authorized,
            authority.ProfileId,
            authority.IsCompanyOperator,
            authority.IsActiveParticipant,
            actions);
    }

    private Uri BuildOwnerUri(Guid commissionId)
    {
        var builder = new UriBuilder(new Uri(options.ApplicationBaseUri));
        builder.Path = builder.Path.TrimEnd('/') + "/trade/orders";
        builder.Query = $"orderId={Uri.EscapeDataString(commissionId.ToString("D"))}";
        builder.Fragment = string.Empty;
        return builder.Uri;
    }

    private string DeriveBootstrapToken(DiscordParticipantBootstrapBinding binding)
    {
        var material = string.Join(
            '|',
            binding.ProviderEventId,
            binding.ProfileId.ToString("D"),
            binding.DiscordUserId,
            binding.CompanyId.ToString(),
            binding.CommissionId.ToString("D"),
            binding.PublicBriefId,
            binding.ParticipantGrantId.ToString("D"),
            binding.ParticipantCapabilityRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            binding.ExpiresAt.ToUnixTimeSeconds().ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        using var hmac = new HMACSHA256(
            Encoding.UTF8.GetBytes(options.BootstrapSecret));
        return DiscordIdentityValue.Base64Url(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(material)));
    }

    private static DiscordInteractionAccessResolution Denied(
        DiscordInteractionAccessStatus status) =>
        new(status, null, false, false, []);
}
