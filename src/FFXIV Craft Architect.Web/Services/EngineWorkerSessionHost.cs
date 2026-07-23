using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace CraftArchitectEngineWorker;

public static partial class ManagedHost
{
    private static readonly object SessionSync = new();
    private static FFXIV_Craft_Architect.Web.Services.AppState _sessionState = new();
    private static long _sessionRevision;
    private static string? _sessionRestoreWarning;
    private static bool _sessionMigratedFromLegacy;

    [JSExport]
    [SupportedOSPlatform("browser")]
    public static Task<string> ExecuteSessionCommandJson(string messageJson) =>
        ExecuteSessionCommandJsonCore(messageJson);

    public static Task<string> ExecuteSessionCommandJsonCore(string messageJson)
    {
        var message = JsonSerializer.Deserialize<EngineWorkerMessage>(messageJson, WireJsonOptions)
            ?? throw new InvalidOperationException("Worker session message is empty.");
        if (!string.Equals(message.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal) ||
            !string.Equals(
                message.Kind,
                FFXIV_Craft_Architect.Web.Services.WorkerSessionProtocol.CommandMessageKind,
                StringComparison.Ordinal) ||
            message.Generation <= 0 ||
            message.ExecutionId is not { } executionId ||
            message.TransactionId is not { } transactionId ||
            message.Payload is not { } payload)
        {
            throw new InvalidOperationException("Worker session message identity is invalid.");
        }

        var command = payload.Deserialize<FFXIV_Craft_Architect.Web.Services.WorkerSessionCommandEnvelope>(
                WireJsonOptions)
            ?? throw new InvalidOperationException("Worker session command is empty.");
        if (!string.Equals(
                command.ContractVersion,
                FFXIV_Craft_Architect.Web.Services.WorkerSessionProtocol.ContractVersion,
                StringComparison.Ordinal))
        {
            return Task.FromResult(CreateSessionResultMessage(
                message,
                command.CommandKind,
                accepted: false,
                "contract-version-mismatch",
                "The Worker session command contract is unsupported.",
                CaptureShellProjection()));
        }

        FFXIV_Craft_Architect.Web.Services.WorkerSessionResultEnvelope result;
        lock (SessionSync)
        {
            if (command.ExpectedRevision != _sessionRevision)
            {
                result = CreateSessionResult(
                    command.CommandKind,
                    accepted: false,
                    "stale-revision",
                    $"Expected Worker session revision {command.ExpectedRevision}, but {_sessionRevision} is active.",
                    CaptureShellProjectionUnderLock());
            }
            else
            {
                result = ExecuteSessionCommandUnderLock(command);
            }
        }

        return Task.FromResult(JsonSerializer.Serialize(
            new EngineWorkerMessage(
                ProtocolVersion,
                FFXIV_Craft_Architect.Web.Services.WorkerSessionProtocol.ResultMessageKind,
                message.Generation,
                executionId,
                transactionId,
                JsonSerializer.SerializeToElement(result, WireJsonOptions)),
            WireJsonOptions));
    }

    private static FFXIV_Craft_Architect.Web.Services.WorkerSessionResultEnvelope
        ExecuteSessionCommandUnderLock(
            FFXIV_Craft_Architect.Web.Services.WorkerSessionCommandEnvelope command)
    {
        try
        {
            return command.CommandKind switch
            {
                "restore" => RestoreSessionUnderLock(command),
                "shell" => CreateSessionResult(
                    command.CommandKind,
                    accepted: true,
                    null,
                    null,
                    CaptureShellProjectionUnderLock()),
                "export" => ExportSessionUnderLock(command),
                _ => CreateSessionResult(
                    command.CommandKind,
                    accepted: false,
                    "unknown-command",
                    $"Unknown Worker session command '{command.CommandKind}'.",
                    CaptureShellProjectionUnderLock())
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return CreateSessionResult(
                command.CommandKind,
                accepted: false,
                "command-rejected",
                ex.Message,
                CaptureShellProjectionUnderLock());
        }
    }

    private static FFXIV_Craft_Architect.Web.Services.WorkerSessionResultEnvelope
        RestoreSessionUnderLock(
            FFXIV_Craft_Architect.Web.Services.WorkerSessionCommandEnvelope command)
    {
        var restore = command.Payload.Deserialize<
                FFXIV_Craft_Architect.Web.Services.WorkerSessionRestorePayload>(WireJsonOptions)
            ?? throw new InvalidOperationException("Worker session restore payload is empty.");
        if (restore.Revision < 0 ||
            (_sessionRevision > 0 && restore.Revision != _sessionRevision + 1))
        {
            throw new InvalidOperationException(
                $"Worker session restore revision {restore.Revision} cannot follow {_sessionRevision}.");
        }

        var replacement = new FFXIV_Craft_Architect.Web.Services.AppState();
        string? warning = null;
        if (restore.StoredPlan is not null)
        {
            var prepared = FFXIV_Craft_Architect.Web.Services.PlanSessionLoadService.Prepare(
                restore.StoredPlan);
            replacement.ApplyLoadedPlanSession(
                prepared,
                restore.TrackStoredPlanIdentity,
                markRestoredStatePersisted: true);
            warning = prepared.Warning;
        }

        _sessionState = replacement;
        _sessionRevision = restore.Revision;
        _sessionRestoreWarning = warning;
        _sessionMigratedFromLegacy = restore.MigratedFromLegacy;
        return CreateSessionResult(
            command.CommandKind,
            accepted: true,
            null,
            warning,
            CaptureShellProjectionUnderLock());
    }

    private static FFXIV_Craft_Architect.Web.Services.WorkerSessionResultEnvelope
        ExportSessionUnderLock(
            FFXIV_Craft_Architect.Web.Services.WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<
                FFXIV_Craft_Architect.Web.Services.WorkerSessionExportRequest>(WireJsonOptions)
            ?? throw new InvalidOperationException("Worker session export request is empty.");
        var storedPlan = _sessionState.HasPlanOrProjectItems
            ? FFXIV_Craft_Architect.Web.Services.StoredPlanSnapshotBuilder.Build(
                _sessionState,
                request.PlanId,
                request.PlanName,
                DateTime.UtcNow,
                request.IncludeSourcePlanIdentity,
                request.IncludeLegacyMarketAnalysisFields)
            : null;
        var projection =
            new FFXIV_Craft_Architect.Web.Services.WorkerSessionExportProjection(
                _sessionRevision,
                storedPlan);
        return CreateSessionResult(
            command.CommandKind,
            accepted: true,
            null,
            null,
            projection);
    }

    private static FFXIV_Craft_Architect.Web.Services.WorkerSessionResultEnvelope
        CreateSessionResult(
            string commandKind,
            bool accepted,
            string? rejectionCode,
            string? message,
            object projection) =>
        new(
            FFXIV_Craft_Architect.Web.Services.WorkerSessionProtocol.ContractVersion,
            commandKind,
            _sessionRevision,
            accepted,
            rejectionCode,
            message,
            JsonSerializer.SerializeToElement(projection, WireJsonOptions));

    private static string CreateSessionResultMessage(
        EngineWorkerMessage request,
        string commandKind,
        bool accepted,
        string? rejectionCode,
        string? message,
        object projection)
    {
        var result = CreateSessionResult(
            commandKind,
            accepted,
            rejectionCode,
            message,
            projection);
        return JsonSerializer.Serialize(
            new EngineWorkerMessage(
                ProtocolVersion,
                FFXIV_Craft_Architect.Web.Services.WorkerSessionProtocol.ResultMessageKind,
                request.Generation,
                request.ExecutionId,
                request.TransactionId,
                JsonSerializer.SerializeToElement(result, WireJsonOptions)),
            WireJsonOptions);
    }

    private static FFXIV_Craft_Architect.Web.Services.WorkerSessionShellProjection
        CaptureShellProjection()
    {
        lock (SessionSync)
        {
            return CaptureShellProjectionUnderLock();
        }
    }

    private static FFXIV_Craft_Architect.Web.Services.WorkerSessionShellProjection
        CaptureShellProjectionUnderLock()
    {
        var plan = _sessionState.CurrentPlan;
        return new FFXIV_Craft_Architect.Web.Services.WorkerSessionShellProjection(
            _sessionRevision,
            _sessionState.HasPlanOrProjectItems,
            _sessionState.CurrentPlanId,
            _sessionState.CurrentPlanName ?? plan?.Name,
            _sessionState.SelectedDataCenter,
            _sessionState.SelectedRegion,
            _sessionState.ProjectItemCount,
            plan?.RootItems.Count ?? 0,
            CountPlanNodes(plan),
            _sessionState.MarketItemAnalyses.Count,
            _sessionState.ShoppingPlans.Count,
            _sessionState.ProcurementRouteDecision is not null,
            _sessionState.PlanSessionVersion,
            _sessionState.CurrentVersions,
            _sessionRestoreWarning,
            _sessionMigratedFromLegacy);
    }

    private static int CountPlanNodes(CraftingPlan? plan)
    {
        if (plan is null)
        {
            return 0;
        }

        var count = 0;
        var pending = new Stack<PlanNode>(plan.RootItems);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            count++;
            foreach (var child in node.Children)
            {
                pending.Push(child);
            }
        }
        return count;
    }
}
