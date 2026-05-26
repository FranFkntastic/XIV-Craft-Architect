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
