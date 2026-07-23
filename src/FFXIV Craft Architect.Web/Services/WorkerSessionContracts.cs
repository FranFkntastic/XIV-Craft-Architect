using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

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
    StoredPlan StoredPlan,
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

    public static bool IsMutation(string commandKind) =>
        commandKind.StartsWith("mutate-", StringComparison.Ordinal);
}

public sealed record WorkerSessionMutationProjection(
    WorkerSessionShellProjection Shell,
    StoredPlan DurableState,
    JsonElement PublicProjection);

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
    bool HasProcurementRoute);

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
    IReadOnlyList<AcquisitionSource> AvailableSources,
    IReadOnlyList<WorkerAcquisitionOptionProjection> Options);

public sealed record WorkerAcquisitionOptionProjection(
    AcquisitionSource Source,
    string Name,
    string Detail,
    string CostText,
    bool IsAvailable,
    bool IsProjectedUnsupported);
