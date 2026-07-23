using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class RecipePlanDiagnosticContractTests
{
    [Fact]
    public void Dump_IsObservationalAndKeepsAcquisitionPricingAuthoritative()
    {
        var state = new AppState();
        state.ApplyBuiltRecipePlan(
            new CraftingPlan
            {
                DataCenter = "Aether",
                RootItems =
                [
                    new PlanNode
                    {
                        NodeId = "clear-glass-lens",
                        ItemId = 5512,
                        Name = "Clear Glass Lens",
                        Quantity = 5,
                        Source = AcquisitionSource.MarketBuyNq,
                        SourceReason = AcquisitionSourceReason.UserSelected,
                        CanBuyFromMarket = true
                    }
                ]
            },
            []);
        state.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 5512, Name = "Clear Glass Lens", QuantityNeeded = 5 }],
            [ShoppingPlan(5512, "Exodus", unitPrice: 100)]);
        state.ReplaceProcurementOverlay(
            [ShoppingPlan(5512, "Siren", unitPrice: 250)]);
        var versions = state.CurrentVersions;
        var routeValidity = state.ProcurementRouteValidity;

        var dump = new RecipePlanDiagnosticDumpService(state).BuildDump();
        var json = RecipePlanDiagnosticDumpService.Serialize(dump);

        Assert.Equal(versions, state.CurrentVersions);
        Assert.Equal(routeValidity, state.ProcurementRouteValidity);
        Assert.Contains("\"tool\": \"recipe-plan-diagnostic-dump\"", json);
        Assert.DoesNotContain("auto-market-analysis", json);
        Assert.Equal(RecipePlanAcquisitionQuoteBasis.MarketAnalysis, dump.Context.DisplayedPriceBasis);
        Assert.Equal(500, Assert.Single(dump.DisplayedQuotes).TotalCost);
        Assert.Equal(RecipePlanAcquisitionQuoteBasis.MarketAnalysis, Assert.Single(dump.DisplayedQuotes).Basis);
        Assert.Equal(1_250, Assert.Single(dump.ProcurementComparisonQuotes).TotalCost);
        Assert.Equal(
            RecipePlanAcquisitionQuoteBasis.ProcurementRoute,
            Assert.Single(dump.ProcurementComparisonQuotes).Basis);
    }

    private static DetailedShoppingPlan ShoppingPlan(int itemId, string world, long unitPrice)
    {
        var listing = new ShoppingListingEntry
        {
            Quantity = 5,
            NeededFromStack = 5,
            PricePerUnit = unitPrice
        };
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = "Clear Glass Lens",
            QuantityNeeded = 5,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = world,
                TotalCost = unitPrice * 5,
                TotalQuantityPurchased = 5,
                Listings = [listing]
            },
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = world,
                    TotalCost = unitPrice * 5,
                    TotalQuantityPurchased = 5,
                    Listings = [listing]
                }
            ]
        };
    }
}
