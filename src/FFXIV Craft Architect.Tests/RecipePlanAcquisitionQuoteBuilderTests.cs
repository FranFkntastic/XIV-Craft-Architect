using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

[Trait(TestTraits.Surface, TestTraits.DeployWeb)]
public sealed class RecipePlanAcquisitionQuoteBuilderTests
{
    [Fact]
    public void Build_MarketQuote_UsesSelectedCashOutCostAndAllocatesRepeatedDemand()
    {
        var first = MarketNode("ore-a", 4);
        var second = MarketNode("ore-b", 6);
        var plan = new CraftingPlan { RootItems = [first, second] };
        var shoppingPlan = ShoppingPlan(quantityNeeded: 10, cashOutCost: 1_200);

        var quotes = RecipePlanAcquisitionQuoteBuilder.Build(
            plan,
            [shoppingPlan],
            RecipePlanAcquisitionQuoteBasis.ProcurementRoute,
            isRefreshing: false,
            evidencePublishedAtUtc: DateTime.UtcNow);

        Assert.Equal(480m, quotes[first.NodeId].TotalCost);
        Assert.Equal(720m, quotes[second.NodeId].TotalCost);
        Assert.Equal(1_200m, quotes.Values.Sum(quote => quote.TotalCost));
        Assert.All(quotes.Values, quote => Assert.Equal(RecipePlanAcquisitionQuoteStatus.Actionable, quote.Status));
    }

    [Fact]
    public void Build_HqQuote_RejectsNormalQualityCoverage()
    {
        var node = MarketNode("hq-ore", 10, hq: true);
        var plan = new CraftingPlan { RootItems = [node] };
        var shoppingPlan = ShoppingPlan(quantityNeeded: 10, cashOutCost: 1_200, hq: false);

        var quote = RecipePlanAcquisitionQuoteBuilder.Build(
            plan,
            [shoppingPlan],
            RecipePlanAcquisitionQuoteBasis.MarketAnalysis,
            isRefreshing: false,
            evidencePublishedAtUtc: DateTime.UtcNow)[node.NodeId];

        Assert.Equal(RecipePlanAcquisitionQuoteStatus.Unavailable, quote.Status);
        Assert.Equal(0, quote.TotalCost);
    }

    [Fact]
    public void Build_MissingMarketEvidence_UsesRefreshingStateWithoutOldScalarPrice()
    {
        var node = MarketNode("missing", 10);
        node.MarketPrice = 7;
        var plan = new CraftingPlan { RootItems = [node] };

        var quote = RecipePlanAcquisitionQuoteBuilder.Build(
            plan,
            Array.Empty<DetailedShoppingPlan>(),
            RecipePlanAcquisitionQuoteBasis.MarketAnalysis,
            isRefreshing: true,
            evidencePublishedAtUtc: null)[node.NodeId];

        Assert.Equal(RecipePlanAcquisitionQuoteStatus.Refreshing, quote.Status);
        Assert.Equal(0, quote.TotalCost);
    }

    [Fact]
    public void Build_CraftQuote_RequiresEveryMaterialToBeActionable()
    {
        var child = MarketNode("child", 10);
        var root = new PlanNode
        {
            NodeId = "root",
            ItemId = 2,
            Name = "Ingot",
            Quantity = 2,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Children = [child]
        };
        var plan = new CraftingPlan { RootItems = [root] };

        var quote = RecipePlanAcquisitionQuoteBuilder.Build(
            plan,
            Array.Empty<DetailedShoppingPlan>(),
            RecipePlanAcquisitionQuoteBasis.MarketAnalysis,
            isRefreshing: false,
            evidencePublishedAtUtc: null)[root.NodeId];

        Assert.Equal(RecipePlanAcquisitionQuoteStatus.Unavailable, quote.Status);
        Assert.Equal(0, quote.TotalCost);
    }

    private static PlanNode MarketNode(string nodeId, int quantity, bool hq = false)
    {
        return new PlanNode
        {
            NodeId = nodeId,
            ItemId = 1,
            Name = "Ore",
            Quantity = quantity,
            Source = hq ? AcquisitionSource.MarketBuyHq : AcquisitionSource.MarketBuyNq,
            MustBeHq = hq,
            CanBeHq = true,
            CanBuyFromMarket = true
        };
    }

    private static DetailedShoppingPlan ShoppingPlan(int quantityNeeded, decimal cashOutCost, bool hq = false)
    {
        var quality = hq ? MarketCoverageQualityPolicy.HqOnly : MarketCoverageQualityPolicy.NqOrHq;
        var coverage = new MarketCoverageOption(
            "selected",
            MarketCoverageTier.SingleWorld,
            MarketCoverageKind.SupportedListings,
            quality,
            quantityNeeded,
            quantityNeeded,
            0,
            cashOutCost,
            cashOutCost,
            cashOutCost / quantityNeeded,
            MarketCoveragePriceBand.Competitive,
            [new MarketCoverageWorld("Aether", "Gilgamesh", quantityNeeded, quantityNeeded, cashOutCost, cashOutCost)],
            [new MarketCoverageListing("Aether", "Gilgamesh", quantityNeeded, quantityNeeded, quantityNeeded, cashOutCost / quantityNeeded, hq)],
            new MarketCoverageFriction(1, 1, quantityNeeded, quantityNeeded, 0),
            MarketCoverageSavings.None,
            true,
            null);
        return new DetailedShoppingPlan
        {
            ItemId = 1,
            Name = "Ore",
            QuantityNeeded = quantityNeeded,
            HqQuantityNeeded = hq ? quantityNeeded : 0,
            CoverageSet = new MarketCoverageSet(1, "Ore", quantityNeeded, coverage, null, null, coverage, [coverage])
        };
    }
}
