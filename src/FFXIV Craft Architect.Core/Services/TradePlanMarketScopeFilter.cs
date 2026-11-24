using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>One plan occurrence, identified by node ancestry rather than display text.</summary>
public sealed record TradePlanMarketScopeOccurrence(
    string NodeId,
    string? ParentNodeId,
    int ItemId,
    bool MustBeHq,
    AcquisitionSource Source,
    bool IsSuppressed);

/// <summary>
/// Decides which market items Trade pricing actually needs. Anything the company supplies
/// (or that sits on hand) is never bought by the crafter, so neither it nor its exclusive
/// subtree needs listings. Commissioned outputs keep their market data so "cost to buy
/// instead" stays populated.
/// </summary>
public static class TradePlanMarketScopeFilter
{
    public static HashSet<int> ComputeTradeRequiredMarketItemIds(
        IEnumerable<(int ItemId, bool MustBeHq)> rootItems,
        IReadOnlyList<TradePlanMarketScopeOccurrence> occurrences,
        IReadOnlySet<int> canBuyFromMarket,
        IReadOnlyList<TradePayrollResponsibilityLine>? materialResponsibilities)
    {
        ArgumentNullException.ThrowIfNull(rootItems);
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentNullException.ThrowIfNull(canBuyFromMarket);

        var companyProvided = (materialResponsibilities ?? [])
            .Where(line => line.Responsibility == CommissionMaterialResponsibility.Provided)
            .Select(line => (line.ItemId, line.RequiresHq))
            .ToHashSet();
        var required = new HashSet<int>();

        foreach (var (itemId, _) in rootItems)
        {
            if (canBuyFromMarket.Contains(itemId))
            {
                required.Add(itemId);
            }
        }

        var childrenByParent = occurrences
            .Where(occurrence => !string.IsNullOrEmpty(occurrence.ParentNodeId))
            .GroupBy(occurrence => occurrence.ParentNodeId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        // Suppressed occurrences (under a bought parent) are still walked: labor-aware
        // craft-vs-buy needs their prices. Only company/on-hand subtrees are pruned.
        var stack = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var knownNodeIds = occurrences
            .Select(occurrence => occurrence.NodeId)
            .ToHashSet(StringComparer.Ordinal);
        var roots = occurrences.Where(occurrence =>
            string.IsNullOrEmpty(occurrence.ParentNodeId) ||
            !knownNodeIds.Contains(occurrence.ParentNodeId));
        foreach (var root in roots)
        {
            // Commissioned outputs are always crafted, so descend unconditionally.
            if (visited.Add(root.NodeId))
            {
                stack.Push(root.NodeId);
            }
        }

        while (stack.Count > 0)
        {
            var parentId = stack.Pop();
            if (!childrenByParent.TryGetValue(parentId, out var children))
            {
                continue;
            }

            foreach (var node in children)
            {
                if (companyProvided.Contains((node.ItemId, node.MustBeHq)) ||
                    node.Source == AcquisitionSource.OnHand)
                {
                    continue;
                }

                if (canBuyFromMarket.Contains(node.ItemId))
                {
                    required.Add(node.ItemId);
                }

                if (visited.Add(node.NodeId))
                {
                    stack.Push(node.NodeId);
                }
            }
        }

        return required;
    }
}
