using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisExecutionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_LoadsEvidenceAnalyzesAndProjectsPlans()
    {
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        cache.Setup(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(123, "Aether")] = CreateCachedData(123, "Aether")
            });

        var analysis = new MarketItemAnalysis
        {
            ItemId = 123,
            Name = "Test Item",
            QuantityNeeded = 5
        };
        var projectedPlan = new DetailedShoppingPlan
        {
            ItemId = 123,
            Name = "Test Item",
            QuantityNeeded = 5
        };
        MarketAnalysisRequest? capturedAnalysisRequest = null;
        var ladder = new Mock<IMarketPriceLadderAnalysisService>();
        ladder.Setup(l => l.AnalyzeAsync(
                It.IsAny<MarketAnalysisRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback<MarketAnalysisRequest, IProgress<string>?, CancellationToken, MarketAnalysisExecutionOptions?>(
                (request, _, _, _) => capturedAnalysisRequest = request)
            .ReturnsAsync([analysis]);
        ladder.Setup(l => l.ProjectToShoppingPlan(
                analysis,
                MarketAcquisitionLens.BulkValue,
                It.IsAny<MarketAnalysisConfig?>()))
            .Returns(projectedPlan);
        var service = new MarketAnalysisExecutionService(cache.Object, ladder.Object);

        var result = await service.ExecuteAsync(
            new MarketAnalysisExecutionRequest
            {
                Items =
                [
                    new MaterialAggregate
                    {
                        ItemId = 123,
                        Name = "Test Item",
                        TotalQuantity = 5
                    }
                ],
                Scope = MarketFetchScope.SelectedDataCenter,
                SelectedDataCenter = "Aether",
                SelectedRegion = "North America",
                RecommendationMode = RecommendationMode.MinimizeTotalCost,
                Lens = MarketAcquisitionLens.BulkValue,
                ExpectedWorldsByDataCenter = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["Aether"] = ["Siren"]
                }
            },
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        Assert.NotNull(capturedAnalysisRequest);
        Assert.Equal(MarketFetchScope.SelectedDataCenter, capturedAnalysisRequest.Evidence.Scope);
        Assert.Equal("Aether", capturedAnalysisRequest.Evidence.SelectedDataCenter);
        Assert.Equal(["Siren"], capturedAnalysisRequest.ExpectedWorldsByDataCenter["Aether"]);
        Assert.Same(analysis, Assert.Single(result.Analyses));
        Assert.Same(projectedPlan, Assert.Single(result.ShoppingPlans));
        Assert.Equal(1, result.Evidence.FetchedCount);
    }

    [Fact]
    public async Task ExecuteAsync_PassesMaxAgeAndCancellationToEvidenceLoader()
    {
        var cache = new Mock<IMarketCacheService>();
        TimeSpan? capturedEnsureMaxAge = null;
        TimeSpan? capturedReadMaxAge = TimeSpan.FromMinutes(1);
        var cts = new CancellationTokenSource();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<List<(int itemId, string dataCenter)>, TimeSpan?, IProgress<string>?, CancellationToken>(
                (_, maxAge, _, token) =>
                {
                    capturedEnsureMaxAge = maxAge;
                    Assert.Equal(cts.Token, token);
                })
            .ReturnsAsync(0);
        cache.Setup(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .Callback<IReadOnlyCollection<(int itemId, string dataCenter)>, TimeSpan?>(
                (_, maxAge) => capturedReadMaxAge = maxAge)
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>());
        var ladder = new Mock<IMarketPriceLadderAnalysisService>();
        ladder.Setup(l => l.AnalyzeAsync(
                It.IsAny<MarketAnalysisRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync([]);
        var service = new MarketAnalysisExecutionService(cache.Object, ladder.Object);

        await service.ExecuteAsync(
            new MarketAnalysisExecutionRequest
            {
                Items =
                [
                    new MaterialAggregate
                    {
                        ItemId = 123,
                        Name = "Test Item",
                        TotalQuantity = 5
                    }
                ],
                Scope = MarketFetchScope.SelectedDataCenter,
                SelectedDataCenter = "Aether",
                SelectedRegion = "North America",
                MaxAge = TimeSpan.Zero
            },
            ct: cts.Token);

        Assert.Equal(TimeSpan.Zero, capturedEnsureMaxAge);
        Assert.Null(capturedReadMaxAge);
    }

    private static CachedMarketData CreateCachedData(int itemId, string dataCenter)
    {
        return new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            DCAveragePrice = 100,
            FetchedAt = DateTime.UtcNow,
            Worlds =
            [
                new CachedWorldData
                {
                    WorldName = "Siren",
                    Listings =
                    [
                        new CachedListing
                        {
                            Quantity = 5,
                            PricePerUnit = 100,
                            RetainerName = "Retainer"
                        }
                    ]
                }
            ]
        };
    }
}
