using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class PlanBulkEditService
{
    public static List<PlanNodeEditRow> FlattenPlanNodes(CraftingPlan plan)
    {
        var rows = new List<PlanNodeEditRow>();

        foreach (var root in plan.RootItems)
        {
            FlattenNode(root, depth: 0, rows);
        }

        return rows;
    }

    public static IEnumerable<PlanNodeEditRow> FilterNodes(
        IEnumerable<PlanNodeEditRow> rows,
        PlanNodeFilter filter)
    {
        return filter switch
        {
            PlanNodeFilter.LeavesOnly => rows.Where(row => !row.Node.Children.Any()),
            PlanNodeFilter.CraftableOnly => rows.Where(row => row.Node.Children.Any()),
            PlanNodeFilter.MarketBuyableOnly => rows.Where(row => row.Node.CanBuyFromMarket),
            PlanNodeFilter.HqCapableOnly => rows.Where(row => row.Node.CanBeHq),
            _ => rows
        };
    }

    public static PlanBulkEditResult ApplyBulkEdit(
        IEnumerable<PlanNode> nodes,
        PlanBulkEditOptions options)
    {
        var result = new PlanBulkEditResult();

        foreach (var node in nodes.DistinctBy(node => node.NodeId))
        {
            var changed = false;

            if (options.Quality != BulkQualitySetting.NoChange)
            {
                changed |= ApplyQuality(node, options.Quality, result);
            }

            if (options.Source.HasValue)
            {
                changed |= ApplySource(node, options.Source.Value, result);
            }

            if (!changed)
            {
                result.SkippedNodes++;
            }
            else
            {
                result.ChangedNodes++;
            }
        }

        return result;
    }

    public static PlanBulkEditResult RequireHqMaterials(PlanNode node, bool includeNested)
    {
        var result = new PlanBulkEditResult();

        foreach (var child in node.Children)
        {
            ApplyHqRequirement(child, includeNested, result);
        }

        return result;
    }

    private static void FlattenNode(PlanNode node, int depth, List<PlanNodeEditRow> rows)
    {
        rows.Add(new PlanNodeEditRow(node, depth));

        foreach (var child in node.Children)
        {
            FlattenNode(child, depth + 1, rows);
        }
    }

    private static bool ApplyQuality(
        PlanNode node,
        BulkQualitySetting quality,
        PlanBulkEditResult result)
    {
        if (!CanApplyHqRequirement(node))
        {
            return false;
        }

        var mustBeHq = quality == BulkQualitySetting.RequireHq;
        if (node.MustBeHq == mustBeHq)
        {
            return false;
        }

        node.MustBeHq = mustBeHq;

        if (node.CanBuyFromMarket)
        {
            if (mustBeHq && node.Source == AcquisitionSource.MarketBuyNq)
            {
                node.Source = AcquisitionSource.MarketBuyHq;
                result.SwitchedMarketBuys++;
            }
            else if (!mustBeHq && node.Source == AcquisitionSource.MarketBuyHq)
            {
                node.Source = AcquisitionSource.MarketBuyNq;
                result.SwitchedMarketBuys++;
            }
        }

        return true;
    }

    private static bool ApplySource(
        PlanNode node,
        AcquisitionSource source,
        PlanBulkEditResult result)
    {
        if (!CanApplySource(node, source) || node.Source == source)
        {
            return false;
        }

        node.Source = source;
        if (source == AcquisitionSource.MarketBuyHq)
        {
            node.MustBeHq = true;
        }
        else if (source == AcquisitionSource.MarketBuyNq)
        {
            node.MustBeHq = false;
        }

        return true;
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

    private static bool CanApplySource(PlanNode node, AcquisitionSource source)
    {
        return source switch
        {
            AcquisitionSource.Craft => node.Children.Any() || node.CanCraft,
            AcquisitionSource.MarketBuyNq => node.CanBuyFromMarket,
            AcquisitionSource.MarketBuyHq => node.CanBuyFromMarket && node.CanBeHq,
            AcquisitionSource.VendorBuy => node.CanBuyFromVendor,
            AcquisitionSource.UnknownSource => !node.CanBuyFromMarket && !node.CanBuyFromVendor && !node.Children.Any(),
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

public sealed record PlanNodeEditRow(PlanNode Node, int Depth);

public sealed class PlanBulkEditOptions
{
    public BulkQualitySetting Quality { get; set; }
    public AcquisitionSource? Source { get; set; }
}

public enum BulkQualitySetting
{
    NoChange,
    RequireHq,
    RequireNq
}

public enum PlanNodeFilter
{
    All,
    LeavesOnly,
    CraftableOnly,
    MarketBuyableOnly,
    HqCapableOnly
}
