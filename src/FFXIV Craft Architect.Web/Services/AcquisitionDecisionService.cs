using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class AcquisitionDecisionService
{
    private readonly AppState _appState;

    public AcquisitionDecisionService(AppState appState)
    {
        _appState = appState;
    }

    public AcquisitionDecisionResult ChangeSource(PlanNode node, AcquisitionSource source)
    {
        if (_appState.CurrentPlan == null)
        {
            return new AcquisitionDecisionResult(false, 0);
        }

        using var batch = _appState.BeginStateChangeBatch();
        var nodesUpdated = ApplySourceToMatchingNodes(node.ItemId, source);
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
        var nodesUpdated = 0;
        foreach (var matchingNode in FindNodesByItemId(_appState.CurrentPlan.RootItems, node.ItemId)
            .Where(matchingNode => matchingNode.CanBeHq))
        {
            if (matchingNode.MustBeHq != isHq)
            {
                matchingNode.MustBeHq = isHq;
                nodesUpdated++;
            }
        }

        if (node.CanBuyFromMarket && isHq && node.Source == AcquisitionSource.MarketBuyNq)
        {
            nodesUpdated += ApplySourceToMatchingNodes(node.ItemId, AcquisitionSource.MarketBuyHq);
        }
        else if (node.CanBuyFromMarket && !isHq && node.Source == AcquisitionSource.MarketBuyHq)
        {
            nodesUpdated += ApplySourceToMatchingNodes(node.ItemId, AcquisitionSource.MarketBuyNq);
        }

        if (nodesUpdated == 0)
        {
            return new AcquisitionDecisionResult(false, 0);
        }

        ReconcileAndPublish();
        return new AcquisitionDecisionResult(true, nodesUpdated);
    }

    private int ApplySourceToMatchingNodes(int itemId, AcquisitionSource source)
    {
        if (_appState.CurrentPlan == null)
        {
            return 0;
        }

        var nodesUpdated = 0;
        foreach (var matchingNode in FindNodesByItemId(_appState.CurrentPlan.RootItems, itemId))
        {
            var previousSource = matchingNode.Source;
            var previousReason = matchingNode.SourceReason;
            var previousMustBeHq = matchingNode.MustBeHq;
            AcquisitionPlanningService.SetAcquisitionSource(
                matchingNode,
                source,
                AcquisitionSourceReason.UserSelected);

            if (matchingNode.Source != previousSource ||
                matchingNode.SourceReason != previousReason ||
                matchingNode.MustBeHq != previousMustBeHq)
            {
                nodesUpdated++;
            }
        }

        return nodesUpdated;
    }

    private void ReconcileAndPublish()
    {
        if (_appState.CurrentPlan == null)
        {
            return;
        }

        var costContext = AcquisitionPlanningService.CreateCostContext(_appState.ShoppingPlans);
        AcquisitionPlanningService.ReconcileAcquisitionDecisions(_appState.CurrentPlan, costContext);
        _appState.ShoppingItems = AcquisitionPlanningService.GetActiveProcurementItems(_appState.CurrentPlan)
            .Where(item => item.TotalQuantity > 0)
            .Select(item => new MarketShoppingItem
            {
                Id = item.ItemId,
                Name = item.Name,
                IconId = item.IconId,
                Quantity = item.TotalQuantity
            })
            .ToList();
        _appState.NotifyShoppingItemsChanged();
        _appState.ProcurementShoppingPlans.Clear();
        _appState.NotifyProcurementOverlayChanged();
        _appState.NotifyPlanDecisionChanged();
        ClearStaleProcurementStatus();
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

    private static IEnumerable<PlanNode> FindNodesByItemId(IEnumerable<PlanNode> nodes, int itemId)
    {
        foreach (var node in nodes)
        {
            foreach (var match in FindNodesByItemId(node, itemId))
            {
                yield return match;
            }
        }
    }

    private static IEnumerable<PlanNode> FindNodesByItemId(PlanNode node, int itemId)
    {
        if (node.ItemId == itemId)
        {
            yield return node;
        }

        foreach (var child in node.Children)
        {
            foreach (var match in FindNodesByItemId(child, itemId))
            {
                yield return match;
            }
        }
    }
}

public sealed record AcquisitionDecisionResult(bool Changed, int NodesUpdated);
