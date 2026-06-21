using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradeRequestedOrderWorkflowTests
{
    [Fact]
    public void CreateSuggestedTitle_UsesHighestEstimatedSaleValue()
    {
        var title = TradeRequestedOrderWorkflow.CreateSuggestedTitle(
        [
            new TradeRequestedOrderOutput(100, "Bulk Ingot", 999, MustBeHq: false, EstimatedSaleValue: 500_000m),
            new TradeRequestedOrderOutput(200, "Rare Plate", 1, MustBeHq: false, EstimatedSaleValue: 2_000_000m)
        ]);

        Assert.Equal("Rare Plate Commission", title);
    }

    [Fact]
    public void CreateSuggestedTitle_WhenNoSaleValueFallsBackToLargestQuantity()
    {
        var title = TradeRequestedOrderWorkflow.CreateSuggestedTitle(
        [
            new TradeRequestedOrderOutput(100, "Small Batch", 2, MustBeHq: false, EstimatedSaleValue: 0m),
            new TradeRequestedOrderOutput(200, "Large Batch", 999, MustBeHq: false, EstimatedSaleValue: 0m)
        ]);

        Assert.Equal("Large Batch Commission", title);
    }

    [Fact]
    public void BuildMaterialSnapshots_ExcludesRequestedOutputsFromMaterials()
    {
        var materials = TradeRequestedOrderWorkflow.BuildMaterialSnapshots(
            [
                new MaterialAggregate
                {
                    ItemId = 100,
                    Name = "Requested Root",
                    TotalQuantity = 1,
                    UnitPrice = 10_000m
                },
                new MaterialAggregate
                {
                    ItemId = 300,
                    Name = "Actual Ingredient",
                    TotalQuantity = 4,
                    UnitPrice = 250m,
                    RequiresHq = true
                }
            ],
            [
                new TradeRequestedOrderOutput(100, "Requested Root", 1, MustBeHq: false, EstimatedSaleValue: 10_000m)
            ]);

        var material = Assert.Single(materials);
        Assert.Equal(300, material.ItemId);
        Assert.Equal("Actual Ingredient", material.Name);
        Assert.Equal(4, material.Quantity);
        Assert.True(material.RequiresHq);
        Assert.Equal(1_000m, material.TotalCost);
    }
}
