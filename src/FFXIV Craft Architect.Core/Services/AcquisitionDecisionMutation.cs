using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class AcquisitionDecisionMutation
{
    public static int ChangeSource(CraftingPlan? plan, int itemId, AcquisitionSource source)
    {
        return plan == null
            ? 0
            : ApplySourceToMatchingNodes(plan.RootItems, itemId, source);
    }

    public static int ChangeMarketHq(
        CraftingPlan? plan,
        int itemId,
        bool isHq,
        AcquisitionSource? sourceTransitionReference = null)
    {
        if (plan == null)
        {
            return 0;
        }

        var nodesUpdated = 0;
        foreach (var matchingNode in FindNodesByItemId(plan.RootItems, itemId)
            .Where(node => node.CanBeHq))
        {
            if (matchingNode.MustBeHq != isHq)
            {
                matchingNode.MustBeHq = isHq;
                nodesUpdated++;
            }

            if (!sourceTransitionReference.HasValue)
            {
                nodesUpdated += ApplyMarketSourceTransition(matchingNode, isHq);
            }
        }

        if (sourceTransitionReference.HasValue &&
            GetMarketSourceTransition(sourceTransitionReference.Value, isHq) is { } targetSource)
        {
            nodesUpdated += ApplySourceToMatchingNodes(plan.RootItems, itemId, targetSource);
        }

        return nodesUpdated;
    }

    public static int ApplySourceToMatchingNodes(
        IEnumerable<PlanNode> nodes,
        int itemId,
        AcquisitionSource source)
    {
        var nodesUpdated = 0;
        foreach (var matchingNode in FindNodesByItemId(nodes, itemId))
        {
            nodesUpdated += ApplySource(matchingNode, source);
        }

        return nodesUpdated;
    }

    public static int ApplySource(PlanNode node, AcquisitionSource source)
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

    public static IEnumerable<PlanNode> FindNodesByItemId(IEnumerable<PlanNode> nodes, int itemId)
    {
        foreach (var node in nodes)
        {
            foreach (var match in FindNodesByItemId(node, itemId))
            {
                yield return match;
            }
        }
    }

    private static int ApplyMarketSourceTransition(PlanNode node, bool isHq)
    {
        return GetMarketSourceTransition(node.Source, isHq) is { } targetSource
            ? ApplySource(node, targetSource)
            : 0;
    }

    private static AcquisitionSource? GetMarketSourceTransition(AcquisitionSource source, bool isHq)
    {
        if (isHq && source == AcquisitionSource.MarketBuyNq)
        {
            return AcquisitionSource.MarketBuyHq;
        }

        if (!isHq && source == AcquisitionSource.MarketBuyHq)
        {
            return AcquisitionSource.MarketBuyNq;
        }

        return null;
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
