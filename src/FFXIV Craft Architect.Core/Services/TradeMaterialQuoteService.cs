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
    public async Task<TradeMaterialQuoteResult> PriceAsync(
        MarketShoppingService shopping,
        IReadOnlyList<DetailedShoppingPlan> plans,
        IReadOnlyList<MaterialAggregate> demand,
        IReadOnlyDictionary<(int ItemId, bool RequiresHq), AcquisitionSource> sources,
        TradeMaterialPricingPolicy policy,
        DateTime now,
        IReadOnlyList<CommissionPayrollInputLine> selectedSourceLines,
        CancellationToken ct = default)
    {
        var stock = demand.Where(item => sources.GetValueOrDefault((item.ItemId, item.RequiresHq)) == AcquisitionSource.OnHand).ToArray();
        var purchases = demand.Except(stock).ToArray();
        var references = selectedSourceLines.Where(line => line.EvidenceSource == TradeOrderWorkflow.OnHandEvidenceSource)
            .ToDictionary(line => (line.ItemId, line.RequiresHq));
        if (stock.Any(item => !references.TryGetValue((item.ItemId, item.RequiresHq), out var line) ||
            line.Quantity != item.TotalQuantity))
        {
            return Failure("On-hand quantities do not match the selected-source material evidence.");
        }

        var stockLines = stock.Select(item => references[(item.ItemId, item.RequiresHq)]).ToArray();
        var stockCoverage = stock.Select(item => new TradeMaterialQuoteLine(
            item.ItemId, item.Name, item.TotalQuantity, item.RequiresHq, 0, [], null)).ToArray();
        if (stock.Length > 0 && purchases.Length == 0)
        {
            var normalized = TradeMaterialPricingPolicyNormalizer.Normalize(policy);
            return new(new TradeMaterialQuote
            {
                RouteSelectionKey = "on-hand",
                WorldStops = 0,
                RouteCashRequired = 0,
                MaterialReimbursement = 0,
                SafetyAllowance = 0,
                Lines = stockCoverage,
                AppliedPolicy = normalized,
                PolicyFingerprint = TradeMaterialPricingPolicyNormalizer.Fingerprint(normalized),
                QuotedAtUtc = now,
                ExpiresAtUtc = now.AddMinutes(normalized.QuoteLifetimeMinutes)
            }, stockLines, null);
        }

        var input = PrepareOptimizationInput(plans, purchases, sources, policy, now);
        var routePolicy = TradeMaterialPricingPolicyNormalizer.Normalize(policy);
        var optimization = await shopping.OptimizeProcurementRouteWithDecisionAsync(
            input, new MarketAnalysisConfig
            {
                MaxWorldsPerItem = routePolicy.MaximumWorldStops,
                MaxRouteWorldStops = routePolicy.MaximumWorldStops,
                EnableSplitWorld = routePolicy.AllowSplitPurchases,
                TravelTolerance = 11
            }, includeSplitPurchases: routePolicy.AllowSplitPurchases,
            executionOptions: MarketAnalysisExecutionOptions.Synchronous, ct: ct);
        var result = Build(optimization, purchases, policy, now);
        return result.Quote is { } quote && stock.Length > 0
            ? new(quote with { Lines = [.. quote.Lines, .. stockCoverage] }, [.. result.MaterialLines, .. stockLines], null)
            : result;
    }

    public static string DescribeNoExecutableRoute(
        TradeMaterialPricingPolicy? requestedPolicy)
    {
        var policy = TradeMaterialPricingPolicyNormalizer.Normalize(requestedPolicy);
        return $"No complete executable route fits company policy and current listing evidence " +
               $"({policy.MaximumWorldStops} worlds, " +
               $"{policy.MaximumConsolidationPremiumPercent:N0}% consolidation premium).";
    }

    public IReadOnlyList<DetailedShoppingPlan> PrepareOptimizationInput(
        IReadOnlyList<DetailedShoppingPlan> plans,
        TradeMaterialPricingPolicy? requestedPolicy,
        DateTime quotedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plans);

        _ = TradeMaterialPricingPolicyNormalizer.Normalize(requestedPolicy);
        return plans
            .Select(PreparePlanForOptimization)
            .ToArray();
    }

    public IReadOnlyList<DetailedShoppingPlan> PrepareOptimizationInput(
        IReadOnlyList<DetailedShoppingPlan> plans,
        IReadOnlyList<MaterialAggregate> demand,
        IReadOnlyDictionary<(int ItemId, bool RequiresHq), AcquisitionSource> selectedSources,
        TradeMaterialPricingPolicy? requestedPolicy,
        DateTime quotedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(selectedSources);

        _ = TradeMaterialPricingPolicyNormalizer.Normalize(requestedPolicy);
        return demand.Select(item =>
            {
                var key = (item.ItemId, item.RequiresHq);
                var source = selectedSources.GetValueOrDefault(key, AcquisitionSource.UnknownSource);
                var plan = plans.FirstOrDefault(candidate => candidate.ItemId == item.ItemId);
                return source switch
                {
                    AcquisitionSource.VendorBuy => BuildVendorPlan(item),
                    AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq when plan != null =>
                        PreparePlanForOptimization(CloneForDemand(plan, item)),
                    _ => BuildUnsupportedPlan(item, source)
                };
            })
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
        if (decision == null)
        {
            return Failure(DescribeNoExecutableRoute(policy, optimization.ShoppingPlans, demand));
        }

        // Every complete route the optimizer exported that fits the stop limit: the slider ticks
        // plus the bounded frontier routes those 12 ticks can skip.
        var bounded = decision.ToleranceSelections
            .Concat(decision.BoundedRouteSelections)
            .Where(candidate => candidate.WorldStops <= policy.MaximumWorldStops)
            .Where(IsCompleteSelection)
            .DistinctBy(candidate => candidate.SelectionKey, StringComparer.Ordinal)
            .ToArray();
        if (bounded.Length == 0)
        {
            return Failure(DescribeNoExecutableRoute(policy, optimization.ShoppingPlans, demand));
        }

        // Premium is measured against the cheapest route overall. When that route needs more
        // stops than policy allows and no bounded route lands inside its premium window, anchor
        // the window to the cheapest bounded route instead of rejecting every bounded route.
        var premiumFactor = 1m + policy.MaximumConsolidationPremiumPercent / 100m;
        var maximumCost = decision.CheapestGilCost * premiumFactor;
        if (!bounded.Any(candidate => candidate.GilCost <= maximumCost))
        {
            maximumCost = bounded.Min(candidate => candidate.GilCost) * premiumFactor;
        }

        var selections = bounded
            .Where(candidate => candidate.GilCost <= maximumCost)
            .OrderBy(candidate => candidate.WorldStops)
            .ThenBy(candidate => candidate.EvidencePenalty)
            .ThenBy(candidate => candidate.GilCost)
            .ThenBy(candidate => candidate.SelectionKey, StringComparer.Ordinal)
            .ToArray();

        var demandByItem = demand
            .Where(row => row.TotalQuantity > 0)
            .GroupBy(row => (row.ItemId, row.RequiresHq))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.TotalQuantity));
        string? firstRejection = null;
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
            firstRejection ??= result.FailureReason;
        }

        return Failure(
            "No complete route within company policy could be reconciled to listing-level purchases." +
            (string.IsNullOrWhiteSpace(firstRejection) ? string.Empty : $" {firstRejection}"));
    }

    private static bool IsCompleteSelection(MarketRouteToleranceSelection selection) =>
        selection.ItemDecisions.Count == selection.ShoppingPlans.Count(plan =>
            !string.Equals(
                plan.RecommendedWorld?.WorldName,
                MarketShoppingConstants.VendorWorldName,
                StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Names the materials that block every executable route, using the plans the optimizer saw:
    /// unsupported sources, stock shortfalls, and items whose stock is spread across more worlds
    /// than the stop limit allows.
    /// </summary>
    private static string DescribeNoExecutableRoute(
        TradeMaterialPricingPolicy policy,
        IReadOnlyList<DetailedShoppingPlan> plans,
        IReadOnlyList<MaterialAggregate> demand)
    {
        var summary = DescribeNoExecutableRoute(policy);
        var blockers = new List<string>();
        foreach (var plan in plans)
        {
            if (string.Equals(
                    plan.RecommendedWorld?.WorldName,
                    MarketShoppingConstants.VendorWorldName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var requiresHq = plan.HqQuantityNeeded > 0;
            var required = demand
                .Where(row => row.ItemId == plan.ItemId && row.RequiresHq == requiresHq)
                .Sum(row => row.TotalQuantity);
            if (required <= 0)
            {
                required = Math.Max(plan.QuantityNeeded, plan.HqQuantityNeeded);
            }

            if (!string.IsNullOrWhiteSpace(plan.Error))
            {
                blockers.Add($"{plan.Name}: {plan.Error.TrimEnd('.')}");
                continue;
            }

            var quality = requiresHq ? "HQ " : string.Empty;
            var stockByWorld = plan.WorldOptions
                .Where(world => !string.Equals(
                    world.WorldName,
                    MarketShoppingConstants.VendorWorldName,
                    StringComparison.OrdinalIgnoreCase))
                .Select(world => world.Listings
                    .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
                    .Where(listing => !requiresHq || listing.IsHq)
                    .Sum(listing => (long)listing.Quantity))
                .Where(quantity => quantity > 0)
                .OrderByDescending(quantity => quantity)
                .ToArray();
            var available = stockByWorld.Sum();
            if (available < required)
            {
                blockers.Add(
                    $"{plan.Name}: only {available:N0} of {required:N0} {quality}available in current listings");
                continue;
            }

            var worldsNeeded = 0;
            var covered = 0L;
            foreach (var quantity in stockByWorld)
            {
                worldsNeeded++;
                covered += quantity;
                if (covered >= required)
                {
                    break;
                }
            }

            if (worldsNeeded > policy.MaximumWorldStops)
            {
                blockers.Add(
                    $"{plan.Name}: {quality}stock for {required:N0} is spread across at least " +
                    $"{worldsNeeded:N0} worlds");
            }
            else if (worldsNeeded > 1 && !policy.AllowSplitPurchases)
            {
                blockers.Add(
                    $"{plan.Name}: no single world has {required:N0} {quality}in current listings " +
                    "and split purchases are disabled");
            }
        }

        if (blockers.Count == 0)
        {
            return $"{summary} Each material is individually available, but no combined route fits " +
                   $"within {policy.MaximumWorldStops} worlds.";
        }

        const int maximumListed = 3;
        var listed = string.Join("; ", blockers.Take(maximumListed));
        var more = blockers.Count > maximumListed ? $"; +{blockers.Count - maximumListed} more" : string.Empty;
        return $"{summary} Blocked by {listed}{more}.";
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

            if (!TryReadSelectedPurchase(plan, out var cash, out var worlds, out var oldestEvidence, out var hasMissingTimestamp))
            {
                return Failure($"The selected route for {plan.Name} is incomplete.");
            }

            var lineWarnings = BuildEvidenceAgeWarnings(plan, oldestEvidence, hasMissingTimestamp, quotedAtUtc, policy);
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
                lineWarnings));
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
            Lines = lines
        };
        return new TradeMaterialQuoteResult(quote, payrollLines, null);
    }

    private static bool TryReadSelectedPurchase(
        DetailedShoppingPlan plan,
        out decimal cash,
        out IReadOnlyList<string> worlds,
        out DateTime? oldestEvidence,
        out bool hasMissingTimestamp)
    {
        cash = 0;
        worlds = [];
        oldestEvidence = null;
        hasMissingTimestamp = false;
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
        oldestEvidence = timestamps.Length > 0 ? timestamps.Min() : null;
        hasMissingTimestamp = timestamps.Length != selectedWorlds.Count;
        return cash >= 0;
    }

    private static IReadOnlyList<string> BuildEvidenceAgeWarnings(
        DetailedShoppingPlan plan,
        DateTime? oldestEvidence,
        bool hasMissingTimestamp,
        DateTime quotedAtUtc,
        TradeMaterialPricingPolicy policy)
    {
        if (string.Equals(
                plan.RecommendedWorld?.WorldName,
                MarketShoppingConstants.VendorWorldName,
                StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var warnings = new List<string>();
        if (hasMissingTimestamp || oldestEvidence is null)
        {
            warnings.Add($"{plan.Name}: market listing upload time is unavailable for one or more selected worlds; verify prices before purchasing.");
        }

        if (oldestEvidence is { } uploadedAtUtc)
        {
            var age = quotedAtUtc - uploadedAtUtc;
            if (age > TimeSpan.FromMinutes(policy.MaximumEvidenceAgeMinutes))
            {
                warnings.Add(
                    $"{plan.Name}: selected market listings are {FormatEvidenceAge(age)} old " +
                    $"(older than the {policy.MaximumEvidenceAgeMinutes:N0}-minute warning threshold).");
            }
        }

        return warnings;
    }

    private static string FormatEvidenceAge(TimeSpan age)
    {
        if (age < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)Math.Round(age.TotalMinutes)):N0}m";
        }

        if (age < TimeSpan.FromDays(1))
        {
            var hours = (int)age.TotalHours;
            var minutes = age.Minutes;
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }

        var days = (int)age.TotalDays;
        var remHours = age.Hours;
        return remHours > 0 ? $"{days}d {remHours}h" : $"{days}d";
    }

    /// <summary>
    /// Preserves all world options (including older Universalis uploads) and rebuilds coverage
    /// when listing-level data is present so cloned demand quantities have accurate tiers.
    /// </summary>
    private static DetailedShoppingPlan PreparePlanForOptimization(DetailedShoppingPlan plan)
    {
        if (string.Equals(
                plan.RecommendedWorld?.WorldName,
                MarketShoppingConstants.VendorWorldName,
                StringComparison.OrdinalIgnoreCase))
        {
            return plan;
        }

        var prepared = new DetailedShoppingPlan
        {
            ItemId = plan.ItemId,
            Name = plan.Name,
            IconId = plan.IconId,
            QuantityNeeded = plan.QuantityNeeded,
            HqQuantityNeeded = plan.HqQuantityNeeded,
            DCAveragePrice = plan.DCAveragePrice,
            WorldOptions = plan.WorldOptions.ToList(),
            RecommendedWorld = plan.RecommendedWorld,
            RecommendedSplit = plan.RecommendedSplit?.ToList(),
            CoverageSet = plan.CoverageSet,
            Error = plan.Error,
            MarketDataWarning = plan.MarketDataWarning,
            HQAveragePrice = plan.HQAveragePrice,
            Vendors = plan.Vendors.ToList()
        };
        var hasListings = prepared.WorldOptions.Any(world =>
            !string.Equals(world.WorldName, MarketShoppingConstants.VendorWorldName, StringComparison.OrdinalIgnoreCase) &&
            world.Listings.Any(listing => listing.Quantity > 0 && listing.PricePerUnit > 0));
        if (prepared.QuantityNeeded > 0 && hasListings)
        {
            prepared.CoverageSet = MarketCoverageBuilder.Build(prepared);
        }

        return prepared;
    }

    private static DetailedShoppingPlan BuildVendorPlan(
        MaterialAggregate demand)
    {
        var cash = (long)Math.Round(
            demand.TotalQuantity * demand.UnitPrice,
            0,
            MidpointRounding.AwayFromZero);
        var vendor = new WorldShoppingSummary
        {
            WorldName = MarketShoppingConstants.VendorWorldName,
            WorldId = 0,
            TotalCost = cash,
            AveragePricePerUnit = demand.UnitPrice,
            ListingsUsed = 1,
            TotalQuantityPurchased = demand.TotalQuantity,
            HasSufficientStock = true,
            Classification = WorldClassification.Standard,
            Listings =
            [
                new ShoppingListingEntry
                {
                    Quantity = demand.TotalQuantity,
                    PricePerUnit = (long)demand.UnitPrice,
                    RetainerName = MarketShoppingConstants.VendorWorldName,
                    IsUnderAverage = true,
                    NeededFromStack = demand.TotalQuantity
                }
            ]
        };
        return new DetailedShoppingPlan
        {
            ItemId = demand.ItemId,
            Name = demand.Name,
            IconId = demand.IconId,
            QuantityNeeded = demand.RequiresHq ? 0 : demand.TotalQuantity,
            HqQuantityNeeded = demand.RequiresHq ? demand.TotalQuantity : 0,
            DCAveragePrice = demand.UnitPrice,
            WorldOptions = [],
            RecommendedWorld = vendor,
            RecommendedSplit = null
        };
    }

    private static DetailedShoppingPlan CloneForDemand(
        DetailedShoppingPlan source,
        MaterialAggregate demand) =>
        new()
        {
            ItemId = source.ItemId,
            Name = source.Name,
            IconId = source.IconId,
            QuantityNeeded = demand.TotalQuantity,
            HqQuantityNeeded = demand.RequiresHq ? demand.TotalQuantity : 0,
            DCAveragePrice = source.DCAveragePrice,
            WorldOptions = source.WorldOptions.ToList(),
            RecommendedWorld = source.RecommendedWorld,
            CoverageSet = source.CoverageSet,
            Error = source.Error,
            MarketDataWarning = source.MarketDataWarning,
            HQAveragePrice = source.HQAveragePrice,
            Vendors = source.Vendors.ToList(),
            RecommendedSplit = source.RecommendedSplit?.ToList()
        };

    private static DetailedShoppingPlan BuildUnsupportedPlan(
        MaterialAggregate demand,
        AcquisitionSource source) =>
        new()
        {
            ItemId = demand.ItemId,
            Name = demand.Name,
            IconId = demand.IconId,
            QuantityNeeded = demand.RequiresHq ? 0 : demand.TotalQuantity,
            HqQuantityNeeded = demand.RequiresHq ? demand.TotalQuantity : 0,
            Error = $"Selected acquisition source {source} cannot be quoted as purchase cash."
        };

    private static TradeMaterialQuoteResult Failure(string reason) =>
        new(null, [], reason);
}
