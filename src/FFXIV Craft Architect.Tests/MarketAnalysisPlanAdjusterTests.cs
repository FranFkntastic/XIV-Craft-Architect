using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisPlanAdjusterTests
{
    [Fact]
    public void ExcludeWorldForItem_RemovesOnlyThatWorldAndDoesNotMutateSourcePlan()
    {
        var plans = new List<DetailedShoppingPlan>
        {
            CreatePlan(100, "Cobalt Ore", ("Goblin", "Crystal", 100), ("Balmung", "Crystal", 200)),
            CreatePlan(200, "Cobalt Ingot", ("Goblin", "Crystal", 300))
        };

        var adjusted = MarketAnalysisPlanAdjuster.ExcludeWorldForItem(
            plans,
            100,
            new MarketWorldKey("Crystal", "Goblin"));

        var adjustedOre = Assert.Single(adjusted, plan => plan.ItemId == 100);
        Assert.DoesNotContain(adjustedOre.WorldOptions, world => world.WorldName == "Goblin");
        Assert.Equal("Balmung", adjustedOre.RecommendedWorld?.WorldName);

        var sourceOre = Assert.Single(plans, plan => plan.ItemId == 100);
        Assert.Contains(sourceOre.WorldOptions, world => world.WorldName == "Goblin");
        Assert.Equal("Goblin", sourceOre.RecommendedWorld?.WorldName);

        var adjustedIngot = Assert.Single(adjusted, plan => plan.ItemId == 200);
        Assert.Contains(adjustedIngot.WorldOptions, world => world.WorldName == "Goblin");
    }

    [Fact]
    public void ExcludeWorlds_RemovesTemporaryBlacklistWorldsFromAllPlans()
    {
        var plans = new List<DetailedShoppingPlan>
        {
            CreatePlan(100, "Cobalt Ore", ("Goblin", "Crystal", 100), ("Balmung", "Crystal", 200)),
            CreatePlan(200, "Cobalt Ingot", ("Goblin", "Crystal", 300), ("Diabolos", "Crystal", 400))
        };

        var adjusted = MarketAnalysisPlanAdjuster.ExcludeWorlds(
            plans,
            [new MarketWorldKey("Crystal", "Goblin")]);

        Assert.All(adjusted, plan =>
            Assert.DoesNotContain(plan.WorldOptions, world => world.WorldName == "Goblin"));
    }

    [Fact]
    public void ExcludeItemWorlds_RemovesWorldOnlyFromMatchingItem()
    {
        var plans = new List<DetailedShoppingPlan>
        {
            CreatePlan(100, "Cobalt Ore", ("Goblin", "Crystal", 100), ("Balmung", "Crystal", 200)),
            CreatePlan(200, "Cobalt Ingot", ("Goblin", "Crystal", 300), ("Diabolos", "Crystal", 400))
        };

        var adjusted = MarketAnalysisPlanAdjuster.ExcludeItemWorlds(
            plans,
            [new MarketItemWorldKey(100, new MarketWorldKey("Crystal", "Goblin"))]);

        var ore = Assert.Single(adjusted, plan => plan.ItemId == 100);
        var ingot = Assert.Single(adjusted, plan => plan.ItemId == 200);
        Assert.DoesNotContain(ore.WorldOptions, world => world.WorldName == "Goblin");
        Assert.Contains(ingot.WorldOptions, world => world.WorldName == "Goblin");
    }

    [Fact]
    public void ExcludeWorldForItem_RemovesUnderfilledSplitRecommendation()
    {
        var plan = CreatePlan(100, "Cobalt Ore", ("Goblin", "Crystal", 100), ("Balmung", "Crystal", 200));
        plan.RecommendedWorld = null;
        plan.RecommendedSplit =
        [
            new SplitWorldPurchase
            {
                WorldName = "Goblin",
                DataCenter = "Crystal",
                QuantityToBuy = 6,
                TotalCost = 600
            },
            new SplitWorldPurchase
            {
                WorldName = "Balmung",
                DataCenter = "Crystal",
                QuantityToBuy = 4,
                TotalCost = 800
            }
        ];

        var adjusted = MarketAnalysisPlanAdjuster.ExcludeWorldForItem(
            [plan],
            100,
            new MarketWorldKey("Crystal", "Goblin"));

        var adjustedPlan = Assert.Single(adjusted);
        Assert.Null(adjustedPlan.RecommendedSplit);
    }

    [Fact]
    public void MarketWorldBlacklist_ExpiresEntriesAfterConfiguredDuration()
    {
        var now = new DateTimeOffset(2026, 5, 27, 12, 0, 0, TimeSpan.Zero);
        var blacklist = new MarketWorldBlacklist();

        blacklist.Add(new MarketWorldKey("Crystal", "Balmung"), TimeSpan.FromMinutes(30), now);

        Assert.Contains(new MarketWorldKey("Crystal", "Balmung"), blacklist.GetActiveWorlds(now.AddMinutes(29)));
        Assert.DoesNotContain(new MarketWorldKey("Crystal", "Balmung"), blacklist.GetActiveWorlds(now.AddMinutes(31)));
    }

    private static DetailedShoppingPlan CreatePlan(
        int itemId,
        string name,
        params (string World, string DataCenter, long TotalCost)[] worlds)
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = 10
        };

        foreach (var world in worlds)
        {
            plan.WorldOptions.Add(new WorldShoppingSummary
            {
                WorldName = world.World,
                DataCenter = world.DataCenter,
                TotalCost = world.TotalCost,
                TotalQuantityPurchased = 10,
                ValueScore = world.TotalCost
            });
        }

        plan.RecommendedWorld = plan.WorldOptions.FirstOrDefault();
        return plan;
    }
}
