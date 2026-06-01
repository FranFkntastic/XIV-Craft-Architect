using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CoreAcquisitionDecisionService
{
    private readonly CraftSessionState _session;
    private readonly ICraftOperationCoordinator _operationCoordinator;

    public CoreAcquisitionDecisionService(
        CraftSessionState session,
        ICraftOperationCoordinator operationCoordinator)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _operationCoordinator = operationCoordinator ?? throw new ArgumentNullException(nameof(operationCoordinator));
    }

    public CoreAcquisitionDecisionResult ChangeSource(int itemId, AcquisitionSource source)
    {
        var plan = _session.ActivePlan;
        var planSessionVersion = _session.PlanSessionVersion;
        if (plan == null)
        {
            return new CoreAcquisitionDecisionResult(false, 0);
        }

        var nodesUpdated = ApplySourceToMatchingNodes(plan, itemId, source);
        if (nodesUpdated == 0)
        {
            return new CoreAcquisitionDecisionResult(false, 0);
        }

        return PublishDecisionChange(plan, planSessionVersion, nodesUpdated);
    }

    public CoreAcquisitionDecisionResult ChangeMarketHq(int itemId, bool isHq)
    {
        var plan = _session.ActivePlan;
        var planSessionVersion = _session.PlanSessionVersion;
        if (plan == null)
        {
            return new CoreAcquisitionDecisionResult(false, 0);
        }

        var nodesUpdated = 0;
        foreach (var matchingNode in FindNodesByItemId(plan.RootItems, itemId).Where(node => node.CanBeHq))
        {
            if (matchingNode.MustBeHq != isHq)
            {
                matchingNode.MustBeHq = isHq;
                nodesUpdated++;
            }

            if (matchingNode.CanBuyFromMarket && isHq && matchingNode.Source == AcquisitionSource.MarketBuyNq)
            {
                nodesUpdated += ApplySource(matchingNode, AcquisitionSource.MarketBuyHq);
            }
            else if (matchingNode.CanBuyFromMarket && !isHq && matchingNode.Source == AcquisitionSource.MarketBuyHq)
            {
                nodesUpdated += ApplySource(matchingNode, AcquisitionSource.MarketBuyNq);
            }
        }

        if (nodesUpdated == 0)
        {
            return new CoreAcquisitionDecisionResult(false, 0);
        }

        return PublishDecisionChange(plan, planSessionVersion, nodesUpdated);
    }

    private CoreAcquisitionDecisionResult PublishDecisionChange(
        CraftingPlan plan,
        long planSessionVersion,
        int nodesUpdated)
    {
        var costContext = AcquisitionPlanningService.CreateCostContext(
            _session.MarketEvidence.ShoppingPlans ?? []);
        AcquisitionPlanningService.ReconcileAcquisitionDecisions(plan, costContext);
        var published = _session.TryReplaceActivePlanDecisions(
            _session.CaptureVersionStamp(),
            plan,
            planSessionVersion,
            "acquisition decision changed");
        if (!published)
        {
            return new CoreAcquisitionDecisionResult(false, 0);
        }

        _operationCoordinator.Cancel(CraftOperationWorkflow.ProcurementAnalysis);
        return new CoreAcquisitionDecisionResult(true, nodesUpdated);
    }

    private static int ApplySourceToMatchingNodes(
        CraftingPlan plan,
        int itemId,
        AcquisitionSource source)
    {
        var nodesUpdated = 0;
        foreach (var matchingNode in FindNodesByItemId(plan.RootItems, itemId))
        {
            nodesUpdated += ApplySource(matchingNode, source);
        }

        return nodesUpdated;
    }

    private static int ApplySource(PlanNode node, AcquisitionSource source)
    {
        var previousSource = node.Source;
        var previousReason = node.SourceReason;
        var previousMustBeHq = node.MustBeHq;
        AcquisitionPlanningService.SetAcquisitionSource(
            node,
            source,
            AcquisitionSourceReason.UserSelected);

        return node.Source != previousSource ||
            node.SourceReason != previousReason ||
            node.MustBeHq != previousMustBeHq
                ? 1
                : 0;
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

public sealed record CoreAcquisitionDecisionResult(bool Changed, int NodesUpdated);
