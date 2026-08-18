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
            return Failure("No complete executable procurement route satisfies the available market evidence.");
        }

        var maximumCost = decision.CheapestGilCost *
            (1m + policy.MaximumConsolidationPremiumPercent / 100m);
        var selection = decision.ToleranceSelections
            .Where(candidate => candidate.GilCost <= maximumCost)
            .Where(candidate => candidate.WorldStops <= policy.MaximumWorldStops)
            .Where(candidate => candidate.DataCenterTransfers <= policy.MaximumDataCenterTransfers)
            .OrderBy(candidate => candidate.DataCenterTransfers)
            .ThenBy(candidate => candidate.WorldStops)
            .ThenBy(candidate => candidate.GilCost)
            .ThenBy(candidate => candidate.SelectionKey, StringComparer.Ordinal)
            .FirstOrDefault();
        if (selection == null)
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
        return cash >= 0 && quotedAtUtc - oldestEvidence.Value <= TimeSpan.FromMinutes(policy.MaximumEvidenceAgeMinutes);
    }

    private static TradeMaterialQuoteResult Failure(string reason) =>
        new(null, [], reason);
}
