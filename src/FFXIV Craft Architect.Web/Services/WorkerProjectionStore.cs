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

    public WorkerRecipePlannerProjection? Recipe { get; private set; }
    public WorkerAcquisitionProjection? Acquisition { get; private set; }
    public WorkerMarketProjection? Market { get; private set; }
    public WorkerProcurementProjection? Procurement { get; private set; }

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

    public bool TryPublishRecipe(WorkerSessionResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Accepted ||
            result.Revision != Shell.Revision ||
            result.Projection.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        var recipe = result.Projection.Deserialize<WorkerRecipePlannerProjection>(
            EngineJsonSerializerOptions.CreateWire());
        if (recipe is null || recipe.Revision != result.Revision)
        {
            return false;
        }

        Recipe = recipe;
        Changed?.Invoke();
        return true;
    }

    public bool TryPublishAcquisition(WorkerSessionResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Accepted ||
            result.Revision != Shell.Revision ||
            result.Projection.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        var acquisition = result.Projection.Deserialize<WorkerAcquisitionProjection>(
            EngineJsonSerializerOptions.CreateWire());
        if (acquisition is null || acquisition.Revision != result.Revision)
        {
            return false;
        }

        Acquisition = acquisition;
        Changed?.Invoke();
        return true;
    }

    public bool TryPublishMarket(WorkerSessionResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Accepted ||
            result.Revision != Shell.Revision ||
            result.Projection.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        var market = result.Projection.Deserialize<WorkerMarketProjection>(
            EngineJsonSerializerOptions.CreateWire());
        if (market is null || market.Revision != result.Revision)
        {
            return false;
        }

        Market = market;
        Changed?.Invoke();
        return true;
    }

    public bool TryPublishProcurement(WorkerSessionResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Accepted ||
            result.Revision != Shell.Revision ||
            result.Projection.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        var procurement = result.Projection.Deserialize<WorkerProcurementProjection>(
            EngineJsonSerializerOptions.CreateWire());
        if (procurement is null || procurement.Revision != result.Revision)
        {
            return false;
        }

        Procurement = procurement;
        Changed?.Invoke();
        return true;
    }

    public bool TryPublishMutation<TProjection>(
        WorkerSessionResultEnvelope result,
        out TProjection? projection)
    {
        projection = default;
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Accepted ||
            result.Revision <= Shell.Revision ||
            result.Projection.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        var accepted = result.Projection.Deserialize<WorkerAcceptedMutationProjection>(
            EngineJsonSerializerOptions.CreateWire());
        if (accepted is null ||
            accepted.Shell.Revision != result.Revision ||
            accepted.Shell.Revision <= Shell.Revision)
        {
            return false;
        }

        projection = accepted.View.Deserialize<TProjection>(
            EngineJsonSerializerOptions.CreateWire());
        if (projection is null)
        {
            return false;
        }

        Shell = accepted.Shell;
        if (projection is WorkerRecipePlannerProjection recipe)
        {
            Recipe = recipe;
        }
        else if (projection is WorkerRecipeBuildOutcome build)
        {
            Recipe = build.Recipe;
        }
        else if (projection is WorkerMarketProjection market)
        {
            Market = market;
        }
        else if (projection is WorkerMarketAnalysisOutcome analysis)
        {
            Market = analysis.Market;
        }
        else if (projection is WorkerMarketItemRefreshOutcome itemRefresh)
        {
            Market = itemRefresh.Market;
        }
        else if (projection is WorkerProcurementProjection procurement)
        {
            Procurement = procurement;
        }
        else if (projection is WorkerProcurementOutcome route)
        {
            Procurement = route.Procurement;
        }
        Changed?.Invoke();
        return true;
    }
}
