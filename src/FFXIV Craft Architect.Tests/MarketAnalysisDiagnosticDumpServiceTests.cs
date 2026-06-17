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
    public void BuildDump_NoSelectedItem_ReturnsNull()
    {
        var service = new MarketAnalysisDiagnosticDumpService();

        var dump = service.BuildDump(
            [Analysis(100, "First")],
            [],
            Context(selectedItemId: null));

        Assert.Null(dump);
    }

    [Fact]
    public void BuildDump_SelectedItemMissing_ReturnsNullInsteadOfFirstAnalysis()
    {
        var service = new MarketAnalysisDiagnosticDumpService();

        var dump = service.BuildDump(
            [Analysis(100, "First"), Analysis(200, "Second")],
            [],
            Context(selectedItemId: 999));

        Assert.Null(dump);
    }

    [Fact]
    public void Serialize_UsesIndentedJsonAndStringEnums()
    {
        var service = new MarketAnalysisDiagnosticDumpService();
        var dump = service.BuildDump(
            [Analysis(100, "First")],
            [],
            Context(selectedItemId: 100));

        var json = service.Serialize(dump!);

        Assert.Contains(Environment.NewLine, json);
        Assert.Contains("\"lens\": \"BulkValue\"", json);
        Assert.Contains("\"competitiveness\": \"Competitive\"", json);
        Assert.Contains("\"depth\": \"Deep\"", json);
        Assert.Contains("\"isPrimaryUsableBand\": true", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("market-analysis-diagnostic-dump", document.RootElement.GetProperty("tool").GetString());
    }

    private static MarketAnalysisDiagnosticDumpContext Context(int? selectedItemId)
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
            SelectedItemId: selectedItemId);
    }

    private static MarketItemAnalysis Analysis(int itemId, string name)
    {
        return new MarketItemAnalysis
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = 10,
            Scope = MarketFetchScope.SelectedDataCenter,
            AnalysisScopeBaselineUnitPrice = 100,
            AnalysisScopeAverageUnitPrice = 110,
            AnalysisCompetitiveAverageUnitPrice = 100,
            CompetitiveThresholdUnitPrice = 150,
            SaneThresholdUnitPrice = 200,
            ScopePriceBands =
            [
                new MarketScopePriceBand
                {
                    MinUnitPrice = 100,
                    MaxUnitPrice = 100,
                    WeightedAverageUnitPrice = 100,
                    TotalQuantity = 10,
                    ListingCount = 1,
                    DistinctWorldCount = 1,
                    DistinctRetainerCount = 1,
                    Competitiveness = PriceBandCompetitiveness.Competitive,
                    Depth = PriceBandDepth.Deep
                }
            ],
            Worlds =
            [
                new WorldMarketAnalysis
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    PrimaryUsableQuantity = 10,
                    PriceBands =
                    [
                        new MarketPriceBand
                        {
                            FirstListingIndex = 0,
                            LastListingIndex = 0,
                            MinUnitPrice = 100,
                            MaxUnitPrice = 100,
                            WeightedAverageUnitPrice = 100,
                            ListingCount = 1,
                            Quantity = 10,
                            Competitiveness = PriceBandCompetitiveness.Competitive,
                            Depth = PriceBandDepth.Deep,
                            IsPrimaryUsableBand = true
                        }
                    ],
                    Listings =
                    [
                        new AnalyzedMarketListing
                        {
                            SortIndex = 0,
                            Quantity = 10,
                            PricePerUnit = 100,
                            RetainerName = "Seller",
                            PriceSanity = MarketListingPriceSanity.Sane,
                            Competitiveness = MarketListingCompetitiveness.Competitive,
                            IsInPrimaryUsableBand = true
                        }
                    ]
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
}
