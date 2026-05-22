using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class MarketShoppingServiceVendorOverrideTests
{
    private readonly MarketShoppingService _service;

    public MarketShoppingServiceVendorOverrideTests()
    {
        var cacheMock = new Mock<IMarketCacheService>();
        _service = new MarketShoppingService(cacheMock.Object, logger: new NullLogger<MarketShoppingService>());
    }

    [Fact]
    public void ApplyVendorPurchaseOverrides_VendorBuyItem_UsesSelectedVendorAsRecommendation()
    {
        // Arrange
        var plan = new CraftingPlan
        {
            RootItems =
            {
                new PlanNode
                {
                    ItemId = 100,
                    Name = "Iron Ore",
                    Source = AcquisitionSource.VendorBuy,
                    SelectedVendorIndex = 1,
                    VendorOptions =
                    {
                        new VendorInfo { Name = "Material Supplier", Location = "Limsa", Price = 18, Currency = "gil" },
                        new VendorInfo { Name = "Material Supplier", Location = "Gridania", Price = 18, Currency = "gil" }
                    }
                }
            }
        };

        var plans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 100,
                Name = "Iron Ore",
                QuantityNeeded = 20,
                RecommendedWorld = new WorldShoppingSummary { WorldName = "Leviathan", TotalCost = 5000 },
                WorldOptions = new List<WorldShoppingSummary>
                {
                    new() { WorldName = "Leviathan", TotalCost = 5000, TotalQuantityPurchased = 20, HasSufficientStock = true }
                }
            }
        };

        // Act
        _service.ApplyVendorPurchaseOverrides(plan, plans);

        // Assert
        var overridden = plans.Single();
        Assert.NotNull(overridden.RecommendedWorld);
        Assert.Equal("Vendor", overridden.RecommendedWorld!.WorldName);
        Assert.Equal(360, overridden.RecommendedWorld.TotalCost);
        Assert.Equal("Material Supplier (Gridania)", overridden.RecommendedWorld.VendorName);
        Assert.Null(overridden.RecommendedSplit);
        Assert.Equal(2, overridden.Vendors.Count);
        Assert.Contains(overridden.WorldOptions, w => w.WorldName == "Vendor");
    }

    [Fact]
    public void ApplyVendorPurchaseOverrides_NoSelectedVendor_FallsBackToCheapestGilVendor()
    {
        // Arrange
        var plan = new CraftingPlan
        {
            RootItems =
            {
                new PlanNode
                {
                    ItemId = 200,
                    Name = "Maple Lumber",
                    Source = AcquisitionSource.VendorBuy,
                    SelectedVendorIndex = -1,
                    VendorOptions =
                    {
                        new VendorInfo { Name = "Vendor A", Location = "A", Price = 25, Currency = "gil" },
                        new VendorInfo { Name = "Vendor B", Location = "B", Price = 12, Currency = "gil" }
                    }
                }
            }
        };

        var plans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Maple Lumber",
                QuantityNeeded = 5,
                WorldOptions = new List<WorldShoppingSummary>()
            }
        };

        // Act
        _service.ApplyVendorPurchaseOverrides(plan, plans);

        // Assert
        var overridden = plans.Single();
        Assert.NotNull(overridden.RecommendedWorld);
        Assert.Equal("Vendor", overridden.RecommendedWorld!.WorldName);
        Assert.Equal(60, overridden.RecommendedWorld.TotalCost);
        Assert.Equal("Vendor B (B)", overridden.RecommendedWorld.VendorName);
    }
}
