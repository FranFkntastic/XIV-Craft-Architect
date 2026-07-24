using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public static class WorkerSessionProtocol
{
    public const string ContractVersion = "1";
    public const string CommandMessageKind = "session-command";
    public const string ResultMessageKind = "session-result";
}

public sealed record WorkerSessionCommandEnvelope(
    string ContractVersion,
    string CommandKind,
    long ExpectedRevision,
    JsonElement Payload);

public sealed record WorkerSessionResultEnvelope(
    string ContractVersion,
    string CommandKind,
    long Revision,
    bool Accepted,
    string? RejectionCode,
    string? Message,
    JsonElement Projection);

public sealed record WorkerSessionRestorePayload(
    long Revision,
    StoredPlan? StoredPlan,
    bool TrackStoredPlanIdentity,
    bool MigratedFromLegacy);

public sealed record WorkerSessionReplacePayload(
    StoredPlan? StoredPlan,
    bool TrackStoredPlanIdentity);

public sealed record WorkerSessionShellProjection(
    long Revision,
    bool HasSession,
    string? PlanId,
    string? PlanName,
    string SelectedDataCenter,
    string SelectedRegion,
    int ProjectItemCount,
    int RootItemCount,
    int PlanNodeCount,
    int MarketAnalysisCount,
    int ShoppingPlanCount,
    bool HasProcurementRoute,
    long PlanSessionVersion,
    AppStateVersionSnapshot Versions,
    string? RestoreWarning,
    bool MigratedFromLegacy);

public sealed record WorkerSessionExportRequest(
    string PlanId,
    string PlanName,
    bool IncludeSourcePlanIdentity,
    bool IncludeLegacyMarketAnalysisFields);

public sealed record WorkerSessionExportProjection(
    long Revision,
    StoredPlan? StoredPlan);

public static class WorkerSessionCommandKinds
{
    public const string RecipeProjection = "recipe-projection";
    public const string ProjectItemsMutation = "mutate-project-items";
    public const string RecipeBuild = "mutate-recipe-build";
    public const string AcquisitionMutation = "mutate-acquisition";
    public const string AcquisitionProjection = "acquisition-projection";
    public const string MarketAnalysisRun = "mutate-market-analysis";
    public const string MarketEvidencePublicationStage = "stage-market-evidence-publication";
    public const string MarketEvidencePublication = "mutate-market-evidence-publication";
    public const string MarketItemEvidencePublication = "mutate-market-item-evidence-publication";
    public const string MarketItemRefresh = "mutate-market-item-refresh";
    public const string MarketLensMutation = "mutate-market-lens";
    public const string MarketProjection = "market-projection";
    public const string ProcurementRun = "mutate-procurement";
    public const string ProcurementToleranceMutation = "mutate-procurement-tolerance";
    public const string ProcurementProjection = "procurement-projection";

    public static bool IsMutation(string commandKind) =>
        commandKind.StartsWith("mutate-", StringComparison.Ordinal);
}

public sealed record WorkerSessionMutationProjection(
    WorkerSessionShellProjection Shell,
    StoredPlan? DurableState,
    WorkerSessionDurablePatch? DurablePatch,
    JsonElement PublicProjection);

public sealed record WorkerSessionDurablePatch(
    string ProcurementRouteJson);

public sealed record WorkerAcceptedMutationProjection(
    WorkerSessionShellProjection Shell,
    JsonElement View);

public sealed record WorkerProjectItemsMutation(
    string Operation,
    ProjectItem? Item = null,
    int ItemId = 0,
    int Quantity = 0,
    bool MustBeHq = false,
    IReadOnlyList<ProjectItem>? Items = null);

public sealed record WorkerRecipeBuildRequest(
    IReadOnlyList<ProjectItem> ProjectItems,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketFetchScope PriceFetchScope);

public sealed record WorkerRecipeBuildOutcome(
    bool Built,
    string Message,
    RecipePlannerCommandMessageLevel MessageLevel,
    WorkerRecipePlannerProjection Recipe);

public sealed record WorkerAcquisitionMutation(
    int ItemId,
    AcquisitionSource? Source,
    bool? MustBeHq);

public sealed record WorkerRecipePlannerProjection(
    long Revision,
    string? PlanId,
    string? PlanName,
    string SelectedDataCenter,
    string SelectedRegion,
    IReadOnlyList<ProjectItem> ProjectItems,
    IReadOnlyList<WorkerRecipeNodeProjection> Roots,
    bool HasMarketEvidence,
    bool HasProcurementRoute);

public sealed record WorkerRecipeNodeProjection(
    string NodeId,
    int ItemId,
    string Name,
    int IconId,
    int Quantity,
    AcquisitionSource Source,
    bool MustBeHq,
    bool CanBeHq,
    bool IsCircularReference,
    RecipeNodeDisplayState Display,
    RecipePlanProcurementRouteSummary? ProcurementRoute,
    IReadOnlyList<WorkerRecipeNodeProjection> Children);

public sealed record WorkerRecipeNodeHqChangeRequest(
    WorkerRecipeNodeProjection Node,
    bool MustBeHq);

public sealed record WorkerAcquisitionProjectionRequest(string Filter);

public sealed record WorkerAcquisitionProjection(
    long Revision,
    bool HasPlan,
    int RootItemCount,
    int PricedItemCount,
    int UnavailableItemCount,
    IReadOnlyList<WorkerAcquisitionRowProjection> Rows,
    int MarketCandidateCount,
    int ActiveProcurementCount,
    bool HasProcurementRoute,
    IReadOnlyList<MaterialAggregate> ActiveProcurementItems,
    IReadOnlyList<CoreMarketDataUnavailableItem> UnavailableMarketItems);

public sealed record WorkerAcquisitionRowProjection(
    string NodeId,
    int ItemId,
    string ItemName,
    int IconId,
    AcquisitionSource Source,
    AcquisitionSourceReason SourceReason,
    bool MustBeHq,
    bool HasChildren,
    bool CanCraft,
    bool CanBeHq,
    bool CanBuyFromMarket,
    bool CanBuyFromVendor,
    int TotalQuantity,
    int ActiveQuantity,
    string UsedIn,
    bool HasSuppressedOccurrences,
    bool IsFullySuppressed,
    IReadOnlyList<string> SuppressedBy,
    bool IsActiveProcurement,
    bool HasEditableOccurrences,
    bool IsMarketCandidate,
    string MarketEvidence,
    string EstimatedCost,
    bool IsMarketUnavailable,
    decimal UnitPrice,
    IReadOnlyList<AcquisitionSource> AvailableSources,
    IReadOnlyList<WorkerAcquisitionOptionProjection> Options);

public sealed record WorkerAcquisitionOptionProjection(
    AcquisitionSource Source,
    string Name,
    string Detail,
    string CostText,
    bool IsAvailable,
    bool IsProjectedUnsupported);

public sealed record WorkerMarketAnalysisRequest(
    bool ForceRefreshData,
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens);

public sealed record WorkerMarketLensMutation(MarketAcquisitionLens Lens);

public sealed record WorkerMarketAnalysisOutcome(
    bool Published,
    int AnalyzedCount,
    int ChangedDecisionCount,
    int FetchedCount,
    WorkerMarketProjection Market);

public sealed record WorkerMarketEvidenceCommitProjection(
    int AnalyzedCount,
    int ChangedDecisionCount,
    int FetchedCount);

public sealed record WorkerMarketProjectionRequest(
    bool IncludeDetails = true);

public sealed record WorkerMarketEvidencePublicationRequest(
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    IReadOnlyList<MarketItemAnalysis> ItemAnalyses,
    IReadOnlyList<DetailedShoppingPlan> ShoppingPlans,
    IReadOnlySet<int> UnavailableItemIds,
    int FetchedCount,
    bool ResetStaging = false,
    bool CompleteStaging = true);

public sealed record WorkerMarketItemEvidencePublicationRequest(
    int ItemId,
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    MarketItemAnalysis ItemAnalysis,
    DetailedShoppingPlan ShoppingPlan);

public sealed record WorkerMarketItemRefreshRequest(
    int ItemId,
    string ItemName,
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    string? TargetDataCenter = null,
    string? TargetWorldName = null,
    MarketWorldEvidenceSnapshot? ObservedEvidence = null);

public sealed record WorkerMarketItemRefreshOutcome(
    CoreProcurementItemRefreshStatus Status,
    string? ItemName,
    WorkerMarketProjection Market);

public sealed record WorkerMarketProjection(
    long Revision,
    bool HasPlan,
    bool HasAnalysis,
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    int CandidateCount,
    int AvailableCount,
    int UnavailableCount,
    long EstimatedTotalCost,
    IReadOnlyList<WorkerMarketItemProjection> Items,
    IReadOnlyList<MaterialAggregate> CandidateItems,
    IReadOnlyList<DetailedShoppingPlan> ShoppingPlans,
    IReadOnlyList<MarketItemAnalysis> ItemAnalyses);

public sealed record WorkerMarketItemProjection(
    int ItemId,
    string Name,
    int IconId,
    int QuantityNeeded,
    bool IsAvailable,
    bool HasSufficientStock,
    int AvailableQuantity,
    long EstimatedTotalCost,
    decimal EstimatedUnitPrice,
    string RecommendedWorld,
    int WorldCount,
    MarketDataQualityBucket DataQuality,
    string? Warning,
    IReadOnlyList<WorkerMarketWorldProjection> Worlds);

public sealed record WorkerMarketWorldProjection(
    string DataCenter,
    string WorldName,
    int Quantity,
    long TotalCost,
    decimal AverageUnitPrice,
    bool HasSufficientStock,
    MarketDataQualityBucket DataQuality,
    TimeSpan? DataAge);

public sealed record WorkerProcurementRequest(
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    int TravelTolerance,
    bool IncludeSplitPurchases,
    bool StartFromHomeDataCenter,
    MarketTravelPriority TravelPriority,
    IReadOnlySet<MarketWorldKey>? ExcludedWorlds = null,
    IReadOnlySet<MarketItemWorldKey>? ExcludedItemWorlds = null);

public sealed record WorkerProcurementToleranceMutation(int TravelTolerance);

public sealed record WorkerProcurementOutcome(
    CoreProcurementWorkflowStatus Status,
    int ShoppingPlanCount,
    WorkerProcurementProjection Procurement,
    WorkerProcurementDiagnostics Diagnostics);

public sealed record WorkerProcurementDiagnostics(
    long WorldDataMilliseconds,
    long PreparationMilliseconds,
    long ReconciliationMilliseconds,
    long OptimizationAndPublicationMilliseconds,
    long TotalWorkflowMilliseconds);

public sealed record WorkerProcurementProjection(
    long Revision,
    bool HasPlan,
    bool HasMarketEvidence,
    bool HasRoute,
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    int ActiveItemCount,
    int TravelTolerance,
    string TravelToleranceLabel,
    MarketTravelPriority TravelPriority,
    bool SearchesEntireRegion,
    bool IncludeSplitPurchases,
    long SelectedGilCost,
    long CheapestGilCost,
    long PremiumGil,
    int WorldStops,
    int DataCenterTransfers,
    bool RouteSearchWasTruncated,
    IReadOnlyList<WorkerProcurementToleranceProjection> ToleranceOptions,
    IReadOnlyList<WorkerProcurementWorldProjection> Worlds,
    IReadOnlyList<DetailedShoppingPlan> ShoppingPlans,
    MarketRouteDecision? RouteDecision);

public sealed record WorkerProcurementToleranceProjection(
    int MinimumTolerance,
    int MaximumTolerance,
    long GilCost,
    int WorldStops,
    int DataCenterTransfers);

public sealed record WorkerProcurementWorldProjection(
    string DataCenter,
    string WorldName,
    bool IsVendor,
    long TotalCost,
    int ItemCount,
    int TotalQuantity,
    IReadOnlyList<WorkerProcurementItemProjection> Items);

public sealed record WorkerProcurementItemProjection(
    int ItemId,
    string Name,
    int IconId,
    int Quantity,
    int TotalQuantityNeeded,
    decimal UnitPrice,
    long TotalCost,
    bool IsSplitPurchase);
