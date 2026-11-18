using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record AccountClaimProjectionRow(
    Guid AccountProfileId,
    Guid CompanyId,
    Guid CommissionId,
    Guid ClaimId);

public sealed record AccountClaimProjectionReport(
    bool DryRun,
    int CompaniesScanned,
    int ActiveClaims,
    int ExpectedRows,
    int UnchangedRows,
    IReadOnlyList<AccountClaimProjectionRow> Added,
    IReadOnlyList<AccountClaimProjectionRow> Removed,
    int SkippedNewerCommissions = 0);

/// <summary>
/// Maintains the account→claims lookup the Crafter hub uses to find an account's claims
/// in companies it has no other relationship with. The lookup is derived from canonical
/// commissions only: every committed commission command rewrites that commission's
/// owners, and <see cref="RebuildAsync"/> recomputes the whole table. Readers still check
/// the canonical claim, so a stale row can hide nothing and grant nothing.
/// </summary>
public sealed class AccountClaimProjectionService(
    SqliteMembershipStore memberships,
    SqliteProfileHostStore profiles,
    CompanyHubService hubs,
    LegacyCrafterAccountResolver crafterAccounts,
    SqliteDiscordIdentityStore identities,
    SqliteDiscordNotificationStore notifications,
    ILogger<AccountClaimProjectionService> logger)
{
    /// <summary>
    /// Bumped whenever owner resolution changes, so hosts rebuild the lookup on start.
    /// 2: legacy claims resolve through their committed Discord claim contact.
    /// </summary>
    public const int ProjectionVersion = 2;

    public async Task ProjectAsync(
        CompanyId companyId,
        TradeOrder order,
        CancellationToken cancellationToken = default)
    {
        var claim = order.CompanyCommission?.ActiveClaim;
        var owners = claim == null
            ? []
            : (await ResolveOwnersAsync(companyId, order.Id, claim, null, cancellationToken))
                .Select(account => new AccountCommissionClaimOwner(
                    account,
                    claim.ClaimId,
                    new DateTimeOffset(DateTime.SpecifyKind(claim.ClaimedAtUtc, DateTimeKind.Utc))))
                .ToArray();
        await memberships.SetCommissionClaimOwnersAsync(
            companyId,
            order.Id,
            owners,
            cancellationToken);
    }

    public async Task<AccountClaimProjectionReport> RebuildAsync(
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        // Live commits after this instant are newer than the snapshot below and win.
        var snapshotStartedAtUtc = DateTimeOffset.UtcNow;
        var directory = await hubs.LoadCommissionDirectoryAsync(cancellationToken);
        var expected = new List<AccountCommissionClaim>();
        var activeClaims = 0;
        foreach (var company in directory)
        {
            var context = new CompanyOwnerContext();
            foreach (var order in company.Orders)
            {
                var claim = order.CompanyCommission?.ActiveClaim;
                if (claim == null)
                {
                    continue;
                }
                activeClaims++;
                foreach (var account in await ResolveOwnersAsync(
                             company.CompanyId,
                             order.Id,
                             claim,
                             context,
                             cancellationToken))
                {
                    expected.Add(new AccountCommissionClaim(
                        account,
                        company.CompanyId,
                        order.Id,
                        claim.ClaimId,
                        new DateTimeOffset(DateTime.SpecifyKind(claim.ClaimedAtUtc, DateTimeKind.Utc))));
                }
            }
        }

        var current = await memberships.LoadAllAccountCommissionClaimsAsync(cancellationToken);
        var expectedKeys = expected.Select(Key).ToHashSet();
        var currentKeys = current.Select(Key).ToHashSet();
        var added = expected.Where(item => !currentKeys.Contains(Key(item))).Select(ToRow).ToArray();
        var removed = current.Where(item => !expectedKeys.Contains(Key(item))).Select(ToRow).ToArray();
        var skipped = 0;
        if (!dryRun)
        {
            var applied = await memberships.ApplyAccountCommissionClaimRebuildAsync(
                expected,
                snapshotStartedAtUtc,
                ProjectionVersion,
                cancellationToken);
            skipped = applied.SkippedCommissions;
            logger.LogInformation(
                "Account claim lookup rebuilt (v{Version}): {Added} added, {Removed} removed, {Skipped} newer commissions kept, {Expected} rows expected.",
                ProjectionVersion,
                applied.AddedRows,
                applied.RemovedRows,
                applied.SkippedCommissions,
                expected.Count);
        }

        return new AccountClaimProjectionReport(
            dryRun,
            directory.Count,
            activeClaims,
            expected.Count,
            expected.Count - added.Length,
            added,
            removed,
            skipped);
    }

    // Mirrors LegacyCrafterAccountResolver ownership: committed account evidence is
    // authoritative; claims without it belong to the account their crafter is bound to
    // with history-authorizing evidence, or to an account directly assigned as crafter.
    // A legacy claim whose crafter has no binding yet is resolved the way committed
    // Discord discovery would bind it (its committed Discord claim contact → that
    // Discord user's linked account), so it is findable before the owner's first read.
    // The canonical check on read still decides access.
    private async Task<IReadOnlyList<Guid>> ResolveOwnersAsync(
        CompanyId companyId,
        Guid commissionId,
        CompanyCommissionClaim claim,
        CompanyOwnerContext? context,
        CancellationToken cancellationToken)
    {
        if (claim.AccountEvidence is { } evidence)
        {
            return evidence.ProfileId == Guid.Empty ? [] : [evidence.ProfileId];
        }

        var crafterIds = new[] { claim.CrafterId, claim.ClaimantCharacterId }
            .Where(id => id is { } value && value != Guid.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (crafterIds.Length == 0)
        {
            return [];
        }

        context ??= new CompanyOwnerContext();
        var owners = new HashSet<Guid>();
        IReadOnlyList<CrafterAccountBinding>? bindings = null;
        try
        {
            bindings = context.Bindings ??= await memberships.LoadCrafterBindingsAsync(
                companyId,
                cancellationToken);
            owners.UnionWith(bindings
                .Where(binding => crafterIds.Contains(binding.LegacyCrafterId) &&
                    LegacyCrafterAccountResolver.AuthorizesLegacyClaims(binding.Evidence))
                .Select(binding => binding.AccountProfileId));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Crafter bindings for company {CompanyId} were unavailable while projecting legacy claim {ClaimId}.",
                companyId,
                claim.ClaimId);
        }
        foreach (var crafterId in crafterIds)
        {
            if (await profiles.LoadProfileAsync(crafterId.ToString("D"), cancellationToken) != null)
            {
                owners.Add(crafterId);
            }
        }
        if (bindings != null)
        {
            owners.UnionWith(await ResolveDiscordClaimOwnerAsync(
                companyId,
                commissionId,
                claim,
                crafterIds,
                bindings,
                context,
                cancellationToken));
        }
        return owners.ToArray();
    }

    // Discovery binds a company crafter to the claim's committed Discord contact when the
    // crafter is unbound or only carries a reuse binding; resolve the same account here.
    private async Task<IReadOnlyList<Guid>> ResolveDiscordClaimOwnerAsync(
        CompanyId companyId,
        Guid commissionId,
        CompanyCommissionClaim claim,
        IReadOnlyList<Guid> crafterIds,
        IReadOnlyList<CrafterAccountBinding> bindings,
        CompanyOwnerContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            context.Candidates ??= (await crafterAccounts.LoadCandidatesAsync(companyId, cancellationToken))
                .Select(candidate => candidate.LegacyCrafterId)
                .ToHashSet();
            var bindable = crafterIds.Any(crafterId =>
                context.Candidates.Contains(crafterId) &&
                bindings.FirstOrDefault(binding => binding.LegacyCrafterId == crafterId) is null or
                    { Evidence: CrafterAccountBindingEvidence.ConfirmedClaimReuse });
            if (!bindable)
            {
                return [];
            }
            var discordUserId = await notifications.LoadCommittedClaimContactDiscordUserIdAsync(
                companyId,
                commissionId,
                claim.ClaimId,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(discordUserId))
            {
                return [];
            }
            var identity = await identities.LoadByDiscordUserAsync(discordUserId, cancellationToken);
            return identity == null || identity.ProfileId == Guid.Empty ? [] : [identity.ProfileId];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Discord claim contact for company {CompanyId} claim {ClaimId} was unavailable while projecting its owner.",
                companyId,
                claim.ClaimId);
            return [];
        }
    }

    private sealed class CompanyOwnerContext
    {
        public IReadOnlyList<CrafterAccountBinding>? Bindings { get; set; }
        public HashSet<Guid>? Candidates { get; set; }
    }

    private static (Guid, Guid, Guid, Guid) Key(AccountCommissionClaim item) =>
        (item.AccountProfileId, item.CompanyId.Value, item.CommissionId, item.ClaimId);

    private static AccountClaimProjectionRow ToRow(AccountCommissionClaim item) =>
        new(item.AccountProfileId, item.CompanyId.Value, item.CommissionId, item.ClaimId);
}

public sealed class AccountClaimProjectionPostCommitSink(
    AccountClaimProjectionService projection) : ICompanyCommissionPostCommitSink
{
    public Task OnCommittedAsync(
        TradeCompanyAccessContext access,
        HostedCompanyCommissionSnapshot committed,
        CompanyCommissionActivityEvent activity,
        CancellationToken cancellationToken) =>
        projection.ProjectAsync(access.CompanyId, committed.Order, cancellationToken);
}

/// <summary>
/// Backfills the account→claims lookup when the host starts with an empty lookup or one
/// built by an older projection version.
/// </summary>
public sealed class AccountClaimProjectionBackfillHostedService(
    ProfileHostOptions options,
    SqliteMembershipStore memberships,
    AccountClaimProjectionService projection,
    ILogger<AccountClaimProjectionBackfillHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            var version = await memberships.LoadAccountCommissionClaimProjectionVersionAsync(stoppingToken);
            var populated = await memberships.HasAccountCommissionClaimsAsync(stoppingToken);
            if (populated && version >= AccountClaimProjectionService.ProjectionVersion)
            {
                logger.LogInformation(
                    "Account claim lookup is current (v{Version}); startup backfill skipped.",
                    version);
                return;
            }

            var report = await projection.RebuildAsync(dryRun: false, stoppingToken);
            logger.LogInformation(
                "Account claim lookup startup backfill (stored v{StoredVersion}, empty {Empty}): {Companies} companies, {ActiveClaims} active claims, {Added} added, {Removed} removed, {Skipped} newer commissions kept, {Expected} rows expected.",
                version?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
                !populated,
                report.CompaniesScanned,
                report.ActiveClaims,
                report.Added.Count,
                report.Removed.Count,
                report.SkippedNewerCommissions,
                report.ExpectedRows);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Account claim lookup startup backfill failed; it retries on the next start.");
        }
    }
}
