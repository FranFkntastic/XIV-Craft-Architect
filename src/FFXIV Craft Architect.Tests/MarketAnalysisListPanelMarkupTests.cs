using Bunit;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisListPanelMarkupTests
{
    [Fact]
    public void MarketAnalysisListPanel_UsesCachedOrderedPlansForRender()
    {
        var source = File.ReadAllText(GetListPanelPath());

        Assert.Contains("@foreach (var plan in _orderedPlans)", source);
        Assert.DoesNotContain("@foreach (var plan in GetGridPlans())", source);
    }

    [Fact]
    public void MarketAnalysisListPanel_DoesNotConsumeAutoExpandFromRenderLifecycle()
    {
        var source = File.ReadAllText(GetListPanelPath());

        Assert.DoesNotContain("OnAutoExpandConsumed", source);
        Assert.DoesNotContain("_autoExpandProcessed", source);
        Assert.DoesNotContain("AutoExpandItemId", source);
    }

    [Fact]
    public void MarketAnalysisListPanel_TotalCostUsesProjectionWarningHelpers()
    {
        var source = File.ReadAllText(GetListPanelPath());

        Assert.Contains("SortHeader(\"Calculated Total\", MarketAnalysisGridSortColumn.Total", source);
        Assert.DoesNotContain("SortHeader(\"Total\", MarketAnalysisGridSortColumn.Total", source);
        Assert.Contains("GetTotalCostClass(plan)", source);
        Assert.Contains("GetTotalCostTooltip(plan)", source);
        Assert.Contains("GetTotalCostClass(_selectedPlan)", source);
        Assert.Contains("GetTotalCostTooltip(_selectedPlan)", source);
        Assert.Contains("MarketAnalysisGridViewService.CalculatedTotalHeaderTooltip", source);
    }

    [Fact]
    public void MarketAnalysisListPanel_DetailHeaderAlignsTitleAndSupportsCopyName()
    {
        var source = File.ReadAllText(GetListPanelPath());
        var styles = File.ReadAllText(GetListPanelStylePath());

        Assert.Contains("@inject IJSRuntime JSRuntime", source);
        Assert.Contains("@inject ISnackbar Snackbar", source);
        Assert.Contains("class=\"ma-detail-title-block\"", source);
        Assert.Contains("class=\"ma-detail-title-row\"", source);
        Assert.Contains("Class=\"ma-detail-copy-button\"", source);
        Assert.Contains("Icons.Material.Filled.ContentCopy", source);
        Assert.Contains("OnClick=\"() => CopySelectedItemNameAsync(_selectedPlan.Name)\"", source);
        Assert.Contains("OnClick:StopPropagation=\"true\"", source);
        Assert.Contains("navigator.clipboard.writeText", source);
        Assert.Contains("Snackbar.Add(\"Item name copied\", Severity.Success)", source);

        Assert.Contains(".ma-detail-title-block", styles);
        Assert.Contains(".ma-detail-title-row", styles);
        Assert.Contains(".ma-detail-copy-button", styles);
    }

    [Fact]
    public void MarketAnalysisListPanel_SelectedDetailHasRefreshItemAction()
    {
        var source = File.ReadAllText(GetListPanelPath());
        var resultsSource = File.ReadAllText(GetSharedPath("MarketAnalysisResultsPanel.razor"));

        Assert.Contains("[Parameter] public EventCallback<DetailedShoppingPlan> OnRefreshItemRequested", source);
        Assert.Contains("Icons.Material.Filled.Refresh", source);
        Assert.Contains("Class=\"ma-detail-refresh-button\"", source);
        Assert.Contains("Title=\"Refresh market data for this item\"", source);
        Assert.Contains("AriaLabel=\"Refresh market data for this item\"", source);
        Assert.Contains("OnClick=\"() => RefreshSelectedItemAsync(_selectedPlan)\"", source);
        Assert.Contains("OnClick:StopPropagation=\"true\"", source);
        Assert.Contains("await OnRefreshItemRequested.InvokeAsync(plan)", source);

        Assert.Contains("[Parameter] public EventCallback<DetailedShoppingPlan> OnRefreshItemRequested", resultsSource);
        Assert.Contains("OnRefreshItemRequested=\"OnRefreshItemRequested\"", resultsSource);
    }

    [Fact]
    public void MarketAnalysisListPanel_SelectedDetailHasWorldsAndPriceBandsTabs()
    {
        var source = File.ReadAllText(GetListPanelPath());
        var styles = File.ReadAllText(GetListPanelStylePath());

        Assert.Contains("MarketAnalysisDetailTab.Worlds", source);
        Assert.Contains("MarketAnalysisDetailTab.PriceBands", source);
        Assert.Contains("role=\"tablist\"", source);
        Assert.Contains("Worlds", source);
        Assert.Contains("Price Bands", source);
        Assert.Contains("ScopePriceBands", source);
        Assert.Contains("Band", source);
        Assert.Contains("Stock", source);
        Assert.Contains("Listings", source);
        Assert.Contains("Role", source);
        Assert.Contains("<WebDataTable TItem=\"PriceBandRow\"", source);
        Assert.Contains("ExpandedContent=\"RenderExpandedPriceBand\"", source);
        Assert.Contains("OpenComponent<WebDataTable<PriceBandListingRow, PriceBandListingColumn>>", source);
        Assert.Contains("\"SortState\", _priceBandListingSortState", source);

        Assert.Contains(".ma-detail-tabs", styles);
        Assert.Contains(".ma-price-band-listing-grid", styles);
        Assert.Contains("::deep .ma-price-band-role.is-competitive", styles);
        Assert.Contains("::deep .ma-price-band-role.is-thin", styles);
        Assert.DoesNotContain(".ma-price-band-table", styles);
    }

    [Fact]
    public void MarketAnalysisListPanel_PriceBandsTabUsesExpandableTableAndResetsToWorldsOnSelectionChange()
    {
        using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        var plans = new[]
        {
            CreatePlan(100, "Cobalt Rivets"),
            CreatePlan(200, "Rose Gold Nugget")
        };
        var analyses = new[]
        {
            CreateAnalysis(100, "Cobalt Rivets", 100, 125, 72, 3, 2, MarketScopePriceBandRole.Competitive, includeListings: true),
            CreateAnalysis(200, "Rose Gold Nugget", 800, 950, 12, 1, 1, MarketScopePriceBandRole.Thin)
        };

        var component = context.Render<MarketAnalysisListPanel>(parameters => parameters
            .Add(panel => panel.ShoppingPlans, plans)
            .Add(panel => panel.MarketItemAnalyses, analyses)
            .Add(panel => panel.SelectedItemId, 100));

        Assert.Equal("Worlds", GetActiveDetailTab(component));
        Assert.Empty(component.FindAll(".ma-price-band-table"));

        component.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Price Bands")
            .Click();

        Assert.Equal("Price Bands", GetActiveDetailTab(component));
        var priceBandTable = component.Find(".ma-price-band-grid");
        Assert.Contains("100g - 125g", priceBandTable.TextContent);
        Assert.Contains("72", priceBandTable.TextContent);
        Assert.Contains("3", priceBandTable.TextContent);
        Assert.Contains("2", priceBandTable.TextContent);
        Assert.Contains("Best shelf", priceBandTable.TextContent);
        Assert.Empty(component.FindAll(".ma-price-band-listing-grid"));
        Assert.DoesNotContain("Band Retainer", component.Markup);

        component.Find(".ma-price-band-expand-button").Click();

        var listingTable = component.Find(".ma-price-band-listing-grid");
        Assert.Contains("Siren", listingTable.TextContent);
        Assert.Contains("Cheap Retainer", listingTable.TextContent);
        Assert.Contains("Pricier Retainer", listingTable.TextContent);
        Assert.Contains("110g", listingTable.TextContent);
        Assert.Contains("NQ", listingTable.TextContent);

        component.Render(parameters => parameters
            .Add(panel => panel.SelectedItemId, 200));

        Assert.Equal("Worlds", GetActiveDetailTab(component));
        Assert.Empty(component.FindAll(".ma-price-band-grid"));
    }

    [Fact]
    public void MarketAnalysisListPanel_PriceBandExpandedListingsAreSortable()
    {
        using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        var component = context.Render<MarketAnalysisListPanel>(parameters => parameters
            .Add(panel => panel.ShoppingPlans, [CreatePlan(100, "Cobalt Rivets")])
            .Add(panel => panel.MarketItemAnalyses, [
                CreateAnalysis(100, "Cobalt Rivets", 100, 125, 72, 3, 2, MarketScopePriceBandRole.Competitive, includeListings: true)
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

    private static string GetListPanelPath()
    {
        return GetSharedPath("MarketAnalysisListPanel.razor");
    }

    private static string GetListPanelStylePath()
    {
        return GetSharedPath("MarketAnalysisListPanel.razor.css");
    }

    private static string GetSharedPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Shared", fileName);
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
        MarketScopePriceBandRole role,
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
                    BandRole = role,
                    IsRepresentative = role is MarketScopePriceBandRole.Competitive or MarketScopePriceBandRole.Representative,
                    IsThin = role == MarketScopePriceBandRole.Thin
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
