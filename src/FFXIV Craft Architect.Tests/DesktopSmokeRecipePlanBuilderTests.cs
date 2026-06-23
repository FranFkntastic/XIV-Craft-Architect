using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Desktop.Services;

namespace FFXIV_Craft_Architect.Tests;

[Collection(DesktopTestCollection.Name)]
public sealed class DesktopSmokeRecipePlanBuilderTests
{
    [Fact]
    public async Task BuildPlanAsync_ReturnsDeterministicCraftingPlanFromTargets()
    {
        var builder = new DesktopSmokeRecipePlanBuilder();

        var plan = await builder.BuildPlanAsync(
            [(5107, "Cobalt Plate", 12, true)],
            "Aether",
            "Jenova");

        Assert.Equal("Desktop Smoke Plan", plan.Name);
        Assert.Equal("Aether", plan.DataCenter);
        Assert.Equal("Jenova", plan.World);

        var root = Assert.Single(plan.RootItems);
        Assert.Equal(5107, root.ItemId);
        Assert.Equal("Cobalt Plate", root.Name);
        Assert.Equal(12, root.Quantity);
        Assert.True(root.MustBeHq);
        Assert.Equal(AcquisitionSource.Craft, root.Source);

        var material = Assert.Single(root.Children);
        Assert.Same(root, material.Parent);
        Assert.Equal("Cobalt Plate Smoke Material", material.Name);
        Assert.Equal(24, material.Quantity);
        Assert.Equal(AcquisitionSource.MarketBuyNq, material.Source);
    }
}
