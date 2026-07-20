using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class NativePlanImportClassifierTests
{
    [Fact]
    public async Task RequiresRecipeGraphBuildAsync_FlatPlanWithCraftableRoot_Rebuilds()
    {
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode { ItemId = 12912, Name = "Celestine", Quantity = 216 },
                new PlanNode { ItemId = 5512, Name = "Clear Glass Lens", Quantity = 720 }
            ]
        };

        var requiresBuild = await NativePlanImportClassifier.RequiresRecipeGraphBuildAsync(
            plan,
            (itemId, _) => Task.FromResult<Recipe?>(itemId == 12912 ? new Recipe { ItemId = itemId } : null));

        Assert.True(requiresBuild);
    }

    [Fact]
    public async Task RequiresRecipeGraphBuildAsync_FlatDirectPurchasePlan_PreservesPlan()
    {
        var plan = new CraftingPlan
        {
            RootItems = [new PlanNode { ItemId = 999, Name = "Purchase only", Quantity = 1 }]
        };

        var requiresBuild = await NativePlanImportClassifier.RequiresRecipeGraphBuildAsync(
            plan,
            (_, _) => Task.FromResult<Recipe?>(null));

        Assert.False(requiresBuild);
    }

    [Fact]
    public async Task RequiresRecipeGraphBuildAsync_StructuredPlan_PreservesWithoutRecipeLookup()
    {
        var lookupCount = 0;
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 12912,
                    Name = "Celestine",
                    Quantity = 216,
                    Children = [new PlanNode { ItemId = 5106, Name = "Electrum Ingot", Quantity = 432 }]
                }
            ]
        };

        var requiresBuild = await NativePlanImportClassifier.RequiresRecipeGraphBuildAsync(
            plan,
            (_, _) =>
            {
                lookupCount++;
                return Task.FromResult<Recipe?>(new Recipe());
            });

        Assert.False(requiresBuild);
        Assert.Equal(0, lookupCount);
    }

    [Fact]
    public async Task RequiresRecipeGraphBuildAsync_PropagatesCancellationFromRecipeLookup()
    {
        var plan = new CraftingPlan
        {
            RootItems = [new PlanNode { ItemId = 12912, Name = "Celestine", Quantity = 216 }]
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativePlanImportClassifier.RequiresRecipeGraphBuildAsync(
                plan,
                (_, token) => Task.FromCanceled<Recipe?>(token),
                cancellation.Token));
    }
}
