using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AcquisitionPlanningServiceTests
{
    [Fact]
    public void GetMarketAnalysisCandidates_IncludesCraftableIntermediateAndLeafCandidates()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var intermediate = plan.RootItems[0].Children[0];
        intermediate.Source = AcquisitionSource.Craft;

        var candidates = AcquisitionPlanningService.GetMarketAnalysisCandidates(plan);

        Assert.Contains(candidates, item => item.ItemId == 200 && item.TotalQuantity == 2);
        Assert.Contains(candidates, item => item.ItemId == 300 && item.TotalQuantity == 6);
    }

    [Fact]
    public void GetActiveProcurementItems_PrunesChildrenWhenParentIsBought()
    {
        var plan = CreatePlanWithBoughtIntermediate();

        var activeItems = AcquisitionPlanningService.GetActiveProcurementItems(plan);

        var item = Assert.Single(activeItems);
        Assert.Equal(200, item.ItemId);
        Assert.Equal(2, item.TotalQuantity);
    }

    [Fact]
    public void FilterShoppingPlansForActiveProcurement_RemovesChildPlansWhenParentIsBought()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new() { ItemId = 200, Name = "Intermediate", QuantityNeeded = 2 },
            new() { ItemId = 300, Name = "Raw Material", QuantityNeeded = 6 }
        };

        var procurementPlans = AcquisitionPlanningService.FilterShoppingPlansForActiveProcurement(plan, marketPlans);

        var planResult = Assert.Single(procurementPlans);
        Assert.Equal(200, planResult.ItemId);
    }

    [Fact]
    public void GetProcurementEvidenceSummary_CountsActiveAnalyzedMissingAndSuppressedCandidates()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Intermediate",
                QuantityNeeded = 2,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Aether",
                    TotalCost = 100,
                    TotalQuantityPurchased = 2
                }
            },
            new()
            {
                ItemId = 300,
                Name = "Raw Material",
                QuantityNeeded = 6,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Primal",
                    TotalCost = 50,
                    TotalQuantityPurchased = 6
                }
            }
        };

        var summary = AcquisitionPlanningService.GetProcurementEvidenceSummary(plan, marketPlans);

        Assert.Equal(1, summary.ActiveProcurementItemCount);
        Assert.Equal(1, summary.ActiveItemsWithEvidence);
        Assert.Equal(0, summary.ActiveItemsMissingEvidence);
        Assert.Equal(1, summary.SuppressedMarketCandidateCount);
        Assert.True(summary.HasCompleteActiveEvidence);
    }

    [Fact]
    public void GetProcurementEvidenceSummary_TreatsErroredPlanAsMissingEvidence()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Intermediate",
                QuantityNeeded = 2,
                Error = "No market data"
            }
        };

        var summary = AcquisitionPlanningService.GetProcurementEvidenceSummary(plan, marketPlans);

        Assert.Equal(0, summary.ActiveItemsWithEvidence);
        Assert.Equal(1, summary.ActiveItemsMissingEvidence);
        Assert.False(summary.HasCompleteActiveEvidence);
    }

    [Fact]
    public void CanReuseProcurementEvidence_SplitDisabled_ReusesCompleteSingleWorldEvidence()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Intermediate",
                QuantityNeeded = 2,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Siren",
                    TotalCost = 100,
                    TotalQuantityPurchased = 2
                }
            }
        };

        var canReuse = AcquisitionPlanningService.CanReuseProcurementEvidence(
            plan,
            marketPlans,
            enableMultiWorldSplits: false);

        Assert.True(canReuse);
    }

    [Fact]
    public void CanReuseProcurementEvidence_SplitEnabled_RejectsSingleWorldOnlyEvidence()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Intermediate",
                QuantityNeeded = 2,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Siren",
                    TotalCost = 100,
                    TotalQuantityPurchased = 2
                }
            }
        };

        var canReuse = AcquisitionPlanningService.CanReuseProcurementEvidence(
            plan,
            marketPlans,
            enableMultiWorldSplits: true);

        Assert.False(canReuse);
    }

    [Fact]
    public void CanReuseProcurementEvidence_SplitEnabled_ReusesActiveSplitEvidence()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Intermediate",
                QuantityNeeded = 2,
                RecommendedSplit =
                [
                    new SplitWorldPurchase
                    {
                        WorldName = "Siren",
                        QuantityToBuy = 1,
                        TotalCost = 40
                    },
                    new SplitWorldPurchase
                    {
                        WorldName = "Leviathan",
                        QuantityToBuy = 1,
                        TotalCost = 50
                    }
                ]
            }
        };

        var canReuse = AcquisitionPlanningService.CanReuseProcurementEvidence(
            plan,
            marketPlans,
            enableMultiWorldSplits: true);

        Assert.True(canReuse);
    }

    [Fact]
    public void CalculateCraftCost_UsesMarketEvidenceForBoughtChildren()
    {
        var ingot = new PlanNode
        {
            ItemId = 1,
            Name = "Silver Ingot",
            Quantity = 120,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
        var ore = new PlanNode
        {
            ItemId = 2,
            Name = "Silver Ore",
            Quantity = 360,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 324,
            Parent = ingot
        };
        var shard = new PlanNode
        {
            ItemId = 3,
            Name = "Ice Shard",
            Quantity = 240,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 45,
            Parent = ingot
        };
        ingot.Children.Add(ore);
        ingot.Children.Add(shard);

        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 2,
                Name = "Silver Ore",
                QuantityNeeded = 360,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Rafflesia",
                    TotalCost = 45_734,
                    TotalQuantityPurchased = 370
                }
            },
            new()
            {
                ItemId = 3,
                Name = "Ice Shard",
                QuantityNeeded = 240,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Rafflesia",
                    TotalCost = 10_800,
                    TotalQuantityPurchased = 240
                }
            }
        };

        var cost = AcquisitionPlanningService.CalculateCraftCost(ingot, marketPlans);

        Assert.Equal(56_534, cost);
    }

    [Fact]
    public void CalculateCraftCost_PrefersRecommendedSplitCostForBoughtChildren()
    {
        var craft = new PlanNode
        {
            ItemId = 1,
            Name = "Route Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
        var boughtChild = new PlanNode
        {
            ItemId = 2,
            Name = "Route Material",
            Quantity = 10,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 1_000,
            Parent = craft
        };
        craft.Children.Add(boughtChild);

        var marketPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 2,
                Name = "Route Material",
                QuantityNeeded = 10,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Siren",
                    TotalCost = 10_000,
                    TotalQuantityPurchased = 10
                },
                RecommendedSplit =
                [
                    new SplitWorldPurchase
                    {
                        WorldName = "Leviathan",
                        QuantityToBuy = 10,
                        TotalCost = 4_000,
                        EffectivePricePerNeededUnit = 400
                    }
                ]
            }
        };

        var cost = AcquisitionPlanningService.CalculateCraftCost(craft, marketPlans);

        Assert.Equal(4_000, cost);
    }

    [Fact]
    public void CalculateCraftCost_DividesByRecipeYield()
    {
        var cloth = new PlanNode
        {
            ItemId = 10,
            Name = "Linen Cloth",
            Quantity = 2,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 2
        };
        var flax = new PlanNode
        {
            ItemId = 11,
            Name = "Moko Grass",
            Quantity = 4,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 100,
            Parent = cloth
        };
        cloth.Children.Add(flax);

        var cost = AcquisitionPlanningService.CalculateCraftCost(cloth, Array.Empty<DetailedShoppingPlan>());

        Assert.Equal(200, cost);
    }

    private static CraftingPlan CreatePlanWithBoughtIntermediate()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Final Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = false
        };

        var intermediate = new PlanNode
        {
            ItemId = 200,
            Name = "Intermediate",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanCraft = true,
            CanBuyFromMarket = true,
            Parent = root
        };

        var raw = new PlanNode
        {
            ItemId = 300,
            Name = "Raw Material",
            Quantity = 6,
            Source = AcquisitionSource.MarketBuyNq,
            CanCraft = false,
            CanBuyFromMarket = true,
            Parent = intermediate
        };

        intermediate.Children.Add(raw);
        root.Children.Add(intermediate);

        return new CraftingPlan
        {
            RootItems = new List<PlanNode> { root }
        };
    }
}
