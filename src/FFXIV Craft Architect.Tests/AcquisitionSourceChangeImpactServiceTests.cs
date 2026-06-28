using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class AcquisitionSourceChangeImpactServiceTests
{
    private readonly AcquisitionSourceChangeImpactService _service = new();

    [Fact]
    public void GetMarketRefreshItemIds_WhenBoughtPrecraftBecomesCraft_ReturnsOnlyChangedChildDemand()
    {
        var before = new RecipeDemandProjection(
            AllPlanDemand: [],
            MarketAnalysisCandidates: [Row(100, "Cobalt Ingot", 4, AcquisitionSource.MarketBuyNq)],
            ActiveProcurementDemand: [Row(100, "Cobalt Ingot", 4, AcquisitionSource.MarketBuyNq)],
            SuppressedDemand: [Row(200, "Cobalt Ore", 8, AcquisitionSource.MarketBuyNq)]);
        var after = new RecipeDemandProjection(
            AllPlanDemand: [],
            MarketAnalysisCandidates: [
                Row(100, "Cobalt Ingot", 4, AcquisitionSource.Craft),
                Row(200, "Cobalt Ore", 8, AcquisitionSource.MarketBuyNq),
                Row(201, "Ice Shard", 4, AcquisitionSource.MarketBuyNq)
            ],
            ActiveProcurementDemand: [
                Row(200, "Cobalt Ore", 8, AcquisitionSource.MarketBuyNq),
                Row(201, "Ice Shard", 4, AcquisitionSource.MarketBuyNq)
            ],
            SuppressedDemand: []);

        var result = _service.GetMarketRefreshItemIds(
            before,
            after,
            changedItemId: 100,
            previousSource: AcquisitionSource.MarketBuyNq,
            newSource: AcquisitionSource.Craft);

        Assert.Equal([200, 201], result);
    }

    [Fact]
    public void GetMarketRefreshItemIds_WhenChildDemandQuantityChanges_ReturnsExistingChildItem()
    {
        var before = new RecipeDemandProjection(
            AllPlanDemand: [],
            MarketAnalysisCandidates: [
                Row(200, "Cobalt Ore", 8, AcquisitionSource.MarketBuyNq),
                Row(201, "Ice Shard", 4, AcquisitionSource.MarketBuyNq)
            ],
            ActiveProcurementDemand: [
                Row(200, "Cobalt Ore", 8, AcquisitionSource.MarketBuyNq),
                Row(201, "Ice Shard", 4, AcquisitionSource.MarketBuyNq)
            ],
            SuppressedDemand: []);
        var after = new RecipeDemandProjection(
            AllPlanDemand: [],
            MarketAnalysisCandidates: [
                Row(200, "Cobalt Ore", 16, AcquisitionSource.MarketBuyNq),
                Row(201, "Ice Shard", 4, AcquisitionSource.MarketBuyNq)
            ],
            ActiveProcurementDemand: [
                Row(200, "Cobalt Ore", 16, AcquisitionSource.MarketBuyNq),
                Row(201, "Ice Shard", 4, AcquisitionSource.MarketBuyNq)
            ],
            SuppressedDemand: []);

        var result = _service.GetMarketRefreshItemIds(
            before,
            after,
            changedItemId: 100,
            previousSource: AcquisitionSource.MarketBuyNq,
            newSource: AcquisitionSource.Craft);

        Assert.Equal([200], result);
    }

    [Fact]
    public void GetMarketRefreshItemIds_WhenCraftBecomesMarket_ReturnsChangedItem()
    {
        var before = new RecipeDemandProjection(
            AllPlanDemand: [],
            MarketAnalysisCandidates: [
                Row(100, "Cobalt Ingot", 4, AcquisitionSource.Craft),
                Row(200, "Cobalt Ore", 8, AcquisitionSource.MarketBuyNq)
            ],
            ActiveProcurementDemand: [Row(200, "Cobalt Ore", 8, AcquisitionSource.MarketBuyNq)],
            SuppressedDemand: []);
        var after = new RecipeDemandProjection(
            AllPlanDemand: [],
            MarketAnalysisCandidates: [
                Row(100, "Cobalt Ingot", 4, AcquisitionSource.MarketBuyNq),
                Row(200, "Cobalt Ore", 8, AcquisitionSource.MarketBuyNq)
            ],
            ActiveProcurementDemand: [Row(100, "Cobalt Ingot", 4, AcquisitionSource.MarketBuyNq)],
            SuppressedDemand: [Row(200, "Cobalt Ore", 8, AcquisitionSource.MarketBuyNq)]);

        var result = _service.GetMarketRefreshItemIds(
            before,
            after,
            changedItemId: 100,
            previousSource: AcquisitionSource.Craft,
            newSource: AcquisitionSource.MarketBuyNq);

        Assert.Equal([100], result);
    }

    [Fact]
    public void GetMarketRefreshItemIds_WhenCraftBecomesVendor_ReturnsNoMarketRefresh()
    {
        var before = new RecipeDemandProjection(
            AllPlanDemand: [],
            MarketAnalysisCandidates: [
                Row(100, "Iron Ore", 4, AcquisitionSource.Craft),
                Row(200, "Fire Shard", 2, AcquisitionSource.MarketBuyNq)
            ],
            ActiveProcurementDemand: [Row(200, "Fire Shard", 2, AcquisitionSource.MarketBuyNq)],
            SuppressedDemand: []);
        var after = new RecipeDemandProjection(
            AllPlanDemand: [],
            MarketAnalysisCandidates: [
                Row(100, "Iron Ore", 4, AcquisitionSource.VendorBuy),
                Row(200, "Fire Shard", 2, AcquisitionSource.MarketBuyNq)
            ],
            ActiveProcurementDemand: [Row(100, "Iron Ore", 4, AcquisitionSource.VendorBuy)],
            SuppressedDemand: [Row(200, "Fire Shard", 2, AcquisitionSource.MarketBuyNq)]);

        var result = _service.GetMarketRefreshItemIds(
            before,
            after,
            changedItemId: 100,
            previousSource: AcquisitionSource.Craft,
            newSource: AcquisitionSource.VendorBuy);

        Assert.Empty(result);
    }

    private static RecipeDemandRow Row(
        int itemId,
        string itemName,
        int quantity,
        AcquisitionSource source,
        bool mustBeHq = false)
    {
        return new RecipeDemandRow(
            viewKind: RecipeDemandViewKind.ActiveProcurement,
            nodeId: $"{itemId}-{source}-{quantity}",
            itemId: itemId,
            itemName: itemName,
            iconId: itemId,
            quantity: quantity,
            quantityBasis: RecipeDemandQuantityBasis.PlanNodeQuantity,
            mustBeHq: mustBeHq,
            source: source,
            sourceReason: AcquisitionSourceReason.UserSelected,
            hasChildren: false,
            canBuyFromMarket: true,
            canBuyFromVendor: source == AcquisitionSource.VendorBuy,
            unitPrice: 0,
            parentNodeId: null,
            parentItemName: "Parent",
            parentOperationNodeId: null,
            parentRecipeId: null,
            operationNodeId: null,
            recipeId: null,
            suppressedByNodeId: null,
            suppressedByItemId: null,
            suppressedByItemName: null);
    }
}
