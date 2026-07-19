using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

namespace FFXIV_Craft_Architect.Web.Services;

public static class MarketAnalysisGridViewService
{
    public const string CalculatedTotalHeaderTooltip = "Cash Out is the gil actually required to buy the selected listing stacks or vendor quantity. It is never extrapolated from averages or incomplete stock.";
    public const string UnsupportedProjectedCostTooltip = "No actionable cash-out total is available because the current search scope cannot cover this purchase.";

    public static IReadOnlyList<DetailedShoppingPlan> GetOrderedPlans(
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        IEnumerable<MarketItemAnalysis> analyses,
        MarketAcquisitionLens lens,
        MarketSortOption defaultSort,
        MarketAnalysisGridSortColumn? sortColumn,
        bool sortDescending)
    {
        ArgumentNullException.ThrowIfNull(shoppingPlans);
        ArgumentNullException.ThrowIfNull(analyses);

        var plans = shoppingPlans.ToList();
        var analysisByItemId = analyses.ToDictionary(analysis => analysis.ItemId);
        IOrderedEnumerable<DetailedShoppingPlan> ordered = sortColumn switch
        {
            MarketAnalysisGridSortColumn.Item => Order(plans, plan => plan.Name, sortDescending),
            MarketAnalysisGridSortColumn.Quantity => Order(plans, plan => plan.QuantityNeeded, sortDescending),
            MarketAnalysisGridSortColumn.Coverage => Order(plans, plan => GetCoverageSortValue(plan, analysisByItemId), sortDescending),
            MarketAnalysisGridSortColumn.Worlds => Order(plans, GetWorldCount, sortDescending),
            MarketAnalysisGridSortColumn.Total => Order(plans, GetTotalCost, sortDescending),
            _ => GetDefaultOrder(plans, analysisByItemId, lens, defaultSort)
        };

        return ordered
            .ThenBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.ItemId)
            .ToList();
    }

    public static DetailedShoppingPlan? ResolveSelectedPlan(
        IReadOnlyList<DetailedShoppingPlan> orderedPlans,
        int? selectedItemId)
    {
        if (orderedPlans.Count == 0)
        {
            return null;
        }

        if (selectedItemId.HasValue)
        {
            var selected = orderedPlans.FirstOrDefault(plan => plan.ItemId == selectedItemId.Value);
            if (selected != null)
            {
                return selected;
            }
        }

        return orderedPlans[0];
    }

    public static MarketAnalysisGridSortState ToggleSort(
        MarketAnalysisGridSortColumn? currentColumn,
        bool currentDescending,
        MarketAnalysisGridSortColumn clickedColumn)
    {
        return new MarketAnalysisGridSortState(
            clickedColumn,
            currentColumn == clickedColumn && !currentDescending);
    }

    public static MarketAnalysisWorldGridSortState ToggleWorldSort(
        MarketAnalysisWorldGridSortColumn? currentColumn,
        bool currentDescending,
        MarketAnalysisWorldGridSortColumn clickedColumn)
    {
        return new MarketAnalysisWorldGridSortState(
            clickedColumn,
            currentColumn == clickedColumn && !currentDescending);
    }

    public static long GetTotalCost(DetailedShoppingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var coverage = PurchaseRecommendationCost.GetDefaultCoverageOption(plan);
        if (coverage is
            {
                Kind: MarketCoverageKind.SupportedListings,
                IsDefaultEligible: true,
                CashOutCost: > 0
            } && coverage.QuantityCovered >= plan.QuantityNeeded)
        {
            return ToLongSaturating(coverage.CashOutCost);
        }

        if (plan.RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName &&
            plan.RecommendedWorld.TotalCost > 0)
        {
            return plan.RecommendedWorld.TotalCost;
        }

        if (plan.RecommendedSplit?.Sum(split => split.QuantityToBuy) >= plan.QuantityNeeded &&
            plan.SplitTotalCost is > 0)
        {
            return plan.SplitTotalCost.Value;
        }

        if (plan.RecommendedWorld is { TotalCost: > 0 } world &&
            world.TotalQuantityPurchased >= plan.QuantityNeeded)
        {
            return world.TotalCost;
        }

        return 0;
    }

    public static string FormatTotalCost(DetailedShoppingPlan plan)
    {
        var total = GetTotalCost(plan);
        return total > 0 ? $"{total:N0}g" : "Unavailable";
    }

    public static bool IsUnsupportedProjectedCost(DetailedShoppingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return MarketPurchaseCostProjectionService.IsUnsupportedProjectedCost(plan);
    }

    public static string GetTotalCostClass(DetailedShoppingPlan plan)
    {
        return IsUnsupportedProjectedCost(plan)
            ? "ma-total-value is-projected-unsupported"
            : GetTotalCost(plan) > 0
                ? "ma-total-value"
                : "ma-total-value is-unavailable";
    }

    public static string GetTotalCostTooltip(DetailedShoppingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var coverage = PurchaseRecommendationCost.GetDefaultCoverageOption(plan);
        if (coverage != null)
        {
            return coverage.CashOutCost == coverage.ExactNeededCost
                ? $"Cash Out uses the selected {coverage.Tier} listing coverage: {coverage.CashOutCost:N0}g."
                : $"Cash Out is {coverage.CashOutCost:N0}g for the selected stacks; {coverage.ExactNeededCost:N0}g of their value covers the exact quantity needed.";
        }

        if (IsUnsupportedProjectedCost(plan))
        {
            return UnsupportedProjectedCostTooltip;
        }

        if (plan.RecommendedSplit?.Any() == true)
        {
            var splitQuantity = plan.RecommendedSplit.Sum(split => split.QuantityToBuy);
            return $"Cash Out is the sum of the selected split purchase: {splitQuantity:N0}/{plan.QuantityNeeded:N0} items across {plan.RecommendedSplit.Count:N0} worlds.";
        }

        if (plan.RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName || plan.Vendors.Any())
        {
            return $"Cash Out uses loaded gil vendor pricing for {plan.QuantityNeeded:N0} needed.";
        }

        if (plan.RecommendedWorld != null)
        {
            return $"Cash Out uses the selected world's listing stacks for {plan.RecommendedWorld.TotalQuantityPurchased:N0}/{plan.QuantityNeeded:N0} needed.";
        }

        return "No actionable cash-out total is available from the current recommendation evidence.";
    }

    private static long ToLongSaturating(decimal value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(value);
    }

    public static string GetWorldPriceBandScoreClass(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (!HasProcurementEvidence(world))
        {
            return "is-unavailable";
        }

        return GetProcurementEvidenceDepth(world) == PriceBandDepth.Thin
            ? "is-competitive"
            : "is-optimal";
    }

    public static string FormatWorldUnitPrice(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var unitPrice = GetComparableUnitPrice(world);
        if (unitPrice > 0)
        {
            return $"~{unitPrice:N0}g / unit";
        }

        return GetProcurementEvidenceQuantity(world) > 0
            ? "Outliers only"
            : "No listings";
    }

    public static string FormatWorldAverageUnitPrice(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (world.WorldAverageUnitPrice <= 0)
        {
            return string.Empty;
        }

        var comparablePrice = GetComparableUnitPrice(world);
        return comparablePrice <= 0 || Math.Round(world.WorldAverageUnitPrice) != Math.Round(comparablePrice)
            ? $"all asks ~{world.WorldAverageUnitPrice:N0}g"
            : string.Empty;
    }

    public static string FormatWorldStockDepth(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var quantity = GetProcurementEvidenceQuantity(world);
        return quantity > 0
            ? $"{quantity:N0} {FormatPriceBandDepth(GetProcurementEvidenceDepth(world))}"
            : "No listings";
    }

    public static string FormatWorldMarketDepthQuantity(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var quantity = GetProcurementEvidenceQuantity(world);
        return quantity > 0
            ? $"{quantity:N0}"
            : "No listings";
    }

    public static string FormatWorldMarketDepthDescriptor(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        return HasProcurementEvidence(world)
            ? FormatPriceBandDepth(GetProcurementEvidenceDepth(world))
            : string.Empty;
    }

    public static string GetWorldUnitPriceScoreClass(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        return GetWorldPriceBandScoreClass(world);
    }

    public static string FormatAnalysisScopePriceSummary(MarketItemAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var parts = new List<string>();
        if (analysis.PrimaryProcurementShelfAverageUnitPrice > 0)
        {
            parts.Add($"best listings average ~{analysis.PrimaryProcurementShelfAverageUnitPrice:N0}g");
        }

        if (analysis.PrimaryProcurementShelfAverageUnitPrice <= 0 &&
            analysis.AnalysisCompetitiveAverageUnitPrice > 0)
        {
            parts.Add($"best available average ~{analysis.AnalysisCompetitiveAverageUnitPrice:N0}g");
        }

        if (analysis.PriceEvaluation?.CentralRegion.Credibility is
            MarketPriceRegionCredibility.Thin or MarketPriceRegionCredibility.Unknown)
        {
            parts.Add("regional price reference uncertain");
            return string.Join("; ", parts);
        }

        if (analysis.PriceEvaluation is { } evaluation &&
            evaluation.Thresholds.InsaneFloorUnitPrice > 0)
        {
            parts.Add($"reference average ~{evaluation.CentralRegion.WeightedAverageUnitPrice:N0}g ({FormatCredibility(evaluation.CentralRegion.Credibility)})");
            parts.Add($"competitive through ~{evaluation.Thresholds.CompetitiveCeilingUnitPrice:N0}g");
            parts.Add($"extreme outliers from ~{evaluation.Thresholds.InsaneFloorUnitPrice:N0}g");
            return string.Join("; ", parts);
        }

        if (analysis.SaneThresholdUnitPrice <= 0)
        {
            return string.Join("; ", parts);
        }

        parts.Add($"typical price ~{analysis.AnalysisScopeBaselineUnitPrice:N0}g");
        parts.Add($"market average ~{analysis.AnalysisScopeAverageUnitPrice:N0}g");
        parts.Add($"competitive through ~{analysis.CompetitiveThresholdUnitPrice:N0}g");
        parts.Add($"extreme outliers from ~{analysis.SaneThresholdUnitPrice:N0}g");
        return string.Join("; ", parts);
    }

    public static string FormatCoverageLabel(
        DetailedShoppingPlan plan,
        IReadOnlyDictionary<int, MarketItemAnalysis> analysisByItemId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(analysisByItemId);

        if (analysisByItemId.TryGetValue(plan.ItemId, out var analysis))
        {
            if (analysis.Worlds.Count == 0)
            {
                return analysis.Warning ?? "No market data";
            }

            var full = analysis.Worlds.Count(world => GetDisplayCoverageBucket(world) == MarketCoverageBucket.Full);
            var partial = analysis.Worlds.Count(world =>
                GetDisplayCoverageBucket(world) is MarketCoverageBucket.PartialDeep or MarketCoverageBucket.PartialThin);
            return $"{full} full, {partial} partial across {analysis.Worlds.Count} worlds";
        }

        if (!string.IsNullOrWhiteSpace(plan.Error))
        {
            return plan.Error;
        }

        return "Analytics unavailable; rerun analysis";
    }

    public static IReadOnlyList<WorldMarketAnalysis> GetOrderedWorlds(
        MarketItemAnalysis analysis,
        MarketAcquisitionLens lens,
        MarketAnalysisWorldGridSortColumn? sortColumn = null,
        bool sortDescending = false,
        MarketAnalysisEvidenceOverlay evidenceOverlay = MarketAnalysisEvidenceOverlay.CompetitivenessOverlay)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var sortState = sortColumn.HasValue
            ? new WebTableSortState<MarketAnalysisWorldGridSortColumn>(sortColumn.Value, sortDescending)
            : WebTableSortState<MarketAnalysisWorldGridSortColumn>.Unsorted;

        return WebTableOrdering.Apply(
            analysis.Worlds,
            sortState,
            GetWorldSortRules(lens, evidenceOverlay),
            worlds => GetDefaultWorldOrder(worlds, lens),
            ordered => ordered
                .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase));
    }

    public static WorldLensScore GetScore(WorldMarketAnalysis world, MarketAcquisitionLens lens)
    {
        ArgumentNullException.ThrowIfNull(world);

        return world.Scores.FirstOrDefault(score => score.Lens == lens)
            ?? world.Scores.FirstOrDefault()
            ?? new WorldLensScore { Lens = lens, ScoreBucket = MarketScoreBucket.Unavailable };
    }

    public static string GetWorldRowClass(WorldMarketAnalysis world, MarketAcquisitionLens lens)
    {
        return $"ma-world-row {GetScoreClass(GetDisplayScoreBucket(world, lens))}";
    }

    public static string GetScoreClass(MarketScoreBucket bucket)
    {
        return bucket switch
        {
            MarketScoreBucket.Optimal => "is-optimal",
            MarketScoreBucket.Competitive => "is-competitive",
            MarketScoreBucket.Expensive => "is-expensive",
            MarketScoreBucket.PoorFit => "is-poor",
            _ => "is-unavailable"
        };
    }

    public static string FormatScoreBucket(MarketScoreBucket bucket)
    {
        return bucket switch
        {
            MarketScoreBucket.Optimal => "optimal",
            MarketScoreBucket.Competitive => "average",
            MarketScoreBucket.Expensive => "expensive",
            MarketScoreBucket.PoorFit => "low stock",
            _ => "unavailable"
        };
    }

    public static string FormatCoverage(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (world.QuantityNeeded <= 0)
        {
            return "0/0";
        }

        return $"{GetCoverageQuantity(world):N0}/{world.QuantityNeeded:N0}";
    }

    public static string FormatCoverageBucket(MarketCoverageBucket bucket)
    {
        return bucket switch
        {
            MarketCoverageBucket.Full => "full",
            MarketCoverageBucket.PartialDeep => "partial, strong supply",
            MarketCoverageBucket.PartialThin => "partial, limited supply",
            _ => "none"
        };
    }

    public static string FormatPriceBandDepth(PriceBandDepth depth)
    {
        return depth switch
        {
            PriceBandDepth.Deep => "strong",
            PriceBandDepth.Usable => "moderate",
            PriceBandDepth.Thin => "limited",
            _ => "no stock"
        };
    }

    public static string FormatCredibility(MarketPriceRegionCredibility credibility)
    {
        return credibility switch
        {
            MarketPriceRegionCredibility.Strong => "strong support",
            MarketPriceRegionCredibility.Credible => "moderate support",
            MarketPriceRegionCredibility.Thin => "thin support",
            _ => "uncertain support"
        };
    }

    public static string FormatCompetitiveStockDetail(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        return world.ScopeInsaneQuantity > 0
            ? $"{world.ScopeInsaneQuantity:N0} extreme outlier"
            : string.Empty;
    }

    public static string FormatCompetitiveValue(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var (referencePrice, _) = GetCompetitiveValueReference(world);
        var comparablePrice = GetComparableUnitPrice(world);
        if (referencePrice <= 0 || comparablePrice <= 0)
        {
            return "-";
        }

        var percent = (comparablePrice - referencePrice) / referencePrice * 100m;
        var rounded = Math.Round(percent, 0, MidpointRounding.AwayFromZero);
        return $"{rounded:+0;-0;0}%";
    }

    public static string GetCompetitiveValueClass(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var (referencePrice, _) = GetCompetitiveValueReference(world);
        var comparablePrice = GetComparableUnitPrice(world);
        if (referencePrice <= 0 || comparablePrice <= 0)
        {
            return "ma-world-value";
        }

        var percent = (comparablePrice - referencePrice) / referencePrice * 100m;
        var rounded = Math.Round(percent, 0, MidpointRounding.AwayFromZero);
        return rounded < 0
            ? "ma-world-value is-below"
            : rounded > 0
                ? "ma-world-value is-above"
                : "ma-world-value";
    }

    public static string FormatCompetitiveValueTooltip(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var (referencePrice, referenceLabel) = GetCompetitiveValueReference(world);
        var comparablePrice = GetComparableUnitPrice(world);
        if (referencePrice <= 0 || comparablePrice <= 0)
        {
            return GetProcurementEvidenceQuantity(world) > 0
                ? "Only extreme outlier listings are available, so this world has no representative price comparison."
                : "No listing price is available for comparison.";
        }

        var percent = (comparablePrice - referencePrice) / referencePrice * 100m;
        var rounded = Math.Round(Math.Abs(percent), 0, MidpointRounding.AwayFromZero);
        var relationship = percent < 0
            ? "less than"
            : percent > 0
                ? "greater than"
                : "equal to";

        return $"{world.WorldName}'s comparable average is {rounded:N0}% {relationship} the regional {referenceLabel}: {comparablePrice:N0}g vs {referencePrice:N0}g.";
    }

    public static IReadOnlyList<MarketListingDivider> GetListingDividersBefore(
        WorldMarketAnalysis world,
        AnalyzedMarketListing listing)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(listing);

        var dividers = new List<MarketListingDivider>();
        var bandBreak = GetBandBreakBefore(world, listing);
        if (bandBreak.HasValue)
        {
            dividers.Add(new MarketListingDivider($"Price band +{bandBreak.Value:N0}%"));
        }

        var primaryShelfAverage = world.PrimaryUsableAverageUnitPrice > 0
            ? world.PrimaryUsableAverageUnitPrice
            : world.AnalysisCompetitiveAverageUnitPrice;
        if (CrossesThresholdBefore(world, listing, primaryShelfAverage))
        {
            var label = world.PrimaryUsableAverageUnitPrice > 0
                ? "Above best listings average"
                : "Above best available average";
            dividers.Add(new MarketListingDivider(label));
        }

        var average = world.AnalysisScopeAverageUnitPrice;
        if (average > 0 && average != primaryShelfAverage && CrossesThresholdBefore(world, listing, average))
        {
            dividers.Add(new MarketListingDivider("Above market average"));
        }

        return dividers;
    }

    public static string GetListingRowClass(
        WorldMarketAnalysis world,
        AnalyzedMarketListing listing,
        MarketAnalysisEvidenceOverlay evidenceOverlay)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(listing);

        var classes = GetCompetitivenessListingClasses(listing);
        if (evidenceOverlay == MarketAnalysisEvidenceOverlay.PriceBandOverlay)
        {
            classes.Add(GetListingPriceBandToneClass(world, listing));
            classes.Add(GetListingPriceBandEdgeClass(world, listing));
        }

        return string.Join(" ", classes.Where(cssClass => !string.IsNullOrWhiteSpace(cssClass)));
    }

    public static string GetListingPriceBandTooltip(
        WorldMarketAnalysis world,
        AnalyzedMarketListing listing,
        MarketAnalysisEvidenceOverlay evidenceOverlay)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(listing);

        if (evidenceOverlay != MarketAnalysisEvidenceOverlay.PriceBandOverlay)
        {
            return string.Empty;
        }

        return GetListingPriceBandSignal(world, listing) switch
        {
            ListingPriceBandSignal.LowOutlier => "Low price region: unusual relative to the reference market, but still actionable.",
            ListingPriceBandSignal.Thin => "Limited-supply price region: weak reference-price support, but the listing remains actionable.",
            ListingPriceBandSignal.Competitive => "Competitive listing: this price is supported by enough useful market evidence.",
            ListingPriceBandSignal.Uncompetitive => "High-priced listing: visible in the market, but above the useful buying range.",
            ListingPriceBandSignal.Insane => "Extreme price region: far from the reference market, but still actionable at its listed price.",
            _ => "Price band signal could not be classified from the loaded listing evidence."
        };
    }

    public static bool IsUncompetitiveListing(AnalyzedMarketListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return IsUncompetitive(listing);
    }

    public static MarketCoverageBucket GetDisplayCoverageBucket(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var coverageQuantity = GetCoverageQuantity(world);
        if (world.QuantityNeeded <= 0 || coverageQuantity <= 0)
        {
            return MarketCoverageBucket.None;
        }

        if (coverageQuantity >= world.QuantityNeeded)
        {
            return MarketCoverageBucket.Full;
        }

        return coverageQuantity >= Math.Max(world.QuantityNeeded / 2, 1)
            ? MarketCoverageBucket.PartialDeep
            : MarketCoverageBucket.PartialThin;
    }

    public static MarketScoreBucket GetDisplayScoreBucket(WorldMarketAnalysis world, MarketAcquisitionLens lens)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (!HasScopePriceContext(world))
        {
            return GetScore(world, lens).ScoreBucket;
        }

        if (world.DataQualityBucket == MarketDataQualityBucket.Missing)
        {
            return MarketScoreBucket.Unavailable;
        }

        var coverageQuantity = GetCoverageQuantity(world);
        if (world.QuantityNeeded <= 0 || coverageQuantity >= world.QuantityNeeded)
        {
            return MarketScoreBucket.Optimal;
        }

        if (coverageQuantity >= Math.Max(world.QuantityNeeded / 2, 1))
        {
            return MarketScoreBucket.Competitive;
        }

        return coverageQuantity > 0 || HasProcurementEvidence(world)
            ? MarketScoreBucket.PoorFit
            : MarketScoreBucket.Unavailable;
    }

    private static IOrderedEnumerable<DetailedShoppingPlan> GetDefaultOrder(
        IEnumerable<DetailedShoppingPlan> plans,
        IReadOnlyDictionary<int, MarketItemAnalysis> analysisByItemId,
        MarketAcquisitionLens lens,
        MarketSortOption defaultSort)
    {
        return defaultSort switch
        {
            MarketSortOption.Alphabetical => plans.OrderBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase),
            MarketSortOption.ByRecommended => plans.OrderBy(plan => GetBestWorldRank(plan, analysisByItemId, lens))
                .ThenBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase),
            _ => plans.OrderBy(plan => plan.ItemId)
        };
    }

    private static IOrderedEnumerable<DetailedShoppingPlan> Order<TKey>(
        IEnumerable<DetailedShoppingPlan> plans,
        Func<DetailedShoppingPlan, TKey> selector,
        bool descending)
    {
        return descending
            ? plans.OrderByDescending(selector)
            : plans.OrderBy(selector);
    }

    private static IOrderedEnumerable<WorldMarketAnalysis> OrderWorlds<TKey>(
        IEnumerable<WorldMarketAnalysis> worlds,
        Func<WorldMarketAnalysis, TKey> selector,
        bool descending)
    {
        return descending
            ? worlds.OrderByDescending(selector)
            : worlds.OrderBy(selector);
    }

    private static IReadOnlyList<WebTableSortRule<WorldMarketAnalysis, MarketAnalysisWorldGridSortColumn>> GetWorldSortRules(
        MarketAcquisitionLens lens,
        MarketAnalysisEvidenceOverlay evidenceOverlay)
    {
        return
        [
            WebTableSortRule<WorldMarketAnalysis, MarketAnalysisWorldGridSortColumn>.Create(
                MarketAnalysisWorldGridSortColumn.World,
                world => world.WorldName,
                StringComparer.OrdinalIgnoreCase),
            WebTableSortRule<WorldMarketAnalysis, MarketAnalysisWorldGridSortColumn>.CreateCustom(
                MarketAnalysisWorldGridSortColumn.StockDepth,
                OrderWorldsByPriceSignalQuantity),
            WebTableSortRule<WorldMarketAnalysis, MarketAnalysisWorldGridSortColumn>.Create(
                MarketAnalysisWorldGridSortColumn.Coverage,
                GetWorldCoverageSortValue),
            WebTableSortRule<WorldMarketAnalysis, MarketAnalysisWorldGridSortColumn>.CreateCustom(
                MarketAnalysisWorldGridSortColumn.PriceValue,
                OrderWorldsByUnitPrice),
            WebTableSortRule<WorldMarketAnalysis, MarketAnalysisWorldGridSortColumn>.Create(
                MarketAnalysisWorldGridSortColumn.Value,
                GetCompetitiveValueSortValue),
            WebTableSortRule<WorldMarketAnalysis, MarketAnalysisWorldGridSortColumn>.Create(
                MarketAnalysisWorldGridSortColumn.Data,
                world => world.DataAge ?? TimeSpan.MaxValue)
        ];
    }

    private static IOrderedEnumerable<WorldMarketAnalysis> GetDefaultWorldOrder(
        IEnumerable<WorldMarketAnalysis> worlds,
        MarketAcquisitionLens lens)
    {
        return worlds
            .OrderBy(world => world.DataQualityBucket == MarketDataQualityBucket.Missing)
            .ThenBy(world => GetDisplayScoreRank(world, lens))
            .ThenBy(world => GetScore(world, lens).Rank == 0 ? int.MaxValue : GetScore(world, lens).Rank);
    }

    private static int GetCoverageSortValue(
        DetailedShoppingPlan plan,
        IReadOnlyDictionary<int, MarketItemAnalysis> analysisByItemId)
    {
        if (!analysisByItemId.TryGetValue(plan.ItemId, out var analysis))
        {
            return plan.HasSufficientStock ? 0 : 1;
        }

        return analysis.Worlds.Count(world => GetDisplayCoverageBucket(world) == MarketCoverageBucket.Full) * -100
            - analysis.Worlds.Count(world => GetDisplayCoverageBucket(world) == MarketCoverageBucket.PartialDeep) * 10
            - analysis.Worlds.Count(world => GetDisplayCoverageBucket(world) == MarketCoverageBucket.PartialThin);
    }

    private static int GetWorldCoverageSortValue(WorldMarketAnalysis world)
    {
        var bucketRank = GetDisplayCoverageBucket(world) switch
        {
            MarketCoverageBucket.Full => 0,
            MarketCoverageBucket.PartialDeep => 1,
            MarketCoverageBucket.PartialThin => 2,
            _ => 3
        };
        return bucketRank * 1_000_000 - Math.Min(GetCoverageQuantity(world), world.QuantityNeeded);
    }

    private static IOrderedEnumerable<WorldMarketAnalysis> OrderWorldsByPriceSignalQuantity(
        IEnumerable<WorldMarketAnalysis> worlds,
        bool descending)
    {
        return descending
            ? worlds
                .OrderBy(world => !HasProcurementEvidence(world))
                .ThenByDescending(GetProcurementEvidenceQuantity)
                .ThenBy(world => GetWorldUnitPriceSortValue(world))
            : worlds
                .OrderBy(world => !HasProcurementEvidence(world))
                .ThenBy(world => GetProcurementEvidenceQuantity(world) == 0 ? int.MaxValue : GetProcurementEvidenceQuantity(world))
                .ThenBy(world => GetWorldUnitPriceSortValue(world));
    }

    private static IOrderedEnumerable<WorldMarketAnalysis> OrderWorldsByUnitPrice(
        IEnumerable<WorldMarketAnalysis> worlds,
        bool descending)
    {
        return descending
            ? worlds
                .OrderBy(world => !HasProcurementEvidence(world))
                .ThenByDescending(world => GetWorldUnitPriceSortValue(world))
                .ThenByDescending(GetProcurementEvidenceQuantity)
            : worlds
                .OrderBy(world => !HasProcurementEvidence(world))
                .ThenBy(world => GetWorldUnitPriceSortValue(world))
                .ThenByDescending(GetProcurementEvidenceQuantity);
    }

    private static decimal GetWorldUnitPriceSortValue(WorldMarketAnalysis world)
    {
        var price = GetComparableUnitPrice(world);
        return price > 0 ? price : decimal.MaxValue;
    }

    private static decimal GetCompetitiveValueSortValue(WorldMarketAnalysis world)
    {
        var (referencePrice, _) = GetCompetitiveValueReference(world);
        var comparablePrice = GetComparableUnitPrice(world);
        return referencePrice > 0 && comparablePrice > 0
            ? (comparablePrice - referencePrice) / referencePrice
            : decimal.MaxValue;
    }

    private static int GetBestWorldRank(
        DetailedShoppingPlan plan,
        IReadOnlyDictionary<int, MarketItemAnalysis> analysisByItemId,
        MarketAcquisitionLens lens)
    {
        return analysisByItemId.TryGetValue(plan.ItemId, out var analysis)
            ? analysis.Worlds
                .Select(world => world.Scores.FirstOrDefault(score => score.Lens == lens)?.Rank ?? int.MaxValue)
                .DefaultIfEmpty(int.MaxValue)
                .Min()
            : int.MaxValue;
    }

    public static int GetWorldCount(DetailedShoppingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.RequiresSplitPurchase && plan.RecommendedSplit?.Any() == true)
        {
            return plan.RecommendedSplit.Select(split => split.WorldName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        }

        return plan.RecommendedWorld == null ? 0 : 1;
    }

    private static bool IsVendorPlan(DetailedShoppingPlan plan)
    {
        return plan.RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName || plan.Vendors.Any();
    }

    private static List<string> GetCompetitivenessListingClasses(AnalyzedMarketListing listing)
    {
        var classes = new List<string>();
        if (listing.IsInPriceSignalBand || listing.IsInPrimaryUsableBand || IsCompetitive(listing))
        {
            classes.Add("is-competitive");
        }

        if (listing.PriceSanity == MarketListingPriceSanity.Insane)
        {
            classes.Add("is-insane");
        }
        else if (listing.PriceSanity == MarketListingPriceSanity.Outlier)
        {
            classes.Add("is-outlier");
        }
        else if (listing.PriceSanity == MarketListingPriceSanity.LowOutlier)
        {
            classes.Add("is-low-outlier");
        }

        if (IsUncompetitive(listing))
        {
            classes.Add("is-uncompetitive");
        }

        return classes;
    }

    private static bool IsCompetitive(AnalyzedMarketListing listing)
    {
        return listing.Competitiveness switch
        {
            MarketListingCompetitiveness.Deal or MarketListingCompetitiveness.Competitive => true,
            MarketListingCompetitiveness.Unknown => false,
            _ => false
        };
    }

    private static bool IsUncompetitive(AnalyzedMarketListing listing)
    {
        return listing.Competitiveness switch
        {
            MarketListingCompetitiveness.Fair or MarketListingCompetitiveness.Uncompetitive => true,
            MarketListingCompetitiveness.Unknown => false,
            _ => false
        };
    }

    private static string GetListingPriceBandToneClass(
        WorldMarketAnalysis world,
        AnalyzedMarketListing listing)
    {
        if (listing.PriceSanity == MarketListingPriceSanity.LowOutlier)
        {
            return "ma-band-tone-low";
        }

        if (listing.PriceSanity is MarketListingPriceSanity.Insane or MarketListingPriceSanity.Outlier ||
            IsUncompetitive(listing))
        {
            return "ma-band-tone-high";
        }

        var band = GetContainingPriceBand(world, listing);
        if (band != null &&
            band.Competitiveness == PriceBandCompetitiveness.LowOutlier)
        {
            return "ma-band-tone-low";
        }

        if (band != null &&
            band.Competitiveness is PriceBandCompetitiveness.Uncompetitive or PriceBandCompetitiveness.Insane)
        {
            return "ma-band-tone-high";
        }

        return "ma-band-tone-mid";
    }

    private static string GetListingPriceBandEdgeClass(
        WorldMarketAnalysis world,
        AnalyzedMarketListing listing)
    {
        return GetListingPriceBandSignal(world, listing) switch
        {
            ListingPriceBandSignal.LowOutlier => "ma-band-edge-low-outlier",
            ListingPriceBandSignal.Thin => "ma-band-edge-thin",
            ListingPriceBandSignal.Competitive => "ma-band-edge-competitive",
            ListingPriceBandSignal.Uncompetitive => "ma-band-edge-uncompetitive",
            ListingPriceBandSignal.Insane => "ma-band-edge-insane",
            _ => "ma-band-edge-unknown"
        };
    }

    private static ListingPriceBandSignal GetListingPriceBandSignal(
        WorldMarketAnalysis world,
        AnalyzedMarketListing listing)
    {
        if (listing.PriceSanity == MarketListingPriceSanity.LowOutlier)
        {
            return ListingPriceBandSignal.LowOutlier;
        }

        if (listing.PriceSanity is MarketListingPriceSanity.Insane or MarketListingPriceSanity.Outlier)
        {
            return ListingPriceBandSignal.Insane;
        }

        var band = GetContainingPriceBand(world, listing);
        if (band?.Depth == PriceBandDepth.Thin)
        {
            return ListingPriceBandSignal.Thin;
        }

        if (band?.IsPriceSignalBand == true ||
            band?.IsPrimaryUsableBand == true ||
            band?.Competitiveness == PriceBandCompetitiveness.Competitive)
        {
            return ListingPriceBandSignal.Competitive;
        }

        if (band?.Competitiveness == PriceBandCompetitiveness.Insane)
        {
            return ListingPriceBandSignal.Insane;
        }

        if (band?.Competitiveness == PriceBandCompetitiveness.Uncompetitive ||
            IsUncompetitive(listing))
        {
            return ListingPriceBandSignal.Uncompetitive;
        }

        if (IsCompetitive(listing))
        {
            return ListingPriceBandSignal.Competitive;
        }

        return ListingPriceBandSignal.Unknown;
    }

    private static MarketPriceBand? GetContainingPriceBand(
        WorldMarketAnalysis world,
        AnalyzedMarketListing listing)
    {
        return world.PriceBands.FirstOrDefault(band =>
            listing.SortIndex >= band.FirstListingIndex &&
            listing.SortIndex <= band.LastListingIndex);
    }

    private static bool HasScopePriceContext(WorldMarketAnalysis world)
    {
        return world.ScopeSaneQuantity > 0 ||
            world.PrimaryUsableQuantity > 0 ||
            world.PriceSignalQuantity > 0 ||
            world.ScopeInsaneQuantity > 0 ||
            world.PrimaryUsableAverageUnitPrice > 0 ||
            world.PriceSignalAverageUnitPrice > 0;
    }

    private static int GetSaneQuantity(WorldMarketAnalysis world)
    {
        return HasScopePriceContext(world)
            ? world.ScopeSaneQuantity
            : world.TotalSaneQuantity;
    }

    private static int GetCoverageQuantity(WorldMarketAnalysis world)
    {
        return GetProcurementEvidenceQuantity(world);
    }

    private static bool HasProcurementEvidence(WorldMarketAnalysis world)
    {
        var evidence = GetProcurementEvidence(world);
        return evidence.Quantity > 0 && evidence.UnitPrice > 0;
    }

    private static int GetProcurementEvidenceQuantity(WorldMarketAnalysis world)
    {
        return GetProcurementEvidence(world).Quantity;
    }

    private static decimal GetProcurementEvidenceUnitPrice(WorldMarketAnalysis world)
    {
        return GetProcurementEvidence(world).UnitPrice;
    }

    private static decimal GetComparableUnitPrice(WorldMarketAnalysis world)
    {
        if (world.ComparableQuantity > 0 && world.ComparableAverageUnitPrice > 0)
        {
            return world.ComparableAverageUnitPrice;
        }

        if (world.Listings.Count > 0)
        {
            var eligibleListings = MarketProcurementEvidencePolicy.GetEligibleListings(world);
            var comparableListings = world.ReferencePriceCredibility is
                MarketPriceRegionCredibility.Credible or MarketPriceRegionCredibility.Strong
                ? eligibleListings.Where(MarketProcurementEvidencePolicy.IsComparableListing).ToList()
                : eligibleListings;
            var comparableQuantity = comparableListings.Sum(listing => listing.Quantity);
            if (comparableQuantity > 0)
            {
                return comparableListings.Sum(listing => (decimal)listing.PricePerUnit * listing.Quantity) /
                    comparableQuantity;
            }

            return 0;
        }

        // Persisted analyses from before comparable pricing existed have no listing
        // projection to rebuild. Preserve their former display until refreshed.
        return GetProcurementEvidenceUnitPrice(world);
    }

    private static ProcurementEvidence GetProcurementEvidence(WorldMarketAnalysis world)
    {
        if (world.ActionableQuantity > 0 && world.ActionableAverageUnitPrice > 0)
        {
            return new ProcurementEvidence(world.ActionableQuantity, world.ActionableAverageUnitPrice);
        }

        if (world.Listings.Count > 0)
        {
            var eligibleListings = MarketProcurementEvidencePolicy.GetEligibleListings(world);
            var availableQuantity = eligibleListings.Sum(listing => listing.Quantity);
            var quotedQuantity = 0;
            long totalCost = 0;
            foreach (var listing in eligibleListings)
            {
                quotedQuantity += listing.Quantity;
                totalCost += listing.PricePerUnit * listing.Quantity;
                if (quotedQuantity >= world.QuantityNeeded)
                {
                    break;
                }
            }

            return new ProcurementEvidence(
                availableQuantity,
                quotedQuantity > 0 ? totalCost / (decimal)quotedQuantity : 0);
        }

        var legacyQuantity = GetSaneQuantity(world);
        if (legacyQuantity <= 0)
        {
            return new ProcurementEvidence(0, 0);
        }

        if (world.PriceSignalAverageUnitPrice > 0)
        {
            return new ProcurementEvidence(legacyQuantity, world.PriceSignalAverageUnitPrice);
        }

        if (world.PrimaryUsableAverageUnitPrice > 0)
        {
            return new ProcurementEvidence(legacyQuantity, world.PrimaryUsableAverageUnitPrice);
        }

        return new ProcurementEvidence(legacyQuantity, world.AnalysisCompetitiveAverageUnitPrice);
    }

    private static PriceBandDepth GetProcurementEvidenceDepth(WorldMarketAnalysis world)
    {
        var quantity = GetProcurementEvidenceQuantity(world);
        if (quantity <= 0 || world.QuantityNeeded <= 0)
        {
            return PriceBandDepth.None;
        }

        if (quantity >= Math.Max(world.QuantityNeeded / 2, 1))
        {
            return PriceBandDepth.Deep;
        }

        return quantity >= Math.Max(world.QuantityNeeded / 4, 1)
            ? PriceBandDepth.Usable
            : PriceBandDepth.Thin;
    }

    private static int GetDisplayScoreRank(WorldMarketAnalysis world, MarketAcquisitionLens lens)
    {
        return GetDisplayScoreBucket(world, lens) switch
        {
            MarketScoreBucket.Optimal => 0,
            MarketScoreBucket.Competitive => 1,
            MarketScoreBucket.Expensive => 2,
            MarketScoreBucket.PoorFit => 3,
            _ => 4
        };
    }

    private static (decimal Price, string Label) GetCompetitiveValueReference(WorldMarketAnalysis world)
    {
        if (world.ReferenceSupportScore > 0 &&
            world.ReferencePriceCredibility is
                MarketPriceRegionCredibility.Thin or MarketPriceRegionCredibility.Unknown)
        {
            return (0, "uncertain regional reference");
        }

        if (world.AnalysisCompetitiveAverageUnitPrice > 0)
        {
            return (world.AnalysisCompetitiveAverageUnitPrice, "best available average");
        }

        if (world.PrimaryUsableAverageUnitPrice > 0)
        {
            return (world.PrimaryUsableAverageUnitPrice, "best listings average");
        }

        if (world.AnalysisScopeBaselineUnitPrice > 0)
        {
            return (world.AnalysisScopeBaselineUnitPrice, "typical price");
        }

        return (0, "best listings average");
    }

    private readonly record struct ProcurementEvidence(int Quantity, decimal UnitPrice);

    private static decimal? GetBandBreakBefore(WorldMarketAnalysis world, AnalyzedMarketListing listing)
    {
        var firstBreak = world.PriceBands
            .Where(band => band.NextBreakPercent >= 10)
            .OrderBy(band => band.LastListingIndex)
            .FirstOrDefault();

        return firstBreak != null && listing.SortIndex == firstBreak.LastListingIndex + 1
            ? firstBreak.NextBreakPercent
            : null;
    }

    private static bool CrossesThresholdBefore(
        WorldMarketAnalysis world,
        AnalyzedMarketListing listing,
        decimal threshold)
    {
        if (threshold <= 0 || listing.PricePerUnit <= threshold)
        {
            return false;
        }

        return world.Listings
            .Where(candidate => candidate.SortIndex < listing.SortIndex)
            .OrderBy(candidate => candidate.SortIndex)
            .All(candidate => candidate.PricePerUnit <= threshold);
    }
}

public sealed record MarketListingDivider(string Label);

internal enum ListingPriceBandSignal
{
    Unknown,
    LowOutlier,
    Thin,
    Competitive,
    Uncompetitive,
    Insane
}
