using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record LegacyCrafterCandidate(
    Guid LegacyCrafterId,
    string DisplayName,
    string? WorldName,
    string? LodestoneCharacterId);

public sealed record LegacyCrafterAccountScope(
    Guid AccountProfileId,
    IReadOnlySet<Guid> AuthorizedCrafterIds)
{
    public bool Owns(CompanyCommissionClaim? claim) =>
        claim != null &&
        ((claim.CrafterId.HasValue && AuthorizedCrafterIds.Contains(claim.CrafterId.Value)) ||
         (claim.ProvisionalCrafterId.HasValue &&
          AuthorizedCrafterIds.Contains(claim.ProvisionalCrafterId.Value)));
}

public sealed class LegacyCrafterAccountResolver(
    SqliteProfileHostStore profiles,
    SqliteMembershipStore memberships,
    SqliteDiscordIdentityStore identities,
    SqliteDiscordNotificationStore notifications,
    ILogger<LegacyCrafterAccountResolver> logger)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task DiscoverCommittedDiscordBindingsAsync(
        CompanyId companyId,
        Guid accountProfileId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await DiscoverCommittedDiscordBindingsCoreAsync(
                companyId,
                accountProfileId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Legacy crafter discovery for company {CompanyId}, account {AccountProfileId} was deferred.",
                companyId,
                accountProfileId);
        }
    }

    private async Task DiscoverCommittedDiscordBindingsCoreAsync(
        CompanyId companyId,
        Guid accountProfileId,
        CancellationToken cancellationToken)
    {
        var identity = await identities.LoadByProfileAsync(accountProfileId, cancellationToken);
        if (identity == null)
        {
            return;
        }

        var candidates = await LoadCandidatesAsync(companyId, cancellationToken);
        if (candidates.Count == 0)
        {
            return;
        }
        var legacyIds = candidates.Select(item => item.LegacyCrafterId).ToHashSet();
        var orders = await LoadOrdersAsync(companyId, cancellationToken);
        foreach (var order in orders)
        {
            var claim = order.CompanyCommission?.ActiveClaim;
            if (claim == null)
            {
                continue;
            }

            var committedDiscordUserId = await notifications
                .LoadCommittedClaimContactDiscordUserIdAsync(
                    companyId,
                    order.Id,
                    claim.ClaimId,
                    cancellationToken);
            if (!string.Equals(
                    committedDiscordUserId,
                    identity.DiscordUserId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var legacyCrafterId in new[] { claim.CrafterId, claim.ProvisionalCrafterId }
                         .Where(id => id.HasValue && legacyIds.Contains(id.Value))
                         .Select(id => id!.Value)
                         .Distinct())
            {
                var result = await memberships.BindCrafterAsync(
                    companyId,
                    legacyCrafterId,
                    accountProfileId,
                    CrafterAccountBindingEvidence.CommittedDiscordClaim,
                    actorProfileId: null,
                    cancellationToken);
                if (result.Status == CrafterAccountBindingMutationStatus.Conflict)
                {
                    logger.LogWarning(
                        "Committed Discord evidence for company {CompanyId} legacy crafter {LegacyCrafterId} conflicts with account {BoundAccountProfileId}; account {AccountProfileId} was not bound.",
                        companyId,
                        legacyCrafterId,
                        result.Binding?.AccountProfileId,
                        accountProfileId);
                }
            }
        }
    }

    public async Task<bool> IsClaimOwnedByAccountAsync(
        CompanyId companyId,
        CompanyCommissionClaim? claim,
        Guid accountProfileId,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(
            companyId,
            accountProfileId,
            cancellationToken);
        return scope.Owns(claim);
    }

    public async Task<LegacyCrafterAccountScope> ResolveScopeAsync(
        CompanyId companyId,
        Guid accountProfileId,
        CancellationToken cancellationToken = default)
    {
        if (accountProfileId == Guid.Empty)
        {
            return new LegacyCrafterAccountScope(accountProfileId, new HashSet<Guid>());
        }
        await DiscoverCommittedDiscordBindingsAsync(
            companyId,
            accountProfileId,
            cancellationToken);
        var authorized = new HashSet<Guid> { accountProfileId };
        try
        {
            authorized.UnionWith((await memberships.LoadCrafterBindingsAsync(
                    companyId,
                    cancellationToken))
                .Where(item => item.AccountProfileId == accountProfileId)
                .Select(item => item.LegacyCrafterId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Legacy crafter bindings for company {CompanyId}, account {AccountProfileId} were unavailable; direct account assignments remain authorized.",
                companyId,
                accountProfileId);
        }
        return new LegacyCrafterAccountScope(accountProfileId, authorized);
    }

    public async Task<IReadOnlyList<LegacyCrafterCandidate>> LoadCandidatesAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        var hostProfileId = await ResolveCompanyHostProfileIdAsync(
            companyId,
            cancellationToken);
        if (hostProfileId == null)
        {
            return [];
        }
        var hosted = await profiles.LoadProfileObjectsAsync(
            hostProfileId,
            ProfileSyncCollections.TradeCrafters,
            cancellationToken);
        var matches = new List<LegacyCrafterCandidate>();
        foreach (var item in hosted)
        {
            try
            {
                var crafter = JsonSerializer.Deserialize<TradeCrafterProfile>(
                    item.Object.PayloadJson,
                    JsonOptions);
                if (crafter?.CompanyProfileId == companyId.Value &&
                    crafter.Id != Guid.Empty &&
                    string.Equals(
                        item.Object.ObjectId,
                        crafter.Id.ToString("D"),
                        StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(new LegacyCrafterCandidate(
                        crafter.Id,
                        string.IsNullOrWhiteSpace(crafter.DisplayName)
                            ? "Legacy crafter"
                            : crafter.DisplayName.Trim(),
                        string.IsNullOrWhiteSpace(crafter.WorldName)
                            ? null
                            : crafter.WorldName.Trim(),
                        string.IsNullOrWhiteSpace(crafter.LodestoneCharacterId)
                            ? null
                            : crafter.LodestoneCharacterId.Trim()));
                }
            }
            catch (JsonException)
            {
            }
        }

        return matches
            .GroupBy(item => item.LegacyCrafterId)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.LegacyCrafterId)
            .ToArray();
    }

    public async Task<bool> IsCompanyCrafterAsync(
        CompanyId companyId,
        Guid legacyCrafterId,
        CancellationToken cancellationToken = default) =>
        (await LoadCandidatesAsync(companyId, cancellationToken))
        .Any(item => item.LegacyCrafterId == legacyCrafterId);

    private async Task<IReadOnlyList<TradeOrder>> LoadOrdersAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var hostProfileId = await ResolveCompanyHostProfileIdAsync(
            companyId,
            cancellationToken);
        if (hostProfileId == null)
        {
            return [];
        }
        var hosted = await profiles.LoadProfileObjectsAsync(
            hostProfileId,
            ProfileSyncCollections.TradeOrders,
            cancellationToken);
        var orders = new List<TradeOrder>();
        foreach (var item in hosted)
        {
            try
            {
                var order = JsonSerializer.Deserialize<TradeOrder>(
                    item.Object.PayloadJson,
                    JsonOptions);
                if (order?.CompanyCommission?.CompanyId == companyId &&
                    order.CompanyCommission.CommissionId == order.Id &&
                    string.Equals(
                        item.Object.ObjectId,
                        order.Id.ToString("D"),
                        StringComparison.OrdinalIgnoreCase))
                {
                    orders.Add(order);
                }
            }
            catch (JsonException)
            {
            }
        }
        return orders;
    }

    private async Task<string?> ResolveCompanyHostProfileIdAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var hosted = await profiles.LoadObjectsAsync(
            ProfileSyncCollections.TradeCompanyProfiles,
            cancellationToken);
        var matches = new List<string>();
        foreach (var item in hosted)
        {
            try
            {
                var company = JsonSerializer.Deserialize<TradeCompanyProfile>(
                    item.Object.PayloadJson,
                    JsonOptions);
                if (company?.Id == companyId.Value &&
                    string.Equals(
                        item.Object.ObjectId,
                        companyId.Value.ToString("D"),
                        StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(item.ProfileId);
                }
            }
            catch (JsonException)
            {
            }
        }

        return matches.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray() is [var only]
            ? only
            : null;
    }
}
