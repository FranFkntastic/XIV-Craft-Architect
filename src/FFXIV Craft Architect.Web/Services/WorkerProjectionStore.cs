using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Main-thread store for immutable Worker projections. It never owns domain entities.
/// </summary>
public sealed class WorkerProjectionStore
{
    public WorkerSessionShellProjection Shell { get; private set; } =
        new(
            Revision: 0,
            HasSession: false,
            PlanId: null,
            PlanName: null,
            SelectedDataCenter: "Aether",
            SelectedRegion: "North America",
            ProjectItemCount: 0,
            RootItemCount: 0,
            PlanNodeCount: 0,
            MarketAnalysisCount: 0,
            ShoppingPlanCount: 0,
            HasProcurementRoute: false,
            PlanSessionVersion: 0,
            Versions: new AppStateVersionSnapshot(0, 0, 0, 0, 0, 0, 0, 0),
            RestoreWarning: null,
            MigratedFromLegacy: false);

    public event Action? Changed;

    public bool TryPublish(WorkerSessionResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Accepted ||
            result.Revision < Shell.Revision ||
            result.Projection.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        var shell = result.Projection.Deserialize<WorkerSessionShellProjection>(
            EngineJsonSerializerOptions.CreateWire());
        if (shell is null || shell.Revision != result.Revision)
        {
            return false;
        }

        Shell = shell;
        Changed?.Invoke();
        return true;
    }
}
