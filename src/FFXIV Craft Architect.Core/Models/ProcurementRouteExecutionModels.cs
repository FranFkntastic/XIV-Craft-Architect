using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Core.Models;

public sealed class ProcurementRouteExecutionRequest
{
    public CraftingPlan? Plan { get; init; }

    public IReadOnlyList<DetailedShoppingPlan> SourceShoppingPlans { get; init; } = Array.Empty<DetailedShoppingPlan>();

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
}

public sealed record ProcurementRouteExecutionResult(
    List<DetailedShoppingPlan> ShoppingPlans,
    List<DetailedShoppingPlan> EvidencePlans,
    List<DetailedShoppingPlan> ReusableEvidence,
    List<DetailedShoppingPlan> RefreshedEvidence,
    IReadOnlyList<MaterialAggregate> MissingItems);
