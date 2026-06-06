namespace FFXIV_Craft_Architect.Core.Models;

public sealed class CoreStoredPlanSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Plan";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
    public string DataCenter { get; set; } = "Aether";
    public List<CoreStoredProjectItem> ProjectItems { get; set; } = new();
    public string? PlanJson { get; set; }
    public string? MarketPlansJson { get; set; }
    public string? MarketIntelligenceJson { get; set; }
    public Guid? ActiveMarketIntelligencePublicationId { get; set; }
    public string? MarketIntelligenceSummaryJson { get; set; }
    public string? MarketItemAnalysesJson { get; set; }
    public string? MarketAnalysisRecipeBasisJson { get; set; }
    public HashSet<int> UnavailableMarketItemIds { get; set; } = new();
    public RecommendationMode SavedRecommendationMode { get; set; } = RecommendationMode.MinimizeTotalCost;
    public MarketAcquisitionLens SavedMarketAnalysisLens { get; set; } = MarketAcquisitionLens.MinimumUpfrontCost;
    public string? SourcePlanId { get; set; }
    public string? SourcePlanName { get; set; }
}

public sealed class CoreStoredProjectItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    public int Quantity { get; set; }
    public bool MustBeHq { get; set; }
}

public sealed class CoreStoredPlanSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime ModifiedAt { get; set; }
    public DateTime SavedAt { get; set; }
    public string DataCenter { get; set; } = string.Empty;
    public int ProjectItemCount { get; set; }
}

public sealed record CorePlanSessionLoadResult(
    CoreStoredPlanSnapshot StoredPlan,
    CraftingPlan? Plan,
    IReadOnlyList<ProjectItem> ProjectItems,
    IReadOnlyList<MarketItemAnalysis> MarketItemAnalyses,
    IReadOnlyList<DetailedShoppingPlan> ShoppingPlans,
    IReadOnlySet<int> UnavailableMarketItemIds,
    MarketIntelligence? MarketIntelligence,
    StoredRecipeOperationSnapshot? MarketAnalysisRecipeBasis,
    bool CanLoad,
    string? Warning);
