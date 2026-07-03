using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class TradeRequestedOrderEditorMapperTests
{
    [Fact]
    public void FromOrderCreatesEditableRowsFromRootItems()
    {
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                RootItems =
                [
                    new TradeOrderRootItemSnapshot(100, "Cobalt Plate", 999, false, 1000m)
                ]
            }
        };

        var rows = TradeRequestedOrderEditorMapper.FromOrder(order);

        var row = Assert.Single(rows);
        Assert.Equal(100, row.ItemId);
        Assert.Equal("Cobalt Plate", row.Name);
        Assert.Equal(999, row.Quantity);
        Assert.False(row.MustBeHq);
        Assert.Equal(1000m, row.EstimatedSaleValue);
    }

    [Fact]
    public void ToOutputsDropsInvalidRowsAndClampsQuantity()
    {
        var rows = new[]
        {
            new TradeRequestedOrderOutputEditorRow(100, " Plate ", 20_000, false, 10m),
            new TradeRequestedOrderOutputEditorRow(0, "Missing", 1, false, 0m),
            new TradeRequestedOrderOutputEditorRow(200, "", 1, false, 0m)
        };

        var outputs = TradeRequestedOrderEditorMapper.ToOutputs(rows);

        var output = Assert.Single(outputs);
        Assert.Equal(100, output.ItemId);
        Assert.Equal("Plate", output.Name);
        Assert.Equal(9999, output.Quantity);
    }

    [Fact]
    public void ToOutputsMergesDuplicateItemAndHqRows()
    {
        var rows = new[]
        {
            new TradeRequestedOrderOutputEditorRow(100, "Cobalt Plate", 10, false, 100m),
            new TradeRequestedOrderOutputEditorRow(100, "Cobalt Plate", 20, false, 200m),
            new TradeRequestedOrderOutputEditorRow(100, "Cobalt Plate", 5, true, 50m)
        };

        var outputs = TradeRequestedOrderEditorMapper.ToOutputs(rows);

        Assert.Contains(outputs, output => output.ItemId == 100 && !output.MustBeHq && output.Quantity == 30 && output.EstimatedSaleValue == 300m);
        Assert.Contains(outputs, output => output.ItemId == 100 && output.MustBeHq && output.Quantity == 5 && output.EstimatedSaleValue == 50m);
    }

    [Fact]
    public void HasChangesDetectsQuantityAndHqChanges()
    {
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                RootItems =
                [
                    new TradeOrderRootItemSnapshot(100, "Cobalt Plate", 999, false, 1000m)
                ]
            }
        };

        Assert.False(TradeRequestedOrderEditorMapper.HasChanges(
            order,
            [new TradeRequestedOrderOutputEditorRow(100, "Cobalt Plate", 999, false, 1000m)]));
        Assert.True(TradeRequestedOrderEditorMapper.HasChanges(
            order,
            [new TradeRequestedOrderOutputEditorRow(100, "Cobalt Plate", 1998, false, 1000m)]));
        Assert.True(TradeRequestedOrderEditorMapper.HasChanges(
            order,
            [new TradeRequestedOrderOutputEditorRow(100, "Cobalt Plate", 999, true, 1000m)]));
    }
}
