using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Builds the distinct material views used by market analysis and procurement.
/// </summary>
public static class AcquisitionPlanningService
{
    public static List<MaterialAggregate> GetMarketAnalysisCandidates(CraftingPlan? plan)
    {
        if (plan == null)
        {
            return new List<MaterialAggregate>();
        }

        var aggregates = new Dictionary<int, MaterialAggregate>();
        foreach (var root in plan.RootItems)
        {
            CollectMarketCandidate(root, aggregates);
        }

        return aggregates.Values.OrderBy(item => item.Name).ToList();
    }

    public static List<MaterialAggregate> GetActiveProcurementItems(CraftingPlan? plan)
    {
        return new RecipeDemandProjectionService()
            .Build(plan, snapshot: null)
            .ToActiveProcurementMaterialAggregates()
            .ToList();
    }

    public static List<DetailedShoppingPlan> FilterShoppingPlansForActiveProcurement(
        CraftingPlan? plan,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        if (plan == null)
        {
            return new List<DetailedShoppingPlan>();
        }

        return FilterShoppingPlansForActiveProcurement(GetActiveProcurementItems(plan), shoppingPlans);
    }

    public static List<DetailedShoppingPlan> FilterShoppingPlansForActiveProcurement(
        IReadOnlyList<MaterialAggregate> activeItems,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        var activeItemIds = activeItems
            .Select(item => item.ItemId)
            .ToHashSet();

        return shoppingPlans
            .Where(shoppingPlan => activeItemIds.Contains(shoppingPlan.ItemId))
            .ToList();
    }

    public static ProcurementEvidenceSummary GetProcurementEvidenceSummary(
        CraftingPlan? plan,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        if (plan == null)
        {
            return new ProcurementEvidenceSummary(0, 0, 0, 0, 0);
        }

        var activeItems = GetActiveProcurementItems(plan)
            .Where(item => item.TotalQuantity > 0)
            .ToList();
        var activeItemIds = activeItems
            .Select(item => item.ItemId)
            .ToHashSet();
        var planByItemId = shoppingPlans
            .GroupBy(shoppingPlan => shoppingPlan.ItemId)
            .ToDictionary(group => group.Key, group => group.First());
        var activeWithEvidence = activeItemIds.Count(itemId =>
            planByItemId.TryGetValue(itemId, out var shoppingPlan) && HasUsableEvidence(shoppingPlan));
        var candidateItemIds = GetMarketAnalysisCandidates(plan)
            .Select(item => item.ItemId)
            .ToHashSet();
        var suppressedCandidateCount = candidateItemIds.Count(itemId => !activeItemIds.Contains(itemId));

        return new ProcurementEvidenceSummary(
            activeItems.Count,
            planByItemId.Count,
            activeWithEvidence,
            activeItems.Count - activeWithEvidence,
            suppressedCandidateCount);
    }

    public static ProcurementEvidenceSummary GetProcurementEvidenceSummary(
        IReadOnlyList<MaterialAggregate> activeItems,
        IReadOnlyList<MaterialAggregate> marketCandidates,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        var activeItemsWithQuantity = activeItems
            .Where(item => item.TotalQuantity > 0)
            .ToList();
        var activeItemIds = activeItemsWithQuantity
            .Select(item => item.ItemId)
            .ToHashSet();
        var planByItemId = shoppingPlans
            .GroupBy(shoppingPlan => shoppingPlan.ItemId)
            .ToDictionary(group => group.Key, group => group.First());
        var activeWithEvidence = activeItemIds.Count(itemId =>
            planByItemId.TryGetValue(itemId, out var shoppingPlan) && HasUsableEvidence(shoppingPlan));
        var candidateItemIds = marketCandidates
            .Select(item => item.ItemId)
            .ToHashSet();
        var suppressedCandidateCount = candidateItemIds.Count(itemId => !activeItemIds.Contains(itemId));

        return new ProcurementEvidenceSummary(
            activeItemsWithQuantity.Count,
            planByItemId.Count,
            activeWithEvidence,
            activeItemsWithQuantity.Count - activeWithEvidence,
            suppressedCandidateCount);
    }

    public static bool HasCompleteProcurementEvidence(
        CraftingPlan? plan,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        var summary = GetProcurementEvidenceSummary(plan, shoppingPlans);
        return summary.HasCompleteActiveEvidence;
    }

    public static List<MaterialAggregate> GetActiveProcurementItemsMissingEvidence(
        CraftingPlan? plan,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        if (plan == null)
        {
            return new List<MaterialAggregate>();
        }

        return GetActiveProcurementItemsMissingEvidence(GetActiveProcurementItems(plan), shoppingPlans);
    }

    public static List<MaterialAggregate> GetActiveProcurementItemsMissingEvidence(
        IReadOnlyList<MaterialAggregate> activeItems,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        var planByItemId = shoppingPlans
            .GroupBy(shoppingPlan => shoppingPlan.ItemId)
            .ToDictionary(group => group.Key, group => group.First());

        return activeItems
            .Where(item => item.TotalQuantity > 0)
            .Where(item => !planByItemId.TryGetValue(item.ItemId, out var shoppingPlan) ||
                !HasUsableEvidence(shoppingPlan))
            .ToList();
    }

    public static decimal CalculateCraftCost(
        PlanNode node,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        return CalculateCraftCost(node, CreateCostContext(shoppingPlans));
    }

    public static int ApplyCheapestAcquisitionDefaults(
        CraftingPlan? plan,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        if (plan == null)
        {
            return 0;
        }

        return ApplyCheapestAcquisitionDefaults(plan, CreateCostContext(shoppingPlans));
    }

    public static int ApplyCheapestAcquisitionDefaults(
        CraftingPlan? plan,
        AcquisitionCostContext context)
    {
        if (plan == null)
        {
            return 0;
        }

        var changed = 0;
        foreach (var root in plan.RootItems)
        {
            changed += ApplyCheapestAcquisitionDefaults(root, context);
        }

        return changed;
    }

    public static AcquisitionSource? DetermineCheapestAcquisitionSource(
        PlanNode node,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        return DetermineCheapestAcquisitionSource(node, CreateCostContext(shoppingPlans));
    }

    public static AcquisitionSource? DetermineCheapestAcquisitionSource(
        PlanNode node,
        AcquisitionCostContext context)
    {
        return DetermineCheapestAcquisitionSource(node, context.PlanByItemId, context);
    }

    public static bool TryGetAcquisitionCost(
        PlanNode node,
        AcquisitionSource source,
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        out decimal cost)
    {
        return TryGetAcquisitionCost(node, source, CreateCostContext(shoppingPlans), out cost);
    }

    public static bool TryGetAcquisitionCost(
        PlanNode node,
        AcquisitionSource source,
        AcquisitionCostContext context,
        out decimal cost)
    {
        return TryGetAcquisitionCost(node, source, context.PlanByItemId, context, out cost);
    }

    public static bool TryGetAcquisitionCost(
        PlanNode node,
        AcquisitionSource source,
        AcquisitionCostContext context,
        int quantity,
        out decimal cost)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);

        if (quantity <= 0)
        {
            cost = 0;
            return false;
        }

        if (quantity == node.Quantity)
        {
            return TryGetAcquisitionCost(node, source, context, out cost);
        }

        cost = source switch
        {
            AcquisitionSource.Craft when node.Children.Any() && node.CanCraft => CalculateCraftCost(node, context, quantity),
            AcquisitionSource.MarketBuyNq when node.CanBuyFromMarket && !node.MustBeHq => GetMarketBuyCost(node, context.PlanByItemId, hqOnly: false, quantity),
            AcquisitionSource.MarketBuyHq when node.CanBuyFromMarket && node.CanBeHq => GetMarketBuyCost(node, context.PlanByItemId, hqOnly: true, quantity),
            AcquisitionSource.VendorBuy when node.CanBuyFromVendor => node.VendorPrice * quantity,
            _ => 0
        };

        return cost > 0;
    }

    public static int ReconcileAcquisitionDecisions(
        CraftingPlan? plan,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        return ApplyCheapestAcquisitionDefaults(plan, shoppingPlans);
    }

    public static int ReconcileAcquisitionDecisions(
        CraftingPlan? plan,
        AcquisitionCostContext context)
    {
        return ApplyCheapestAcquisitionDefaults(plan, context);
    }

    public static bool TryGetSelectedAcquisitionCost(
        IEnumerable<PlanNode> nodes,
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        out decimal cost)
    {
        return TryGetSelectedAcquisitionCost(nodes, CreateCostContext(shoppingPlans), out cost);
    }

    public static bool TryGetSelectedAcquisitionCost(
        IEnumerable<PlanNode> nodes,
        AcquisitionCostContext context,
        out decimal cost)
    {
        cost = 0;
        var hasAnyCost = false;

        foreach (var node in nodes)
        {
            if (!TryGetAcquisitionCost(node, node.Source, context, out var nodeCost))
            {
                continue;
            }

            cost += nodeCost;
            hasAnyCost = true;
        }

        return hasAnyCost;
    }

    public static AcquisitionCostContext CreateCostContext(IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        return new AcquisitionCostContext(CreatePlanLookup(shoppingPlans));
    }

    public static List<AcquisitionSource> GetAvailableSources(PlanNode node)
    {
        var sources = new List<AcquisitionSource>();

        if (node.CanCraft && node.Children.Any())
        {
            sources.Add(AcquisitionSource.Craft);
        }

        if (node.CanBuyFromMarket)
        {
            if (!node.MustBeHq)
            {
                sources.Add(AcquisitionSource.MarketBuyNq);
            }

            if (node.CanBeHq)
            {
                sources.Add(AcquisitionSource.MarketBuyHq);
            }
        }

        if (node.CanBuyFromVendor)
        {
            sources.Add(AcquisitionSource.VendorBuy);
        }

        if (HasSpecialCurrencyVendor(node) || node.Source == AcquisitionSource.VendorSpecialCurrency)
        {
            sources.Add(AcquisitionSource.VendorSpecialCurrency);
        }

        if (sources.Count == 0)
        {
            sources.Add(AcquisitionSource.UnknownSource);
        }

        return sources;
    }

    public static bool CanUseAcquisitionSource(PlanNode node, AcquisitionSource source)
    {
        return GetAvailableSources(node).Contains(source);
    }

    public static void SetAcquisitionSource(
        PlanNode node,
        AcquisitionSource source,
        AcquisitionSourceReason reason = AcquisitionSourceReason.UserSelected)
    {
        var availableSources = GetAvailableSources(node);
        if (availableSources.Contains(source))
        {
            node.Source = source;
            node.SourceReason = availableSources.Count == 1
                ? AcquisitionSourceReason.RequiredByAvailability
                : reason;
        }
        else
        {
            node.Source = availableSources[0];
            node.SourceReason = availableSources.Count == 1
                ? AcquisitionSourceReason.RequiredByAvailability
                : AcquisitionSourceReason.Coerced;
        }

        if (reason == AcquisitionSourceReason.UserSelected &&
            node.Source == AcquisitionSource.MarketBuyHq)
        {
            node.MustBeHq = true;
        }
        else if (reason == AcquisitionSourceReason.UserSelected &&
            node.Source == AcquisitionSource.MarketBuyNq)
        {
            node.MustBeHq = false;
        }

        EnsureValidAcquisitionSource(node);
    }

    public static void EnsureValidAcquisitionSource(PlanNode node)
    {
        var availableSources = GetAvailableSources(node);
        if (availableSources.Contains(node.Source))
        {
            if (availableSources.Count == 1 && node.SourceReason != AcquisitionSourceReason.UserSelected)
            {
                node.SourceReason = AcquisitionSourceReason.RequiredByAvailability;
            }

            return;
        }

        node.Source = availableSources[0];
        node.SourceReason = availableSources.Count == 1
            ? AcquisitionSourceReason.RequiredByAvailability
            : AcquisitionSourceReason.Coerced;
        if (node.Source == AcquisitionSource.UnknownSource)
        {
            node.PriceSource = PriceSource.Untradeable;
            node.PriceSourceDetails = "Unknown acquisition source";
        }
    }

    public static bool TryGetMarketBoardPurchase(
        DetailedShoppingPlan? shoppingPlan,
        int quantity,
        out WorldShoppingSummary? world,
        out decimal cost)
    {
        return TryGetMarketBoardPurchase(shoppingPlan, quantity, hqOnly: false, out world, out cost);
    }

    public static bool TryGetMarketBoardPurchase(
        DetailedShoppingPlan? shoppingPlan,
        int quantity,
        bool hqOnly,
        out WorldShoppingSummary? world,
        out decimal cost)
    {
        world = null;
        cost = 0;

        if (shoppingPlan == null || quantity <= 0 || shoppingPlan.QuantityNeeded <= 0)
        {
            return false;
        }

        var candidates = new List<MarketBoardPurchaseCandidate>();

        var recommendedWorld = shoppingPlan.RecommendedWorld;
        if (recommendedWorld != null &&
            IsMarketWorld(recommendedWorld) &&
            TryGetMarketBoardWorldPurchase(
                recommendedWorld,
                quantity,
                shoppingPlan.QuantityNeeded,
                hqOnly,
                out var recommendedCost))
        {
            candidates.Add(new MarketBoardPurchaseCandidate(recommendedWorld, recommendedCost));
        }

        if (shoppingPlan.RecommendedSplit != null &&
            TryGetMarketBoardSplitPurchase(shoppingPlan, quantity, hqOnly, out var splitCost))
        {
            candidates.Add(new MarketBoardPurchaseCandidate(null, splitCost));
        }

        candidates.AddRange(shoppingPlan.WorldOptions
            .Where(IsMarketWorld)
            .Select(option => new
            {
                World = option,
                HasCost = TryGetMarketBoardWorldPurchase(
                    option,
                    quantity,
                    shoppingPlan.QuantityNeeded,
                    hqOnly,
                    out var worldCost),
                Cost = worldCost
            })
            .Where(option => option.HasCost)
            .Select(option => new MarketBoardPurchaseCandidate(option.World, option.Cost)));

        if (candidates.Count > 0)
        {
            var bestCandidate = candidates.OrderBy(candidate => candidate.Cost).First();
            world = bestCandidate.World;
            cost = bestCandidate.Cost;
            return true;
        }

        return false;
    }

    private static bool TryGetMarketBoardWorldPurchase(
        WorldShoppingSummary world,
        int quantity,
        int quantityNeeded,
        bool hqOnly,
        out decimal cost)
    {
        if (TryGetListingsCost(world.Listings, quantity, hqOnly, out cost))
        {
            return true;
        }

        if (!hqOnly &&
            world.TotalQuantityPurchased >= quantity &&
            world.TotalCost > 0)
        {
            cost = ScaleEvidenceCost(world.TotalCost, quantity, quantityNeeded);
            return cost > 0;
        }

        cost = 0;
        return false;
    }

    private static bool TryGetMarketBoardSplitPurchase(
        DetailedShoppingPlan shoppingPlan,
        int quantity,
        bool hqOnly,
        out decimal cost)
    {
        if (TryGetListingsCost(
                shoppingPlan.RecommendedSplit?.SelectMany(split => split.Listings) ?? [],
                quantity,
                hqOnly,
                out cost))
        {
            return true;
        }

        if (!hqOnly && TryGetMarketBoardSplitCost(shoppingPlan, quantity, out cost))
        {
            return true;
        }

        cost = 0;
        return false;
    }

    private static void CollectMarketCandidate(PlanNode node, Dictionary<int, MaterialAggregate> aggregates)
    {
        if (node.Quantity > 0 && node.CanBuyFromMarket)
        {
            AddToAggregation(node, aggregates);
        }

        foreach (var child in node.Children)
        {
            CollectMarketCandidate(child, aggregates);
        }
    }

    private static void AddToAggregation(PlanNode node, Dictionary<int, MaterialAggregate> aggregates)
    {
        if (!aggregates.TryGetValue(node.ItemId, out var aggregate))
        {
            aggregate = new MaterialAggregate
            {
                ItemId = node.ItemId,
                Name = node.Name,
                IconId = node.IconId,
                UnitPrice = node.MarketPrice,
                RequiresHq = node.MustBeHq
            };
            aggregates[node.ItemId] = aggregate;
        }

        aggregate.TotalQuantity += node.Quantity;
        aggregate.UnitPrice = node.MarketPrice;
        aggregate.RequiresHq = aggregate.RequiresHq || node.MustBeHq;
        aggregate.Sources.Add(new MaterialSource
        {
            ParentItemName = node.Parent?.Name ?? "Direct",
            Quantity = node.Quantity,
            IsCrafted = node.Children.Any()
        });
    }

    private static bool HasUsableEvidence(DetailedShoppingPlan shoppingPlan)
    {
        return string.IsNullOrWhiteSpace(shoppingPlan.Error) &&
            ((shoppingPlan.RecommendedWorld != null &&
              shoppingPlan.RecommendedWorld.TotalQuantityPurchased >= shoppingPlan.QuantityNeeded) ||
             HasFulfilledRecommendedSplit(shoppingPlan, shoppingPlan.QuantityNeeded) ||
             shoppingPlan.Vendors.Any());
    }

    private static IReadOnlyDictionary<int, DetailedShoppingPlan> CreatePlanLookup(IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        return shoppingPlans
            .GroupBy(shoppingPlan => shoppingPlan.ItemId)
            .ToDictionary(group => group.Key, group => group.First());
    }

    private static int ApplyCheapestAcquisitionDefaults(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId)
    {
        return ApplyCheapestAcquisitionDefaults(node, new AcquisitionCostContext(planByItemId));
    }

    private static int ApplyCheapestAcquisitionDefaults(
        PlanNode node,
        AcquisitionCostContext context)
    {
        var changed = 0;
        foreach (var child in node.Children)
        {
            changed += ApplyCheapestAcquisitionDefaults(child, context);
        }

        var originalSource = node.Source;
        var bestSource = DetermineCheapestAcquisitionSource(node, context);
        if (bestSource.HasValue &&
            node.Source != bestSource.Value &&
            CanAutomaticallyChangeSource(node))
        {
            SetAcquisitionSource(node, bestSource.Value, AcquisitionSourceReason.SystemDefault);
        }

        node.EnsureValidAcquisitionSource();
        if (node.Source != originalSource)
        {
            changed++;
        }

        return changed;
    }

    private static bool CanAutomaticallyChangeSource(PlanNode node)
    {
        return node.SourceReason is
            AcquisitionSourceReason.SystemDefault or
            AcquisitionSourceReason.Restored or
            AcquisitionSourceReason.Coerced or
            AcquisitionSourceReason.RequiredByAvailability;
    }

    private static AcquisitionSource? DetermineCheapestAcquisitionSource(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId)
    {
        return DetermineCheapestAcquisitionSource(node, planByItemId, context: null);
    }

    private static AcquisitionSource? DetermineCheapestAcquisitionSource(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId,
        AcquisitionCostContext? context)
    {
        var candidates = GetAcquisitionCostCandidates(node, planByItemId, context)
            .OrderBy(candidate => candidate.Cost)
            .ThenBy(candidate => GetSourceTieBreak(candidate.Source))
            .ToList();

        return candidates.Count == 0 ? null : candidates[0].Source;
    }

    private static IEnumerable<(AcquisitionSource Source, decimal Cost)> GetAcquisitionCostCandidates(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId)
    {
        return GetAcquisitionCostCandidates(node, planByItemId, context: null);
    }

    private static IEnumerable<(AcquisitionSource Source, decimal Cost)> GetAcquisitionCostCandidates(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId,
        AcquisitionCostContext? context)
    {
        foreach (var source in GetAvailableSources(node))
        {
            if (TryGetDefaultEligibleAcquisitionCost(node, source, planByItemId, context, out var cost))
            {
                yield return (source, cost);
            }
        }
    }

    private static bool TryGetDefaultEligibleAcquisitionCost(
        PlanNode node,
        AcquisitionSource source,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId,
        AcquisitionCostContext? context,
        out decimal cost)
    {
        cost = source switch
        {
            AcquisitionSource.MarketBuyNq when node.CanBuyFromMarket && !node.MustBeHq =>
                GetDefaultEligibleMarketBuyCost(node, planByItemId, hqOnly: false),
            AcquisitionSource.MarketBuyHq when node.CanBuyFromMarket && node.CanBeHq =>
                GetDefaultEligibleMarketBuyCost(node, planByItemId, hqOnly: true),
            _ => TryGetAcquisitionCost(node, source, planByItemId, context, out var sourceCost) ? sourceCost : 0
        };

        return cost > 0;
    }

    private static bool TryGetAcquisitionCost(
        PlanNode node,
        AcquisitionSource source,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId,
        out decimal cost)
    {
        return TryGetAcquisitionCost(node, source, planByItemId, context: null, out cost);
    }

    private static bool TryGetAcquisitionCost(
        PlanNode node,
        AcquisitionSource source,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId,
        AcquisitionCostContext? context,
        out decimal cost)
    {
        if (context != null && context.TryGetCachedCost(node, source, out cost))
        {
            return cost > 0;
        }

        cost = source switch
        {
            AcquisitionSource.Craft when node.Children.Any() && node.CanCraft => CalculateCraftCost(node, context ?? new AcquisitionCostContext(planByItemId)),
            AcquisitionSource.MarketBuyNq when node.CanBuyFromMarket && !node.MustBeHq => GetMarketBuyCost(node, planByItemId, hqOnly: false),
            AcquisitionSource.MarketBuyHq when node.CanBuyFromMarket && node.CanBeHq => GetMarketBuyCost(node, planByItemId, hqOnly: true),
            AcquisitionSource.VendorBuy when node.CanBuyFromVendor => node.VendorPrice * node.Quantity,
            _ => 0
        };

        var hasCost = cost > 0;
        if (context != null && hasCost)
        {
            context.SetCachedCost(node, source, cost);
        }

        return hasCost;
    }

    private static decimal GetMarketBuyCost(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId,
        bool hqOnly)
    {
        return GetMarketBuyCost(node, planByItemId, hqOnly, node.Quantity);
    }

    private static decimal GetMarketBuyCost(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId,
        bool hqOnly,
        int quantity)
    {
        if (planByItemId.TryGetValue(node.ItemId, out var shoppingPlan))
        {
            var estimate = MarketPurchaseCostProjectionService.Estimate(
                shoppingPlan,
                quantity,
                hqOnly,
                includeVendor: false);
            if (estimate.HasCost)
            {
                return estimate.Cost;
            }

            return 0;
        }

        return (hqOnly ? node.HqMarketPrice : node.MarketPrice) * quantity;
    }

    private static decimal GetDefaultEligibleMarketBuyCost(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId,
        bool hqOnly)
    {
        if (planByItemId.TryGetValue(node.ItemId, out var shoppingPlan))
        {
            var estimate = MarketPurchaseCostProjectionService.Estimate(
                shoppingPlan,
                node.Quantity,
                hqOnly,
                includeVendor: false);
            if (estimate.IsDefaultEligible)
            {
                return estimate.Cost;
            }

            return 0;
        }

        return (hqOnly ? node.HqMarketPrice : node.MarketPrice) * node.Quantity;
    }

    private static int GetSourceTieBreak(AcquisitionSource source)
    {
        return source switch
        {
            AcquisitionSource.VendorBuy => 0,
            AcquisitionSource.MarketBuyNq => 1,
            AcquisitionSource.MarketBuyHq => 2,
            AcquisitionSource.Craft => 3,
            _ => 4
        };
    }

    private static decimal CalculateCraftCost(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId)
    {
        return CalculateCraftCost(node, new AcquisitionCostContext(planByItemId));
    }

    private static decimal CalculateCraftCost(
        PlanNode node,
        AcquisitionCostContext context)
    {
        if (context.TryGetCachedCost(node, AcquisitionSource.Craft, out var cached))
        {
            return cached;
        }

        if (!node.Children.Any())
        {
            var direct = GetDirectAcquisitionCost(node, context.PlanByItemId);
            context.SetCachedCost(node, AcquisitionSource.Craft, direct);
            return direct;
        }

        decimal cost = 0;
        foreach (var child in node.Children)
        {
            if (child.Source == AcquisitionSource.Craft)
            {
                if (TryGetAcquisitionCost(child, child.Source, context, out var childCost))
                {
                    cost += childCost;
                }

                continue;
            }

            var directCost = GetDirectAcquisitionCost(child, context.PlanByItemId);
            if (directCost > 0)
            {
                context.SetCachedCost(child, child.Source, directCost);
                cost += directCost;
            }
        }

        var result = node.Yield > 1 ? cost / node.Yield : cost;
        context.SetCachedCost(node, AcquisitionSource.Craft, result);
        return result;
    }

    private static decimal CalculateCraftCost(
        PlanNode node,
        AcquisitionCostContext context,
        int quantity)
    {
        var baseCost = CalculateCraftCost(node, context);
        return node.Quantity > 0
            ? baseCost * quantity / node.Quantity
            : baseCost;
    }

    private static decimal GetDirectAcquisitionCost(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId)
    {
        if (node.Source is AcquisitionSource.MarketBuyNq &&
            planByItemId.TryGetValue(node.ItemId, out var shoppingPlan))
        {
            var estimate = MarketPurchaseCostProjectionService.Estimate(
                shoppingPlan,
                node.Quantity,
                hqOnly: false,
                includeVendor: false);
            if (estimate.HasCost)
            {
                return estimate.Cost;
            }

            return 0;
        }

        if (node.Source is AcquisitionSource.MarketBuyHq &&
            planByItemId.TryGetValue(node.ItemId, out var hqShoppingPlan))
        {
            var estimate = MarketPurchaseCostProjectionService.Estimate(
                hqShoppingPlan,
                node.Quantity,
                hqOnly: true,
                includeVendor: false);
            if (estimate.HasCost)
            {
                return estimate.Cost;
            }

            return 0;
        }

        return node.Source switch
        {
            AcquisitionSource.MarketBuyNq => node.MarketPrice * node.Quantity,
            AcquisitionSource.MarketBuyHq => node.HqMarketPrice * node.Quantity,
            AcquisitionSource.VendorBuy => node.VendorPrice * node.Quantity,
            _ => 0
        };
    }

    private static bool HasSpecialCurrencyVendor(PlanNode node)
    {
        return node.VendorOptions.Any(vendor => !vendor.IsGilVendor);
    }

    private static bool TryGetListingsCost(
        IEnumerable<ShoppingListingEntry> listings,
        int quantity,
        bool hqOnly,
        out decimal cost)
    {
        cost = 0;
        var remaining = quantity;
        foreach (var listing in listings
            .Where(listing => !hqOnly || listing.IsHq)
            .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
            .OrderBy(listing => listing.PricePerUnit))
        {
            var quantityToBuy = Math.Min(remaining, listing.Quantity);
            cost += quantityToBuy * listing.PricePerUnit;
            remaining -= quantityToBuy;
            if (remaining <= 0)
            {
                return true;
            }
        }

        cost = 0;
        return false;
    }

    private static bool IsMarketWorld(WorldShoppingSummary? world)
    {
        return world != null &&
            !string.Equals(world.WorldName, MarketShoppingConstants.VendorWorldName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetMarketBoardSplitCost(DetailedShoppingPlan shoppingPlan, int quantity, out decimal cost)
    {
        cost = 0;
        if (shoppingPlan.RecommendedSplit?.Any() != true ||
            shoppingPlan.RecommendedSplit.Any(split =>
                string.Equals(split.WorldName, MarketShoppingConstants.VendorWorldName, StringComparison.OrdinalIgnoreCase)) ||
            !HasFulfilledRecommendedSplit(shoppingPlan, quantity) ||
            shoppingPlan.SplitTotalCost is not { } splitTotalCost ||
            splitTotalCost <= 0)
        {
            return false;
        }

        cost = ScaleEvidenceCost(splitTotalCost, quantity, shoppingPlan.QuantityNeeded);
        return true;
    }

    private static bool HasFulfilledRecommendedSplit(DetailedShoppingPlan shoppingPlan, int quantity)
    {
        return shoppingPlan.RecommendedSplit?.Sum(split => split.QuantityToBuy) >= quantity;
    }

    private static decimal ScaleEvidenceCost(long totalCost, int quantity, int quantityNeeded)
    {
        return quantity == quantityNeeded
            ? totalCost
            : totalCost * quantity / quantityNeeded;
    }

    private sealed record MarketBoardPurchaseCandidate(
        WorldShoppingSummary? World,
        decimal Cost);
}

public sealed record ProcurementEvidenceSummary(
    int ActiveProcurementItemCount,
    int AnalyzedCandidateCount,
    int ActiveItemsWithEvidence,
    int ActiveItemsMissingEvidence,
    int SuppressedMarketCandidateCount)
{
    public bool HasCompleteActiveEvidence => ActiveProcurementItemCount > 0 && ActiveItemsMissingEvidence == 0;
}

public sealed class AcquisitionCostContext
{
    private readonly Dictionary<(string NodeId, AcquisitionSource Source), decimal> _costByNodeAndSource = new();

    internal AcquisitionCostContext(IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId)
    {
        PlanByItemId = planByItemId;
    }

    internal IReadOnlyDictionary<int, DetailedShoppingPlan> PlanByItemId { get; }

    public int CachedCostEntryCount => _costByNodeAndSource.Count;

    internal bool TryGetCachedCost(PlanNode node, AcquisitionSource source, out decimal cost)
    {
        return _costByNodeAndSource.TryGetValue((node.NodeId, source), out cost);
    }

    internal void SetCachedCost(PlanNode node, AcquisitionSource source, decimal cost)
    {
        _costByNodeAndSource[(node.NodeId, source)] = cost;
    }

    public bool TryGetShoppingPlan(int itemId, out DetailedShoppingPlan? shoppingPlan)
    {
        return PlanByItemId.TryGetValue(itemId, out shoppingPlan);
    }
}
