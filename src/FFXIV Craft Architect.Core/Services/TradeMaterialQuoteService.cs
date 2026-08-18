using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed record TradeMaterialQuoteResult(
    TradeMaterialQuote? Quote,
    IReadOnlyList<CommissionPayrollInputLine> MaterialLines,
    string? FailureReason)
{
    public bool IsComplete => Quote != null && string.IsNullOrWhiteSpace(FailureReason);
}

public sealed class TradeMaterialQuoteService
{
    public IReadOnlyList<DetailedShoppingPlan> PrepareOptimizationInput(
        IReadOnlyList<DetailedShoppingPlan> plans,
        TradeMaterialPricingPolicy? requestedPolicy,
        DateTime quotedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var policy = TradeMaterialPricingPolicyNormalizer.Normalize(requestedPolicy);
        return plans
            .Select(plan => FilterToFreshEvidence(plan, policy, quotedAtUtc))
            .ToArray();
    }

    public TradeMaterialQuoteResult Build(
        ProcurementRouteOptimizationResult optimization,
        IReadOnlyList<MaterialAggregate> demand,
        TradeMaterialPricingPolicy? requestedPolicy,
        DateTime quotedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(optimization);
        ArgumentNullException.ThrowIfNull(demand);

        var policy = TradeMaterialPricingPolicyNormalizer.Normalize(requestedPolicy);
        var decision = optimization.Decision;
        if (!optimization.IsComplete || decision == null)
        {
            return Failure(
                $"No complete executable route fits company policy and current listing evidence " +
                $"({policy.MaximumWorldStops} worlds, " +
                $"{policy.MaximumDataCenterTransfers} data-center transfers, " +
                $"{policy.MaximumConsolidationPremiumPercent:N0}% consolidation premium, " +
                $"listings at most {policy.MaximumEvidenceAgeMinutes:N0} minutes old).");
        }

        var maximumCost = decision.CheapestGilCost *
            (1m + policy.MaximumConsolidationPremiumPercent / 100m);
        var selections = decision.ToleranceSelections
            .Where(candidate => candidate.GilCost <= maximumCost)
            .Where(candidate => candidate.WorldStops <= policy.MaximumWorldStops)
            .Where(candidate => candidate.DataCenterTransfers <= policy.MaximumDataCenterTransfers)
            .OrderBy(candidate => candidate.DataCenterTransfers)
            .ThenBy(candidate => candidate.WorldStops)
            .ThenBy(candidate => candidate.GilCost)
            .ThenBy(candidate => candidate.SelectionKey, StringComparer.Ordinal)
            .ToArray();
        if (selections.Length == 0)
        {
            return Failure(
                $"No complete route fits company policy ({policy.MaximumWorldStops} worlds, " +
                $"{policy.MaximumDataCenterTransfers} data-center transfers, " +
                $"{policy.MaximumConsolidationPremiumPercent:N0}% consolidation premium).");
        }

        var demandByItem = demand
            .Where(row => row.TotalQuantity > 0)
            .GroupBy(row => (row.ItemId, row.RequiresHq))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.TotalQuantity));
        foreach (var selection in selections)
        {
            var result = TryBuildSelection(
                selection,
                demandByItem,
                policy,
                quotedAtUtc);
            if (result.IsComplete)
            {
                return result;
            }
        }

        return Failure(
            "No complete route within company policy uses sufficiently fresh listing evidence.");
    }

    private static TradeMaterialQuoteResult TryBuildSelection(
        MarketRouteToleranceSelection selection,
        IReadOnlyDictionary<(int ItemId, bool RequiresHq), int> demandByItem,
        TradeMaterialPricingPolicy policy,
        DateTime quotedAtUtc)
    {
        var lines = new List<TradeMaterialQuoteLine>();
        var payrollLines = new List<CommissionPayrollInputLine>();
        foreach (var plan in selection.ShoppingPlans)
        {
            var requiresHq = plan.HqQuantityNeeded > 0;
            var required = demandByItem.GetValueOrDefault((plan.ItemId, requiresHq));
            if (required <= 0)
            {
                required = Math.Max(plan.QuantityNeeded, plan.HqQuantityNeeded);
            }

            if (!TryReadSelectedPurchase(plan, quotedAtUtc, policy, out var cash, out var worlds, out var oldestEvidence))
            {
                return Failure($"The selected route for {plan.Name} is incomplete or uses stale market evidence.");
            }

            var unitCost = required > 0 ? cash / required : 0m;
            lines.Add(new TradeMaterialQuoteLine(
                plan.ItemId,
                plan.Name,
                required,
                requiresHq,
                cash,
                worlds,
                oldestEvidence));
            payrollLines.Add(new CommissionPayrollInputLine(
                plan.ItemId,
                plan.Name,
                required,
                unitCost,
                requiresHq,
                CommissionMaterialResponsibility.Crafter,
                "Procurement route",
                $"Whole-listing cash for the company-policy route across {string.Join(", ", worlds)}.",
                oldestEvidence,
                []));
        }

        if (lines.Count != demandByItem.Count)
        {
            return Failure("The selected procurement route does not cover every active material requirement.");
        }

        var routeCash = lines.Sum(line => line.CashRequired);
        if (routeCash != selection.GilCost)
        {
            return Failure("The selected route cash does not reconcile to its listing-level purchases.");
        }

        var allowance = Math.Clamp(
            Math.Round(routeCash * policy.SafetyAllowancePercent / 100m, 0, MidpointRounding.AwayFromZero),
            policy.MinimumSafetyAllowanceGil,
            policy.MaximumSafetyAllowanceGil);
        var quote = new TradeMaterialQuote
        {
            RouteSelectionKey = selection.SelectionKey,
            PolicyFingerprint = TradeMaterialPricingPolicyNormalizer.Fingerprint(policy),
            AppliedPolicy = policy,
            QuotedAtUtc = quotedAtUtc,
            ExpiresAtUtc = quotedAtUtc.AddMinutes(policy.QuoteLifetimeMinutes),
            RouteCashRequired = routeCash,
            SafetyAllowance = allowance,
            MaterialReimbursement = routeCash + allowance,
            WorldStops = selection.WorldStops,
            DataCenterTransfers = selection.DataCenterTransfers,
            Lines = lines
        };
        return new TradeMaterialQuoteResult(quote, payrollLines, null);
    }

    private static bool TryReadSelectedPurchase(
        DetailedShoppingPlan plan,
        DateTime quotedAtUtc,
        TradeMaterialPricingPolicy policy,
        out decimal cash,
        out IReadOnlyList<string> worlds,
        out DateTime? oldestEvidence)
    {
        cash = 0;
        worlds = [];
        oldestEvidence = null;
        if (plan.RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName)
        {
            cash = plan.RecommendedWorld.TotalCost;
            worlds = [MarketShoppingConstants.VendorWorldName];
            return cash >= 0;
        }

        var selectedWorlds = new List<WorldShoppingSummary>();
        if (plan.CoverageSet?.AllCandidates.FirstOrDefault(candidate => candidate.IsDefaultEligible) is { } coverage)
        {
            cash = coverage.CashOutCost;
            foreach (var world in coverage.Worlds)
            {
                var selected = plan.WorldOptions.FirstOrDefault(option =>
                    string.Equals(option.DataCenter, world.DataCenter, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(option.WorldName, world.WorldName, StringComparison.OrdinalIgnoreCase));
                if (selected == null)
                {
                    return false;
                }
                selectedWorlds.Add(selected);
            }
        }
        else if (plan.RecommendedSplit is { Count: > 0 })
        {
            cash = plan.RecommendedSplit.Sum(part => (decimal)part.TotalCost);
            foreach (var part in plan.RecommendedSplit)
            {
                var selected = plan.WorldOptions.FirstOrDefault(option =>
                    string.Equals(option.DataCenter, part.DataCenter, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(option.WorldName, part.WorldName, StringComparison.OrdinalIgnoreCase));
                if (selected == null)
                {
                    return false;
                }
                selectedWorlds.Add(selected);
            }
        }
        else if (plan.RecommendedWorld != null)
        {
            cash = plan.RecommendedWorld.TotalCost;
            selectedWorlds.Add(plan.RecommendedWorld);
        }
        else
        {
            return false;
        }

        worlds = selectedWorlds
            .Select(world => $"{world.WorldName} ({world.DataCenter})")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(world => world, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var timestamps = selectedWorlds
            .Select(world => world.MarketUploadedAtUtc)
            .Where(timestamp => timestamp.HasValue)
            .Select(timestamp => timestamp!.Value)
            .ToArray();
        if (timestamps.Length != selectedWorlds.Count)
        {
            return false;
        }
        oldestEvidence = timestamps.Min();
        return cash >= 0 && IsFresh(oldestEvidence.Value, quotedAtUtc, policy);
    }

    private static DetailedShoppingPlan FilterToFreshEvidence(
        DetailedShoppingPlan plan,
        TradeMaterialPricingPolicy policy,
        DateTime quotedAtUtc)
    {
        if (string.Equals(
                plan.RecommendedWorld?.WorldName,
                MarketShoppingConstants.VendorWorldName,
                StringComparison.OrdinalIgnoreCase))
        {
            return plan;
        }

        var worldOptions = plan.WorldOptions
            .Where(world => world.MarketUploadedAtUtc is { } uploadedAt &&
                            IsFresh(uploadedAt, quotedAtUtc, policy))
            .ToList();
        var freshWorlds = worldOptions
            .Select(world => (DataCenter: world.DataCenter, WorldName: world.WorldName))
            .ToHashSet(WorldKeyComparer.Instance);
        var recommendedWorld = plan.RecommendedWorld is { } recommended &&
                               freshWorlds.Contains((recommended.DataCenter, recommended.WorldName))
            ? recommended
            : null;
        var recommendedSplit = plan.RecommendedSplit is { Count: > 0 } split &&
                               split.All(part => freshWorlds.Contains((part.DataCenter, part.WorldName)))
            ? split.ToList()
            : null;

        return new DetailedShoppingPlan
        {
            ItemId = plan.ItemId,
            Name = plan.Name,
            IconId = plan.IconId,
            QuantityNeeded = plan.QuantityNeeded,
            HqQuantityNeeded = plan.HqQuantityNeeded,
            DCAveragePrice = plan.DCAveragePrice,
            WorldOptions = worldOptions,
            RecommendedWorld = recommendedWorld,
            RecommendedSplit = recommendedSplit,
            CoverageSet = FilterCoverageSet(plan.CoverageSet, freshWorlds),
            Error = plan.Error,
            MarketDataWarning = plan.MarketDataWarning,
            HQAveragePrice = plan.HQAveragePrice,
            Vendors = plan.Vendors.ToList()
        };
    }

    private static MarketCoverageSet? FilterCoverageSet(
        MarketCoverageSet? source,
        IReadOnlySet<(string DataCenter, string WorldName)> freshWorlds)
    {
        if (source is null)
        {
            return null;
        }

        var candidates = source.AllCandidates
            .Where(candidate =>
                candidate.Worlds.Count > 0 &&
                candidate.Worlds.All(world => freshWorlds.Contains((world.DataCenter, world.WorldName))) &&
                candidate.Listings.All(listing => freshWorlds.Contains((listing.DataCenter, listing.WorldName))))
            .ToArray();
        MarketCoverageOption? Keep(MarketCoverageOption? candidate) =>
            candidate is not null &&
            candidates.Any(allowed => string.Equals(
                allowed.CandidateId,
                candidate.CandidateId,
                StringComparison.Ordinal))
                ? candidate
                : null;

        return new MarketCoverageSet(
            source.ItemId,
            source.ItemName,
            source.QuantityNeeded,
            Keep(source.SingleWorld),
            Keep(source.CompactSplit),
            Keep(source.WideSplit),
            Keep(source.CheapestObserved),
            candidates);
    }

    private static bool IsFresh(
        DateTime uploadedAtUtc,
        DateTime quotedAtUtc,
        TradeMaterialPricingPolicy policy) =>
        quotedAtUtc - uploadedAtUtc <= TimeSpan.FromMinutes(policy.MaximumEvidenceAgeMinutes);

    private sealed class WorldKeyComparer : IEqualityComparer<(string DataCenter, string WorldName)>
    {
        public static WorldKeyComparer Instance { get; } = new();

        public bool Equals(
            (string DataCenter, string WorldName) left,
            (string DataCenter, string WorldName) right) =>
            string.Equals(left.DataCenter, right.DataCenter, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.WorldName, right.WorldName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string DataCenter, string WorldName) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.DataCenter),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.WorldName));
    }

    private static TradeMaterialQuoteResult Failure(string reason) =>
        new(null, [], reason);
}
