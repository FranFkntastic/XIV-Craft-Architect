using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Core.Models;

public sealed class ProcurementRouteExecutionRequest
{
    public CraftingPlan? Plan { get; init; }

    public IReadOnlyList<MaterialAggregate> ActiveProcurementItems { get; init; } = Array.Empty<MaterialAggregate>();

    public IReadOnlyList<DetailedShoppingPlan> SourceShoppingPlans { get; init; } = Array.Empty<DetailedShoppingPlan>();

    public IReadOnlyList<MarketItemAnalysis> SourceMarketAnalyses { get; init; } = Array.Empty<MarketItemAnalysis>();

    public MarketFetchScope Scope { get; init; } = MarketFetchScope.SelectedDataCenter;

    public string SelectedDataCenter { get; init; } = string.Empty;

    public string SelectedRegion { get; init; } = string.Empty;

    public MarketAcquisitionLens Lens { get; init; } = MarketAcquisitionLens.MinimumUpfrontCost;

    public MarketAnalysisConfig ProcurementConfig { get; init; } = new();

    public bool IncludeSplitPurchases { get; init; }

    public IReadOnlySet<MarketWorldKey> BlacklistedWorlds { get; init; } = new HashSet<MarketWorldKey>();

    public IReadOnlySet<MarketItemWorldKey> ExcludedItemWorlds { get; init; } = new HashSet<MarketItemWorldKey>();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectedWorldsByDataCenter { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public MarketEvidenceReconciliationPolicy ReconciliationPolicy { get; init; } = new();
}

public sealed record ProcurementRouteExecutionResult(
    List<DetailedShoppingPlan> ShoppingPlans,
    List<DetailedShoppingPlan> EvidencePlans,
    List<DetailedShoppingPlan> ReusableEvidence,
    List<DetailedShoppingPlan> RefreshedEvidence,
    IReadOnlyList<MaterialAggregate> MissingItems,
    MarketRouteDecision? RouteDecision = null,
    IReadOnlyList<MarketEvidenceReconciliationItemResult>? ReconciliationItems = null,
    CraftingPlan? OptimizedPlan = null,
    IReadOnlyList<MaterialAggregate>? ActiveProcurementItems = null);

public sealed record ProcurementRouteOptimizationResult(
    List<DetailedShoppingPlan> ShoppingPlans,
    MarketRouteDecision? Decision);

public sealed record MarketRouteDecision(
    int TravelTolerance,
    decimal? MaximumPremiumRate,
    long CheapestGilCost,
    long SelectedGilCost,
    long SelectedEvidencePenalty,
    int CheapestWorldStops,
    int SelectedWorldStops,
    int CheapestDataCenterTransfers,
    int SelectedDataCenterTransfers,
    bool StartsFromHomeDataCenter,
    string? HomeDataCenter,
    MarketTravelPriority TravelPriority = MarketTravelPriority.DataCenterTransfersFirst,
    IReadOnlyList<MarketRouteFrontierOption>? FrontierOptions = null,
    IReadOnlyList<MarketRouteItemDecision>? ItemDecisions = null)
{
    public long FixedAcquisitionGilCost { get; init; }

    public long PremiumGil => Math.Max(0, SelectedGilCost - CheapestGilCost);

    public decimal PremiumRate => CheapestGilCost > 0
        ? PremiumGil / (decimal)CheapestGilCost
        : 0;

    public int WorldStopsAvoided => Math.Max(0, CheapestWorldStops - SelectedWorldStops);

    public int DataCenterTransfersAvoided => Math.Max(0, CheapestDataCenterTransfers - SelectedDataCenterTransfers);

    public IReadOnlyList<MarketRouteFrontierOption> RepresentativeRoutes =>
        FrontierOptions ?? Array.Empty<MarketRouteFrontierOption>();

    public IReadOnlyList<MarketRouteItemDecision> ItemPremiums =>
        ItemDecisions ?? Array.Empty<MarketRouteItemDecision>();
}

public sealed record MarketRouteFrontierOption(
    int MinimumTolerance,
    int MaximumTolerance,
    long GilCost,
    int WorldStops,
    int DataCenterTransfers)
{
    public int RepresentativeTolerance => (MinimumTolerance + MaximumTolerance) / 2;
}

public sealed record MarketRouteItemDecision(
    int ItemId,
    string ItemName,
    long CheapestEligibleGilCost,
    long SelectedGilCost)
{
    public long ConsolidationPremiumGil => Math.Max(0, SelectedGilCost - CheapestEligibleGilCost);

    public decimal ConsolidationPremiumRate => CheapestEligibleGilCost > 0
        ? ConsolidationPremiumGil / (decimal)CheapestEligibleGilCost
        : 0;
}
