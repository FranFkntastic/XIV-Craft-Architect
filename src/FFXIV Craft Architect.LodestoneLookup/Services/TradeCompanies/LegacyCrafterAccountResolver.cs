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

public sealed record HostedCompanyCrafter(
    string HostProfileId,
    TradeCrafterProfile Crafter,
    long Revision,
    string PayloadJson);

public sealed record LegacyCrafterAccountScope(
    Guid AccountProfileId,
    IReadOnlySet<Guid> AuthorizedCrafterIds)
{
    public bool Owns(CompanyCommissionClaim? claim) =>
        claim != null &&
        AccountProfileId != Guid.Empty &&
        (claim.AccountEvidence is { } evidence
            ? evidence.ProfileId == AccountProfileId
            : (claim.CrafterId.HasValue && AuthorizedCrafterIds.Contains(claim.CrafterId.Value)) ||
              (claim.ClaimantCharacterId.HasValue &&
               AuthorizedCrafterIds.Contains(claim.ClaimantCharacterId.Value)));
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

    private readonly CompanyHostProfileResolver hosts = new(profiles);

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

            foreach (var legacyCrafterId in new[] { claim.CrafterId, claim.ClaimantCharacterId }
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

    /// <summary>
    /// Binds the crafter a commissioner just confirmed for an account-bound claim to the
    /// claiming account, so later claims by that person reuse the same crafter. The
    /// binding only prevents duplicate crafters: the crafter may have been matched by a
    /// public Lodestone ID, so it never authorizes the crafter's legacy claims. Existing
    /// bindings to another account and operator unbind suppressions are respected.
    /// </summary>
    public async Task BindConfirmedClaimCrafterAsync(
        CompanyId companyId,
        CompanyCommissionClaim? claim,
        CancellationToken cancellationToken = default)
    {
        if (claim?.AccountEvidence is not { } evidence ||
            evidence.ProfileId == Guid.Empty ||
            claim.CrafterId is not { } crafterId ||
            crafterId == Guid.Empty ||
            crafterId == evidence.ProfileId)
        {
            return;
        }

        try
        {
            var result = await memberships.BindCrafterAsync(
                companyId,
                crafterId,
                evidence.ProfileId,
                CrafterAccountBindingEvidence.ConfirmedClaimReuse,
                actorProfileId: null,
                cancellationToken);
            if (result.Status == CrafterAccountBindingMutationStatus.Conflict)
            {
                logger.LogWarning(
                    "Confirmed claim crafter {LegacyCrafterId} in company {CompanyId} is bound to account {BoundAccountProfileId}; claiming account {AccountProfileId} was not bound.",
                    crafterId,
                    companyId,
                    result.Binding?.AccountProfileId,
                    evidence.ProfileId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Binding confirmed claim crafter {LegacyCrafterId} in company {CompanyId} to account {AccountProfileId} failed after the commission committed.",
                crafterId,
                companyId,
                evidence.ProfileId);
        }
    }

    /// <summary>
    /// A claim belongs to the account that made it. Authenticated claims carry that
    /// account as committed evidence, which is authoritative; crafter bindings only
    /// decide ownership for legacy claims that predate account evidence.
    /// </summary>
    public async Task<bool> IsClaimOwnedByAccountAsync(
        CompanyId companyId,
        CompanyCommissionClaim? claim,
        Guid accountProfileId,
        CancellationToken cancellationToken = default)
    {
        if (claim == null || accountProfileId == Guid.Empty)
        {
            return false;
        }
        if (claim.AccountEvidence is { } evidence)
        {
            return evidence.ProfileId == accountProfileId;
        }

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
                .Where(item => item.AccountProfileId == accountProfileId &&
                    AuthorizesLegacyClaims(item.Evidence))
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

    /// <summary>
    /// Resolves the claiming account's existing crafter in this company so one person
    /// keeps one crafter identity. Preference: a crafter bound to the account for the
    /// same Lodestone character, then the account's only bound crafter that has no
    /// Lodestone character yet, then an unbound or same-account company crafter with the
    /// exact Lodestone character. A crafter bound to another account is never reused.
    /// </summary>
    public async Task<Guid?> ResolveClaimantCrafterIdAsync(
        CompanyId companyId,
        Guid accountProfileId,
        string? lodestoneCharacterId,
        CancellationToken cancellationToken = default)
    {
        if (accountProfileId == Guid.Empty)
        {
            return null;
        }

        var crafters = await LoadCompanyCraftersAsync(companyId, cancellationToken);
        if (crafters.Count == 0)
        {
            return null;
        }
        var bindings = (await memberships.LoadCrafterBindingsAsync(companyId, cancellationToken))
            .ToDictionary(item => item.LegacyCrafterId, item => item.AccountProfileId);
        var lodestone = NormalizeLodestoneId(lodestoneCharacterId);
        var ordered = crafters
            .OrderBy(item => item.Crafter.CreatedAtUtc)
            .ThenBy(item => item.Crafter.Id)
            .ToArray();
        var bound = ordered
            .Where(item => bindings.TryGetValue(item.Crafter.Id, out var owner) &&
                owner == accountProfileId)
            .ToArray();
        if (lodestone != null &&
            bound.FirstOrDefault(item =>
                NormalizeLodestoneId(item.Crafter.LodestoneCharacterId) == lodestone) is { } boundMatch)
        {
            return boundMatch.Crafter.Id;
        }
        var boundWithoutCharacter = bound
            .Where(item => NormalizeLodestoneId(item.Crafter.LodestoneCharacterId) == null)
            .ToArray();
        if (boundWithoutCharacter.Length == 1)
        {
            return boundWithoutCharacter[0].Crafter.Id;
        }
        if (lodestone == null)
        {
            return null;
        }

        return ordered.FirstOrDefault(item =>
                NormalizeLodestoneId(item.Crafter.LodestoneCharacterId) == lodestone &&
                (!bindings.TryGetValue(item.Crafter.Id, out var owner) ||
                 owner == accountProfileId))
            ?.Crafter.Id;
    }

    /// <summary>
    /// Loads every live company crafter row from the company's single hosting profile,
    /// with the sync revision needed to update or tombstone it.
    /// </summary>
    public async Task<IReadOnlyList<HostedCompanyCrafter>> LoadCompanyCraftersAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        var hostProfileId = await ResolveCompanyHostProfileIdAsync(
            companyId,
            cancellationToken);
        return hostProfileId == null
            ? []
            : await hosts.LoadCraftersAsync(hostProfileId, companyId, cancellationToken);
    }

    // Only evidence that ties the account to the person behind the crafter's history
    // (a committed Discord claim contact or an operator's explicit bind) can authorize
    // claims that predate account evidence.
    public static bool AuthorizesLegacyClaims(CrafterAccountBindingEvidence evidence) =>
        evidence is CrafterAccountBindingEvidence.CommittedDiscordClaim
            or CrafterAccountBindingEvidence.OperatorConfirmed;

    public static string? NormalizeLodestoneId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
        CancellationToken cancellationToken) =>
        await hosts.ResolveAsync(companyId, cancellationToken) is
        { Status: CompanyHostProfileStatus.Resolved, ProfileId: { } hostProfileId }
            ? hostProfileId
            : null;
}
