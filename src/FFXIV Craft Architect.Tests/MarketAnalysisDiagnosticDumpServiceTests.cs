using System.Text.Json;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisDiagnosticDumpServiceTests
{
    [Fact]
    public void BuildDump_SelectedItem_ExportsOnlySelectedAnalysisWithMatchingShoppingPlan()
    {
        var service = new MarketAnalysisDiagnosticDumpService();
        var first = Analysis(100, "First");
        var selected = Analysis(200, "Selected");
        var matchingPlan = ShoppingPlan(200);

        var dump = service.BuildDump(
            [first, selected],
            [ShoppingPlan(100), matchingPlan],
            Context(selectedItemId: 200));

        Assert.NotNull(dump);
        Assert.Equal(200, dump.Analysis.ItemId);
        Assert.Equal("Selected", dump.Analysis.Name);
        Assert.Equal(200, dump.ShoppingPlan?.ItemId);
        Assert.False(dump.Selection.UsedFallbackSelection);
    }

    [Fact]
    public void BuildDump_MissingSelection_FallsBackToFirstAnalysis()
    {
        var service = new MarketAnalysisDiagnosticDumpService();
        var first = Analysis(100, "First");

        var dump = service.BuildDump(
            [first, Analysis(200, "Second")],
            [],
            Context(selectedItemId: 999));

        Assert.NotNull(dump);
        Assert.Equal(999, dump.Selection.RequestedItemId);
        Assert.Equal(100, dump.Selection.ExportedItemId);
        Assert.True(dump.Selection.UsedFallbackSelection);
        Assert.Null(dump.ShoppingPlan);
    }

    [Fact]
    public void Serialize_UsesIndentedJsonAndStringEnums()
    {
        var service = new MarketAnalysisDiagnosticDumpService();
        var dump = service.BuildDump(
            [Analysis(100, "First")],
            [],
            Context(selectedItemId: 100))! with
        {
            DetailAvailability =
            [
                new MarketAnalysisDiagnosticDetailAvailability(
                    "Aether",
                    "Siren",
                    MarketAnalysisWorldDetailHydrationStatus.Pruned,
                    "Listing detail was pruned from local storage.",
                    0,
                    false)
            ]
        };

        var json = service.Serialize(dump);

        Assert.Contains(Environment.NewLine, json);
        Assert.Contains("\"lens\": \"BulkValue\"", json);
        Assert.Contains("\"status\": \"Pruned\"", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("market-analysis-diagnostic-dump", document.RootElement.GetProperty("tool").GetString());
    }

    [Fact]
    public async Task BuildDumpAsync_WhenColdDetailExists_HydratesSelectedAnalysisWorld()
    {
        var publicationId = Guid.NewGuid();
        var hydration = new StubDetailHydrationService(
            MarketAnalysisWorldDetailHydrationResult.Loaded(
                [
                    new AnalyzedMarketListing
                    {
                        SortIndex = 0,
                        Quantity = 3,
                        PricePerUnit = 120,
                        RetainerName = "Cold Seller"
                    }
                ],
                [],
                fromEmbeddedHotState: false));
        var service = new MarketAnalysisDiagnosticDumpService(hydration);

        var dump = await service.BuildDumpAsync(
            [Analysis(100, "First", includeListings: false)],
            [],
            Context(selectedItemId: 100, publicationId));

        Assert.NotNull(dump);
        Assert.Single(hydration.Requests);
        Assert.Equal(publicationId, hydration.Requests[0].PublicationId);
        Assert.Equal(100, hydration.Requests[0].ItemId);
        Assert.Equal("Cold Seller", Assert.Single(Assert.Single(dump.Analysis.Worlds).Listings).RetainerName);
        var detailAvailability = Assert.Single(dump.DetailAvailability);
        Assert.Equal(MarketAnalysisWorldDetailHydrationStatus.Loaded, detailAvailability.Status);
        Assert.Equal(1, detailAvailability.ListingCount);
        Assert.False(detailAvailability.FromEmbeddedHotState);
    }

    [Fact]
    public async Task BuildDumpAsync_WhenDetailMissing_AnnotatesUnavailableWorldWithoutDroppingSummary()
    {
        var hydration = new StubDetailHydrationService(
            MarketAnalysisWorldDetailHydrationResult.Unavailable(
                MarketIntelligenceDetailAvailability.Pruned,
                "Listing detail was pruned from local storage."));
        var service = new MarketAnalysisDiagnosticDumpService(hydration);

        var dump = await service.BuildDumpAsync(
            [Analysis(100, "First", includeListings: false)],
            [],
            Context(selectedItemId: 100, Guid.NewGuid()));

        Assert.NotNull(dump);
        Assert.Empty(Assert.Single(dump.Analysis.Worlds).Listings);
        var detailAvailability = Assert.Single(dump.DetailAvailability);
        Assert.Equal(MarketAnalysisWorldDetailHydrationStatus.Pruned, detailAvailability.Status);
        Assert.Contains("pruned", detailAvailability.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static MarketAnalysisDiagnosticDumpContext Context(
        int? selectedItemId,
        Guid? publicationId = null)
    {
        return new MarketAnalysisDiagnosticDumpContext(
            PlanName: "Test Plan",
            SelectedDataCenter: "Aether",
            SelectedRegion: "North America",
            SearchEntireRegion: true,
            Lens: MarketAcquisitionLens.BulkValue,
            SortPreference: MarketSortOption.ByRecommended,
            GridSortColumn: MarketAnalysisGridSortColumn.Total,
            GridSortDescending: true,
            WorldSortColumn: MarketAnalysisWorldGridSortColumn.PriceValue,
            WorldSortDescending: false,
            SelectedItemId: selectedItemId,
            MarketIntelligencePublicationId: publicationId);
    }

    private static MarketItemAnalysis Analysis(
        int itemId,
        string name,
        bool includeListings = true)
    {
        return new MarketItemAnalysis
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = 10,
            Scope = MarketFetchScope.SelectedDataCenter,
            AnalysisScopeBaselineUnitPrice = 100,
            AnalysisScopeAverageUnitPrice = 110,
            AnalysisScopeCompetitiveAverageUnitPrice = 100,
            CompetitiveThresholdUnitPrice = 150,
            SaneThresholdUnitPrice = 200,
            Worlds =
            [
                new WorldMarketAnalysis
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    ScopeCompetitiveQuantity = 10,
                    Listings = includeListings
                        ?
                    [
                        new AnalyzedMarketListing
                        {
                            SortIndex = 0,
                            Quantity = 10,
                            PricePerUnit = 100,
                            RetainerName = "Seller",
                            PriceSanity = MarketListingPriceSanity.Sane,
                            IsScopeCompetitive = true
                        }
                    ]
                        : []
                }
            ]
        };
    }

    private static DetailedShoppingPlan ShoppingPlan(int itemId)
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = $"Item {itemId}",
            QuantityNeeded = 10
        };
    }

    private sealed class StubDetailHydrationService : IMarketAnalysisDetailHydrationService
    {
        private readonly MarketAnalysisWorldDetailHydrationResult _result;

        public StubDetailHydrationService(MarketAnalysisWorldDetailHydrationResult result)
        {
            _result = result;
        }

        public List<(Guid? PublicationId, int ItemId, string DataCenter, string WorldName)> Requests { get; } = [];

        public Task<MarketAnalysisWorldDetailHydrationResult> LoadWorldDetailAsync(
            Guid? publicationId,
            int itemId,
            WorldMarketAnalysis world,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((publicationId, itemId, world.DataCenter, world.WorldName));
            return Task.FromResult(_result);
        }
    }
}
