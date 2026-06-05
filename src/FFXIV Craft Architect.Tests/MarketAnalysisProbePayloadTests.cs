using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisProbePayloadTests
{
    [Fact]
    public void FromExecutionResult_ReportsCompactSummaryAndColdDetailBytes()
    {
        var result = new MarketAnalysisExecutionResult(
            new MarketEvidenceSet(
                new Dictionary<(int itemId, string dataCenter), CachedMarketData>(),
                [],
                MarketFetchScope.SelectedDataCenter,
                ["Aether"],
                "Aether",
                "North America",
                TimeSpan.Zero,
                fetchedCount: 1,
                DateTime.UtcNow),
            [
                new MarketItemAnalysis
                {
                    ItemId = 1,
                    Name = "Material",
                    QuantityNeeded = 2,
                    Scope = MarketFetchScope.SelectedDataCenter,
                    Worlds =
                    [
                        new WorldMarketAnalysis
                        {
                            DataCenter = "Aether",
                            WorldName = "Siren",
                            QuantityNeeded = 2,
                            CompetitiveQuantity = 2,
                            TotalListingQuantity = 2,
                            CompetitiveCoverageRatio = 1m,
                            Listings =
                            [
                                new AnalyzedMarketListing
                                {
                                    Quantity = 2,
                                    PricePerUnit = 100,
                                    RetainerName = "Test Retainer",
                                    PriceSanity = MarketListingPriceSanity.Sane,
                                    Competitiveness = MarketListingCompetitiveness.Competitive
                                }
                            ]
                        }
                    ]
                }
            ],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 1,
                    Name = "Material",
                    QuantityNeeded = 2,
                    RecommendedWorld = new WorldShoppingSummary
                    {
                        DataCenter = "Aether",
                        WorldName = "Siren",
                        TotalQuantityPurchased = 2,
                        TotalCost = 200
                    }
                }
            ]);

        var metrics = BenchmarkPayloadMetrics.FromExecutionResult(result);

        Assert.True(metrics.CompactSummaryJsonBytes > 0);
        Assert.True(metrics.ColdDetailJsonBytes > 0);
        Assert.True(metrics.SourceFactJsonBytes > 0);
        Assert.True(metrics.MarketIntelligenceJsonBytes > metrics.CompactSummaryJsonBytes);
        Assert.Equal(metrics.ColdDetailJsonBytes, metrics.RetainedDetailEstimateBytes);
    }
}
