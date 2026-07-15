using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Optimizes make/buy/vendor decisions and the market route as one problem.
/// The acquisition frontier is independent of travel tolerance; tolerance is
/// applied only after the globally cheapest attainable plan is known.
/// </summary>
public sealed class JointAcquisitionRouteOptimizationService
{
    private const int MaxAcquisitionFrontierPlans = 4_096;
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

        var evidenceByItem = evidencePlans
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.First());
        var lowerBoundUnitCosts = evidenceByItem.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.WorldOptions
                .SelectMany(world => world.Listings)
                .Where(listing => listing.PricePerUnit > 0)
                .Select(listing => listing.PricePerUnit)
                .DefaultIfEmpty(long.MaxValue)
                .Min());
        var variants = BuildAcquisitionFrontier(plan, lowerBoundUnitCosts, ct);
        progress?.Report($"Evaluating {variants.Count:N0} non-dominated acquisition plans...");
        var cheapestConfig = CopyConfig(config, travelTolerance: 11);
        var evaluated = new List<EvaluatedVariant>(variants.Count);

        for (var index = 0; index < variants.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var variant = variants[index];
            var marketPlans = BuildDemandPlans(variant, evidenceByItem);
            if (marketPlans == null)
            {
                continue;
            }

            var route = await OptimizeRouteAsync(
                marketPlans,
                cheapestConfig,
                includeSplitPurchases,
                executionOptions,
                progress: null,
                ct);
            if (variant.MarketDemand.Count > 0 && route.Decision == null)
            {
                continue;
            }

            var marketCost = route.Decision?.SelectedGilCost ?? 0;
            evaluated.Add(new EvaluatedVariant(variant, marketPlans, route, Add(variant.FixedGilCost, marketCost)));

            if ((index + 1) % 25 == 0)
            {
                progress?.Report($"Evaluating acquisition plans {index + 1:N0}/{variants.Count:N0}...");
            }
        }

        if (evaluated.Count == 0)
        {
            return JointAcquisitionRouteOptimizationResult.NoSolution(ClonePlan(plan));
        }

        var cheapestTotal = evaluated.Min(candidate => candidate.TotalGilCost);
        var maximumPremiumRate = MarketRouteScoring.GetMaximumPremiumRate(config.TravelTolerance);
        var maximumTotal = GetMaximumTotal(cheapestTotal, maximumPremiumRate);
        var finalists = new List<EvaluatedVariant>();

        foreach (var candidate in evaluated.Where(candidate => candidate.Variant.FixedGilCost <= maximumTotal))
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

            var marketBudget = maximumTotal == long.MaxValue
                ? (long?)null
                : Math.Max(0, maximumTotal - candidate.Variant.FixedGilCost);
            var route = await OptimizeRouteAsync(
                candidate.MarketPlans,
                config,
                includeSplitPurchases,
                executionOptions,
                progress: null,
                ct,
                marketBudget);
            if (candidate.Variant.MarketDemand.Count > 0 && route.Decision == null)
            {
                continue;
            }

            var total = Add(candidate.Variant.FixedGilCost, route.Decision?.SelectedGilCost ?? 0);
            if (total <= maximumTotal)
            {
                finalists.Add(candidate with { Route = route, TotalGilCost = total });
            }
        }

        var selected = OrderFinalists(finalists, config).First();
        var cheapest = evaluated
            .Where(candidate => candidate.TotalGilCost == cheapestTotal)
            .OrderBy(candidate => candidate.Route.Decision?.SelectedEvidencePenalty ?? 0)
            .ThenBy(candidate => candidate.Variant.DecisionKey, StringComparer.Ordinal)
            .First();
        var representativeRoutes = BuildJointRepresentativeRoutes(evaluated, config, cheapestTotal);
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
            representativeRoutes);

        return new JointAcquisitionRouteOptimizationResult(
            optimizedPlan,
            selectedPlans,
            routeDecision,
            activeItems,
            variants.Count,
            evaluated.Count);
    }

    private Task<ProcurementRouteOptimizationResult> OptimizeRouteAsync(
        IReadOnlyList<DetailedShoppingPlan> plans,
        MarketAnalysisConfig config,
        bool includeSplitPurchases,
        MarketAnalysisExecutionOptions? executionOptions,
        IProgress<string>? progress,
        CancellationToken ct,
        long? absoluteMaximumGilCost = null)
    {
        return _marketShoppingService.OptimizeProcurementRouteWithDecisionAsync(
            plans,
            config,
            includeSplitPurchases,
            executionOptions,
            progress,
            ct,
            absoluteMaximumGilCost);
    }

    private static IReadOnlyList<AcquisitionVariant> BuildAcquisitionFrontier(
        CraftingPlan plan,
        IReadOnlyDictionary<int, long> lowerBoundUnitCosts,
        CancellationToken ct)
    {
        var combined = new List<AcquisitionVariant> { AcquisitionVariant.Empty };
        foreach (var root in plan.RootItems)
        {
            ct.ThrowIfCancellationRequested();
            combined = Combine(combined, BuildNodeVariants(root, lowerBoundUnitCosts, ct), lowerBoundUnitCosts, ct);
        }

        var current = BuildCurrentPlanVariant(plan);
        combined.Add(current);
        var frontier = Prune(combined, lowerBoundUnitCosts);
        if (frontier.All(candidate => !string.Equals(
                candidate.EconomicKey,
                current.EconomicKey,
                StringComparison.Ordinal)))
        {
            frontier.Add(current);
        }

        return frontier;
    }

    private static List<AcquisitionVariant> BuildNodeVariants(
        PlanNode node,
        IReadOnlyDictionary<int, long> lowerBoundUnitCosts,
        CancellationToken ct)
    {
        var variants = new List<AcquisitionVariant>();
        foreach (var source in GetAllowedSources(node))
        {
            ct.ThrowIfCancellationRequested();
            switch (source)
            {
                case AcquisitionSource.Craft:
                    {
                        var crafted = new List<AcquisitionVariant> { AcquisitionVariant.Empty };
                        foreach (var child in node.Children)
                        {
                            crafted = Combine(
                                crafted,
                                BuildNodeVariants(child, lowerBoundUnitCosts, ct),
                                lowerBoundUnitCosts,
                                ct);
                        }

                        variants.AddRange(crafted.Select(value => value.WithDecision(node.NodeId, source)));
                        break;
                    }
                case AcquisitionSource.MarketBuyNq:
                case AcquisitionSource.MarketBuyHq:
                    variants.Add(AcquisitionVariant.Empty
                        .WithMarketDemand(node, source == AcquisitionSource.MarketBuyHq)
                        .WithDecision(node.NodeId, source));
                    break;
                case AcquisitionSource.VendorBuy:
                    variants.Add(AcquisitionVariant.Empty
                        .WithFixedCost(GetVendorCost(node))
                        .WithDecision(node.NodeId, source));
                    break;
                case AcquisitionSource.VendorSpecialCurrency:
                case AcquisitionSource.UnknownSource:
                    variants.Add(AcquisitionVariant.Empty.WithDecision(node.NodeId, source));
                    break;
            }
        }

        return Prune(variants, lowerBoundUnitCosts);
    }

    private static IReadOnlyList<AcquisitionSource> GetAllowedSources(PlanNode node)
    {
        if (node.SourceReason == AcquisitionSourceReason.UserSelected)
        {
            return [node.Source];
        }

        var sources = new List<AcquisitionSource>();
        if (node.CanCraft && node.Children.Count > 0)
        {
            sources.Add(AcquisitionSource.Craft);
        }

        if (node.CanBuyFromMarket)
        {
            if (!node.MustBeHq)
            {
                sources.Add(AcquisitionSource.MarketBuyNq);
            }

            if (node.MustBeHq || node.CanBeHq)
            {
                sources.Add(AcquisitionSource.MarketBuyHq);
            }
        }

        if (node.CanBuyFromVendor && !node.MustBeHq && GetVendorCost(node) > 0)
        {
            sources.Add(AcquisitionSource.VendorBuy);
        }

        if (sources.Count == 0)
        {
            sources.Add(node.Source is AcquisitionSource.UnknownSource or AcquisitionSource.VendorSpecialCurrency
                ? node.Source
                : AcquisitionSource.UnknownSource);
        }

        return sources;
    }

    private static long GetVendorCost(PlanNode node)
    {
        var unitPrice = node.SelectedVendor?.Price ?? node.VendorPrice;
        if (unitPrice <= 0 || node.Quantity <= 0)
        {
            return 0;
        }

        return ToLong(decimal.Ceiling(unitPrice * node.Quantity));
    }

    private static List<AcquisitionVariant> Combine(
        IReadOnlyList<AcquisitionVariant> left,
        IReadOnlyList<AcquisitionVariant> right,
        IReadOnlyDictionary<int, long> lowerBoundUnitCosts,
        CancellationToken ct)
    {
        var product = (long)left.Count * right.Count;
        var combined = new List<AcquisitionVariant>((int)Math.Min(product, MaxAcquisitionFrontierPlans * 2L));
        foreach (var leftValue in left)
        {
            foreach (var rightValue in right)
            {
                ct.ThrowIfCancellationRequested();
                combined.Add(leftValue.Combine(rightValue));
                if (combined.Count >= MaxAcquisitionFrontierPlans * 2)
                {
                    combined = Prune(combined, lowerBoundUnitCosts);
                }
            }
        }

        return Prune(combined, lowerBoundUnitCosts);
    }

    private static List<AcquisitionVariant> Prune(
        IEnumerable<AcquisitionVariant> source,
        IReadOnlyDictionary<int, long> lowerBoundUnitCosts)
    {
        var distinct = source
            .GroupBy(value => value.EconomicKey, StringComparer.Ordinal)
            .Select(group => group.OrderBy(value => value.DecisionKey, StringComparer.Ordinal).First())
            .ToList();

        if (distinct.Count > MaxAcquisitionFrontierPlans)
        {
            var cheapest = distinct
                .OrderBy(value => EstimateLowerBound(value, lowerBoundUnitCosts))
                .ThenBy(value => value.DecisionKey, StringComparer.Ordinal)
                .Take(3_072);
            var leastComplex = distinct
                .OrderBy(value => value.MarketDemand.Count)
                .ThenBy(value => EstimateLowerBound(value, lowerBoundUnitCosts))
                .ThenBy(value => value.DecisionKey, StringComparer.Ordinal)
                .Take(512);
            var smallestDemand = distinct
                .OrderBy(value => value.MarketDemand.Values.Sum(demand => (long)demand.Quantity))
                .ThenBy(value => EstimateLowerBound(value, lowerBoundUnitCosts))
                .ThenBy(value => value.DecisionKey, StringComparer.Ordinal)
                .Take(512);
            distinct = cheapest
                .Concat(leastComplex)
                .Concat(smallestDemand)
                .DistinctBy(value => value.EconomicKey, StringComparer.Ordinal)
                .Take(MaxAcquisitionFrontierPlans)
                .ToList();
        }

        if (distinct.Count > 1_024)
        {
            return distinct.OrderBy(value => value.DecisionKey, StringComparer.Ordinal).ToList();
        }

        return distinct
            .Where(candidate => !distinct.Any(other =>
                !ReferenceEquals(candidate, other) && Dominates(other, candidate)))
            .OrderBy(value => value.DecisionKey, StringComparer.Ordinal)
            .ToList();
    }

    private static long EstimateLowerBound(
        AcquisitionVariant variant,
        IReadOnlyDictionary<int, long> lowerBoundUnitCosts)
    {
        var total = variant.FixedGilCost;
        foreach (var (itemId, demand) in variant.MarketDemand)
        {
            var unitCost = lowerBoundUnitCosts.GetValueOrDefault(itemId, long.MaxValue);
            total = Add(total, SaturatingMultiply(demand.Quantity, unitCost));
        }

        return total;
    }

    private static AcquisitionVariant BuildCurrentPlanVariant(CraftingPlan plan)
    {
        var combined = AcquisitionVariant.Empty;
        foreach (var root in plan.RootItems)
        {
            combined = combined.Combine(BuildCurrentNodeVariant(root));
        }

        return combined;
    }

    private static AcquisitionVariant BuildCurrentNodeVariant(PlanNode node)
    {
        var variant = AcquisitionVariant.Empty.WithDecision(node.NodeId, node.Source);
        return node.Source switch
        {
            AcquisitionSource.Craft => node.Children.Aggregate(
                variant,
                (current, child) => current.Combine(BuildCurrentNodeVariant(child))),
            AcquisitionSource.MarketBuyNq => variant.WithMarketDemand(node, hq: false),
            AcquisitionSource.MarketBuyHq => variant.WithMarketDemand(node, hq: true),
            AcquisitionSource.VendorBuy => variant.WithFixedCost(GetVendorCost(node)),
            _ => variant
        };
    }

    private static bool Dominates(AcquisitionVariant left, AcquisitionVariant right)
    {
        if (left.FixedGilCost > right.FixedGilCost)
        {
            return false;
        }

        var allItems = left.MarketDemand.Keys.Concat(right.MarketDemand.Keys).Distinct();
        var strictlyBetter = left.FixedGilCost < right.FixedGilCost;
        foreach (var itemId in allItems)
        {
            var leftDemand = left.MarketDemand.GetValueOrDefault(itemId);
            var rightDemand = right.MarketDemand.GetValueOrDefault(itemId);
            if (leftDemand.Quantity > rightDemand.Quantity || leftDemand.HqQuantity > rightDemand.HqQuantity)
            {
                return false;
            }

            strictlyBetter |= leftDemand.Quantity < rightDemand.Quantity || leftDemand.HqQuantity < rightDemand.HqQuantity;
        }

        return strictlyBetter;
    }

    private static List<DetailedShoppingPlan>? BuildDemandPlans(
        AcquisitionVariant variant,
        IReadOnlyDictionary<int, DetailedShoppingPlan> evidenceByItem)
    {
        var plans = new List<DetailedShoppingPlan>();
        foreach (var (itemId, demand) in variant.MarketDemand.OrderBy(pair => pair.Key))
        {
            if (!evidenceByItem.TryGetValue(itemId, out var evidence))
            {
                return null;
            }

            var adjusted = AdjustEvidence(evidence, demand);
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
        var hqOnly = demand.HqQuantity > 0;
        var worlds = source.WorldOptions
            .Select(world => AdjustWorld(world, demand.Quantity, hqOnly))
            .Where(world => world.TotalQuantityPurchased > 0)
            .ToList();

        return new DetailedShoppingPlan
        {
            ItemId = source.ItemId,
            Name = source.Name,
            IconId = source.IconId,
            QuantityNeeded = demand.Quantity,
            DCAveragePrice = source.DCAveragePrice,
            HQAveragePrice = source.HQAveragePrice,
            WorldOptions = worlds,
            Error = source.Error,
            MarketDataWarning = source.MarketDataWarning,
            Vendors = source.Vendors.ToList()
        };
    }

    private static WorldShoppingSummary AdjustWorld(WorldShoppingSummary source, int quantityNeeded, bool hqOnly)
    {
        var listings = source.Listings
            .Where(listing => !hqOnly || listing.IsHq)
            .Select(CloneListing)
            .OrderBy(listing => listing.PricePerUnit)
            .ThenBy(listing => listing.Quantity)
            .Select(listing =>
            {
                listing.IsAdditionalOption = false;
                return listing;
            })
            .ToList();
        var selected = SelectListings(listings, quantityNeeded);
        var totalAvailable = listings.Sum(listing => listing.Quantity);
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
            TotalQuantityPurchased = totalAvailable,
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
            HasSufficientStock = totalAvailable >= quantityNeeded,
            ShortfallQuantity = Math.Max(0, quantityNeeded - totalAvailable),
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
        int quantityNeeded)
    {
        var selected = new List<ShoppingListingEntry>();
        var remaining = quantityNeeded;
        foreach (var listing in listings)
        {
            if (remaining <= 0)
            {
                break;
            }

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
        IReadOnlyList<MarketRouteFrontierOption> representativeRoutes)
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
            FixedAcquisitionGilCost = fixedGilCost
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
        var frontier = choices
            .Where(candidate => !choices.Any(other =>
                !ReferenceEquals(candidate, other) &&
                other.GilCost <= candidate.GilCost &&
                other.WorldStops <= candidate.WorldStops &&
                other.DataCenterTransfers <= candidate.DataCenterTransfers &&
                (other.GilCost < candidate.GilCost ||
                 other.WorldStops < candidate.WorldStops ||
                 other.DataCenterTransfers < candidate.DataCenterTransfers)))
            .ToList();

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

        var maximum = decimal.Ceiling(cheapestTotal * (1m + premiumRate.Value));
        return ToLong(maximum);
    }

    private static long Add(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long SaturatingMultiply(int quantity, long price) =>
        quantity > 0 && price > long.MaxValue / quantity ? long.MaxValue : quantity * price;

    private static long ToLong(decimal value) => value >= long.MaxValue ? long.MaxValue : (long)value;

    private readonly record struct MarketDemand(int Quantity, int HqQuantity, string Name, int IconId)
    {
        public MarketDemand Add(PlanNode node, bool hq) => new(
            checked(Quantity + node.Quantity),
            checked(HqQuantity + (hq ? node.Quantity : 0)),
            string.IsNullOrWhiteSpace(Name) ? node.Name : Name,
            IconId == 0 ? node.IconId : IconId);
    }

    private sealed record AcquisitionVariant(
        IReadOnlyDictionary<int, MarketDemand> MarketDemand,
        long FixedGilCost,
        IReadOnlyDictionary<string, AcquisitionSource> Decisions)
    {
        public static AcquisitionVariant Empty { get; } = new(
            new Dictionary<int, MarketDemand>(),
            0,
            new Dictionary<string, AcquisitionSource>(StringComparer.Ordinal));

        public string EconomicKey => $"{FixedGilCost}|{string.Join(';', MarketDemand.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value.Quantity}:{pair.Value.HqQuantity}"))}";
        public string DecisionKey => string.Join(';', Decisions.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{(int)pair.Value}"));

        public AcquisitionVariant WithMarketDemand(PlanNode node, bool hq)
        {
            var demand = MarketDemand.ToDictionary(pair => pair.Key, pair => pair.Value);
            demand[node.ItemId] = demand.GetValueOrDefault(node.ItemId).Add(node, hq);
            return this with { MarketDemand = demand };
        }

        public AcquisitionVariant WithFixedCost(long cost) => this with { FixedGilCost = Add(FixedGilCost, cost) };

        public AcquisitionVariant WithDecision(string nodeId, AcquisitionSource source)
        {
            var decisions = Decisions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            decisions[nodeId] = source;
            return this with { Decisions = decisions };
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
}

public sealed record JointAcquisitionRouteOptimizationResult(
    CraftingPlan OptimizedPlan,
    List<DetailedShoppingPlan> ShoppingPlans,
    MarketRouteDecision? RouteDecision,
    IReadOnlyList<MaterialAggregate> ActiveProcurementItems,
    int FrontierPlanCount,
    int FeasiblePlanCount)
{
    public static JointAcquisitionRouteOptimizationResult NoSolution(CraftingPlan plan) =>
        new(plan, [], null, [], 0, 0);
}
