using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed record TradeMaterialQuoteResult(
    TradeMaterialQuote? Quote,
    IReadOnlyList<CommissionPayrollInputLine> MaterialLines,
    string? FailureReason,
    IReadOnlyList<string>? Warnings = null)
{
    public bool IsComplete => Quote != null && string.IsNullOrWhiteSpace(FailureReason);

    /// <summary>Non-blocking route notes, such as a route that needs more worlds than policy.</summary>
    public IReadOnlyList<string> RouteWarnings => Warnings ?? [];
}

public sealed class TradeMaterialQuoteService
{
    /// <summary>
    /// Quotes the executable route for the crafter's purchases only. On-hand stock is covered at
    /// zero cash, and company-provided materials keep their selected-source company value: they
    /// never join the route, so they cannot add world stops or block the crafter's quote.
    /// </summary>
    public async Task<TradeMaterialQuoteResult> PriceAsync(
        MarketShoppingService shopping,
        IReadOnlyList<DetailedShoppingPlan> plans,
        IReadOnlyList<MaterialAggregate> demand,
        IReadOnlyDictionary<(int ItemId, bool RequiresHq), AcquisitionSource> sources,
        TradeMaterialPricingPolicy policy,
        DateTime now,
        IReadOnlyList<CommissionPayrollInputLine> selectedSourceLines,
        IReadOnlySet<(int ItemId, bool RequiresHq)>? companyProvided = null,
        CancellationToken ct = default)
    {
        var stock = demand.Where(item => sources.GetValueOrDefault((item.ItemId, item.RequiresHq)) == AcquisitionSource.OnHand).ToArray();
        var provided = companyProvided is { Count: > 0 }
            ? demand.Except(stock).Where(item => companyProvided.Contains((item.ItemId, item.RequiresHq))).ToArray()
            : [];
        var purchases = demand.Except(stock).Except(provided).ToArray();
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
        var providedLines = provided.Select(item => BuildCompanyValueLine(item, selectedSourceLines)).ToArray();
        if (purchases.Length == 0 && (stock.Length > 0 || provided.Length > 0))
        {
            var normalized = TradeMaterialPricingPolicyNormalizer.Normalize(policy);
            return new(new TradeMaterialQuote
            {
                RouteSelectionKey = provided.Length > 0 ? "company-provided" : "on-hand",
                WorldStops = 0,
                RouteCashRequired = 0,
                MaterialReimbursement = 0,
                SafetyAllowance = 0,
                Lines = stockCoverage,
                AppliedPolicy = normalized,
                PolicyFingerprint = TradeMaterialPricingPolicyNormalizer.Fingerprint(normalized),
                QuotedAtUtc = now,
                ExpiresAtUtc = now.AddMinutes(normalized.QuoteLifetimeMinutes)
            }, [.. stockLines, .. providedLines], null);
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
        if (!result.IsComplete)
        {
            // The stop limit is advisory: when no capped route completes, search again without
            // the per-item and route caps so Build can return the best route with a warning.
            // Genuine shortfalls and unsupported sources still fail on this uncapped pass.
            var uncapped = await shopping.OptimizeProcurementRouteWithDecisionAsync(
                input, new MarketAnalysisConfig
                {
                    EnableSplitWorld = routePolicy.AllowSplitPurchases,
                    TravelTolerance = 11
                }, includeSplitPurchases: routePolicy.AllowSplitPurchases,
                executionOptions: MarketAnalysisExecutionOptions.Synchronous, ct: ct);
            result = Build(uncapped, purchases, policy, now);
        }

        return result.Quote is { } quote
            ? new(
                quote with { Lines = [.. quote.Lines, .. stockCoverage] },
                [.. result.MaterialLines, .. stockLines, .. providedLines],
                null,
                result.RouteWarnings)
            : result;
    }

    /// <summary>
    /// The company-value line for a provided material: its selected-source evidence, or the
    /// demand's own estimate when that evidence is missing.
    /// </summary>
    private static CommissionPayrollInputLine BuildCompanyValueLine(
        MaterialAggregate item,
        IReadOnlyList<CommissionPayrollInputLine> selectedSourceLines) =>
        selectedSourceLines.FirstOrDefault(line =>
            line.ItemId == item.ItemId &&
            line.RequiresHq == item.RequiresHq &&
            line.EvidenceSource != TradeOrderWorkflow.OnHandEvidenceSource)
        ?? new CommissionPayrollInputLine(
            item.ItemId,
            item.Name,
            item.TotalQuantity,
            Math.Max(0m, item.UnitPrice),
            item.RequiresHq,
            CommissionMaterialResponsibility.Provided,
            "Company value",
            "Company-provided material; not part of the crafter's route.",
            null,
            []);

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

        // Every complete route the optimizer exported: the slider ticks plus the bounded frontier
        // routes those 12 ticks can skip.
        var complete = decision.ToleranceSelections
            .Concat(decision.BoundedRouteSelections)
            .Where(IsCompleteSelection)
            .DistinctBy(candidate => candidate.SelectionKey, StringComparer.Ordinal)
            .ToArray();
        if (complete.Length == 0)
        {
            return Failure(DescribeNoExecutableRoute(policy, optimization.ShoppingPlans, demand));
        }

        // The stop limit is advisory. Routes within it win; when none exists, the best complete
        // route is quoted anyway and carries a warning naming the stops it actually needs.
        var bounded = complete
            .Where(candidate => candidate.WorldStops <= policy.MaximumWorldStops)
            .ToArray();
        var overLimit = bounded.Length == 0;
        var eligible = overLimit ? complete : bounded;

        // Premium is measured against the cheapest route overall. When that route needs more
        // stops than policy allows and no eligible route lands inside its premium window, anchor
        // the window to the cheapest eligible route instead of rejecting every eligible route.
        var premiumFactor = 1m + policy.MaximumConsolidationPremiumPercent / 100m;
        var maximumCost = decision.CheapestGilCost * premiumFactor;
        if (!eligible.Any(candidate => candidate.GilCost <= maximumCost))
        {
            maximumCost = eligible.Min(candidate => candidate.GilCost) * premiumFactor;
        }

        var selections = eligible
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
                return overLimit && result.Quote is { } quote
                    ? result with { Warnings = [DescribeOverStopLimit(policy, quote)] }
                    : result;
            }
            firstRejection ??= result.FailureReason;
        }

        return Failure(
            "No complete route within company policy could be reconciled to listing-level purchases." +
            (string.IsNullOrWhiteSpace(firstRejection) ? string.Empty : $" {firstRejection}"));
    }

    /// <summary>Leading text of the stop-limit advisory, so refreshed pricing can replace it.</summary>
    public const string OverStopLimitPrefix = "Route needs ";

    /// <summary>
    /// "Route needs 11 worlds (company policy: 8)." plus the material with the most stops when
    /// one material alone visits several worlds.
    /// </summary>
    private static string DescribeOverStopLimit(
        TradeMaterialPricingPolicy policy,
        TradeMaterialQuote quote)
    {
        var summary = $"{OverStopLimitPrefix}{quote.WorldStops:N0} worlds (company policy: {policy.MaximumWorldStops:N0})";
        var widest = quote.Lines
            .Where(line => !line.Worlds.Contains(MarketShoppingConstants.VendorWorldName, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(line => line.Worlds.Count)
            .ThenBy(line => line.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return widest is { Worlds.Count: > 1 }
            ? $"{summary}; {widest.Name} spans {widest.Worlds.Count:N0}."
            : $"{summary}.";
    }

    private static bool IsCompleteSelection(MarketRouteToleranceSelection selection) =>
        selection.ItemDecisions.Count == selection.ShoppingPlans.Count(plan =>
            !string.Equals(
                plan.RecommendedWorld?.WorldName,
                MarketShoppingConstants.VendorWorldName,
                StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Names the materials that block every executable route, using the plans the optimizer saw:
    /// unsupported sources, stock shortfalls, and single-world requirements when split purchases
    /// are disabled. The world-stop limit is advisory and never named as a blocker.
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

            // World count alone never blocks: over-limit routes are quoted with a warning.
            if (worldsNeeded > 1 && !policy.AllowSplitPurchases)
            {
                blockers.Add(
                    $"{plan.Name}: no single world has {required:N0} {quality}in current listings " +
                    "and split purchases are disabled");
            }
        }

        if (blockers.Count == 0)
        {
            return $"{summary} Each material is individually available, but no complete combined " +
                   "route could be built.";
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
