using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Desktop.Services;

public sealed class DesktopSmokeRecipePlanBuilder : IDesktopRecipePlanBuilder
{
    public Task<CraftingPlan> BuildPlanAsync(
        IReadOnlyList<(int ItemId, string Name, int Quantity, bool MustBeHq)> targetItems,
        string dataCenter,
        string world,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var plan = new CraftingPlan
        {
            Name = "Desktop Smoke Plan",
            DataCenter = dataCenter,
            World = world,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };

        foreach (var target in targetItems)
        {
            plan.RootItems.Add(CreateRootNode(target));
        }

        return Task.FromResult(plan);
    }

    private static PlanNode CreateRootNode((int ItemId, string Name, int Quantity, bool MustBeHq) target)
    {
        var childQuantity = Math.Max(1, target.Quantity * 2);
        var root = new PlanNode
        {
            ItemId = target.ItemId,
            Name = target.Name,
            Quantity = target.Quantity,
            MustBeHq = target.MustBeHq,
            CanBeHq = true,
            CanBuyFromMarket = true,
            RecipeLevel = 1,
            Job = "Smoke",
            Yield = 1,
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            MarketPrice = 125,
            HqMarketPrice = 175
        };

        var child = new PlanNode
        {
            ItemId = target.ItemId + 900_000,
            Name = $"{target.Name} Smoke Material",
            Quantity = childQuantity,
            MustBeHq = false,
            CanBeHq = false,
            CanBuyFromMarket = true,
            RecipeLevel = 1,
            Job = string.Empty,
            Yield = 1,
            Source = AcquisitionSource.MarketBuyNq,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            MarketPrice = 25
        };

        child.Parent = root;
        root.Children.Add(child);
        return root;
    }
}
