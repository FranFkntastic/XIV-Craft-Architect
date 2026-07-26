using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class WorkerMarketEvidenceCompactionTests
{
    [Fact]
    public void PublicationCompactionPreservesProjectedCostAndHqCoverage()
    {
        var analysis = new MarketItemAnalysis
        {
            ItemId = 1,
            Name = "Crasher Material",
            QuantityNeeded = 10,
            ScopePriceBands =
            [
                new MarketScopePriceBand
                {
                    MinUnitPrice = 10,
                    MaxUnitPrice = 999_999,
                    ListingCount = 5,
                    TotalQuantity = 1_025
                }
            ],
            Worlds =
            [
                new WorldMarketAnalysis
                {
                    DataCenter = "Aether",
                    WorldName = "Sargatanas",
                    DataQualityBucket = MarketDataQualityBucket.Current,
                    Scores =
                    [
                        new WorldLensScore { Lens = MarketAcquisitionLens.MinimumUpfrontCost, Rank = 1 },
                        new WorldLensScore { Lens = MarketAcquisitionLens.BulkValue, Rank = 1 }
                    ],
                    Listings =
                    [
                        Listing(0, 6, 10),
                        Listing(1, 6, 11),
                        Listing(2, 7, 12, isHq: true),
                        Listing(3, 7, 13, isHq: true),
                        Listing(4, 999, 999_999)
                    ]
                }
            ]
        };
        var ladder = new MarketPriceLadderAnalysisService();
        var before = ladder.ProjectToShoppingPlan(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost);
        Assert.True(WorkerSessionCoordinator.HasCompleteMarketDetail(analysis));

        var compact =
            WorkerSessionCoordinator.CloneAndCompactMarketAnalysisForPublication(analysis);

        var after = ladder.ProjectToShoppingPlan(
            compact,
            MarketAcquisitionLens.MinimumUpfrontCost);
        Assert.Equal(before.RecommendedWorld?.TotalCost, after.RecommendedWorld?.TotalCost);
        Assert.Equal([0, 1, 2, 3], compact.Worlds[0].Listings.Select(listing => listing.SortIndex));
        Assert.True(compact.Worlds[0].Listings
            .Where(listing => listing.IsHq)
            .Sum(listing => listing.Quantity) >= analysis.QuantityNeeded);
        Assert.Equal(5, analysis.Worlds[0].Listings.Count);
        Assert.True(WorkerSessionCoordinator.HasCompleteMarketDetail(analysis));
        Assert.False(WorkerSessionCoordinator.HasCompleteMarketDetail(compact));
    }

    private static AnalyzedMarketListing Listing(
        int sortIndex,
        int quantity,
        long price,
        bool isHq = false) =>
        new()
        {
            SortIndex = sortIndex,
            Quantity = quantity,
            PricePerUnit = price,
            IsHq = isHq,
            PriceSanity = MarketListingPriceSanity.Sane
        };
}
