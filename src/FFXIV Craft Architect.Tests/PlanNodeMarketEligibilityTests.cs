using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Tests;

public class PlanNodeMarketEligibilityTests
{
    [Fact]
    public void CanBuyFromMarket_DefaultsToTrue_ForExistingPlans()
    {
        var node = new PlanNode();

        Assert.True(node.CanBuyFromMarket);
    }

    [Fact]
    public void GarlandItem_CanListOnMarket_IsFalse_WhenTradeableButUnlistable()
    {
        var item = new GarlandItem
        {
            TradeableRaw = 1,
            UnlistableRaw = 1
        };

        Assert.True(item.Tradeable);
        Assert.True(item.Unlistable);
        Assert.False(item.CanListOnMarket);
    }

    [Fact]
    public void GarlandItem_CanListOnMarket_DefaultsToTrue_WhenMarketFlagsMissing()
    {
        var item = new GarlandItem();

        Assert.True(item.CanListOnMarket);
    }

    [Fact]
    public void EnsureValidAcquisitionSource_NonMarketCraftableMarketBuy_SwitchesToCraft()
    {
        var node = new PlanNode
        {
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = false,
            CanCraft = true
        };

        node.EnsureValidAcquisitionSource();

        Assert.Equal(AcquisitionSource.Craft, node.Source);
    }

    [Fact]
    public void EnsureValidAcquisitionSource_NonMarketVendorAvailableMarketBuy_SwitchesToVendor()
    {
        var node = new PlanNode
        {
            Source = AcquisitionSource.MarketBuyHq,
            CanBuyFromMarket = false,
            CanCraft = false,
            CanBuyFromVendor = true
        };

        node.EnsureValidAcquisitionSource();

        Assert.Equal(AcquisitionSource.VendorBuy, node.Source);
    }

    [Fact]
    public void EnsureValidAcquisitionSource_NonMarketUnsupportedMarketBuy_SwitchesToUnknownSource()
    {
        var node = new PlanNode
        {
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = false,
            CanCraft = false,
            CanBuyFromVendor = false
        };

        node.EnsureValidAcquisitionSource();

        Assert.Equal(AcquisitionSource.UnknownSource, node.Source);
        Assert.Equal(PriceSource.Untradeable, node.PriceSource);
    }
}
