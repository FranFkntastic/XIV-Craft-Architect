using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class PlanBulkEditService
{
    public static PlanBulkEditResult RequireHqMaterials(PlanNode node, bool includeNested)
    {
        var result = new PlanBulkEditResult();

        foreach (var child in node.Children)
        {
            ApplyHqRequirement(child, includeNested, result);
        }

        return result;
    }

    public static PlanBulkEditResult RequireHqMaterialsForHqRoots(CraftingPlan plan, bool includeNested)
    {
        var result = new PlanBulkEditResult();

        foreach (var root in plan.RootItems.Where(r => r.MustBeHq && r.Children.Any()))
        {
            foreach (var child in root.Children)
            {
                ApplyHqRequirement(child, includeNested, result);
            }
        }

        return result;
    }

    private static void ApplyHqRequirement(PlanNode node, bool includeNested, PlanBulkEditResult result)
    {
        if (CanApplyHqRequirement(node))
        {
            var wasChanged = !node.MustBeHq;
            node.MustBeHq = true;

            if (node.Source == AcquisitionSource.MarketBuyNq && node.CanBuyFromMarket)
            {
                node.Source = AcquisitionSource.MarketBuyHq;
                wasChanged = true;
                result.SwitchedMarketBuys++;
            }

            if (wasChanged)
            {
                result.ChangedNodes++;
            }
        }
        else
        {
            result.SkippedNodes++;
        }

        if (!includeNested)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            ApplyHqRequirement(child, includeNested, result);
        }
    }

    private static bool CanApplyHqRequirement(PlanNode node)
    {
        if (!node.CanBeHq)
        {
            return false;
        }

        return node.Source switch
        {
            AcquisitionSource.Craft => true,
            AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq => node.CanBuyFromMarket,
            _ => false
        };
    }
}

public sealed class PlanBulkEditResult
{
    public int ChangedNodes { get; set; }
    public int SwitchedMarketBuys { get; set; }
    public int SkippedNodes { get; set; }
}
