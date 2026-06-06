namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisProbeRetryAfterTests
{
    [Fact]
    public async Task GetMarketDataBulkAsync_Fake429RetryAfter_RecordsHeaderDelayAndCompletes()
    {
        using var httpClient = new HttpClient(new ProbeFakeUniversalisHttpMessageHandler(
            FakeUniversalisScenario.RetryAfter429FirstPerDataCenter));
        var source = new InstrumentedUniversalisMarketDataSource(
            httpClient,
            CreateOptions(respectRetryAfter: true));

        var result = await source.GetMarketDataBulkAsync(
            "Aether",
            new[] { 101, 102, 103 },
            CancellationToken.None);

        var metrics = source.GetMetrics();
        Assert.Equal(new[] { 101, 102, 103 }, result.Keys.Order().ToArray());
        Assert.Equal(3, metrics.FetchedItems);
        Assert.Equal(1, metrics.RetryCount);
        Assert.Equal(1, metrics.RateLimit429Count);
        Assert.Equal(1, metrics.RetryAfterCount);
        Assert.True(metrics.RetryAfterDelay >= TimeSpan.FromSeconds(1));
        Assert.True(metrics.BackoffDelay >= TimeSpan.FromSeconds(1));
        Assert.Equal(0, metrics.FinalMissingItemCount);
    }

    [Fact]
    public async Task GetMarketDataBulkAsync_Fake504RetryAfter_SplitsChunkAndCompletes()
    {
        using var httpClient = new HttpClient(new ProbeFakeUniversalisHttpMessageHandler(
            FakeUniversalisScenario.RetryAfter504FirstPerDataCenter));
        var source = new InstrumentedUniversalisMarketDataSource(
            httpClient,
            CreateOptions(respectRetryAfter: true));

        var result = await source.GetMarketDataBulkAsync(
            "Aether",
            new[] { 201, 202, 203, 204, 205, 206 },
            CancellationToken.None);

        var metrics = source.GetMetrics();
        Assert.Equal(new[] { 201, 202, 203, 204, 205, 206 }, result.Keys.Order().ToArray());
        Assert.Equal(6, metrics.FetchedItems);
        Assert.Equal(1, metrics.GatewayTimeout504Count);
        Assert.Equal(1, metrics.RetryAfterCount);
        Assert.Equal(2, metrics.SplitCount);
        Assert.Equal(0, metrics.FinalMissingItemCount);
    }

    [Fact]
    public async Task GetMarketDataBulkAsync_LiveShaped504Pressure_HasNoRetryAfterAndCompletesViaSplits()
    {
        using var httpClient = new HttpClient(new ProbeFakeUniversalisHttpMessageHandler(
            FakeUniversalisScenario.LiveShaped504Pressure,
            TimeSpan.Zero));
        var source = new InstrumentedUniversalisMarketDataSource(
            httpClient,
            CreateOptions(respectRetryAfter: true));

        var result = await source.GetMarketDataBulkAsync(
            "Primal",
            Enumerable.Range(1, 53).ToArray(),
            CancellationToken.None);

        var metrics = source.GetMetrics();
        Assert.Equal(53, result.Count);
        Assert.Equal(4, metrics.GatewayTimeout504Count);
        Assert.Equal(8, metrics.SplitCount);
        Assert.Equal(0, metrics.RetryAfterCount);
        Assert.Equal(TimeSpan.Zero, metrics.RetryAfterDelay);
        Assert.Equal(0, metrics.FinalMissingItemCount);
    }

    private static ProbeOptions CreateOptions(bool respectRetryAfter)
    {
        return new ProbeOptions(
            PlanPath: "unused.craftplan",
            SearchRegion: false,
            DataCenter: "Aether",
            CacheMode: BenchmarkCacheMode.Cold,
            FixturePath: null,
            WriteFixturePath: null,
            JsonOutPath: null,
            MaxAge: null,
            StaleAge: TimeSpan.FromDays(2),
            RegionDataCenterConcurrency: 1,
            AdaptiveDataCenterConcurrency: false,
            DataCenterOrder: DataCenterOrderMode.Default,
            PerDataCenterChunkConcurrency: 1,
            InitialChunkSize: 25,
            MinChunkSize: 2,
            MaxRetries: 3,
            RespectRetryAfter: respectRetryAfter,
            FakeHttpScenario: FakeUniversalisScenario.None,
            FakeGatewayTimeoutDelayMs: 250,
            FakeSuccessDelayMs: 0,
            ShowHelp: false,
            HasError: false);
    }
}
