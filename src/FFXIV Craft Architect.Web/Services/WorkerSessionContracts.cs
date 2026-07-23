using System.Text.Json;

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
