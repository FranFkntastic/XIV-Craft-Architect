using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Optimizes make/buy/vendor decisions and the market route as one problem.
/// The acquisition frontier is independent of travel tolerance; tolerance is
/// applied only after the globally cheapest attainable plan is known.
/// </summary>
public sealed class JointAcquisitionRouteOptimizationService
{
    private readonly MarketShoppingService _marketShoppingService;

    public JointAcquisitionRouteOptimizationService(MarketShoppingService marketShoppingService)
    {
        _marketShoppingService = marketShoppingService;
    }

    public async Task<JointAcquisitionRouteOptimizationResult> OptimizeAsync(
        CraftingPlan plan,
        IReadOnlyList<DetailedShoppingPlan> evidencePlans,
        MarketAnalysisConfig config,
        bool includeSplitPurchases,
        MarketAnalysisExecutionOptions? executionOptions = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(evidencePlans);
        ArgumentNullException.ThrowIfNull(config);
        var execution = executionOptions ?? MarketAnalysisExecutionOptions.Interactive;

        var evidenceByItem = evidencePlans
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.First());
        progress?.Report($"[stage] joint route optimization starting ({evidencePlans.Count} evidence plans)...");
        var lowerBoundUnitCosts = evidenceByItem.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.WorldOptions
                .SelectMany(world => world.Listings)
                .Where(listing => listing.PricePerUnit > 0)
                .Select(listing => listing.PricePerUnit)
                .DefaultIfEmpty(long.MaxValue)
                .Min());
        var frontierSearch = AcquisitionVariantFrontierBuilder.Build(plan, lowerBoundUnitCosts, ct, progress);
        var variants = frontierSearch.Variants;
        progress?.Report(
            $"[stage] acquisition frontier built ({variants.Count:N0} variants, " +
            $"{frontierSearch.CombinationEvaluations:N0} combinations), evaluating...");
        progress?.Report($"Evaluating {variants.Count:N0} candidate acquisition plans...");
        var cheapestConfig = CopyConfig(config, travelTolerance: 11);
        var evaluated = new List<EvaluatedVariant>(variants.Count);
        var routeSession = new ProcurementRouteOptimizationSession(execution, ct);
        var realizedDecisionKeys = new HashSet<string>(StringComparer.Ordinal);
        var anyRouteSearchWasTruncated = false;

        // Evaluate cheapest-lower-bound first so the incumbent converges early.
        // Output is order-independent (winners/frontier use deterministic tiebreaks),
        // so reordering evaluation cannot change the result.
        var orderedVariants = variants
            .Select(variant => new
            {
                Variant = variant,
                LowerBound = AcquisitionVariantFrontierBuilder.EstimateLowerBound(variant, lowerBoundUnitCosts)
            })
            .OrderBy(entry => entry.LowerBound)
            .ThenBy(entry => entry.Variant.DecisionKey, StringComparer.Ordinal)
            .ToList();

        for (var index = 0; index < orderedVariants.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = orderedVariants[index];
            var variant = AcquisitionVariantFrontierBuilder.BuildRealizedPlanVariant(
                plan,
                entry.Variant.Decisions,
                ct);
            if (variant is null || !realizedDecisionKeys.Add(variant.DecisionKey))
            {
                continue;
            }

            var marketPlans = BuildDemandPlans(variant, evidenceByItem, routeSession);
            if (marketPlans == null)
            {
                continue;
            }

            var route = await OptimizeRouteAsync(
                marketPlans,
                cheapestConfig,
                includeSplitPurchases,
                execution,
                routeSession,
                ct);
            anyRouteSearchWasTruncated |= route.Decision?.RouteSearchWasTruncated ?? false;
            if (variant.MarketDemand.Count > 0 && (!route.IsComplete || route.Decision == null))
            {
                continue;
            }

            var marketCost = route.Decision?.SelectedGilCost ?? 0;
            evaluated.Add(new EvaluatedVariant(
                variant,
                marketPlans,
                route,
                Add(variant.FixedGilCost, marketCost)));

            if ((index + 1) % 25 == 0)
            {
                progress?.Report($"Evaluating acquisition plans {index + 1:N0}/{orderedVariants.Count:N0}...");
            }
        }

        progress?.Report(
            $"[stage] variant evaluation complete ({evaluated.Count:N0} feasible of " +
            $"{orderedVariants.Count:N0}), applying travel tolerance...");
        if (evaluated.Count == 0)
        {
            return JointAcquisitionRouteOptimizationResult.NoSolution(
                ClonePlan(plan),
                variants.Count,
                frontierSearch.WasTruncated,
                frontierSearch.CombinationEvaluations);
        }

        var cheapestTotal = evaluated.Min(candidate => candidate.TotalGilCost);
        var maximumPremiumRate = MarketRouteScoring.GetMaximumPremiumRate(config.TravelTolerance);
        var maximumTotal = GetMaximumTotal(cheapestTotal, maximumPremiumRate);
        var finalists = new List<EvaluatedVariant>();
        var travelSearchWasTruncated = false;
        if (config.TravelTolerance != 11)
        {
            finalists.Add(OrderFinalists(
                    evaluated.Where(candidate => candidate.TotalGilCost <= maximumTotal),
                    config)
                .First());
        }

        var candidatesByTravelPotential = OrderFinalists(
                evaluated.Where(candidate =>
                    AcquisitionVariantFrontierBuilder.EstimateLowerBound(
                        candidate.Variant,
                        lowerBoundUnitCosts) <= maximumTotal),
                config)
            .ToList();
        var excludedByTravelCostFloor = evaluated.Count - candidatesByTravelPotential.Count;
        if (excludedByTravelCostFloor > 0)
        {
            progress?.Report(
                $"Excluded {excludedByTravelCostFloor:N0} acquisition plans whose admissible gil floor " +
                "exceeds the travel premium ceiling.");
        }

        var completedTravelRoutes = 0;
        progress?.Report(
            $"[stage] travel-aware optimization starting ({candidatesByTravelPotential.Count:N0} acquisition plans)...");

        foreach (var candidate in candidatesByTravelPotential)
        {
            ct.ThrowIfCancellationRequested();
            if (config.TravelTolerance == 11 || candidate.Variant.MarketDemand.Count == 0)
            {
                if (candidate.TotalGilCost <= maximumTotal)
                {
                    finalists.Add(candidate);
                }
                continue;
            }

            if (execution.MaxTravelRouteEvaluations.HasValue &&
                completedTravelRoutes >= execution.MaxTravelRouteEvaluations.Value)
            {
                travelSearchWasTruncated = true;
                break;
            }

            var marketBudget = maximumTotal == long.MaxValue
                ? (long?)null
                : Math.Max(0, maximumTotal - candidate.Variant.FixedGilCost);
            var route = await OptimizeRouteAsync(
                candidate.MarketPlans,
                config,
                includeSplitPurchases,
                execution,
                routeSession,
                ct,
                marketBudget);
            anyRouteSearchWasTruncated |= route.Decision?.RouteSearchWasTruncated ?? false;
            completedTravelRoutes++;
            progress?.Report(
                $"Evaluated {completedTravelRoutes:N0} bounded travel " +
                $"{(completedTravelRoutes == 1 ? "route" : "routes")} from " +
                $"{candidatesByTravelPotential.Count:N0} acquisition plans...");
            if (candidate.Variant.MarketDemand.Count > 0 && (!route.IsComplete || route.Decision == null))
            {
                continue;
            }

            var total = Add(candidate.Variant.FixedGilCost, route.Decision?.SelectedGilCost ?? 0);
            if (total <= maximumTotal)
            {
                finalists.Add(candidate with { Route = route, TotalGilCost = total });
            }
        }

        if (travelSearchWasTruncated)
        {
            progress?.Report(
                $"Travel-aware optimization reached its {completedTravelRoutes:N0}-route work limit; " +
                "using the best complete route found so far.");
        }

        var selected = OrderFinalists(finalists, config).First();
        var cheapest = evaluated
            .Where(candidate => candidate.TotalGilCost == cheapestTotal)
            .OrderBy(candidate => candidate.Route.Decision?.SelectedEvidencePenalty ?? 0)
            .ThenBy(candidate => candidate.Variant.DecisionKey, StringComparer.Ordinal)
            .First();
        // The cheapest pass deliberately trims each item's candidates to its minimum-cost
        // purchase. The tolerance pass expands those candidates again so it can discover
        // consolidated routes. Feed both passes into the displayed frontier; otherwise the
        // selected route can be absent from the clutch even though the recommendation is valid.
        var representativeRoutes = BuildJointRepresentativeRoutes(
            evaluated.Concat(finalists).ToList(),
            config,
            cheapestTotal);
        var optimizedPlan = ApplyVariant(plan, selected.Variant);
        var activeItems = AcquisitionPlanningService.GetActiveProcurementItems(optimizedPlan);
        var selectedPlans = selected.Route.ShoppingPlans.ToList();
        foreach (var item in activeItems.Where(item => selectedPlans.All(plan => plan.ItemId != item.ItemId)))
        {
            if (evidenceByItem.TryGetValue(item.ItemId, out var evidence))
            {
                selectedPlans.Add(AdjustEvidence(
                    evidence,
                    new MarketDemand(item.TotalQuantity, item.RequiresHq ? item.TotalQuantity : 0, item.Name, item.IconId)));
            }
            else
            {
                selectedPlans.Add(new DetailedShoppingPlan
                {
                    ItemId = item.ItemId,
                    Name = item.Name,
                    IconId = item.IconId,
                    QuantityNeeded = item.TotalQuantity,
                    Error = "No market evidence is available for this acquisition."
                });
            }
        }
        _marketShoppingService.ApplyVendorPurchaseOverrides(optimizedPlan, selectedPlans);

        var routeDecision = CreateJointDecision(
            selected.Route.Decision,
            config,
            cheapestTotal,
            selected.TotalGilCost,
            selected.Variant.FixedGilCost,
            cheapest.Route.Decision,
            representativeRoutes,
            frontierSearch.WasTruncated,
            anyRouteSearchWasTruncated,
            travelSearchWasTruncated,
            completedTravelRoutes,
            frontierSearch.CombinationEvaluations);

        return new JointAcquisitionRouteOptimizationResult(
            optimizedPlan,
            selectedPlans,
            routeDecision,
            activeItems,
            variants.Count,
            evaluated.Count,
            frontierSearch.WasTruncated || anyRouteSearchWasTruncated || travelSearchWasTruncated,
            frontierSearch.CombinationEvaluations);
    }

    private Task<ProcurementRouteOptimizationResult> OptimizeRouteAsync(
        IReadOnlyList<DetailedShoppingPlan> plans,
        MarketAnalysisConfig config,
        bool includeSplitPurchases,
        MarketAnalysisExecutionOptions? executionOptions,
        ProcurementRouteOptimizationSession session,
        CancellationToken ct,
        long? absoluteMaximumGilCost = null)
    {
        return _marketShoppingService.OptimizeProcurementRouteInSessionAsync(
            plans,
            config,
            includeSplitPurchases,
            executionOptions ?? MarketAnalysisExecutionOptions.Interactive,
            session,
            ct,
            absoluteMaximumGilCost);
    }

    private static List<DetailedShoppingPlan>? BuildDemandPlans(
        AcquisitionVariant variant,
        IReadOnlyDictionary<int, DetailedShoppingPlan> evidenceByItem,
        ProcurementRouteOptimizationSession session)
    {
        var plans = new List<DetailedShoppingPlan>();
        foreach (var (itemId, demand) in variant.MarketDemand.OrderBy(pair => pair.Key))
        {
            if (!evidenceByItem.TryGetValue(itemId, out var evidence))
            {
                return null;
            }

            var adjusted = session.GetOrAddAdjustedPlan(
                evidence,
                demand.Quantity,
                demand.HqQuantity,
                () => AdjustEvidence(evidence, demand));
            if (adjusted.WorldOptions.Count == 0)
            {
                return null;
            }

            plans.Add(adjusted);
        }

        return plans;
    }

    private static DetailedShoppingPlan AdjustEvidence(DetailedShoppingPlan source, MarketDemand demand)
    {
        var worlds = source.WorldOptions
            .Select(world => AdjustWorld(world, demand.Quantity, demand.HqQuantity))
            .Where(world => world.TotalQuantityPurchased > 0)
            .ToList();

        return new DetailedShoppingPlan
        {
            ItemId = source.ItemId,
            Name = source.Name,
            IconId = source.IconId,
            QuantityNeeded = demand.Quantity,
            HqQuantityNeeded = demand.HqQuantity,
            DCAveragePrice = source.DCAveragePrice,
            HQAveragePrice = source.HQAveragePrice,
            WorldOptions = worlds,
            Error = source.Error,
            MarketDataWarning = source.MarketDataWarning,
            Vendors = source.Vendors.ToList()
        };
    }

    private static WorldShoppingSummary AdjustWorld(
        WorldShoppingSummary source,
        int quantityNeeded,
        int hqQuantityNeeded)
    {
        var listings = source.Listings
            .Select(CloneListing)
            .OrderBy(listing => listing.PricePerUnit)
            .ThenBy(listing => listing.Quantity)
            .Select(listing =>
            {
                listing.IsAdditionalOption = false;
                return listing;
            })
            .ToList();
        var selected = SelectListings(listings, quantityNeeded, hqQuantityNeeded);
        var totalAvailable = listings.Sum(listing => listing.Quantity);
        var hqAvailable = listings.Where(listing => listing.IsHq).Sum(listing => listing.Quantity);
        var qualityShortfall = Math.Max(0, hqQuantityNeeded - hqAvailable);
        var usableQuantity = Math.Max(0, totalAvailable - qualityShortfall);
        var totalCost = selected.Sum(listing => SaturatingMultiply(listing.Quantity, listing.PricePerUnit));
        var purchased = selected.Sum(listing => listing.Quantity);

        return new WorldShoppingSummary
        {
            DataCenter = source.DataCenter,
            WorldName = source.WorldName,
            WorldId = source.WorldId,
            TotalCost = totalCost,
            AveragePricePerUnit = purchased > 0 ? totalCost / (decimal)purchased : 0,
            ListingsUsed = selected.Count,
            Listings = listings,
            ExcludedListings = source.ExcludedListings.Select(CloneListing).ToList(),
            IsFullyUnderAverage = source.IsFullyUnderAverage,
            TotalQuantityPurchased = usableQuantity,
            ExcessQuantity = Math.Max(0, purchased - quantityNeeded),
            ModePricePerUnit = source.ModePricePerUnit,
            ValueScore = source.ValueScore,
            MarketDataQualityScore = source.MarketDataQualityScore,
            MarketDataQualityBucket = source.MarketDataQualityBucket,
            MarketDataAgeSource = source.MarketDataAgeSource,
            MarketDataAge = source.MarketDataAge,
            MarketUploadedAtUtc = source.MarketUploadedAtUtc,
            LensRank = source.LensRank,
            LensScoreBucket = source.LensScoreBucket,
            ProcurementPriorityScore = source.ProcurementPriorityScore,
            VendorName = source.VendorName,
            HasSufficientStock = usableQuantity >= quantityNeeded,
            ShortfallQuantity = Math.Max(0, quantityNeeded - usableQuantity),
            BestSingleListing = listings.FirstOrDefault(),
            Classification = source.Classification,
            IsHomeWorld = source.IsHomeWorld,
            IsBlacklisted = source.IsBlacklisted,
            IsTravelProhibited = source.IsTravelProhibited,
            CongestedWarning = source.CongestedWarning
        };
    }

    private static List<ShoppingListingEntry> SelectListings(
        IReadOnlyList<ShoppingListingEntry> listings,
        int quantityNeeded,
        int hqQuantityNeeded)
    {
        var selected = new List<ShoppingListingEntry>();
        var selectedSources = new HashSet<ShoppingListingEntry>();
        var purchased = 0;
        var hqPurchased = 0;

        foreach (var listing in listings.Where(listing => listing.IsHq))
        {
            if (hqPurchased >= hqQuantityNeeded)
            {
                break;
            }

            selectedSources.Add(listing);
            purchased += listing.Quantity;
            hqPurchased += listing.Quantity;
        }

        foreach (var listing in listings)
        {
            if (purchased >= quantityNeeded)
            {
                break;
            }

            if (selectedSources.Add(listing))
            {
                purchased += listing.Quantity;
            }
        }

        var remaining = quantityNeeded;
        foreach (var listing in selectedSources.OrderBy(listing => listing.PricePerUnit))
        {
            var clone = CloneListing(listing);
            clone.IsAdditionalOption = false;
            clone.NeededFromStack = Math.Min(remaining, clone.Quantity);
            clone.ExcessQuantity = clone.Quantity - clone.NeededFromStack;
            selected.Add(clone);
            remaining -= clone.NeededFromStack;
        }

        return selected;
    }

    private static ShoppingListingEntry CloneListing(ShoppingListingEntry source) => new()
    {
        Quantity = source.Quantity,
        PricePerUnit = source.PricePerUnit,
        RetainerName = source.RetainerName,
        IsUnderAverage = source.IsUnderAverage,
        IsHq = source.IsHq,
        NeededFromStack = source.NeededFromStack,
        ExcessQuantity = source.ExcessQuantity,
        IsAdditionalOption = source.IsAdditionalOption
    };

    private static CraftingPlan ApplyVariant(CraftingPlan source, AcquisitionVariant variant)
    {
        var clone = ClonePlan(source);
        foreach (var root in clone.RootItems)
        {
            ApplyDecisions(root, variant.Decisions);
        }

        return clone;
    }

    private static CraftingPlan ClonePlan(CraftingPlan source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        CreatedAt = source.CreatedAt,
        ModifiedAt = source.ModifiedAt,
        DataCenter = source.DataCenter,
        World = source.World,
        RootItems = source.RootItems.Select(root => root.Clone()).ToList(),
        SavedMarketPlans = source.SavedMarketPlans.ToList(),
        PriceVersion = source.PriceVersion
    };

    private static void ApplyDecisions(PlanNode node, IReadOnlyDictionary<string, AcquisitionSource> decisions)
    {
        if (decisions.TryGetValue(node.NodeId, out var source))
        {
            node.Source = source;
            if (node.SourceReason != AcquisitionSourceReason.UserSelected)
            {
                node.SourceReason = AcquisitionSourceReason.SystemDefault;
            }
        }

        foreach (var child in node.Children)
        {
            ApplyDecisions(child, decisions);
        }
    }

    private static IEnumerable<EvaluatedVariant> OrderFinalists(
        IEnumerable<EvaluatedVariant> candidates,
        MarketAnalysisConfig config)
    {
        var ordered = config.TravelPriority == MarketTravelPriority.WorldVisitsFirst
            ? candidates.OrderBy(candidate => candidate.Route.Decision?.SelectedWorldStops ?? 0)
                .ThenBy(candidate => candidate.Route.Decision?.SelectedDataCenterTransfers ?? 0)
            : candidates.OrderBy(candidate => candidate.Route.Decision?.SelectedDataCenterTransfers ?? 0)
                .ThenBy(candidate => candidate.Route.Decision?.SelectedWorldStops ?? 0);

        return ordered
            .ThenBy(candidate => candidate.Route.Decision?.SelectedEvidencePenalty ?? 0)
            .ThenBy(candidate => candidate.TotalGilCost)
            .ThenBy(candidate => candidate.Variant.DecisionKey, StringComparer.Ordinal);
    }

    private static MarketRouteDecision CreateJointDecision(
        MarketRouteDecision? route,
        MarketAnalysisConfig config,
        long cheapestTotal,
        long selectedTotal,
        long fixedGilCost,
        MarketRouteDecision? cheapestRoute,
        IReadOnlyList<MarketRouteFrontierOption> representativeRoutes,
        bool acquisitionSearchWasTruncated,
        bool routeSearchWasTruncated,
        bool travelSearchWasTruncated,
        int travelRoutesEvaluated,
        long acquisitionCombinationEvaluations)
    {
        return new MarketRouteDecision(
            config.TravelTolerance,
            MarketRouteScoring.GetMaximumPremiumRate(config.TravelTolerance),
            cheapestTotal,
            selectedTotal,
            route?.SelectedEvidencePenalty ?? 0,
            cheapestRoute?.SelectedWorldStops ?? 0,
            route?.SelectedWorldStops ?? 0,
            cheapestRoute?.SelectedDataCenterTransfers ?? 0,
            route?.SelectedDataCenterTransfers ?? 0,
            config.StartFromHomeDataCenter && !string.IsNullOrWhiteSpace(config.HomeDataCenter),
            config.StartFromHomeDataCenter ? config.HomeDataCenter : null,
            config.TravelPriority,
            representativeRoutes,
            route?.ItemDecisions)
        {
            FixedAcquisitionGilCost = fixedGilCost,
            AcquisitionSearchWasTruncated = acquisitionSearchWasTruncated,
            RouteSearchWasTruncated = routeSearchWasTruncated,
            TravelSearchWasTruncated = travelSearchWasTruncated,
            TravelRoutesEvaluated = travelRoutesEvaluated,
            AcquisitionCombinationEvaluations = acquisitionCombinationEvaluations
        };
    }

    private static IReadOnlyList<MarketRouteFrontierOption> BuildJointRepresentativeRoutes(
        IReadOnlyList<EvaluatedVariant> evaluated,
        MarketAnalysisConfig config,
        long cheapestTotal)
    {
        var choices = evaluated
            .SelectMany(candidate =>
            {
                var routes = candidate.Route.Decision?.RepresentativeRoutes;
                if (routes == null || routes.Count == 0)
                {
                    return
                    [
                        new JointRouteChoice(
                            candidate.TotalGilCost,
                            candidate.Route.Decision?.SelectedWorldStops ?? 0,
                            candidate.Route.Decision?.SelectedDataCenterTransfers ?? 0,
                            candidate.Variant.DecisionKey)
                    ];
                }

                return routes.Select(route => new JointRouteChoice(
                    Add(candidate.Variant.FixedGilCost, route.GilCost),
                    route.WorldStops,
                    route.DataCenterTransfers,
                    $"{candidate.Variant.DecisionKey}|{route.GilCost}:{route.WorldStops}:{route.DataCenterTransfers}"));
            })
            .DistinctBy(choice => (choice.GilCost, choice.WorldStops, choice.DataCenterTransfers))
            .ToList();
        var frontier = new List<JointRouteChoice>();
        foreach (var candidate in choices
                     .OrderBy(choice => choice.GilCost)
                     .ThenBy(choice => choice.WorldStops)
                     .ThenBy(choice => choice.DataCenterTransfers)
                     .ThenBy(choice => choice.Key, StringComparer.Ordinal))
        {
            if (frontier.Any(other => Dominates(other, candidate)))
            {
                continue;
            }
            frontier.RemoveAll(other => Dominates(candidate, other));
            frontier.Add(candidate);
        }

        var representatives = new List<MarketRouteFrontierOption>();
        JointRouteChoice? previous = null;
        for (var tolerance = 0; tolerance <= 11; tolerance++)
        {
            var premium = MarketRouteScoring.GetMaximumPremiumRate(tolerance);
            var eligible = frontier.Where(choice => premium == null ||
                choice.GilCost <= cheapestTotal * (1m + premium.Value));
            var selected = config.TravelPriority == MarketTravelPriority.WorldVisitsFirst
                ? eligible.OrderBy(choice => choice.WorldStops)
                    .ThenBy(choice => choice.DataCenterTransfers)
                    .ThenBy(choice => choice.GilCost)
                    .ThenBy(choice => choice.Key, StringComparer.Ordinal)
                    .First()
                : eligible.OrderBy(choice => choice.DataCenterTransfers)
                    .ThenBy(choice => choice.WorldStops)
                    .ThenBy(choice => choice.GilCost)
                    .ThenBy(choice => choice.Key, StringComparer.Ordinal)
                    .First();

            if (previous != null &&
                previous.GilCost == selected.GilCost &&
                previous.WorldStops == selected.WorldStops &&
                previous.DataCenterTransfers == selected.DataCenterTransfers)
            {
                representatives[^1] = representatives[^1] with { MaximumTolerance = tolerance };
            }
            else
            {
                representatives.Add(new MarketRouteFrontierOption(
                    tolerance,
                    tolerance,
                    selected.GilCost,
                    selected.WorldStops,
                    selected.DataCenterTransfers));
            }

            previous = selected;
        }

        return representatives;
    }

    private static bool Dominates(JointRouteChoice left, JointRouteChoice right) =>
        left.GilCost <= right.GilCost &&
        left.WorldStops <= right.WorldStops &&
        left.DataCenterTransfers <= right.DataCenterTransfers &&
        (left.GilCost < right.GilCost ||
         left.WorldStops < right.WorldStops ||
         left.DataCenterTransfers < right.DataCenterTransfers);

    private static MarketAnalysisConfig CopyConfig(MarketAnalysisConfig source, int travelTolerance) => new()
    {
        MaxWorldsPerItem = source.MaxWorldsPerItem,
        TravelTolerance = travelTolerance,
        EnableSplitWorld = source.EnableSplitWorld,
        MaxPriceMultiplier = source.MaxPriceMultiplier,
        StartFromHomeDataCenter = source.StartFromHomeDataCenter,
        HomeDataCenter = source.HomeDataCenter,
        TravelPriority = source.TravelPriority
    };

    private static long GetMaximumTotal(long cheapestTotal, decimal? premiumRate)
    {
        if (premiumRate == null)
        {
            return long.MaxValue;
        }

        var maximum = decimal.Floor(cheapestTotal * (1m + premiumRate.Value));
        return ToLong(maximum);
    }

    private static long Add(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long SaturatingMultiply(int quantity, long price) =>
        quantity > 0 && price > long.MaxValue / quantity ? long.MaxValue : quantity * price;

    private static long ToLong(decimal value) => value >= long.MaxValue ? long.MaxValue : (long)value;

    internal readonly record struct MarketDemand(int Quantity, int HqQuantity, string Name, int IconId)
    {
        public MarketDemand Add(PlanNode node, bool hq) => new(
            checked(Quantity + node.Quantity),
            checked(HqQuantity + (hq ? node.Quantity : 0)),
            string.IsNullOrWhiteSpace(Name) ? node.Name : Name,
            IconId == 0 ? node.IconId : IconId);
    }

    internal sealed class AcquisitionVariant(
        IReadOnlyDictionary<int, MarketDemand> marketDemand,
        long fixedGilCost,
        IReadOnlyDictionary<string, AcquisitionSource> decisions)
    {
        public static AcquisitionVariant Empty { get; } = new(
            new Dictionary<int, MarketDemand>(),
            0,
            new Dictionary<string, AcquisitionSource>(StringComparer.Ordinal));

        public IReadOnlyDictionary<int, MarketDemand> MarketDemand { get; } = marketDemand;

        public long FixedGilCost { get; } = fixedGilCost;

        public IReadOnlyDictionary<string, AcquisitionSource> Decisions { get; } = decisions;

        public string EconomicKey { get; } =
            $"{fixedGilCost}|{string.Join(';', marketDemand.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value.Quantity}:{pair.Value.HqQuantity}"))}";

        public string DecisionKey { get; } =
            string.Join(';', decisions.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{(int)pair.Value}"));

        public AcquisitionVariant WithMarketDemand(PlanNode node, bool hq)
        {
            var demand = MarketDemand.ToDictionary(pair => pair.Key, pair => pair.Value);
            demand[node.ItemId] = demand.GetValueOrDefault(node.ItemId).Add(node, hq);
            return new AcquisitionVariant(demand, FixedGilCost, Decisions);
        }

        public AcquisitionVariant WithFixedCost(long cost) =>
            new(MarketDemand, Add(FixedGilCost, cost), Decisions);

        public AcquisitionVariant WithDecision(string nodeId, AcquisitionSource source)
        {
            var decisions = Decisions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            decisions[nodeId] = source;
            return new AcquisitionVariant(MarketDemand, FixedGilCost, decisions);
        }

        public AcquisitionVariant Combine(AcquisitionVariant other)
        {
            var demand = MarketDemand.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var (itemId, value) in other.MarketDemand)
            {
                var current = demand.GetValueOrDefault(itemId);
                demand[itemId] = new MarketDemand(
                    checked(current.Quantity + value.Quantity),
                    checked(current.HqQuantity + value.HqQuantity),
                    string.IsNullOrWhiteSpace(current.Name) ? value.Name : current.Name,
                    current.IconId == 0 ? value.IconId : current.IconId);
            }

            var decisions = Decisions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var (nodeId, source) in other.Decisions)
            {
                decisions[nodeId] = source;
            }

            return new AcquisitionVariant(demand, Add(FixedGilCost, other.FixedGilCost), decisions);
        }
    }

    private sealed record EvaluatedVariant(
        AcquisitionVariant Variant,
        IReadOnlyList<DetailedShoppingPlan> MarketPlans,
        ProcurementRouteOptimizationResult Route,
        long TotalGilCost);

    private sealed record JointRouteChoice(long GilCost, int WorldStops, int DataCenterTransfers, string Key);

    internal sealed record AcquisitionFrontierBuildResult(
        IReadOnlyList<AcquisitionVariant> Variants,
        bool WasTruncated,
        long CombinationEvaluations);
}

public sealed record JointAcquisitionRouteOptimizationResult(
    CraftingPlan OptimizedPlan,
    List<DetailedShoppingPlan> ShoppingPlans,
    MarketRouteDecision? RouteDecision,
    IReadOnlyList<MaterialAggregate> ActiveProcurementItems,
    int FrontierPlanCount,
    int FeasiblePlanCount,
    bool SearchWasTruncated,
    long AcquisitionCombinationEvaluations)
{
    public static JointAcquisitionRouteOptimizationResult NoSolution(
        CraftingPlan plan,
        int frontierPlanCount = 0,
        bool searchWasTruncated = false,
        long acquisitionCombinationEvaluations = 0) =>
        new(
            plan,
            [],
            null,
            [],
            frontierPlanCount,
            0,
            searchWasTruncated,
            acquisitionCombinationEvaluations);
}
