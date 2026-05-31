using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Tests;

public class RecipeDemandProjectionParityServiceTests
{
    [Fact]
    public void Compare_AcquisitionViewsMatch_ReturnsNoMismatches()
    {
        var plan = CreatePlan();
        var projectionService = new RecipeDemandProjectionService();
        var parityService = new RecipeDemandProjectionParityService(projectionService);

        var report = parityService.Compare(plan, snapshot: null);

        Assert.True(report.Matches);
        Assert.Empty(report.Mismatches);
    }

    [Fact]
    public void Compare_VendorMarketUnknownRepeatedAndHqRows_ReturnsNoMismatches()
    {
        var plan = CreateMixedPlan();
        var projectionService = new RecipeDemandProjectionService();
        var parityService = new RecipeDemandProjectionParityService(projectionService);

        var report = parityService.Compare(plan, snapshot: null);

        Assert.True(report.Matches);
        Assert.Empty(report.Mismatches);
    }

    [Fact]
    public void Compare_ProjectionActiveProcurementDiffers_ReportsQuantityMismatch()
    {
        var plan = CreatePlan();
        var projection = new RecipeDemandProjection(
            AllPlanDemand: Array.Empty<RecipeDemandRow>(),
            MarketAnalysisCandidates: AcquisitionPlanningService.GetMarketAnalysisCandidates(plan)
                .SelectMany(ToRows)
                .ToList(),
            ActiveProcurementDemand:
            [
                new RecipeDemandRow(
                    RecipeDemandViewKind.ActiveProcurement,
                    "wrong",
                    200,
                    "Intermediate",
                    0,
                    99,
                    RecipeDemandQuantityBasis.PlanNodeQuantity,
                    false,
                    AcquisitionSource.MarketBuyNq,
                    AcquisitionSourceReason.SystemDefault,
                    true,
                    true,
                    false,
                    0,
                    null,
                    "Direct",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null)
            ],
            SuppressedDemand: Array.Empty<RecipeDemandRow>());
        var projectionService = new StubDemandProjectionService(projection);
        var parityService = new RecipeDemandProjectionParityService(projectionService);

        var report = parityService.Compare(plan, snapshot: null);

        var mismatch = Assert.Single(report.Mismatches, item =>
            item.View == RecipeDemandParityView.ActiveProcurement &&
            item.Field == RecipeDemandParityField.TotalQuantity);
        Assert.Equal(RecipeDemandParityView.ActiveProcurement, mismatch.View);
        Assert.Equal(RecipeDemandParityField.TotalQuantity, mismatch.Field);
        Assert.Equal(200, mismatch.ItemId);
    }

    private static IEnumerable<RecipeDemandRow> ToRows(MaterialAggregate aggregate)
    {
        foreach (var source in aggregate.Sources)
        {
            yield return new RecipeDemandRow(
                RecipeDemandViewKind.MarketAnalysisCandidate,
                $"row-{aggregate.ItemId}",
                aggregate.ItemId,
                aggregate.Name,
                aggregate.IconId,
                source.Quantity,
                RecipeDemandQuantityBasis.PlanNodeQuantity,
                aggregate.RequiresHq,
                AcquisitionSource.MarketBuyNq,
                AcquisitionSourceReason.SystemDefault,
                source.IsCrafted,
                true,
                false,
                aggregate.UnitPrice,
                null,
                source.ParentItemName,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }
    }

    private static CraftingPlan CreatePlan()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = true
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
            CanBuyFromMarket = true,
            Parent = intermediate
        };
        root.Children.Add(intermediate);
        intermediate.Children.Add(raw);

        return new CraftingPlan { RootItems = [root] };
    }

    private static CraftingPlan CreateMixedPlan()
    {
        var firstRoot = new PlanNode
        {
            ItemId = 100,
            Name = "First Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = true
        };
        var vendorChild = new PlanNode
        {
            ItemId = 200,
            Name = "Vendor Child",
            Quantity = 2,
            Source = AcquisitionSource.VendorBuy,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            Parent = firstRoot
        };
        var suppressedGrandchild = new PlanNode
        {
            ItemId = 300,
            Name = "Suppressed Grandchild",
            Quantity = 7,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            Parent = vendorChild
        };
        firstRoot.Children.Add(vendorChild);
        vendorChild.Children.Add(suppressedGrandchild);

        var secondRoot = new PlanNode
        {
            ItemId = 101,
            Name = "Second Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = true
        };
        var marketHqChild = new PlanNode
        {
            ItemId = 400,
            Name = "Repeated Market Child",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyHq,
            MustBeHq = true,
            CanBuyFromMarket = true,
            Parent = secondRoot
        };
        var unknownLeaf = new PlanNode
        {
            ItemId = 500,
            Name = "Unknown Leaf",
            Quantity = 4,
            Source = AcquisitionSource.UnknownSource,
            CanBuyFromMarket = false,
            Parent = secondRoot
        };
        var repeatedMarketChild = new PlanNode
        {
            ItemId = 400,
            Name = "Repeated Market Child",
            Quantity = 5,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            Parent = secondRoot
        };
        secondRoot.Children.Add(marketHqChild);
        secondRoot.Children.Add(unknownLeaf);
        secondRoot.Children.Add(repeatedMarketChild);

        return new CraftingPlan { RootItems = [firstRoot, secondRoot] };
    }

    private sealed class StubDemandProjectionService : IRecipeDemandProjectionService
    {
        private readonly RecipeDemandProjection _projection;

        public StubDemandProjectionService(RecipeDemandProjection projection)
        {
            _projection = projection;
        }

        public RecipeDemandProjection Build(CraftingPlan? plan, RecipeOperationSnapshot? snapshot)
        {
            return _projection;
        }
    }
}
