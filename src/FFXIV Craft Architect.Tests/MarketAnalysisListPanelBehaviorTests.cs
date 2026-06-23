using Bunit;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FFXIV_Craft_Architect.Tests;

[Trait(TestTraits.Surface, TestTraits.DeployWeb)]
public class MarketAnalysisListPanelBehaviorTests
{
    [Fact]
    public void PriceBandsTab_ExpandsListingsAndResetsToWorldsOnSelectionChange()
    {
        using var context = CreateContext();
        var plans = new[]
        {
            CreatePlan(100, "Cobalt Rivets"),
            CreatePlan(200, "Rose Gold Nugget")
        };
        var analyses = new[]
        {
            CreateAnalysis(100, "Cobalt Rivets", 100, 125, 72, 3, 2, PriceBandCompetitiveness.Competitive, PriceBandDepth.Deep, includeListings: true),
            CreateAnalysis(200, "Rose Gold Nugget", 800, 950, 12, 1, 1, PriceBandCompetitiveness.Competitive, PriceBandDepth.Thin)
        };

        var component = context.Render<MarketAnalysisListPanel>(parameters => parameters
            .Add(panel => panel.ShoppingPlans, plans)
            .Add(panel => panel.MarketItemAnalyses, analyses)
            .Add(panel => panel.SelectedItemId, 100));

        Assert.Equal("Worlds", GetActiveDetailTab(component));

        component.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Price Bands")
            .Click();

        Assert.Equal("Price Bands", GetActiveDetailTab(component));
        var priceBandTable = component.Find(".ma-price-band-grid");
        Assert.Contains("100g - 125g", priceBandTable.TextContent);
        Assert.Contains("Acceptable", priceBandTable.TextContent);

        component.Find(".ma-price-band-expand-button").Click();

        var listingTable = component.Find(".ma-price-band-listing-grid");
        Assert.Contains("Siren", listingTable.TextContent);
        Assert.Contains("Cheap Retainer", listingTable.TextContent);
        Assert.Contains("Pricier Retainer", listingTable.TextContent);

        component.Render(parameters => parameters.Add(panel => panel.SelectedItemId, 200));

        Assert.Equal("Worlds", GetActiveDetailTab(component));
        Assert.Empty(component.FindAll(".ma-price-band-grid"));
    }

    [Fact]
    public void PriceBandExpandedListings_AreSortableByUnitPrice()
    {
        using var context = CreateContext();
        var component = context.Render<MarketAnalysisListPanel>(parameters => parameters
            .Add(panel => panel.ShoppingPlans, [CreatePlan(100, "Cobalt Rivets")])
            .Add(panel => panel.MarketItemAnalyses, [
                CreateAnalysis(100, "Cobalt Rivets", 100, 125, 72, 3, 2, PriceBandCompetitiveness.Competitive, PriceBandDepth.Deep, includeListings: true)
            ])
            .Add(panel => panel.SelectedItemId, 100));

        component.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Price Bands")
            .Click();
        component.Find(".ma-price-band-expand-button").Click();

        var listingGrid = component.Find(".ma-price-band-listing-grid");
        Assert.True(
            listingGrid.TextContent.IndexOf("Cheap Retainer", StringComparison.Ordinal) <
            listingGrid.TextContent.IndexOf("Pricier Retainer", StringComparison.Ordinal));

        component.FindAll(".ma-price-band-listing-grid .web-table-sort-button")
            .Single(button => button.TextContent.Contains("Unit", StringComparison.Ordinal))
            .Click();

        listingGrid = component.Find(".ma-price-band-listing-grid");
        Assert.True(
            listingGrid.TextContent.IndexOf("Pricier Retainer", StringComparison.Ordinal) <
            listingGrid.TextContent.IndexOf("Cheap Retainer", StringComparison.Ordinal));
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static DetailedShoppingPlan CreatePlan(int itemId, string name)
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = 10
        };
    }

    private static MarketItemAnalysis CreateAnalysis(
        int itemId,
        string name,
        long minPrice,
        long maxPrice,
        int quantity,
        int listingCount,
        int worldCount,
        PriceBandCompetitiveness competitiveness,
        PriceBandDepth depth,
        bool includeListings = false)
    {
        return new MarketItemAnalysis
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = 10,
            ScopePriceBands =
            [
                new MarketScopePriceBand
                {
                    MinUnitPrice = minPrice,
                    MaxUnitPrice = maxPrice,
                    WeightedAverageUnitPrice = (minPrice + maxPrice) / 2m,
                    TotalQuantity = quantity,
                    ListingCount = listingCount,
                    DistinctWorldCount = worldCount,
                    DistinctRetainerCount = listingCount,
                    Competitiveness = competitiveness,
                    Depth = depth
                }
            ],
            Worlds = includeListings
                ?
                [
                    new WorldMarketAnalysis
                    {
                        DataCenter = "Aether",
                        WorldName = "Siren",
                        Listings =
                        [
                            new AnalyzedMarketListing
                            {
                                SortIndex = 0,
                                Quantity = 5,
                                PricePerUnit = 110,
                                RetainerName = "Cheap Retainer",
                                PriceSanity = MarketListingPriceSanity.Sane,
                                Competitiveness = MarketListingCompetitiveness.Competitive,
                                LastReviewTimeUtc = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc)
                            },
                            new AnalyzedMarketListing
                            {
                                SortIndex = 1,
                                Quantity = 7,
                                PricePerUnit = 123,
                                RetainerName = "Pricier Retainer",
                                PriceSanity = MarketListingPriceSanity.Sane,
                                Competitiveness = MarketListingCompetitiveness.Competitive,
                                LastReviewTimeUtc = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc)
                            }
                        ]
                    }
                ]
                : []
        };
    }

    private static string GetActiveDetailTab(IRenderedComponent<MarketAnalysisListPanel> component)
    {
        return component.Find(".ma-detail-tab.is-active").TextContent.Trim();
    }
}
