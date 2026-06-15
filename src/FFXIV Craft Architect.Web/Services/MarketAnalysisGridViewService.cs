using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

namespace FFXIV_Craft_Architect.Web.Services;

public static class MarketAnalysisGridViewService
{
    public const string CalculatedTotalHeaderTooltip = "Calculated Total is the gil cost computed for the needed quantity from the current recommendation. Market totals use loaded listing evidence, split totals add the recommended route, and vendor totals use loaded gil vendor prices.";
    public const string UnsupportedProjectedCostTooltip = "Calculated Total is projected because the current search scope cannot support this purchase. It scales from available market evidence or data-center average pricing, so it is highlighted as unsupported.";

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

        var estimate = MarketPurchaseCostProjectionService.Estimate(
            plan,
            plan.QuantityNeeded,
            hqOnly: false);
        return estimate.HasCost ? (long)estimate.Cost : 0;
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
            : "ma-total-value";
    }

    public static string GetTotalCostTooltip(DetailedShoppingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (IsUnsupportedProjectedCost(plan))
        {
            return UnsupportedProjectedCostTooltip;
        }

        if (plan.RecommendedSplit?.Any() == true)
        {
            var splitQuantity = plan.RecommendedSplit.Sum(split => split.QuantityToBuy);
            return $"Calculated Total is the sum of the recommended split purchase: {splitQuantity:N0}/{plan.QuantityNeeded:N0} items across {plan.RecommendedSplit.Count:N0} worlds.";
        }

        if (plan.RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName || plan.Vendors.Any())
        {
            return $"Calculated Total uses loaded gil vendor pricing for {plan.QuantityNeeded:N0} needed.";
        }

        if (plan.RecommendedWorld != null)
        {
            return $"Calculated Total uses the recommended world's market listings for {plan.RecommendedWorld.TotalQuantityPurchased:N0}/{plan.QuantityNeeded:N0} needed.";
        }

        return "Calculated Total is the computed gil cost for the needed quantity. Run Market Analysis again if this row lacks current recommendation evidence.";
    }

    public static string FormatWorldPriceSummary(WorldMarketAnalysis world, MarketAcquisitionLens lens)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (HasScopePriceContext(world))
        {
            return world.ScopeCompetitiveAverageUnitPrice > 0
                ? $"{world.ScopeCompetitiveQuantity:N0} competitive at ~{world.ScopeCompetitiveAverageUnitPrice:N0}g"
                : $"{world.ScopeCompetitiveQuantity:N0} competitive";
        }

        var lensSummary = world.Scores.FirstOrDefault(score => score.Lens == lens)?.Summary
            ?? world.Scores.FirstOrDefault()?.Summary;
        if (!string.IsNullOrWhiteSpace(lensSummary))
        {
            return lensSummary;
        }

        var competitiveBand = world.PriceBands.FirstOrDefault(band => band.IsCompetitiveShelf);
        if (competitiveBand != null)
        {
            return $"{competitiveBand.Quantity:N0} competitive at ~{competitiveBand.WeightedAverageUnitPrice:N0}g";
        }

        var firstListing = world.Listings.OrderBy(listing => listing.SortIndex).FirstOrDefault();
        return firstListing != null
            ? $"{firstListing.Quantity:N0} listed at {firstListing.PricePerUnit:N0}g"
            : "No listings";
    }

    public static string FormatWorldPriceBandRole(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var bestBand = GetBestSortPriceBand(world);
        if (bestBand == null)
        {
            return "unavailable";
        }

        return GetPriceBandRole(world, bestBand) switch
        {
            ListingPriceBandRole.Representative => "included evidence",
            ListingPriceBandRole.Thin => "thin",
            ListingPriceBandRole.LowOutlier => "low outlier",
            ListingPriceBandRole.ExpensiveTail => "expensive tail",
            _ => "other band"
        };
    }

    public static string GetWorldPriceBandScoreClass(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var bestBand = GetBestSortPriceBand(world);
        if (bestBand == null)
        {
            return "is-unavailable";
        }

        return GetPriceBandRole(world, bestBand) switch
        {
            ListingPriceBandRole.Representative => "is-optimal",
            ListingPriceBandRole.Thin => "is-competitive",
            ListingPriceBandRole.LowOutlier => "is-competitive",
            ListingPriceBandRole.ExpensiveTail => "is-expensive",
            _ => "is-unavailable"
        };
    }

    public static string FormatWorldPriceBandSummary(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var bestBand = GetBestSortPriceBand(world);
        return bestBand != null
            ? $"{bestBand.Quantity:N0} in band at ~{bestBand.WeightedAverageUnitPrice:N0}g"
            : "No price bands";
    }

    public static string FormatAnalysisScopePriceSummary(MarketItemAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        if (analysis.PriceEvaluation is { } evaluation &&
            evaluation.Thresholds.InsaneFloorUnitPrice > 0)
        {
            return $"good avg ~{analysis.AnalysisScopeCompetitiveAverageUnitPrice:N0}g; avg ~{evaluation.CentralRegion.WeightedAverageUnitPrice:N0}g; competitive <= {evaluation.Thresholds.CompetitiveCeilingUnitPrice:N0}g; insane >= {evaluation.Thresholds.InsaneFloorUnitPrice:N0}g";
        }

        return analysis.SaneThresholdUnitPrice > 0
            ? $"good avg ~{analysis.AnalysisScopeCompetitiveAverageUnitPrice:N0}g; base ~{analysis.AnalysisScopeBaselineUnitPrice:N0}g; avg ~{analysis.AnalysisScopeAverageUnitPrice:N0}g; competitive <= {analysis.CompetitiveThresholdUnitPrice:N0}g; insane >= {analysis.SaneThresholdUnitPrice:N0}g"
            : string.Empty;
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
        MarketAnalysisEvidenceOverlay evidenceOverlay = MarketAnalysisEvidenceOverlay.ShelfOverlay)
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

        return $"{Math.Min(GetCoverageQuantity(world), world.QuantityNeeded):N0}/{world.QuantityNeeded:N0}";
    }

    public static string FormatCoverageBucket(MarketCoverageBucket bucket)
    {
        return bucket switch
        {
            MarketCoverageBucket.Full => "full",
            MarketCoverageBucket.PartialDeep => "partial deep",
            MarketCoverageBucket.PartialThin => "partial thin",
            _ => "none"
        };
    }

    public static string FormatCompetitiveStockDetail(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        return world.ScopeInsaneQuantity > 0
            ? $"{world.ScopeInsaneQuantity:N0} insane"
            : string.Empty;
    }

    public static string FormatCompetitiveValue(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var (referencePrice, referenceLabel) = GetCompetitiveValueReference(world);
        if (referencePrice <= 0 || world.ScopeCompetitiveAverageUnitPrice <= 0)
        {
            return "-";
        }

        var percent = (world.ScopeCompetitiveAverageUnitPrice - referencePrice) / referencePrice * 100m;
        var rounded = Math.Round(percent, 0, MidpointRounding.AwayFromZero);
        return $"{rounded:+0;-0;0}%";
    }

    public static string FormatCompetitiveValueTooltip(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var (referencePrice, referenceLabel) = GetCompetitiveValueReference(world);
        if (referencePrice <= 0 || world.ScopeCompetitiveAverageUnitPrice <= 0)
        {
            return "No competitive average is available for comparison.";
        }

        var percent = (world.ScopeCompetitiveAverageUnitPrice - referencePrice) / referencePrice * 100m;
        var rounded = Math.Round(Math.Abs(percent), 0, MidpointRounding.AwayFromZero);
        var relationship = percent < 0
            ? "less than"
            : percent > 0
                ? "greater than"
                : "equal to";

        return $"{world.WorldName}'s competitive average is {rounded:N0}% {relationship} the regional {referenceLabel} average: {world.ScopeCompetitiveAverageUnitPrice:N0}g vs {referencePrice:N0}g.";
    }

    public static IReadOnlyList<MarketListingDivider> GetListingDividersBefore(
        WorldMarketAnalysis world,
        AnalyzedMarketListing listing)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(listing);

        var dividers = new List<MarketListingDivider>();
        var shelfBreak = GetShelfBreakBefore(world, listing);
        if (shelfBreak.HasValue)
        {
            dividers.Add(new MarketListingDivider($"Price shelf +{shelfBreak.Value:N0}%"));
        }

        var goodAverage = world.AnalysisScopeCompetitiveAverageUnitPrice;
        if (CrossesThresholdBefore(world, listing, goodAverage))
        {
            dividers.Add(new MarketListingDivider("Above good avg"));
        }

        var average = world.AnalysisScopeAverageUnitPrice;
        if (average > 0 && average != goodAverage && CrossesThresholdBefore(world, listing, average))
        {
            dividers.Add(new MarketListingDivider("Above avg"));
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

        var classes = GetShelfListingClasses(listing);
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

        return GetListingPriceBandRole(world, listing) switch
        {
            ListingPriceBandRole.LowOutlier => "Low outlier price band: cheap shelf evidence is visible but does not contribute to representative market evidence.",
            ListingPriceBandRole.Thin => "Thin price band: low stock depth is visible but does not contribute to representative market evidence.",
            ListingPriceBandRole.Representative => "Representative price band: this listing contributes to the readable market evidence for ordinary pricing.",
            ListingPriceBandRole.ExpensiveTail => "Expensive tail price band: high-price evidence is visible but does not contribute to representative market evidence.",
            _ => "Price band role could not be classified from the loaded listing evidence."
        };
    }

    public static bool IsUncompetitiveListing(AnalyzedMarketListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return IsUncompetitive(listing);
    }

    public static int GetCompetitiveQuantity(WorldMarketAnalysis world)
    {
        ArgumentNullException.ThrowIfNull(world);

        return HasScopePriceContext(world)
            ? world.ScopeCompetitiveQuantity
            : world.CompetitiveQuantity;
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

        return GetCompetitiveQuantity(world) >= Math.Max(world.QuantityNeeded / 2, 1)
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

        if (world.QuantityNeeded <= 0 || world.ScopeCompetitiveQuantity >= world.QuantityNeeded)
        {
            return MarketScoreBucket.Optimal;
        }

        if (world.ScopeCompetitiveQuantity >= Math.Max(world.QuantityNeeded / 2, 1))
        {
            return MarketScoreBucket.Competitive;
        }

        return world.ScopeCompetitiveQuantity > 0
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
            WebTableSortRule<WorldMarketAnalysis, MarketAnalysisWorldGridSortColumn>.Create(
                MarketAnalysisWorldGridSortColumn.StockDepth,
                GetCompetitiveQuantity),
            WebTableSortRule<WorldMarketAnalysis, MarketAnalysisWorldGridSortColumn>.Create(
                MarketAnalysisWorldGridSortColumn.Coverage,
                GetWorldCoverageSortValue),
            WebTableSortRule<WorldMarketAnalysis, MarketAnalysisWorldGridSortColumn>.Create(
                MarketAnalysisWorldGridSortColumn.PriceValue,
                world => GetWorldPriceValueSortValue(world, lens, evidenceOverlay)),
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

    private static decimal GetWorldPriceValueSortValue(
        WorldMarketAnalysis world,
        MarketAcquisitionLens lens,
        MarketAnalysisEvidenceOverlay evidenceOverlay)
    {
        if (evidenceOverlay == MarketAnalysisEvidenceOverlay.PriceBandOverlay)
        {
            return GetWorldPriceBandSortValue(world);
        }

        var displayRank = GetDisplayScoreRank(world, lens);
        var rank = GetScore(world, lens).Rank;
        var lensRank = rank == 0 ? int.MaxValue : rank;
        return displayRank * 1_000_000 + lensRank;
    }

    private static decimal GetWorldPriceBandSortValue(WorldMarketAnalysis world)
    {
        var bestBand = GetBestSortPriceBand(world);

        if (bestBand == null)
        {
            return decimal.MaxValue;
        }

        return GetPriceBandSortRank(world, bestBand) * 1_000_000_000m
            - Math.Min(bestBand.Quantity, 999_999)
            + bestBand.WeightedAverageUnitPrice / 1_000_000m;
    }

    private static MarketPriceBand? GetBestSortPriceBand(WorldMarketAnalysis world)
    {
        return world.PriceBands
            .OrderBy(band => GetPriceBandSortRank(world, band))
            .ThenByDescending(band => band.Quantity)
            .ThenBy(band => band.WeightedAverageUnitPrice)
            .FirstOrDefault();
    }

    private static int GetPriceBandSortRank(WorldMarketAnalysis world, MarketPriceBand band)
    {
        if (band.IsCompetitiveShelf && IsCredibleCompetitiveBand(world, band))
        {
            return 0;
        }

        if (IsThinBand(world, band))
        {
            return 2;
        }

        return 1;
    }

    private static decimal GetCompetitiveValueSortValue(WorldMarketAnalysis world)
    {
        var (referencePrice, _) = GetCompetitiveValueReference(world);
        return referencePrice > 0 && world.ScopeCompetitiveAverageUnitPrice > 0
            ? (world.ScopeCompetitiveAverageUnitPrice - referencePrice) / referencePrice
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

    private static List<string> GetShelfListingClasses(AnalyzedMarketListing listing)
    {
        var classes = new List<string>();
        if (listing.IsInCompetitiveShelf || IsCompetitive(listing))
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
            MarketListingCompetitiveness.Unknown => listing.IsScopeCompetitive,
            _ => false
        };
    }

    private static bool IsUncompetitive(AnalyzedMarketListing listing)
    {
        return listing.Competitiveness switch
        {
            MarketListingCompetitiveness.Fair or MarketListingCompetitiveness.Uncompetitive => true,
            MarketListingCompetitiveness.Unknown => listing.IsScopeUncompetitive,
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
            world.AnalysisScopeCompetitiveAverageUnitPrice > 0 &&
            band.WeightedAverageUnitPrice < world.AnalysisScopeCompetitiveAverageUnitPrice * 0.8m)
        {
            return "ma-band-tone-low";
        }

        return "ma-band-tone-mid";
    }

    private static string GetListingPriceBandEdgeClass(
        WorldMarketAnalysis world,
        AnalyzedMarketListing listing)
    {
        return GetListingPriceBandRole(world, listing) switch
        {
            ListingPriceBandRole.LowOutlier => "ma-band-edge-low-outlier",
            ListingPriceBandRole.Thin => "ma-band-edge-thin",
            ListingPriceBandRole.Representative => "ma-band-edge-representative",
            ListingPriceBandRole.ExpensiveTail => "ma-band-edge-expensive-tail",
            _ => "ma-band-edge-unknown"
        };
    }

    private static ListingPriceBandRole GetListingPriceBandRole(
        WorldMarketAnalysis world,
        AnalyzedMarketListing listing)
    {
        if (listing.PriceSanity == MarketListingPriceSanity.LowOutlier)
        {
            return ListingPriceBandRole.LowOutlier;
        }

        if (listing.PriceSanity is MarketListingPriceSanity.Insane or MarketListingPriceSanity.Outlier ||
            IsUncompetitive(listing))
        {
            return ListingPriceBandRole.ExpensiveTail;
        }

        var band = GetContainingPriceBand(world, listing);
        if (band?.IsCompetitiveShelf == true && IsCredibleCompetitiveBand(world, band))
        {
            return ListingPriceBandRole.Representative;
        }

        if (band != null && IsThinBand(world, band))
        {
            return ListingPriceBandRole.Thin;
        }

        if (IsCompetitive(listing))
        {
            return ListingPriceBandRole.Representative;
        }

        return ListingPriceBandRole.Unknown;
    }

    private static ListingPriceBandRole GetPriceBandRole(WorldMarketAnalysis world, MarketPriceBand band)
    {
        if (band.IsCompetitiveShelf && IsCredibleCompetitiveBand(world, band))
        {
            return ListingPriceBandRole.Representative;
        }

        if (IsThinBand(world, band))
        {
            return ListingPriceBandRole.Thin;
        }

        return ListingPriceBandRole.Unknown;
    }

    private static MarketPriceBand? GetContainingPriceBand(
        WorldMarketAnalysis world,
        AnalyzedMarketListing listing)
    {
        return world.PriceBands.FirstOrDefault(band =>
            listing.SortIndex >= band.FirstListingIndex &&
            listing.SortIndex <= band.LastListingIndex);
    }

    private static bool IsThinBand(WorldMarketAnalysis world, MarketPriceBand band)
    {
        return !IsCredibleCompetitiveBand(world, band);
    }

    private static bool IsCredibleCompetitiveBand(WorldMarketAnalysis world, MarketPriceBand band)
    {
        return band.ListingCount >= 2 || band.Quantity >= Math.Max(world.QuantityNeeded / 4, 1);
    }

    private static bool HasScopePriceContext(WorldMarketAnalysis world)
    {
        return world.ScopeSaneQuantity > 0 ||
            world.ScopeCompetitiveQuantity > 0 ||
            world.ScopeInsaneQuantity > 0 ||
            world.ScopeCompetitiveAverageUnitPrice > 0;
    }

    private static int GetSaneQuantity(WorldMarketAnalysis world)
    {
        return HasScopePriceContext(world)
            ? world.ScopeSaneQuantity
            : world.TotalSaneQuantity;
    }

    private static int GetCoverageQuantity(WorldMarketAnalysis world)
    {
        return HasScopePriceContext(world)
            ? GetCompetitiveQuantity(world)
            : GetSaneQuantity(world);
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
        if (world.AnalysisScopeCompetitiveAverageUnitPrice > 0)
        {
            return (world.AnalysisScopeCompetitiveAverageUnitPrice, "good");
        }

        if (world.AnalysisScopeBaselineUnitPrice > 0)
        {
            return (world.AnalysisScopeBaselineUnitPrice, "base");
        }

        return (0, "good");
    }

    private static decimal? GetShelfBreakBefore(WorldMarketAnalysis world, AnalyzedMarketListing listing)
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

internal enum ListingPriceBandRole
{
    Unknown,
    LowOutlier,
    Thin,
    Representative,
    ExpensiveTail
}
