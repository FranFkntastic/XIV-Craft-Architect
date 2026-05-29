using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class StoredPlanSnapshotMetricsTests
{
    [Fact]
    public void FromStoredPlan_CountsPayloadSizesAndMarketEvidence()
    {
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 1,
                    Name = "Root Ω",
                    Quantity = 1,
                    Children =
                    [
                        new PlanNode
                        {
                            ItemId = 2,
                            Name = "Child",
                            Quantity = 2
                        }
                    ]
                }
            ]
        };
        var shoppingPlans = new List<DetailedShoppingPlan>
        {
            new() { ItemId = 1, QuantityNeeded = 1 },
            new() { ItemId = 2, QuantityNeeded = 2 }
        };
        var analyses = new List<MarketItemAnalysis>
        {
            new() { ItemId = 1, QuantityNeeded = 1 }
        };
        var storedPlan = new StoredPlan
        {
            PlanJson = JsonSerializer.Serialize(plan),
            MarketPlansJson = JsonSerializer.Serialize(shoppingPlans),
            MarketItemAnalysesJson = JsonSerializer.Serialize(analyses)
        };

        var metrics = StoredPlanSnapshotMetrics.FromStoredPlan(storedPlan);

        Assert.Equal(2, metrics.PlanNodeCount);
        Assert.Equal(2, metrics.ShoppingPlanCount);
        Assert.Equal(1, metrics.MarketAnalysisCount);
        Assert.Equal(Encoding.UTF8.GetByteCount(storedPlan.PlanJson!), metrics.PlanJsonBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(storedPlan.MarketPlansJson!), metrics.MarketPlansJsonBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(storedPlan.MarketItemAnalysesJson!), metrics.MarketItemAnalysesJsonBytes);
        Assert.Equal(
            metrics.PlanJsonBytes + metrics.MarketPlansJsonBytes + metrics.MarketItemAnalysesJsonBytes,
            metrics.TotalJsonBytes);
    }

    [Fact]
    public void FromStoredPlan_ToleratesMissingOrInvalidJson()
    {
        var storedPlan = new StoredPlan
        {
            PlanJson = "{not valid",
            MarketPlansJson = null,
            MarketItemAnalysesJson = string.Empty
        };

        var metrics = StoredPlanSnapshotMetrics.FromStoredPlan(storedPlan);

        Assert.Equal(0, metrics.PlanNodeCount);
        Assert.Equal(0, metrics.ShoppingPlanCount);
        Assert.Equal(0, metrics.MarketAnalysisCount);
        Assert.True(metrics.PlanJsonBytes > 0);
        Assert.Equal(0, metrics.MarketPlansJsonBytes);
        Assert.Equal(0, metrics.MarketItemAnalysesJsonBytes);
    }
}
