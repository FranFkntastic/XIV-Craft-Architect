using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AcquisitionEvaluationSnapshotBuilderTests
{
    [Fact]
    public void Build_UsesMarketEvidenceLookupAndProducesRows()
    {
        var plan = CreatePlan();
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
                    TotalCost = 300,
                    TotalQuantityPurchased = 2
                }
            }
        };

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            marketPlans,
            Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.All);

        Assert.Contains(snapshot.Rows, row => row.Node.ItemId == 100);
        Assert.Contains(snapshot.Rows, row => row.Node.ItemId == 200 && row.MarketEvidence.StartsWith("Siren"));
        Assert.Equal(snapshot.Rows.Count, snapshot.VisibleRows.Count);
        Assert.Contains(snapshot.ActiveProcurementItems, item => item.ItemId == 200);
    }

    [Fact]
    public void Build_ActiveFilterShowsOnlyActiveProcurementRows()
    {
        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            CreatePlan(),
            Array.Empty<DetailedShoppingPlan>(),
            Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.Active);

        Assert.All(snapshot.VisibleRows, row => Assert.True(row.IsActiveProcurement));
    }

    [Fact]
    public void Build_UsesUnavailableMarketItemsForMissingDataEvidence()
    {
        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            CreatePlan(),
            Array.Empty<DetailedShoppingPlan>(),
            [new MarketDataUnavailableItem(200, "Intermediate")],
            AcquisitionFilter.All);

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);
        Assert.Equal("Needs data", row.MarketEvidence);
    }

    [Fact]
    public void Build_EstimateMatchesPrimaryNodeSelectedOptionCost()
    {
        var firstRoot = CreateRoot(100, "First Root");
        var secondRoot = CreateRoot(101, "Second Root");
        var firstShared = CreateCraftedSharedChild(firstRoot, quantity: 2, rawQuantity: 10);
        var secondShared = CreateCraftedSharedChild(secondRoot, quantity: 6, rawQuantity: 30);
        firstRoot.Children.Add(firstShared);
        secondRoot.Children.Add(secondShared);
        var plan = new CraftingPlan { RootItems = [firstRoot, secondRoot] };

        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            Array.Empty<DetailedShoppingPlan>(),
            Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.All);

        var row = snapshot.Rows.Single(row => row.Node.ItemId == 200);

        Assert.Equal("50g", row.EstimatedCost);
    }

    private static CraftingPlan CreatePlan()
    {
        var root = CreateRoot(100, "Final Craft");
        var intermediate = new PlanNode
        {
            ItemId = 200,
            Name = "Intermediate",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanCraft = true,
            CanBuyFromMarket = true,
            MarketPrice = 10_000,
            Parent = root
        };
        var child = new PlanNode
        {
            ItemId = 300,
            Name = "Raw Child",
            Quantity = 4,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 100,
            Parent = intermediate
        };

        intermediate.Children.Add(child);
        root.Children.Add(intermediate);
        return new CraftingPlan { RootItems = [root] };
    }

    private static PlanNode CreateRoot(int itemId, string name)
    {
        return new PlanNode
        {
            ItemId = itemId,
            Name = name,
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
    }

    private static PlanNode CreateCraftedSharedChild(PlanNode parent, int quantity, int rawQuantity)
    {
        var shared = new PlanNode
        {
            ItemId = 200,
            Name = "Shared Intermediate",
            Quantity = quantity,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = true,
            MarketPrice = 1_000,
            Yield = 1,
            Parent = parent
        };
        shared.Children.Add(new PlanNode
        {
            ItemId = 300,
            Name = "Raw Child",
            Quantity = rawQuantity,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 5,
            Parent = shared
        });
        return shared;
    }
}
