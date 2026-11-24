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
    JsonElement Payload,
    Guid? OperationId = null);

public sealed record WorkerSessionResultEnvelope(
    string ContractVersion,
    string CommandKind,
    long Revision,
    bool Accepted,
    string? RejectionCode,
    string? Message,
    JsonElement Projection,
    WorkerSessionDurablePatch? DurableRepairPatch = null);

public sealed class WorkerSessionCommandRejectedException : InvalidOperationException
{
    public WorkerSessionCommandRejectedException(
        string? rejectionCode,
        long currentRevision,
        string message)
        : base(message)
    {
        RejectionCode = rejectionCode;
        CurrentRevision = currentRevision;
    }

    public string? RejectionCode { get; }

    public long CurrentRevision { get; }
}

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
    bool MigratedFromLegacy,
    WorkerSessionOperationProjection? Operation = null);

public sealed record WorkerSessionExportRequest(
    string PlanId,
    string PlanName,
    bool IncludeSourcePlanIdentity);

public sealed record WorkerSessionExportProjection(
    long Revision,
    StoredPlan? StoredPlan);

public static class WorkerSessionCommandKinds
{
    public const string OperationBegin = "operation-begin";
    public const string OperationRenew = "operation-renew";
    public const string OperationComplete = "operation-complete";
    public const string OperationAbort = "operation-abort";
    public const string RecipeProjection = "recipe-projection";
    public const string ProjectItemsMutation = "mutate-project-items";
    public const string PlanIdentityMutation = "mutate-plan-identity";
    public const string StoredPlanRevisionMutation = "mutate-stored-plan-revision";
    public const string ActiveContextMutation = "mutate-active-context";
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
    public const string TradeProjection = "trade-projection";

    public static bool IsMutation(string commandKind) =>
        commandKind.StartsWith("mutate-", StringComparison.Ordinal);
}

public enum WorkerSessionOperationKind
{
    PlanDerivation,
    MarketAnalysis,
    ItemMarketRefresh,
    ProcurementAnalysis,
    TradeOrderPricing
}

public enum WorkerSessionOperationDisposition
{
    Acquired,
    Current,
    Busy,
    Completed,
    Aborted
}

public sealed record WorkerSessionOperationBeginRequest(
    Guid OperationId,
    WorkerSessionOperationKind Kind,
    string IntentKey,
    string StatusMessage);

public sealed record WorkerSessionOperationControlRequest(Guid OperationId);

public sealed record WorkerSessionOperationProjection(
    Guid? OperationId,
    WorkerSessionOperationKind? Kind,
    string IntentKey,
    long BaseRevision,
    WorkerSessionOperationDisposition Disposition,
    bool IsActive,
    string StatusMessage);

public sealed record WorkerTradeProjectionRequest(
    bool IncludeCraftLabor = false,
    TradeMaterialPricingPolicy? MaterialPricingPolicy = null,
    bool IncludeOutputPurchaseQuotes = false,
    WorkerRoutePaymentBasis? RoutePaymentBasis = null,
    // Company-provided materials stay out of the crafter's executable route quote. Falls back
    // to RoutePaymentBasis responsibilities when absent.
    IReadOnlyList<TradePayrollResponsibilityLine>? MaterialResponsibilities = null);

/// <summary>
/// The order's payment terms for pricing precraft route choices. When present on a plan with a
/// labor rate, each precraft option total becomes its marginal effect on the order payment,
/// priced by the same quote, labor and payment path the order uses.
/// </summary>
public sealed record WorkerRoutePaymentBasis(
    TradePaymentPolicy PaymentPolicy,
    IReadOnlyList<TradePayrollResponsibilityLine>? Responsibilities);

public sealed record WorkerTradeProjection(
    long Revision,
    bool HasPlan,
    string? PlanId,
    string PlanName,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketFetchScope MarketFetchScope,
    IReadOnlyList<string> RequestedDataCenters,
    MarketAcquisitionLens MarketLens,
    long PlanSessionVersion,
    long MarketAnalysisVersion,
    IReadOnlyList<ProjectItem> CraftedItems,
    IReadOnlyList<TradeOrderRootItemSnapshot> RootItems,
    IReadOnlyList<CommissionPayrollInputLine> MaterialLines,
    IReadOnlyList<MaterialAggregate> ActiveProcurementItems,
    IReadOnlyList<WorkerAcquisitionRowProjection> AcquisitionRows,
    IReadOnlyList<TradeOrderCraftLaborSnapshot> CraftLabor,
    IReadOnlyList<string> Warnings,
    TradeMaterialQuote? MaterialQuote = null,
    string? MaterialQuoteFailureReason = null,
    IReadOnlyList<WorkerPlanOccurrence>? PlanOccurrences = null,
    IReadOnlyList<WorkerOutputPurchaseQuote>? OutputPurchaseQuotes = null);

/// <summary>
/// What buying one commissioned output whole would cost from real listings in the pricing
/// scope. <see cref="TotalCost"/> is null whenever the listings cannot cover the full quantity
/// or the executable quote fails; a partial total is never reported.
/// </summary>
public sealed record WorkerOutputPurchaseQuote(
    int ItemId,
    string ItemName,
    int Quantity,
    bool MustBeHq,
    decimal? TotalCost,
    int ListedQuantity,
    int ListingCount,
    DateTime? ObservedAtUtc)
{
    public bool IsShort => TotalCost is null && ListedQuantity < Quantity;
}

// Occurrence identity, not presentation strings, defines acquisition ancestry.
public sealed record WorkerPlanOccurrence(
    string NodeId,
    string? ParentNodeId,
    int ItemId,
    bool MustBeHq,
    int Quantity,
    AcquisitionSource Source,
    bool IsActiveProcurement,
    bool IsSuppressed);

public sealed record WorkerSessionMutationProjection(
    WorkerSessionShellProjection Shell,
    StoredPlan? DurableState,
    WorkerSessionDurablePatch? DurablePatch,
    JsonElement PublicProjection);

public sealed record WorkerSessionDurablePatch(
    bool ReplacePlanJson = false,
    string? PlanJson = null,
    bool ReplacePlanStateJson = false,
    string? PlanStateJson = null,
    bool ReplaceProjectItems = false,
    IReadOnlyList<StoredProjectItem>? ProjectItems = null,
    bool ReplaceMarketEvidence = false,
    string? MarketIntelligenceJson = null,
    string? MarketAnalysisRecipeBasisJson = null,
    RecommendationMode? SavedRecommendationMode = null,
    MarketAcquisitionLens? SavedMarketAnalysisLens = null,
    bool ReplaceProcurementRoute = false,
    string? ProcurementRouteJson = null,
    int? ProcurementTravelTolerance = null,
    bool ReplaceContext = false,
    string? DataCenter = null,
    bool ReplaceSourceIdentity = false,
    string? SourcePlanId = null,
    string? SourcePlanName = null,
    bool ReplaceStoredRevision = false,
    string? StoredPlanId = null,
    string? StoredPlanName = null,
    DateTime? CreatedAt = null,
    DateTime? ModifiedAt = null,
    DateTime? SavedAt = null,
    Guid? LinkedOrderId = null);

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

/// <param name="AcquisitionLaborGilPerSynth">
/// When set, the plan's per-synth labor rate is replaced and automatic acquisition defaults
/// are re-evaluated with it. Null leaves the rate and the plan's routes untouched.
/// </param>
public sealed record WorkerPlanIdentityMutation(
    string PlanId,
    string PlanName,
    decimal? AcquisitionLaborGilPerSynth = null);

/// <summary>
/// Stamps the identity of a stored snapshot that was just exported from the live
/// session and sealed to plan storage, so the session (and its durable record)
/// track that exact revision without re-importing the whole plan.
/// </summary>
public sealed record WorkerStoredPlanRevisionMutation(
    string PlanId,
    string PlanName,
    DateTime CreatedAt,
    DateTime ModifiedAt,
    DateTime SavedAt,
    Guid? LinkedOrderId);

public sealed record WorkerActiveContextMutation(
    string SelectedDataCenter,
    string SelectedRegion,
    MarketFetchScope Scope);

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
    string Filter,
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
    decimal CalculatedTotalCost,
    IReadOnlyList<AcquisitionSource> AvailableSources,
    IReadOnlyList<WorkerAcquisitionOptionProjection> Options,
    AcquisitionSource? DefaultSource = null,
    int CraftSynthCount = 0,
    decimal LaborGilPerSynth = 0);

public sealed record WorkerAcquisitionOptionProjection(
    AcquisitionSource Source,
    string Name,
    string Detail,
    string CostText,
    bool IsAvailable,
    bool IsProjectedUnsupported,
    decimal? TotalCost = null,
    decimal? MaterialCost = null,
    decimal? LaborCost = null,
    int SynthCount = 0,
    int RequiredQuantity = 0,
    int ListedQuantity = 0,
    int ListingCount = 0,
    DateTime? ObservedAtUtc = null)
{
    /// <summary>Market route whose scope lists fewer units than the purchase needs.</summary>
    public bool IsShortOfListings => ListedQuantity < RequiredQuantity &&
        Source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq;
}

public sealed record WorkerMarketAnalysisRequest(
    bool ForceRefreshData,
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    IReadOnlyList<string>? SelectedRegions = null,
    // When set, only these candidate items are fetched and analyzed; the publication
    // replaces market evidence with exactly this subset.
    IReadOnlyCollection<int>? TargetItemIds = null);

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
    bool IncludeDetails = true,
    int? WorldDetailItemId = null);

public sealed record WorkerMarketEvidencePublicationRequest(
    Guid OperationId,
    long BaseRevision,
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    IReadOnlyList<MarketItemAnalysis> ItemAnalyses,
    IReadOnlyList<DetailedShoppingPlan> ShoppingPlans,
    IReadOnlySet<int> UnavailableItemIds,
    int FetchedCount,
    bool ResetStaging = false,
    bool CompleteStaging = true,
    IReadOnlyList<string>? RequestedDataCenters = null);

public sealed record WorkerMarketItemEvidencePublicationRequest(
    int ItemId,
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    MarketItemAnalysis ItemAnalysis,
    DetailedShoppingPlan ShoppingPlan,
    IReadOnlyList<string>? RequestedDataCenters = null);

public sealed record WorkerMarketItemRefreshRequest(
    int ItemId,
    string ItemName,
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    string? TargetDataCenter = null,
    string? TargetWorldName = null,
    MarketWorldEvidenceSnapshot? ObservedEvidence = null,
    IReadOnlyList<string>? RequestedDataCenters = null);

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
    IReadOnlyList<MarketItemAnalysis> ItemAnalyses,
    IReadOnlyList<string>? RequestedDataCenters = null);

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
    bool SearchesEntireRegion,
    bool? IncludeSplitPurchases,
    long SelectedGilCost,
    long CheapestGilCost,
    long PremiumGil,
    int WorldStops,
    bool RouteSearchWasTruncated,
    IReadOnlyList<WorkerProcurementToleranceProjection> ToleranceOptions,
    IReadOnlyList<WorkerProcurementWorldProjection> Worlds,
    IReadOnlyList<WorkerProcurementIssueProjection> Issues,
    IReadOnlyList<DetailedShoppingPlan> ShoppingPlans,
    MarketRouteDecision? RouteDecision);

public sealed record WorkerProcurementToleranceProjection(
    int MinimumTolerance,
    int MaximumTolerance,
    long GilCost,
    int WorldStops);

public sealed record WorkerProcurementWorldProjection(
    string DataCenter,
    string WorldName,
    bool IsVendor,
    bool IsCongested,
    string? CongestedWarning,
    WorldClassification Classification,
    long TotalCost,
    int ItemCount,
    int TotalQuantity,
    IReadOnlyList<VendorInfo> Vendors,
    string? SelectedVendorName,
    IReadOnlyList<WorkerProcurementItemProjection> Items);

public sealed record WorkerProcurementItemProjection(
    int ItemId,
    string Name,
    int IconId,
    int Quantity,
    int TotalQuantityNeeded,
    decimal UnitPrice,
    bool PriceIsEffectiveCost,
    long TotalCost,
    bool IsSplitPurchase,
    string TravelContext,
    VendorInfo? Vendor);

public sealed record WorkerProcurementIssueProjection(
    int ItemId,
    string Name,
    string? Error,
    string? Warning);
