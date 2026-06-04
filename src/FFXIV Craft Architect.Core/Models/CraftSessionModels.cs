namespace FFXIV_Craft_Architect.Core.Models;

[Flags]
public enum CraftSessionChangeScope
{
    None = 0,
    PlanCore = 1 << 0,
    PlanDecision = 1 << 1,
    MarketAnalysis = 1 << 2,
    ProcurementOverlay = 1 << 3,
    SettingsContext = 1 << 4,
    ViewState = 1 << 5
}

public enum CraftSessionDirtyBucket
{
    PlanCore,
    MarketAnalysis,
    Procurement,
    SettingsContext,
    ViewState
}

public enum CraftEvidenceOwner
{
    Unknown,
    PlanNodePrice,
    MarketAnalysis,
    ProcurementOverlay,
    UnavailableMarketItem,
    RawMarketCache
}

public enum CraftSettingsKey
{
    Unknown,
    Region,
    DefaultDataCenter,
    DefaultMarketFetchScope,
    RecommendationMode,
    MarketAcquisitionLens,
    HomeWorld,
    IncludeCrossWorld,
    ExcludeCongestedWorlds,
    ParallelApiRequests,
    WarmCacheForCraftedItems,
    MarketCacheTtlHours
}

public enum CraftOperationWorkflow
{
    Unknown,
    RecipeBuild,
    PlanActivation,
    PlanEdit,
    PriceRefresh,
    MarketAnalysis,
    ProcurementAnalysis,
    ItemMarketRefresh,
    StartupRestore
}

public sealed record CraftSessionIdentity(
    Guid SessionId,
    string Name,
    string? SourcePlanId = null,
    string? SourcePlanName = null,
    string? SourceFilePath = null)
{
    public static CraftSessionIdentity CreateNew(string name = "New Plan") =>
        new(Guid.NewGuid(), name);
}

public sealed class CraftSessionVersions
{
    public long PlanCore { get; internal set; }
    public long PlanDecision { get; internal set; }
    public long PlanPrice { get; internal set; }
    public long MarketAnalysis { get; internal set; }
    public long Procurement { get; internal set; }
    public long SettingsContext { get; internal set; }
    public long ViewState { get; internal set; }

    public CraftSessionVersionStamp Capture(long planSessionVersion) =>
        new(planSessionVersion, PlanCore, PlanDecision, PlanPrice, MarketAnalysis, Procurement, SettingsContext, ViewState);

    public CraftSessionVersions Clone() =>
        new()
        {
            PlanCore = PlanCore,
            PlanDecision = PlanDecision,
            PlanPrice = PlanPrice,
            MarketAnalysis = MarketAnalysis,
            Procurement = Procurement,
            SettingsContext = SettingsContext,
            ViewState = ViewState
        };
}

public readonly record struct CraftSessionVersionStamp(
    long PlanSession,
    long PlanCore,
    long PlanDecision,
    long PlanPrice,
    long MarketAnalysis,
    long Procurement,
    long SettingsContext,
    long ViewState);

public sealed record CraftSessionChange(
    CraftSessionChangeScope Scope,
    IReadOnlySet<CraftSessionDirtyBucket> DirtyBuckets,
    bool InvalidatesProcurementOverlay,
    string Reason);

public sealed class CraftSessionViewState
{
    public int? SelectedMarketItemId { get; set; }
    public HashSet<string> ExpandedMarketWorlds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? MarketSortKey { get; set; }
    public bool SortDescending { get; set; }

    public CraftSessionViewState Clone()
    {
        var clone = new CraftSessionViewState
        {
            SelectedMarketItemId = SelectedMarketItemId,
            MarketSortKey = MarketSortKey,
            SortDescending = SortDescending
        };

        foreach (var world in ExpandedMarketWorlds)
        {
            clone.ExpandedMarketWorlds.Add(world);
        }

        return clone;
    }
}

public sealed record CraftSessionActiveContext(
    string? Region,
    string? DataCenter,
    string? World,
    MarketFetchScope? MarketFetchScope);

public sealed record CraftSessionMarketEvidence(
    IReadOnlyList<MarketItemAnalysis> ItemAnalyses,
    IReadOnlySet<int> UnavailableMarketItemIds,
    IReadOnlyList<DetailedShoppingPlan>? ShoppingPlans = null,
    CraftSessionVersionStamp? PublishedAgainstVersion = null,
    RecommendationMode RecommendationMode = RecommendationMode.MinimizeTotalCost,
    MarketAcquisitionLens Lens = MarketAcquisitionLens.MinimumUpfrontCost,
    StoredRecipeOperationSnapshot? RecipeBasis = null)
{
    public static CraftSessionMarketEvidence Empty { get; } =
        new(Array.Empty<MarketItemAnalysis>(), new HashSet<int>(), Array.Empty<DetailedShoppingPlan>(), null);
}

public sealed record CraftSessionProcurementOverlay(
    DateTime PublishedAtUtc,
    IReadOnlyList<int> ActiveItemIds,
    string SourceDescription,
    IReadOnlyList<DetailedShoppingPlan>? ShoppingPlans = null,
    IReadOnlyList<WorldProcurementCardModel>? RouteCards = null);

public static class CraftSettingsKeyMap
{
    private static readonly Dictionary<string, CraftSettingsKey> StorageKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["market.region"] = CraftSettingsKey.Region,
        ["market.default_datacenter"] = CraftSettingsKey.DefaultDataCenter,
        ["market.default_data_center"] = CraftSettingsKey.DefaultDataCenter,
        ["market.default_search_scope"] = CraftSettingsKey.DefaultMarketFetchScope,
        ["market.recommendation_mode"] = CraftSettingsKey.RecommendationMode,
        ["market.acquisition_lens"] = CraftSettingsKey.MarketAcquisitionLens,
        ["market.home_world"] = CraftSettingsKey.HomeWorld,
        ["market.include_cross_world"] = CraftSettingsKey.IncludeCrossWorld,
        ["market.exclude_congested_worlds"] = CraftSettingsKey.ExcludeCongestedWorlds,
        ["market.parallel_api_requests"] = CraftSettingsKey.ParallelApiRequests,
        ["market.warm_cache_for_crafted_items"] = CraftSettingsKey.WarmCacheForCraftedItems,
        ["market.cache_ttl_hours"] = CraftSettingsKey.MarketCacheTtlHours,
        ["planning.default_recommendation_mode"] = CraftSettingsKey.RecommendationMode,
        ["Market.Region"] = CraftSettingsKey.Region,
        ["Market.DefaultDataCenter"] = CraftSettingsKey.DefaultDataCenter,
        ["Market.DefaultSearchScope"] = CraftSettingsKey.DefaultMarketFetchScope,
        ["Market.RecommendationMode"] = CraftSettingsKey.RecommendationMode,
        ["Market.AcquisitionLens"] = CraftSettingsKey.MarketAcquisitionLens,
        ["Market.HomeWorld"] = CraftSettingsKey.HomeWorld,
        ["Market.IncludeCrossWorld"] = CraftSettingsKey.IncludeCrossWorld,
        ["Market.ExcludeCongestedWorlds"] = CraftSettingsKey.ExcludeCongestedWorlds,
        ["Market.ParallelApiRequests"] = CraftSettingsKey.ParallelApiRequests,
        ["Market.WarmCacheForCraftedItems"] = CraftSettingsKey.WarmCacheForCraftedItems,
        ["Market.CacheTtlHours"] = CraftSettingsKey.MarketCacheTtlHours,
        ["Planning.DefaultRecommendationMode"] = CraftSettingsKey.RecommendationMode
    };

    public static CraftSettingsKey FromStorageKey(string storageKey) =>
        StorageKeys.TryGetValue(storageKey, out var key) ? key : CraftSettingsKey.Unknown;
}

public static class CraftEvidenceOwnership
{
    public static CraftSessionDirtyBucket? GetDirtyBucket(CraftEvidenceOwner owner) =>
        owner switch
        {
            CraftEvidenceOwner.PlanNodePrice => CraftSessionDirtyBucket.PlanCore,
            CraftEvidenceOwner.MarketAnalysis => CraftSessionDirtyBucket.MarketAnalysis,
            CraftEvidenceOwner.ProcurementOverlay => CraftSessionDirtyBucket.Procurement,
            CraftEvidenceOwner.UnavailableMarketItem => CraftSessionDirtyBucket.MarketAnalysis,
            CraftEvidenceOwner.RawMarketCache => null,
            _ => null
        };
}

public sealed record CraftOperationSnapshot(
    Guid? CurrentOperationId,
    CraftOperationWorkflow Workflow,
    string OperationName,
    string StatusMessage,
    int ProgressPercent,
    bool IsBusy,
    bool IsCancellationRequested);
