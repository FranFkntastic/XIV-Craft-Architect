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
        Assert.True(result.Timings.HasMeasuredDuration);
        Assert.True(result.Timings.MarketFetchDuration >= TimeSpan.Zero);
        Assert.True(result.Timings.LadderAnalysisDuration >= TimeSpan.Zero);
        Assert.True(result.Timings.ShoppingPlanProjectionDuration >= TimeSpan.Zero);
        Assert.True(result.Timings.AnalysisDuration >= result.Timings.LadderAnalysisDuration);
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

    [Fact]
    public async Task ExecuteAsync_SuspiciousCacheShapeRefreshFails_AppendsBlockedWarningAfterProjection()
    {
        var cache = new Mock<IMarketCacheService>();
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
                [(123, "Aether")] = CreateSuspiciousCachedData(123, "Aether")
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
        ladder.Setup(l => l.ProjectToShoppingPlan(
                It.Is<MarketItemAnalysis>(item => item.ItemId == 123),
                MarketAcquisitionLens.BulkValue,
                It.IsAny<MarketAnalysisConfig?>()))
            .Callback<MarketItemAnalysis, MarketAcquisitionLens, MarketAnalysisConfig?>(
                (item, _, _) => projectedAnalysis = item)
            .Returns(new DetailedShoppingPlan { ItemId = 123 });
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
                Lens = MarketAcquisitionLens.BulkValue
            },
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        var resultAnalysis = Assert.Single(result.Analyses);
        Assert.Contains("Existing warning.", resultAnalysis.Warning, StringComparison.Ordinal);
        Assert.Contains("could not be refreshed", resultAnalysis.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Siren", resultAnalysis.Warning, StringComparison.Ordinal);
        Assert.Contains("30 repeated", resultAnalysis.Warning, StringComparison.Ordinal);
        Assert.NotNull(projectedAnalysis);
        Assert.Equal("Existing warning.", projectedAnalysis.Warning);
        var shoppingPlan = Assert.Single(result.ShoppingPlans);
        Assert.Contains("could not be refreshed", shoppingPlan.MarketDataWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blocked", shoppingPlan.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_SuspiciousCacheShape_ForceRefreshesAndAnalyzesCleanReplacement()
    {
        var cache = new Mock<IMarketCacheService>();
        var suspectData = CreateSuspiciousCachedData(123, "Aether");
        var freshData = CreateCachedData(123, "Aether");
        freshData.FetchedAt = DateTime.UtcNow.AddMinutes(1);
        freshData.Worlds[0].Listings[0].RetainerName = "Fresh Retainer";
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.Is<TimeSpan?>(maxAge => maxAge != TimeSpan.Zero),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.Is<List<(int itemId, string dataCenter)>>(requests =>
                    requests.Count == 1 &&
                    requests[0].itemId == 123 &&
                    requests[0].dataCenter == "Aether"),
                TimeSpan.Zero,
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        cache.SetupSequence(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(123, "Aether")] = suspectData
            })
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(123, "Aether")] = freshData
            });
        MarketAnalysisRequest? capturedAnalysisRequest = null;
        var ladder = new Mock<IMarketPriceLadderAnalysisService>();
        ladder.Setup(l => l.AnalyzeAsync(
                It.IsAny<MarketAnalysisRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback<MarketAnalysisRequest, IProgress<string>?, CancellationToken, MarketAnalysisExecutionOptions?>(
                (request, _, _, _) => capturedAnalysisRequest = request)
            .ReturnsAsync([
                new MarketItemAnalysis
                {
                    ItemId = 123,
                    Name = "Test Item",
                    QuantityNeeded = 5
                }
            ]);
        ladder.Setup(l => l.ProjectToShoppingPlan(
                It.IsAny<MarketItemAnalysis>(),
                MarketAcquisitionLens.BulkValue,
                It.IsAny<MarketAnalysisConfig?>()))
            .Returns(new DetailedShoppingPlan { ItemId = 123 });
        var service = new MarketAnalysisExecutionService(cache.Object, ladder.Object);

        var result = await service.ExecuteAsync(
            CreateExecutionRequest(),
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        Assert.NotNull(capturedAnalysisRequest);
        var entry = Assert.Single(capturedAnalysisRequest.Evidence.Entries).Value;
        Assert.Equal("Fresh Retainer", Assert.Single(Assert.Single(entry.Worlds).Listings).RetainerName);
        Assert.Empty(capturedAnalysisRequest.Evidence.MissingRequests);
        Assert.DoesNotContain(
            "suspicious cached market evidence payload",
            Assert.Single(result.Analyses).Warning ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_SuspiciousCacheShapeRefreshFails_BlocksAffectedRecommendation()
    {
        var cache = new Mock<IMarketCacheService>();
        var suspectData = CreateSuspiciousCachedData(123, "Aether");
        suspectData.FetchedAt = DateTime.UtcNow.AddMinutes(-5);
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        cache.SetupSequence(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(123, "Aether")] = suspectData
            })
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>());
        var service = new MarketAnalysisExecutionService(cache.Object, new MarketPriceLadderAnalysisService());

        var result = await service.ExecuteAsync(
            CreateExecutionRequest(),
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        var analysis = Assert.Single(result.Analyses);
        Assert.Equal(["Aether"], analysis.MissingDataCenters);
        Assert.Contains("could not be refreshed", analysis.Warning, StringComparison.OrdinalIgnoreCase);
        var plan = Assert.Single(result.ShoppingPlans);
        Assert.Null(plan.RecommendedWorld);
        Assert.Null(plan.RecommendedSplit);
        Assert.Contains("blocked", plan.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Aether", plan.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_SuspiciousCacheShapeRefreshInitiallyMisses_RetriesBeforeBlocking()
    {
        var cache = new Mock<IMarketCacheService>();
        var suspectData = CreateSuspiciousCachedData(123, "Aether");
        var freshData = CreateCachedData(123, "Aether");
        freshData.FetchedAt = DateTime.UtcNow.AddMinutes(1);
        freshData.Worlds[0].Listings[0].RetainerName = "Fresh Retainer";
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.Is<List<(int itemId, string dataCenter)>>(requests =>
                    requests.Count == 1 &&
                    requests[0].itemId == 123 &&
                    requests[0].dataCenter == "Aether"),
                TimeSpan.Zero,
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        cache.SetupSequence(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(123, "Aether")] = suspectData
            })
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>())
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(123, "Aether")] = freshData
            });
        var ladder = new Mock<IMarketPriceLadderAnalysisService>();
        ladder.Setup(l => l.AnalyzeAsync(
                It.IsAny<MarketAnalysisRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync([
                new MarketItemAnalysis
                {
                    ItemId = 123,
                    Name = "Test Item",
                    QuantityNeeded = 5
                }
            ]);
        ladder.Setup(l => l.ProjectToShoppingPlan(
                It.IsAny<MarketItemAnalysis>(),
                MarketAcquisitionLens.BulkValue,
                It.IsAny<MarketAnalysisConfig?>()))
            .Returns(new DetailedShoppingPlan { ItemId = 123 });
        var service = new MarketAnalysisExecutionService(cache.Object, ladder.Object);

        var result = await service.ExecuteAsync(
            CreateExecutionRequest(),
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        cache.Verify(c => c.EnsurePopulatedAsync(
            It.Is<List<(int itemId, string dataCenter)>>(requests =>
                requests.Count == 1 &&
                requests[0].itemId == 123 &&
                requests[0].dataCenter == "Aether"),
            TimeSpan.Zero,
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.DoesNotContain(
            "could not be refreshed",
            Assert.Single(result.Analyses).Warning ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
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

    private static CachedMarketData CreateSuspiciousCachedData(int itemId, string dataCenter)
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
        return new MarketAnalysisExecutionRequest
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
            Lens = MarketAcquisitionLens.BulkValue,
            ExpectedWorldsByDataCenter = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Aether"] = ["Siren"]
            }
        };
    }
}
