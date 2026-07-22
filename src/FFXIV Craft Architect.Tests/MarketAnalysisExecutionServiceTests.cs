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
        ladder.Setup(l => l.ProjectToShoppingPlanAsync(
                analysis,
                MarketAcquisitionLens.BulkValue,
                It.IsAny<MarketAnalysisConfig?>(),
                It.IsAny<MarketAnalysisExecutionOptions?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(projectedPlan);
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
        Assert.True(result.Timings.HasMeasuredDuration);
        Assert.True(result.Timings.MarketFetchDuration >= TimeSpan.Zero);
        Assert.True(result.Timings.LadderAnalysisDuration >= TimeSpan.Zero);
        Assert.True(result.Timings.ShoppingPlanProjectionDuration >= TimeSpan.Zero);
        Assert.True(result.Timings.AnalysisDuration >= result.Timings.LadderAnalysisDuration);
    }

    [Fact]
    public async Task ExecuteAsync_ForceRefreshUsesExplicitPairRefreshAndCancellation()
    {
        var cache = new Mock<IMarketCacheService>();
        TimeSpan? capturedReadMaxAge = TimeSpan.FromMinutes(1);
        var refreshRequestedPairs = false;
        var cts = new CancellationTokenSource();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        cache.Setup(c => c.RefreshRequestedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<List<(int itemId, string dataCenter)>, IProgress<string>?, CancellationToken>(
                (_, _, token) =>
                {
                    refreshRequestedPairs = true;
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
                ForceRefreshData = true
            },
            ct: cts.Token);

        Assert.True(refreshRequestedPairs);
        Assert.Null(capturedReadMaxAge);
        cache.Verify(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroMaxAge_ThrowsInsteadOfOverloadingForceRefresh()
    {
        var cache = new Mock<IMarketCacheService>(MockBehavior.Strict);
        var ladder = new Mock<IMarketPriceLadderAnalysisService>(MockBehavior.Strict);
        var service = new MarketAnalysisExecutionService(cache.Object, ladder.Object);

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ExecuteAsync(
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
                }));

        Assert.Equal("MaxAge", ex.ParamName);
    }

    [Fact]
    public async Task ExecuteAsync_RepeatedBulkSellerStacks_DoesNotForceRefreshOrBlockRecommendation()
    {
        var cache = new Mock<IMarketCacheService>();
        cache.As<IMarketCacheDiagnosticsProvider>()
            .SetupGet(c => c.LastDecisionSnapshot)
            .Returns(new MarketCacheDecisionSnapshot
            {
                RequestedItemCount = 1,
                RequestedPairCount = 1,
                FreshHitCount = 1
            });
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        cache.Setup(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(123, "Aether")] = CreateRepeatedBulkSellerCachedData(123, "Aether")
            });
        var analysis = new MarketItemAnalysis
        {
            ItemId = 123,
            Name = "Test Item",
            QuantityNeeded = 5,
            Warning = "Existing warning."
        };
        MarketItemAnalysis? projectedAnalysis = null;
        var ladder = new Mock<IMarketPriceLadderAnalysisService>();
        ladder.Setup(l => l.AnalyzeAsync(
                It.IsAny<MarketAnalysisRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync([analysis]);
        ladder.Setup(l => l.ProjectToShoppingPlanAsync(
                It.Is<MarketItemAnalysis>(item => item.ItemId == 123),
                MarketAcquisitionLens.BulkValue,
                It.IsAny<MarketAnalysisConfig?>(),
                It.IsAny<MarketAnalysisExecutionOptions?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<MarketItemAnalysis, MarketAcquisitionLens, MarketAnalysisConfig?, MarketAnalysisExecutionOptions?, IProgress<string>?, CancellationToken>(
                (item, _, _, _, _, _) => projectedAnalysis = item)
            .ReturnsAsync(new DetailedShoppingPlan
            {
                ItemId = 123
            });
        var service = new MarketAnalysisExecutionService(cache.Object, ladder.Object);

        var result = await service.ExecuteAsync(
            CreateExecutionRequest(),
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        cache.Verify(c => c.RefreshRequestedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        var resultAnalysis = Assert.Single(result.Analyses);
        Assert.Equal("Existing warning.", resultAnalysis.Warning);
        Assert.NotNull(projectedAnalysis);
        Assert.Equal("Existing warning.", projectedAnalysis.Warning);
        var shoppingPlan = Assert.Single(result.ShoppingPlans);
        Assert.Null(shoppingPlan.MarketDataWarning);
        Assert.Null(shoppingPlan.Error);
        Assert.NotNull(result.Evidence.CacheDecision);
        Assert.Equal(1, result.Evidence.CacheDecision!.FreshHitCount);
        Assert.Equal(0, result.Evidence.CacheDecision.SuspectRefreshPairCount);
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

    private static CachedMarketData CreateRepeatedBulkSellerCachedData(int itemId, string dataCenter)
    {
        var data = CreateCachedData(itemId, dataCenter);
        data.Worlds[0].Listings = Enumerable.Range(0, 30)
            .Select(_ => new CachedListing
            {
                Quantity = 99,
                PricePerUnit = 100,
                RetainerName = "Repeated Retainer",
                LastReviewTimeUnix = 1_710_000_000
            })
            .ToList();
        return data;
    }

    private static MarketAnalysisExecutionRequest CreateExecutionRequest()
    {
        return CreateExecutionRequest([123]);
    }

    private static MarketAnalysisExecutionRequest CreateExecutionRequest(IReadOnlyList<int> itemIds)
    {
        return new MarketAnalysisExecutionRequest
        {
            Items = itemIds
                .Select(itemId => new MaterialAggregate
                {
                    ItemId = itemId,
                    Name = $"Test Item {itemId}",
                    TotalQuantity = 5
                })
                .ToList(),
            Scope = MarketFetchScope.SelectedDataCenter,
            SelectedDataCenter = "Aether",
            SelectedRegion = "North America",
            Lens = MarketAcquisitionLens.BulkValue,
            ExpectedWorldsByDataCenter = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Aether"] = ["Siren"]
            }
        };
    }
}
