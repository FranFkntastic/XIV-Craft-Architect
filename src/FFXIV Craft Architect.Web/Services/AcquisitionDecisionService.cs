using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class AcquisitionDecisionService
{
    private readonly AppState _appState;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;

    public AcquisitionDecisionService(
        AppState appState,
        IRecipeLayerWorkflowService? recipeLayerWorkflow = null)
    {
        _appState = appState;
        _recipeLayerWorkflow = recipeLayerWorkflow ?? new LightweightRecipeLayerWorkflowService();
    }

    public AcquisitionDecisionResult ChangeSource(PlanNode node, AcquisitionSource source)
    {
        if (_appState.CurrentPlan == null)
        {
            return new AcquisitionDecisionResult(false, 0);
        }

        using var batch = _appState.BeginStateChangeBatch();
        var nodesUpdated = AcquisitionDecisionMutation.ChangeSource(
            _appState.CurrentPlan,
            node.ItemId,
            source);
        if (nodesUpdated == 0)
        {
            return new AcquisitionDecisionResult(false, 0);
        }

        ReconcileAndPublish();
        return new AcquisitionDecisionResult(nodesUpdated > 0, nodesUpdated);
    }

    public AcquisitionDecisionResult ChangeMarketHq(PlanNode node, bool isHq)
    {
        if (!node.CanBeHq || _appState.CurrentPlan == null)
        {
            return new AcquisitionDecisionResult(false, 0);
        }

        using var batch = _appState.BeginStateChangeBatch();
        var nodesUpdated = AcquisitionDecisionMutation.ChangeMarketHq(
            _appState.CurrentPlan,
            node.ItemId,
            isHq,
            node.CanBuyFromMarket ? node.Source : null);

        if (nodesUpdated == 0)
        {
            return new AcquisitionDecisionResult(false, 0);
        }

        ReconcileAndPublish();
        return new AcquisitionDecisionResult(true, nodesUpdated);
    }

    private void ReconcileAndPublish()
    {
        if (_appState.CurrentPlan == null)
        {
            return;
        }

        _appState.ApplyPlanDecisionChange(GetActiveProcurementItems(), clearProcurementOverlay: true);
        ClearStaleProcurementStatus();
    }

    private IReadOnlyList<MaterialAggregate> GetActiveProcurementItems()
    {
        return _recipeLayerWorkflow.BuildActiveProcurementItems(_appState.CurrentPlan);
    }

    private void ClearStaleProcurementStatus()
    {
        const string message = "Acquisition choices changed; regenerate procurement route when ready.";
        if (string.Equals(_appState.CurrentOperation, "Procurement Analysis", StringComparison.Ordinal))
        {
            _appState.EndOperation(message);
            return;
        }

        if (!_appState.IsBusy)
        {
            _appState.SetStatus(message, busy: false);
        }
    }
}

public sealed record AcquisitionDecisionResult(bool Changed, int NodesUpdated);
