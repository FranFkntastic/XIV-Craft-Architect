using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

var options = ProbeOptions.Parse(args);
if (options.ShowHelp)
{
    ProbeOptions.PrintUsage();
    return options.HasError ? 1 : 0;
}

if (!File.Exists(options.PlanPath))
{
    Console.WriteLine($"Plan not found: {options.PlanPath}");
    return 1;
}

var run = await MarketAnalysisBenchmark.RunAsync(options);
MarketAnalysisBenchmark.PrintSummary(run);

if (!string.IsNullOrWhiteSpace(options.JsonOutPath))
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(options.JsonOutPath));
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    await File.WriteAllTextAsync(
        options.JsonOutPath,
        JsonSerializer.Serialize(run, ProbeJson.Options));
    Console.WriteLine($"Wrote JSON result: {options.JsonOutPath}");
}

return run.Errors == 0 ? 0 : 2;

internal sealed class MarketAnalysisBenchmark
{
    public static async Task<BenchmarkRunResult> RunAsync(ProbeOptions options)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var stages = new List<BenchmarkStageResult>();
        var memorySamples = new List<BenchmarkMemorySample>();
        var progressEvents = new List<BenchmarkProgressEvent>();
        var startedAt = DateTimeOffset.UtcNow;
        var fixture = await MarketDataFixture.LoadAsync(options.FixturePath);

        SampleMemory(memorySamples, "Start");

        var planStage = Stopwatch.StartNew();
        var planJson = await File.ReadAllTextAsync(options.PlanPath);
        var recipeService = new RecipeCalculationService(null!, null!);
        var plan = recipeService.DeserializePlan(planJson);
        planStage.Stop();
        stages.Add(BenchmarkStageResult.FromStopwatch("PlanLoad", planStage));

        if (plan == null)
        {
            throw new InvalidOperationException("Failed to deserialize plan.");
        }

        var dataCenters = ResolveDataCenters(options, plan);
        var candidates = AcquisitionPlanningService.GetMarketAnalysisCandidates(plan)
            .Where(item => item.TotalQuantity > 0)
            .ToList();
        var requests = candidates
            .SelectMany(item => dataCenters.Select(dataCenter => (item.ItemId, dataCenter)))
            .Distinct()
            .ToList();

        SampleMemory(memorySamples, "AfterPlanLoad");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var fakeMarketHttpClient = options.FakeHttpScenario == FakeUniversalisScenario.None
            ? null
            : new HttpClient(new ProbeFakeUniversalisHttpMessageHandler(
                options.FakeHttpScenario,
                TimeSpan.FromMilliseconds(options.FakeGatewayTimeoutDelayMs),
                TimeSpan.FromMilliseconds(options.FakeSuccessDelayMs)))
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
        var universalis = new UniversalisService(httpClient);
        IMarketDataSource marketSource = fixture == null
            ? new InstrumentedUniversalisMarketDataSource(fakeMarketHttpClient ?? httpClient, options)
            : new FixtureMarketDataSource(fixture);
        var cache = new BenchmarkMarketCache(marketSource, options);
        cache.Seed(fixture, options.CacheMode, requests, options.StaleAge);

        var worldDataStage = Stopwatch.StartNew();
        var worldData = await LoadWorldDataAsync(universalis, fixture);
        worldDataStage.Stop();
        stages.Add(BenchmarkStageResult.FromStopwatch("WorldDataLoad", worldDataStage));

        var expectedWorlds = dataCenters.ToDictionary(
            dataCenter => dataCenter,
            dataCenter => worldData.TryGetValue(dataCenter, out var worlds)
                ? worlds
                : Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var scope = options.SearchRegion
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;
        var progress = new Progress<string>(message =>
        {
            var elapsed = totalStopwatch.Elapsed;
            progressEvents.Add(new BenchmarkProgressEvent(elapsed, message));
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        });
        var analysisService = new MarketAnalysisExecutionService(
            cache,
            new MarketPriceLadderAnalysisService());

        var analysisStage = Stopwatch.StartNew();
        var executionResult = await analysisService.ExecuteAsync(
            new MarketAnalysisExecutionRequest
            {
                Items = candidates,
                Scope = scope,
                SelectedDataCenter = dataCenters[0],
                SelectedRegion = "North America",
                MaxAge = options.MaxAge,
                RecommendationMode = RecommendationMode.MinimizeTotalCost,
                Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                ExpectedWorldsByDataCenter = expectedWorlds
            },
            progress);
        analysisStage.Stop();
        stages.Add(BenchmarkStageResult.FromStopwatch("ExecuteMarketAnalysis", analysisStage));
        stages.Add(new BenchmarkStageResult("MarketFetch", cache.FetchElapsed));
        stages.Add(new BenchmarkStageResult(
            "AnalysisAndProjectionEstimate",
            analysisStage.Elapsed - cache.FetchElapsed));

        SampleMemory(memorySamples, "AfterMarketAnalysis");

        var shape = BenchmarkShapeMetrics.FromExecutionResult(executionResult);
        var serializationStage = Stopwatch.StartNew();
        var payload = BenchmarkPayloadMetrics.FromExecutionResult(executionResult);
        serializationStage.Stop();
        stages.Add(BenchmarkStageResult.FromStopwatch("SerializationMeasurement", serializationStage));

        SampleMemory(memorySamples, "AfterSerializationMeasurement");

        if (!string.IsNullOrWhiteSpace(options.WriteFixturePath))
        {
            var writtenFixture = MarketDataFixture.FromCache(cache.Snapshot(), worldData);
            await writtenFixture.SaveAsync(options.WriteFixturePath);
            Console.WriteLine($"Wrote fixture: {options.WriteFixturePath}");
        }

        totalStopwatch.Stop();

        var plansForSummary = executionResult.ShoppingPlans;
        var errors = plansForSummary.Count(item => !string.IsNullOrWhiteSpace(item.Error));
        var recommended = plansForSummary.Count(item => item.RecommendedWorld != null);
        var totalCost = plansForSummary.Sum(item => item.RecommendedWorld?.TotalCost ?? 0);
        var topItems = plansForSummary
            .Where(item => item.RecommendedWorld != null)
            .OrderByDescending(item => item.RecommendedWorld!.TotalCost)
            .Take(10)
            .Select(item => new BenchmarkRecommendedItem(
                item.ItemId,
                item.Name,
                item.QuantityNeeded,
                item.RecommendedWorld!.WorldName,
                item.RecommendedWorld.TotalCost))
            .ToList();
        var errorItems = plansForSummary
            .Where(item => !string.IsNullOrWhiteSpace(item.Error))
            .Take(10)
            .Select(item => new BenchmarkErrorItem(
                item.ItemId,
                item.Name,
                item.QuantityNeeded,
                item.Error ?? string.Empty))
            .ToList();

        return new BenchmarkRunResult(
            StartedAtUtc: startedAt,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            PlanPath: options.PlanPath,
            PlanName: plan.Name,
            RootItems: plan.RootItems.Select(root => $"{root.Name} x{root.Quantity}").ToList(),
            Scope: options.SearchRegion ? "North America" : dataCenters[0],
            DataCenters: dataCenters,
            CacheMode: options.CacheMode.ToString(),
            SourceMode: GetSourceMode(fixture, options),
            FixturePath: options.FixturePath,
            PlanNodes: CountPlanNodes(plan),
            UniquePlanItemIds: plan.GetAllItemIds().Count,
            MarketAnalysisCandidates: candidates.Count,
            Requests: requests.Count,
            TotalElapsed: totalStopwatch.Elapsed,
            Stages: stages,
            MemorySamples: memorySamples,
            ProgressEvents: progressEvents,
            Cache: cache.ToResult(),
            Fetch: marketSource.GetMetrics(),
            Shape: shape,
            Payload: payload,
            Retention: BenchmarkRetentionMetrics.FromExecutionResult(executionResult),
            Fetched: executionResult.Evidence.FetchedCount,
            Plans: plansForSummary.Count,
            Recommended: recommended,
            Errors: errors,
            BestValueTotal: totalCost,
            TopRecommendedItems: topItems,
            ErrorItems: errorItems);
    }

    public static void PrintSummary(BenchmarkRunResult run)
    {
        Console.WriteLine($"Plan: {run.PlanName}");
        Console.WriteLine($"Roots: {string.Join(", ", run.RootItems)}");
        Console.WriteLine($"Plan nodes: {run.PlanNodes}");
        Console.WriteLine($"Unique plan item ids: {run.UniquePlanItemIds}");
        Console.WriteLine($"Market analysis candidates: {run.MarketAnalysisCandidates}");
        Console.WriteLine($"Scope: {run.Scope}");
        Console.WriteLine($"Cache mode: {run.CacheMode}; source: {run.SourceMode}");
        Console.WriteLine($"Requests: {run.Requests}");
        Console.WriteLine("Stages:");
        foreach (var stage in run.Stages)
        {
            Console.WriteLine($"  {stage.Name}: {FormatDuration(stage.Elapsed)}");
        }

        Console.WriteLine("Memory:");
        foreach (var sample in run.MemorySamples)
        {
            Console.WriteLine(
                $"  {sample.Stage}: managed={sample.ManagedBytes / 1024d / 1024d:N1} MB; " +
                $"workingSet={sample.WorkingSetBytes / 1024d / 1024d:N1} MB; " +
                $"private={sample.PrivateBytes / 1024d / 1024d:N1} MB");
        }

        Console.WriteLine("Current analysis shape:");
        Console.WriteLine(
            $"  Evidence entries: {run.Shape.EvidenceEntries}; cached listings: {run.Shape.CachedListings}");
        Console.WriteLine(
            $"  Analyses: {run.Shape.Analyses}; analyzed worlds: {run.Shape.AnalyzedWorlds}; " +
            $"analyzed listings: {run.Shape.AnalyzedListings}; price bands: {run.Shape.PriceBands}");
        Console.WriteLine(
            $"  Shopping plans: {run.Shape.ShoppingPlans}; world options: {run.Shape.WorldOptions}; " +
            $"shopping listings: {run.Shape.ShoppingListings}");
        Console.WriteLine("Payloads:");
        Console.WriteLine($"  Market intelligence bytes: {run.Payload.MarketIntelligenceJsonBytes:N0}");
        Console.WriteLine($"  Compact summary bytes: {run.Payload.CompactSummaryJsonBytes:N0}");
        Console.WriteLine($"  Cold detail bytes: {run.Payload.ColdDetailJsonBytes:N0}");
        Console.WriteLine($"  Source fact bytes: {run.Payload.SourceFactJsonBytes:N0}");
        Console.WriteLine($"  Legacy plans JSON bytes: {run.Payload.LegacyPlansJsonBytes:N0}");
        Console.WriteLine($"  Legacy analyses JSON bytes: {run.Payload.LegacyAnalysesJsonBytes:N0}");
        Console.WriteLine($"  Retained detail estimate bytes: {run.Payload.RetainedDetailEstimateBytes:N0}");
        Console.WriteLine("Cache:");
        Console.WriteLine(
            $"  Seeded={run.Cache.SeededEntries}; freshHits={run.Cache.FreshCacheHits}; " +
            $"staleHits={run.Cache.StaleCacheHits}; missing={run.Cache.MissingCacheRequests}; " +
            $"sourceFetches={run.Cache.SourceFetchRequests}");
        Console.WriteLine("Fetch orchestration:");
        Console.WriteLine(
            $"  regionDcConcurrency={run.Fetch.RegionDataCenterConcurrency}; " +
            $"adaptiveDc={run.Cache.AdaptiveDataCenterConcurrency}; " +
            $"dcOrder={run.Cache.DataCenterOrder}; " +
            $"perDcChunkConcurrency={run.Fetch.PerDataCenterChunkConcurrency}; " +
            $"respectRetryAfter={run.Fetch.RespectRetryAfter}; " +
            $"chunkRequests={run.Fetch.ChunkRequests}; retries={run.Fetch.RetryCount}; " +
            $"splits={run.Fetch.SplitCount}; 429={run.Fetch.RateLimit429Count}; " +
            $"504={run.Fetch.GatewayTimeout504Count}; timeouts={run.Fetch.TimeoutCount}; " +
            $"finalMissing={run.Fetch.FinalMissingItemCount}; " +
            $"retryAfter={run.Fetch.RetryAfterCount}; " +
            $"retryDelay={FormatDuration(run.Fetch.RetryAfterDelay + run.Fetch.BackoffDelay)}; " +
            $"observedDc={run.Cache.MinimumObservedDataCenterConcurrency}-{run.Cache.MaximumObservedDataCenterConcurrency}; " +
            $"dcReductions={run.Cache.DataCenterConcurrencyReductions}; " +
            $"dcIncreases={run.Cache.DataCenterConcurrencyIncreases}");
        foreach (var dataCenter in run.Fetch.DataCenters)
        {
            Console.WriteLine(
                $"  {dataCenter.DataCenter}: elapsed={FormatDuration(dataCenter.Elapsed)}; " +
                $"requested={dataCenter.RequestedItems}; fetched={dataCenter.FetchedItems}; " +
                $"missing={dataCenter.FinalMissingItems}; chunks={dataCenter.ChunkRequests}; " +
                $"retries={dataCenter.RetryCount}; splits={dataCenter.SplitCount}; " +
                $"429={dataCenter.RateLimit429Count}; 504={dataCenter.GatewayTimeout504Count}; " +
                $"timeouts={dataCenter.TimeoutCount}; retryAfter={dataCenter.RetryAfterCount}; " +
                $"retryDelay={FormatDuration(dataCenter.RetryAfterDelay + dataCenter.BackoffDelay)}");
        }
        Console.WriteLine("Analysis complete.");
        Console.WriteLine($"Elapsed: {FormatDuration(run.TotalElapsed)}");
        Console.WriteLine($"Fetched: {run.Fetched}");
        Console.WriteLine($"Plans: {run.Plans}; Recommended: {run.Recommended}; Errors: {run.Errors}");
        Console.WriteLine($"Best value total: {run.BestValueTotal:N0}g");

        foreach (var item in run.TopRecommendedItems)
        {
            Console.WriteLine(
                $"{item.Name} x{item.QuantityNeeded}: {item.WorldName} {item.TotalCost:N0}g");
        }

        foreach (var item in run.ErrorItems)
        {
            Console.WriteLine($"ERROR {item.Name} x{item.QuantityNeeded}: {item.Error}");
        }
    }

    private static List<string> ResolveDataCenters(ProbeOptions options, CraftingPlan plan)
    {
        List<string> dataCenters;
        if (options.SearchRegion)
        {
            dataCenters = ["Aether", "Primal", "Crystal", "Dynamis"];
        }
        else if (!string.IsNullOrWhiteSpace(options.DataCenter))
        {
            dataCenters = [options.DataCenter];
        }
        else if (!string.IsNullOrWhiteSpace(plan.DataCenter))
        {
            dataCenters = [plan.DataCenter];
        }
        else
        {
            dataCenters = ["Aether"];
        }

        return ApplyDataCenterOrder(dataCenters, options.DataCenterOrder);
    }

    private static List<string> ApplyDataCenterOrder(
        List<string> dataCenters,
        DataCenterOrderMode order)
    {
        return order switch
        {
            DataCenterOrderMode.Alphabetical => dataCenters
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            DataCenterOrderMode.SlowFirst => dataCenters
                .OrderBy(dataCenter => GetObservedSpeedRank(dataCenter))
                .ToList(),
            DataCenterOrderMode.FastFirst => dataCenters
                .OrderByDescending(dataCenter => GetObservedSpeedRank(dataCenter))
                .ToList(),
            DataCenterOrderMode.Paired => dataCenters
                .OrderBy(dataCenter => GetPairedRank(dataCenter))
                .ToList(),
            _ => dataCenters
        };
    }

    private static int GetObservedSpeedRank(string dataCenter)
    {
        return dataCenter.ToUpperInvariant() switch
        {
            "DYNAMIS" => 0,
            "CRYSTAL" => 1,
            "PRIMAL" => 2,
            "AETHER" => 3,
            _ => 4
        };
    }

    private static int GetPairedRank(string dataCenter)
    {
        return dataCenter.ToUpperInvariant() switch
        {
            "DYNAMIS" => 0,
            "AETHER" => 1,
            "CRYSTAL" => 2,
            "PRIMAL" => 3,
            _ => 4
        };
    }

    private static async Task<Dictionary<string, IReadOnlyList<string>>> LoadWorldDataAsync(
        UniversalisService universalis,
        MarketDataFixture? fixture)
    {
        if (fixture?.WorldsByDataCenter.Count > 0)
        {
            return fixture.WorldsByDataCenter.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        var worldData = await universalis.GetWorldDataAsync();
        return worldData.DataCenterToWorlds.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static string GetSourceMode(MarketDataFixture? fixture, ProbeOptions options)
    {
        if (fixture != null)
        {
            return "Fixture";
        }

        return options.FakeHttpScenario == FakeUniversalisScenario.None
            ? "LiveUniversalis"
            : $"FakeHttp:{options.FakeHttpScenario}";
    }

    private static int CountPlanNodes(CraftingPlan plan)
    {
        return plan.RootItems.Sum(CountNode);

        static int CountNode(PlanNode node)
        {
            return 1 + node.Children.Sum(CountNode);
        }
    }

    private static void SampleMemory(List<BenchmarkMemorySample> samples, string stage)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        samples.Add(new BenchmarkMemorySample(
            stage,
            GC.GetTotalMemory(forceFullCollection: false),
            process.WorkingSet64,
            process.PrivateMemorySize64));
    }

    private static string FormatDuration(TimeSpan value)
    {
        return value.TotalSeconds >= 1
            ? value.ToString("mm\\:ss\\.fff")
            : $"{value.TotalMilliseconds:N0} ms";
    }
}

internal sealed record ProbeOptions(
    string PlanPath,
    bool SearchRegion,
    string? DataCenter,
    BenchmarkCacheMode CacheMode,
    string? FixturePath,
    string? WriteFixturePath,
    string? JsonOutPath,
    TimeSpan? MaxAge,
    TimeSpan StaleAge,
    int RegionDataCenterConcurrency,
    bool AdaptiveDataCenterConcurrency,
    DataCenterOrderMode DataCenterOrder,
    int PerDataCenterChunkConcurrency,
    int InitialChunkSize,
    int MinChunkSize,
    int MaxRetries,
    bool RespectRetryAfter,
    FakeUniversalisScenario FakeHttpScenario,
    int FakeGatewayTimeoutDelayMs,
    int FakeSuccessDelayMs,
    bool ShowHelp,
    bool HasError)
{
    public static ProbeOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            return new ProbeOptions(
                string.Empty,
                false,
                null,
                BenchmarkCacheMode.Cold,
                null,
                null,
                null,
                null,
                TimeSpan.FromDays(2),
                1,
                false,
                DataCenterOrderMode.Default,
                3,
                25,
                5,
                3,
                false,
                FakeUniversalisScenario.None,
                250,
                0,
                true,
                args.Length == 0);
        }

        var planPath = args[0];
        var cacheMode = Enum.TryParse<BenchmarkCacheMode>(
            GetOption(args, "--cache") ?? "Cold",
            ignoreCase: true,
            out var parsedCacheMode)
            ? parsedCacheMode
            : BenchmarkCacheMode.Cold;
        var maxAge = TryGetMinutes(args, "--max-age-minutes");
        var staleAge = TryGetMinutes(args, "--stale-age-minutes") ?? TimeSpan.FromDays(2);
        var regionDataCenterConcurrency = Math.Max(1, TryGetInt(args, "--region-dc-concurrency") ?? 1);
        var adaptiveDataCenterConcurrency = args.Contains("--adaptive-dc-concurrency", StringComparer.OrdinalIgnoreCase);
        var dataCenterOrder = Enum.TryParse<DataCenterOrderMode>(
            GetOption(args, "--dc-order") ?? "Default",
            ignoreCase: true,
            out var parsedDataCenterOrder)
            ? parsedDataCenterOrder
            : DataCenterOrderMode.Default;
        var perDataCenterChunkConcurrency = Math.Max(1, TryGetInt(args, "--per-dc-chunk-concurrency") ?? 3);
        var initialChunkSize = Math.Max(1, TryGetInt(args, "--initial-chunk-size") ?? 25);
        var minChunkSize = Math.Max(1, TryGetInt(args, "--min-chunk-size") ?? 5);
        var maxRetries = Math.Max(1, TryGetInt(args, "--max-retries") ?? 3);
        var respectRetryAfter = args.Contains("--respect-retry-after", StringComparer.OrdinalIgnoreCase);
        var fakeHttpScenario = Enum.TryParse<FakeUniversalisScenario>(
            GetOption(args, "--fake-http-scenario") ?? "None",
            ignoreCase: true,
            out var parsedFakeHttpScenario)
            ? parsedFakeHttpScenario
            : FakeUniversalisScenario.None;
        var fakeGatewayTimeoutDelayMs = Math.Max(0, TryGetInt(args, "--fake-504-delay-ms") ?? 250);
        var fakeSuccessDelayMs = Math.Max(0, TryGetInt(args, "--fake-success-delay-ms") ?? 0);

        return new ProbeOptions(
            planPath,
            args.Contains("--region", StringComparer.OrdinalIgnoreCase),
            GetOption(args, "--dc"),
            cacheMode,
            GetOption(args, "--fixture"),
            GetOption(args, "--write-fixture"),
            GetOption(args, "--json-out"),
            maxAge,
            staleAge,
            regionDataCenterConcurrency,
            adaptiveDataCenterConcurrency,
            dataCenterOrder,
            perDataCenterChunkConcurrency,
            initialChunkSize,
            minChunkSize,
            maxRetries,
            respectRetryAfter,
            fakeHttpScenario,
            fakeGatewayTimeoutDelayMs,
            fakeSuccessDelayMs,
            false,
            false);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project tools/MarketAnalysisProbe -- <plan.craftplan> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --region                       Analyze the North America region.");
        Console.WriteLine("  --dc <name>                     Analyze one data center. Default: plan data center or Aether.");
        Console.WriteLine("  --cache <cold|warm|partial|stale>");
        Console.WriteLine("                                  Seed cache mode when --fixture is provided.");
        Console.WriteLine("  --fixture <path>                Read deterministic market data fixture JSON.");
        Console.WriteLine("  --write-fixture <path>          Write fetched live market data fixture JSON.");
        Console.WriteLine("  --json-out <path>               Write benchmark result JSON.");
        Console.WriteLine("  --max-age-minutes <minutes>     Cache freshness threshold.");
        Console.WriteLine("  --stale-age-minutes <minutes>   Age assigned to stale seeded fixture entries.");
        Console.WriteLine("  --region-dc-concurrency <count> Number of data centers fetched in parallel. Default: 1.");
        Console.WriteLine("  --adaptive-dc-concurrency       Start at bounded DC2, then raise/lower future DC batches by pressure.");
        Console.WriteLine("  --dc-order <default|alphabetical|slowfirst|fastfirst|paired>");
        Console.WriteLine("                                  Order regional data-center scheduling. Default preserves app order.");
        Console.WriteLine("  --per-dc-chunk-concurrency <count>");
        Console.WriteLine("                                  Number of bulk chunks per data center fetched in parallel. Default: 3.");
        Console.WriteLine("  --initial-chunk-size <count>    Initial Universalis bulk chunk size. Default: 25.");
        Console.WriteLine("  --min-chunk-size <count>        Minimum chunk size before permanent failure. Default: 5.");
        Console.WriteLine("  --max-retries <count>           Retries per chunk attempt. Default: 3.");
        Console.WriteLine("  --respect-retry-after           Honor server Retry-After headers when present.");
        Console.WriteLine("  --fake-http-scenario <none|retryafter429firstperdatacenter|retryafter504firstperdatacenter|liveshaped504pressure>");
        Console.WriteLine("                                  Use a probe-only fake Universalis market HTTP source.");
        Console.WriteLine("  --fake-504-delay-ms <ms>        Delay before fake 504 responses. Default: 250.");
        Console.WriteLine("  --fake-success-delay-ms <ms>    Delay before fake success responses. Default: 0.");
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static TimeSpan? TryGetMinutes(string[] args, string name)
    {
        var value = GetOption(args, name);
        return int.TryParse(value, out var minutes)
            ? TimeSpan.FromMinutes(minutes)
            : null;
    }

    private static int? TryGetInt(string[] args, string name)
    {
        var value = GetOption(args, name);
        return int.TryParse(value, out var parsed)
            ? parsed
            : null;
    }
}

internal enum BenchmarkCacheMode
{
    Cold,
    Warm,
    Partial,
    Stale
}

internal enum DataCenterOrderMode
{
    Default,
    Alphabetical,
    SlowFirst,
    FastFirst,
    Paired
}

internal enum FakeUniversalisScenario
{
    None,
    RetryAfter429FirstPerDataCenter,
    RetryAfter504FirstPerDataCenter,
    LiveShaped504Pressure
}

internal interface IMarketDataSource
{
    Task<Dictionary<int, UniversalisResponse>> GetMarketDataBulkAsync(
        string dataCenter,
        IReadOnlyList<int> itemIds,
        CancellationToken ct);

    BenchmarkFetchMetrics GetMetrics();
}

internal sealed class InstrumentedUniversalisMarketDataSource : IMarketDataSource
{
    private const string UniversalisApiUrl = "https://universalis.app/api/v2/{0}/{1}";

    private readonly HttpClient _httpClient;
    private readonly ProbeOptions _options;
    private readonly ConcurrentDictionary<string, MutableDataCenterFetchMetrics> _dataCenters =
        new(StringComparer.OrdinalIgnoreCase);

    public InstrumentedUniversalisMarketDataSource(HttpClient httpClient, ProbeOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<Dictionary<int, UniversalisResponse>> GetMarketDataBulkAsync(
        string dataCenter,
        IReadOnlyList<int> itemIds,
        CancellationToken ct)
    {
        var distinctItemIds = itemIds.Distinct().ToList();
        var dcMetrics = _dataCenters.GetOrAdd(dataCenter, static key => new MutableDataCenterFetchMetrics(key));
        dcMetrics.AddRequested(distinctItemIds.Count);

        if (distinctItemIds.Count == 0)
        {
            return new Dictionary<int, UniversalisResponse>();
        }

        var stopwatch = Stopwatch.StartNew();
        var results = new ConcurrentDictionary<int, UniversalisResponse>();
        var workQueue = new Queue<ProbeChunkWorkItem>();
        foreach (var (chunk, index) in distinctItemIds.Chunk(_options.InitialChunkSize).Select((chunk, index) => (chunk, index)))
        {
            workQueue.Enqueue(new ProbeChunkWorkItem(chunk.ToList(), index, SplitDepth: 0));
        }

        var delay = new ProbeDelayStrategy();
        while (workQueue.Count > 0)
        {
            var batch = new List<ProbeChunkWorkItem>();
            while (workQueue.Count > 0 && batch.Count < _options.PerDataCenterChunkConcurrency)
            {
                batch.Add(workQueue.Dequeue());
            }

            var tasks = batch.Select(workItem => FetchChunkWithSplitAsync(
                workItem,
                dataCenter,
                results,
                delay,
                dcMetrics,
                ct));
            var chunkResults = await Task.WhenAll(tasks);
            foreach (var result in chunkResults)
            {
                if (result.ShouldSplit && result.WorkItem.CanSplit(_options.MinChunkSize))
                {
                    var split = result.WorkItem.Split();
                    dcMetrics.AddSplit(split.Count);
                    foreach (var splitItem in split)
                    {
                        workQueue.Enqueue(splitItem);
                    }
                }
            }

            if (workQueue.Count > 0)
            {
                var interBatchDelay = TimeSpan.FromMilliseconds(delay.GetDelay());
                dcMetrics.AddBackoffDelay(interBatchDelay);
                await Task.Delay(interBatchDelay, ct);
            }
        }

        var missing = distinctItemIds.Where(itemId => !results.ContainsKey(itemId)).ToList();
        if (missing.Count > 0)
        {
            dcMetrics.AddMissing(missing.Count);
        }

        stopwatch.Stop();
        dcMetrics.AddElapsed(stopwatch.Elapsed);
        dcMetrics.AddFetched(results.Count);
        return new Dictionary<int, UniversalisResponse>(results);
    }

    public BenchmarkFetchMetrics GetMetrics()
    {
        var dataCenters = _dataCenters.Values
            .Select(metrics => metrics.ToResult())
            .OrderBy(metrics => metrics.DataCenter, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BenchmarkFetchMetrics(
            _options.RegionDataCenterConcurrency,
            _options.PerDataCenterChunkConcurrency,
            _options.InitialChunkSize,
            _options.MinChunkSize,
            _options.RespectRetryAfter,
            dataCenters.Sum(metrics => metrics.RequestedItems),
            dataCenters.Sum(metrics => metrics.FetchedItems),
            dataCenters.Sum(metrics => metrics.ChunkRequests),
            dataCenters.Sum(metrics => metrics.RetryCount),
            dataCenters.Sum(metrics => metrics.SplitCount),
            dataCenters.Sum(metrics => metrics.RateLimit429Count),
            dataCenters.Sum(metrics => metrics.GatewayTimeout504Count),
            dataCenters.Sum(metrics => metrics.TimeoutCount),
            dataCenters.Sum(metrics => metrics.MissingInResponseCount),
            dataCenters.Sum(metrics => metrics.FinalMissingItems),
            dataCenters.Sum(metrics => metrics.RetryAfterCount),
            TimeSpan.FromTicks(dataCenters.Sum(metrics => metrics.RetryAfterDelay.Ticks)),
            TimeSpan.FromTicks(dataCenters.Sum(metrics => metrics.BackoffDelay.Ticks)),
            dataCenters);
    }

    private async Task<ProbeChunkFetchResult> FetchChunkWithSplitAsync(
        ProbeChunkWorkItem workItem,
        string dataCenter,
        ConcurrentDictionary<int, UniversalisResponse> results,
        ProbeDelayStrategy delay,
        MutableDataCenterFetchMetrics dcMetrics,
        CancellationToken ct)
    {
        for (var retry = 0; retry < _options.MaxRetries; retry++)
        {
            if (retry > 0)
            {
                dcMetrics.IncrementRetries();
                var retryDelay = delay.GetRetryDelay(retry);
                dcMetrics.AddBackoffDelay(retryDelay);
                await Task.Delay(retryDelay, ct);
            }

            dcMetrics.IncrementChunkRequests();
            var url = string.Format(
                UniversalisApiUrl,
                Uri.EscapeDataString(dataCenter),
                string.Join(",", workItem.ItemIds));

            try
            {
                using var response = await _httpClient.GetAsync(url, ct);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    dcMetrics.Increment429();
                    var retryAfter = _options.RespectRetryAfter
                        ? GetRetryAfterDelay(response)
                        : null;
                    if (retryAfter.HasValue)
                    {
                        dcMetrics.AddRetryAfter(retryAfter.Value);
                    }

                    delay.ReportFailure(retryAfter);
                    continue;
                }

                if ((int)response.StatusCode == 504)
                {
                    dcMetrics.Increment504();
                    var retryAfter = _options.RespectRetryAfter
                        ? GetRetryAfterDelay(response)
                        : null;
                    if (retryAfter.HasValue)
                    {
                        dcMetrics.AddRetryAfter(retryAfter.Value);
                    }

                    delay.ReportFailure(retryAfter);
                    if (workItem.CanSplit(_options.MinChunkSize))
                    {
                        return new ProbeChunkFetchResult(workItem, ShouldSplit: true);
                    }

                    continue;
                }

                response.EnsureSuccessStatusCode();

                if (workItem.ItemIds.Count == 1)
                {
                    var singleResult = await response.Content.ReadFromJsonAsync<UniversalisResponse>(ct);
                    if (singleResult != null)
                    {
                        results[workItem.ItemIds[0]] = singleResult;
                    }
                }
                else
                {
                    var bulkResult = await response.Content.ReadFromJsonAsync<UniversalisBulkResponse>(ct);
                    if (bulkResult?.Items != null)
                    {
                        var returnedIds = new HashSet<int>();
                        foreach (var (itemId, itemResponse) in bulkResult.Items)
                        {
                            results[itemId] = itemResponse;
                            returnedIds.Add(itemId);
                        }

                        var missing = workItem.ItemIds.Except(returnedIds).ToList();
                        if (missing.Count > 0)
                        {
                            dcMetrics.AddMissingInResponse(missing.Count);
                            delay.ReportFailure(null);
                            if (workItem.CanSplit(_options.MinChunkSize))
                            {
                                return new ProbeChunkFetchResult(workItem, ShouldSplit: true);
                            }
                        }
                    }
                }

                delay.ReportSuccess();
                return new ProbeChunkFetchResult(workItem, ShouldSplit: false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                dcMetrics.IncrementTimeouts();
                delay.ReportFailure(null);
                if (workItem.CanSplit(_options.MinChunkSize))
                {
                    return new ProbeChunkFetchResult(workItem, ShouldSplit: true);
                }
            }
            catch (HttpRequestException)
            {
                delay.ReportFailure(null);
            }
        }

        return new ProbeChunkFetchResult(workItem, ShouldSplit: false);
    }

    private static TimeSpan? GetRetryAfterDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null)
        {
            return null;
        }

        if (retryAfter.Delta.HasValue)
        {
            return retryAfter.Delta.Value;
        }

        if (retryAfter.Date.HasValue)
        {
            var delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }
}

internal sealed class FixtureMarketDataSource : IMarketDataSource
{
    private readonly MarketDataFixture _fixture;

    public FixtureMarketDataSource(MarketDataFixture fixture)
    {
        _fixture = fixture;
    }

    public Task<Dictionary<int, UniversalisResponse>> GetMarketDataBulkAsync(
        string dataCenter,
        IReadOnlyList<int> itemIds,
        CancellationToken ct)
    {
        var responses = new Dictionary<int, UniversalisResponse>();
        foreach (var itemId in itemIds)
        {
            ct.ThrowIfCancellationRequested();
            if (_fixture.TryGet(itemId, dataCenter, out var data))
            {
                responses[itemId] = data.ToUniversalisResponse();
            }
        }

        return Task.FromResult(responses);
    }

    public BenchmarkFetchMetrics GetMetrics()
    {
        return BenchmarkFetchMetrics.Empty;
    }
}

internal sealed class ProbeFakeUniversalisHttpMessageHandler : HttpMessageHandler
{
    private static readonly IReadOnlyDictionary<string, string[]> WorldsByDataCenter =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Aether"] = ["Cactuar", "Gilgamesh", "Jenova", "Sargatanas"],
            ["Primal"] = ["Excalibur", "Famfrit", "Leviathan", "Ultros"],
            ["Crystal"] = ["Balmung", "Brynhildr", "Mateus", "Zalera"],
            ["Dynamis"] = ["Golem", "Marilith", "Rafflesia", "Seraph"]
        };

    private readonly FakeUniversalisScenario _scenario;
    private readonly TimeSpan _gatewayTimeoutDelay;
    private readonly TimeSpan _successDelay;
    private readonly ConcurrentDictionary<string, int> _dataCenterAttempts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _liveShapedRemaining504s =
        new(StringComparer.OrdinalIgnoreCase);

    public ProbeFakeUniversalisHttpMessageHandler(
        FakeUniversalisScenario scenario,
        TimeSpan? gatewayTimeoutDelay = null,
        TimeSpan? successDelay = null)
    {
        _scenario = scenario;
        _gatewayTimeoutDelay = gatewayTimeoutDelay ?? TimeSpan.FromMilliseconds(250);
        _successDelay = successDelay ?? TimeSpan.Zero;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var (dataCenter, itemIds) = ParseRequest(request);
        if (itemIds.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        var attempt = _dataCenterAttempts.AddOrUpdate(
            dataCenter,
            static _ => 1,
            static (_, current) => current + 1);
        if (attempt == 1 && _scenario == FakeUniversalisScenario.RetryAfter429FirstPerDataCenter)
        {
            return CreateRetryAfterResponse(HttpStatusCode.TooManyRequests);
        }

        if (attempt == 1 && _scenario == FakeUniversalisScenario.RetryAfter504FirstPerDataCenter)
        {
            return CreateRetryAfterResponse(HttpStatusCode.GatewayTimeout);
        }

        if (ShouldLiveShaped504(dataCenter, itemIds))
        {
            if (_gatewayTimeoutDelay > TimeSpan.Zero)
            {
                await Task.Delay(_gatewayTimeoutDelay, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
        }

        if (_successDelay > TimeSpan.Zero)
        {
            await Task.Delay(_successDelay, cancellationToken);
        }

        return CreateSuccessResponse(dataCenter, itemIds);
    }

    private bool ShouldLiveShaped504(string dataCenter, IReadOnlyList<int> itemIds)
    {
        if (_scenario != FakeUniversalisScenario.LiveShaped504Pressure)
        {
            return false;
        }

        var threshold = string.Equals(dataCenter, "Aether", StringComparison.OrdinalIgnoreCase)
            ? 5
            : 10;
        if (itemIds.Count <= threshold)
        {
            return false;
        }

        var budget = string.Equals(dataCenter, "Aether", StringComparison.OrdinalIgnoreCase)
            ? 8
            : 4;
        while (true)
        {
            var current = _liveShapedRemaining504s.GetOrAdd(dataCenter, budget);
            if (current <= 0)
            {
                return false;
            }

            if (_liveShapedRemaining504s.TryUpdate(dataCenter, current - 1, current))
            {
                return true;
            }
        }
    }

    private static (string DataCenter, IReadOnlyList<int> ItemIds) ParseRequest(HttpRequestMessage request)
    {
        var segments = request.RequestUri?.Segments
            .Select(segment => segment.Trim('/'))
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList() ?? [];
        if (segments.Count < 2)
        {
            return ("Aether", []);
        }

        var dataCenter = Uri.UnescapeDataString(segments[^2]);
        var itemIds = Uri.UnescapeDataString(segments[^1])
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var parsed) ? parsed : 0)
            .Where(value => value > 0)
            .ToList();
        return (dataCenter, itemIds);
    }

    private static HttpResponseMessage CreateRetryAfterResponse(HttpStatusCode statusCode)
    {
        var response = new HttpResponseMessage(statusCode);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
        return response;
    }

    private static HttpResponseMessage CreateSuccessResponse(
        string dataCenter,
        IReadOnlyList<int> itemIds)
    {
        return itemIds.Count == 1
            ? JsonResponse(CreateItemJson(dataCenter, itemIds[0]))
            : JsonResponse(CreateBulkJson(dataCenter, itemIds));
    }

    private static HttpResponseMessage JsonResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private static string CreateBulkJson(string dataCenter, IReadOnlyList<int> itemIds)
    {
        var itemJson = string.Join(
            ",",
            itemIds.Select(itemId => $"\"{itemId}\":{CreateItemJson(dataCenter, itemId)}"));
        return $$"""
            {
              "itemIDs": [{{string.Join(",", itemIds)}}],
              "items": { {{itemJson}} }
            }
            """;
    }

    private static string CreateItemJson(string dataCenter, int itemId)
    {
        var worlds = WorldsByDataCenter.TryGetValue(dataCenter, out var knownWorlds)
            ? knownWorlds
            : WorldsByDataCenter["Aether"];
        var basePrice = 1_000 + itemId % 250;
        var uploadTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var reviewTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var listingsJson = string.Join(
            ",",
            worlds.Select((world, index) => $$"""
                {
                  "pricePerUnit": {{basePrice + index * 17}},
                  "quantity": {{10000 + index * 100}},
                  "worldName": "{{world}}",
                  "dataCenterName": "{{dataCenter}}",
                  "retainerName": "ProbeRetainer{{index + 1}}",
                  "hq": false,
                  "lastReviewTime": {{reviewTime - index * 60}}
                }
                """));
        var worldUploadTimesJson = string.Join(
            ",",
            worlds.Select((_, index) => $"\"{1000 + index}\":{uploadTime - index * 60000}"));

        return $$"""
            {
              "itemID": {{itemId}},
              "dcName": "{{dataCenter}}",
              "lastUploadTime": {{uploadTime}},
              "worldUploadTimes": { {{worldUploadTimesJson}} },
              "listings": [{{listingsJson}}],
              "averagePrice": {{basePrice + 25}},
              "minPrice": {{basePrice}},
              "minPriceHQ": 0,
              "minPriceNQ": {{basePrice}},
              "averagePriceHQ": 0,
              "averagePriceNQ": {{basePrice + 25}}
            }
            """;
    }
}

internal sealed class MutableDataCenterFetchMetrics
{
    private long _elapsedTicks;
    private int _requestedItems;
    private int _fetchedItems;
    private int _chunkRequests;
    private int _retryCount;
    private int _splitCount;
    private int _rateLimit429Count;
    private int _gatewayTimeout504Count;
    private int _timeoutCount;
    private int _missingInResponseCount;
    private int _finalMissingItems;
    private int _retryAfterCount;
    private long _retryAfterDelayTicks;
    private long _backoffDelayTicks;

    public MutableDataCenterFetchMetrics(string dataCenter)
    {
        DataCenter = dataCenter;
    }

    public string DataCenter { get; }

    public void AddElapsed(TimeSpan elapsed) => Interlocked.Add(ref _elapsedTicks, elapsed.Ticks);

    public void AddRequested(int count) => Interlocked.Add(ref _requestedItems, count);

    public void AddFetched(int count) => Interlocked.Add(ref _fetchedItems, count);

    public void IncrementChunkRequests() => Interlocked.Increment(ref _chunkRequests);

    public void IncrementRetries() => Interlocked.Increment(ref _retryCount);

    public void AddSplit(int count) => Interlocked.Add(ref _splitCount, count);

    public void Increment429() => Interlocked.Increment(ref _rateLimit429Count);

    public void Increment504() => Interlocked.Increment(ref _gatewayTimeout504Count);

    public void IncrementTimeouts() => Interlocked.Increment(ref _timeoutCount);

    public void AddMissingInResponse(int count) => Interlocked.Add(ref _missingInResponseCount, count);

    public void AddMissing(int count) => Interlocked.Add(ref _finalMissingItems, count);

    public void AddRetryAfter(TimeSpan delay)
    {
        Interlocked.Increment(ref _retryAfterCount);
        Interlocked.Add(ref _retryAfterDelayTicks, ClampDelay(delay).Ticks);
    }

    public void AddBackoffDelay(TimeSpan delay) => Interlocked.Add(ref _backoffDelayTicks, delay.Ticks);

    public BenchmarkDataCenterFetchMetrics ToResult()
    {
        return new BenchmarkDataCenterFetchMetrics(
            DataCenter,
            TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks)),
            Volatile.Read(ref _requestedItems),
            Volatile.Read(ref _fetchedItems),
            Volatile.Read(ref _chunkRequests),
            Volatile.Read(ref _retryCount),
            Volatile.Read(ref _splitCount),
            Volatile.Read(ref _rateLimit429Count),
            Volatile.Read(ref _gatewayTimeout504Count),
            Volatile.Read(ref _timeoutCount),
            Volatile.Read(ref _missingInResponseCount),
            Volatile.Read(ref _finalMissingItems),
            Volatile.Read(ref _retryAfterCount),
            TimeSpan.FromTicks(Interlocked.Read(ref _retryAfterDelayTicks)),
            TimeSpan.FromTicks(Interlocked.Read(ref _backoffDelayTicks)));
    }

    private static TimeSpan ClampDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > TimeSpan.FromSeconds(10)
            ? TimeSpan.FromSeconds(10)
            : delay;
    }
}

internal sealed record ProbeChunkFetchResult(ProbeChunkWorkItem WorkItem, bool ShouldSplit);

internal sealed record ProbeChunkWorkItem(
    IReadOnlyList<int> ItemIds,
    int ChunkIndex,
    int SplitDepth)
{
    public bool CanSplit(int minChunkSize) => ItemIds.Count > minChunkSize;

    public IReadOnlyList<ProbeChunkWorkItem> Split()
    {
        var midpoint = ItemIds.Count / 2;
        return
        [
            new ProbeChunkWorkItem(ItemIds.Take(midpoint).ToList(), ChunkIndex * 2, SplitDepth + 1),
            new ProbeChunkWorkItem(ItemIds.Skip(midpoint).ToList(), ChunkIndex * 2 + 1, SplitDepth + 1)
        ];
    }
}

internal sealed class ProbeDelayStrategy
{
    private int _currentDelayMs = 200;

    public int GetDelay() => Volatile.Read(ref _currentDelayMs);

    public TimeSpan GetRetryDelay(int retry) => TimeSpan.FromMilliseconds(GetDelay() * retry);

    public void ReportSuccess()
    {
        var current = Volatile.Read(ref _currentDelayMs);
        if (current > 200)
        {
            Interlocked.Exchange(ref _currentDelayMs, Math.Max(200, current / 2));
        }
    }

    public void ReportFailure(TimeSpan? retryAfter)
    {
        var current = Volatile.Read(ref _currentDelayMs);
        var retryAfterMs = retryAfter.HasValue
            ? (int)Math.Ceiling(Math.Clamp(retryAfter.Value.TotalMilliseconds, 0, 10000))
            : 0;
        Interlocked.Exchange(ref _currentDelayMs, Math.Min(10000, Math.Max(current * 2, retryAfterMs)));
    }
}

internal sealed class BenchmarkMarketCache : IMarketCacheService
{
    private readonly ConcurrentDictionary<(int ItemId, string DataCenter), CachedMarketData> _cache = new();
    private readonly IMarketDataSource _marketSource;
    private readonly int _regionDataCenterConcurrency;
    private readonly bool _adaptiveDataCenterConcurrency;
    private readonly DataCenterOrderMode _dataCenterOrder;
    private readonly Stopwatch _fetchStopwatch = new();
    private int _sourceFetchRequests;
    private int _sourceFetchItems;
    private int _minimumObservedDataCenterConcurrency = int.MaxValue;
    private int _maximumObservedDataCenterConcurrency;
    private int _dataCenterConcurrencyReductions;
    private int _dataCenterConcurrencyIncreases;

    public BenchmarkMarketCache(IMarketDataSource marketSource, ProbeOptions options)
    {
        _marketSource = marketSource;
        _regionDataCenterConcurrency = Math.Max(1, options.RegionDataCenterConcurrency);
        _adaptiveDataCenterConcurrency = options.AdaptiveDataCenterConcurrency;
        _dataCenterOrder = options.DataCenterOrder;
    }

    public int SeededEntries { get; private set; }

    public int FreshCacheHits { get; private set; }

    public int StaleCacheHits { get; private set; }

    public int MissingCacheRequests { get; private set; }

    public int SourceFetchRequests => Volatile.Read(ref _sourceFetchRequests);

    public int SourceFetchItems => Volatile.Read(ref _sourceFetchItems);

    public TimeSpan FetchElapsed => _fetchStopwatch.Elapsed;

    public Task<CachedMarketData?> GetAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        if (_cache.TryGetValue((itemId, dataCenter), out var value) && !IsStale(value, maxAge))
        {
            FreshCacheHits++;
            return Task.FromResult<CachedMarketData?>(value);
        }

        return Task.FromResult<CachedMarketData?>(null);
    }

    public Task<(CachedMarketData? Data, bool IsStale)> GetWithStaleAsync(
        int itemId,
        string dataCenter,
        TimeSpan? maxAge = null)
    {
        if (!_cache.TryGetValue((itemId, dataCenter), out var value))
        {
            return Task.FromResult<(CachedMarketData?, bool)>((null, false));
        }

        var stale = IsStale(value, maxAge);
        if (stale)
        {
            StaleCacheHits++;
        }
        else
        {
            FreshCacheHits++;
        }

        return Task.FromResult<(CachedMarketData?, bool)>((value, stale));
    }

    public Task<IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData>> GetManyAsync(
        IReadOnlyCollection<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null)
    {
        var result = new Dictionary<(int itemId, string dataCenter), CachedMarketData>();
        foreach (var request in requests.Distinct())
        {
            if (!_cache.TryGetValue((request.itemId, request.dataCenter), out var value))
            {
                continue;
            }

            if (IsStale(value, maxAge))
            {
                StaleCacheHits++;
                continue;
            }

            FreshCacheHits++;
            result[request] = value;
        }

        return Task.FromResult<IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData>>(result);
    }

    public Task SetAsync(int itemId, string dataCenter, CachedMarketData data)
    {
        _cache[(itemId, dataCenter)] = data;
        return Task.CompletedTask;
    }

    public Task<bool> HasValidCacheAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        return Task.FromResult(
            _cache.TryGetValue((itemId, dataCenter), out var value) &&
            !IsStale(value, maxAge));
    }

    public Task<List<(int itemId, string dataCenter)>> GetMissingAsync(
        List<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null)
    {
        var missing = new List<(int itemId, string dataCenter)>();
        foreach (var request in requests.Distinct())
        {
            if (!_cache.TryGetValue((request.itemId, request.dataCenter), out var value))
            {
                missing.Add(request);
                continue;
            }

            if (IsStale(value, maxAge))
            {
                StaleCacheHits++;
                missing.Add(request);
            }
        }

        MissingCacheRequests += missing.Count;
        return Task.FromResult(missing);
    }

    public Task<int> CleanupStaleAsync(TimeSpan maxAge) => Task.FromResult(0);

    public Task<CacheStats> GetStatsAsync()
    {
        var values = _cache.Values.ToList();
        return Task.FromResult(new CacheStats
        {
            TotalEntries = values.Count,
            ValidEntries = values.Count(value => !value.IsOlderThan(TimeSpan.FromHours(1))),
            StaleEntries = values.Count(value => value.IsOlderThan(TimeSpan.FromHours(1))),
            OldestEntry = values.Count == 0 ? null : values.Min(value => value.FetchedAt),
            NewestEntry = values.Count == 0 ? null : values.Max(value => value.FetchedAt),
            ApproximateSizeBytes = JsonSerializer.SerializeToUtf8Bytes(values, ProbeJson.Options).LongLength
        });
    }

    public async Task<int> EnsurePopulatedAsync(
        List<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAge),
                maxAge,
                "Use RefreshRequestedAsync when fresh data is required for specific pairs.");
        }

        var missing = await GetMissingAsync(requests, maxAge);
        return await FetchMissingAsync(missing, progress, ct);
    }

    public async Task<int> RefreshRequestedAsync(
        List<(int itemId, string dataCenter)> requests,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return await FetchMissingAsync(requests, progress, ct);
    }

    private async Task<int> FetchMissingAsync(
        IReadOnlyCollection<(int itemId, string dataCenter)> requests,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var fetched = 0;
        var dcGroups = requests
            .GroupBy(request => request.dataCenter)
            .Select(group => (DataCenter: group.Key, ItemIds: group.Select(request => request.itemId).Distinct().ToList()))
            .OrderBy(group => GetDataCenterOrderRank(group.DataCenter))
            .ToList();

        _fetchStopwatch.Start();
        if (_regionDataCenterConcurrency <= 1)
        {
            foreach (var dcGroup in dcGroups)
            {
                RecordObservedDataCenterConcurrency(1);
                fetched += await FetchDataCenterAsync(dcGroup.DataCenter, dcGroup.ItemIds, progress, ct);
            }
        }
        else if (_adaptiveDataCenterConcurrency)
        {
            fetched = await FetchAdaptiveDataCenterBatchesAsync(dcGroups, progress, ct);
        }
        else
        {
            using var semaphore = new SemaphoreSlim(_regionDataCenterConcurrency);
            var tasks = dcGroups.Select(async dcGroup =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    return await FetchDataCenterAsync(dcGroup.DataCenter, dcGroup.ItemIds, progress, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            fetched = (await Task.WhenAll(tasks)).Sum();
            RecordObservedDataCenterConcurrency(Math.Min(_regionDataCenterConcurrency, dcGroups.Count));
        }
        _fetchStopwatch.Stop();

        return fetched;
    }

    private async Task<int> FetchAdaptiveDataCenterBatchesAsync(
        IReadOnlyList<(string DataCenter, List<int> ItemIds)> dcGroups,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var fetched = 0;
        var index = 0;
        var currentConcurrency = Math.Min(2, _regionDataCenterConcurrency);

        while (index < dcGroups.Count)
        {
            var batch = dcGroups
                .Skip(index)
                .Take(currentConcurrency)
                .ToList();
            RecordObservedDataCenterConcurrency(batch.Count);

            var before = _marketSource.GetMetrics();
            var tasks = batch.Select(dcGroup => FetchDataCenterAsync(
                dcGroup.DataCenter,
                dcGroup.ItemIds,
                progress,
                ct));
            fetched += (await Task.WhenAll(tasks)).Sum();

            var after = _marketSource.GetMetrics();
            var pressure = (after.RateLimit429Count - before.RateLimit429Count) +
                (after.GatewayTimeout504Count - before.GatewayTimeout504Count) +
                (after.TimeoutCount - before.TimeoutCount) +
                (after.FinalMissingItemCount - before.FinalMissingItemCount);
            if (pressure > 0 && currentConcurrency > 1)
            {
                currentConcurrency--;
                Interlocked.Increment(ref _dataCenterConcurrencyReductions);
            }
            else if (pressure == 0 && currentConcurrency < _regionDataCenterConcurrency)
            {
                currentConcurrency++;
                Interlocked.Increment(ref _dataCenterConcurrencyIncreases);
            }

            index += batch.Count;
        }

        return fetched;
    }

    private int GetDataCenterOrderRank(string dataCenter)
    {
        return _dataCenterOrder switch
        {
            DataCenterOrderMode.Alphabetical => dataCenter.ToUpperInvariant() switch
            {
                "AETHER" => 0,
                "CRYSTAL" => 1,
                "DYNAMIS" => 2,
                "PRIMAL" => 3,
                _ => 4
            },
            DataCenterOrderMode.SlowFirst => dataCenter.ToUpperInvariant() switch
            {
                "DYNAMIS" => 0,
                "CRYSTAL" => 1,
                "PRIMAL" => 2,
                "AETHER" => 3,
                _ => 4
            },
            DataCenterOrderMode.FastFirst => dataCenter.ToUpperInvariant() switch
            {
                "AETHER" => 0,
                "PRIMAL" => 1,
                "CRYSTAL" => 2,
                "DYNAMIS" => 3,
                _ => 4
            },
            DataCenterOrderMode.Paired => dataCenter.ToUpperInvariant() switch
            {
                "DYNAMIS" => 0,
                "AETHER" => 1,
                "CRYSTAL" => 2,
                "PRIMAL" => 3,
                _ => 4
            },
            _ => dataCenter.ToUpperInvariant() switch
            {
                "AETHER" => 0,
                "PRIMAL" => 1,
                "CRYSTAL" => 2,
                "DYNAMIS" => 3,
                _ => 4
            }
        };
    }

    private void RecordObservedDataCenterConcurrency(int value)
    {
        int currentMin;
        do
        {
            currentMin = Volatile.Read(ref _minimumObservedDataCenterConcurrency);
            if (value >= currentMin)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(
            ref _minimumObservedDataCenterConcurrency,
            value,
            currentMin) != currentMin);

        int currentMax;
        do
        {
            currentMax = Volatile.Read(ref _maximumObservedDataCenterConcurrency);
            if (value <= currentMax)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(
            ref _maximumObservedDataCenterConcurrency,
            value,
            currentMax) != currentMax);
    }

    private async Task<int> FetchDataCenterAsync(
        string dataCenter,
        IReadOnlyList<int> itemIds,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report($"Fetching {itemIds.Count} items from {dataCenter}...");
        Interlocked.Increment(ref _sourceFetchRequests);
        Interlocked.Add(ref _sourceFetchItems, itemIds.Count);

        var responses = await _marketSource.GetMarketDataBulkAsync(dataCenter, itemIds, ct);
        foreach (var (itemId, response) in responses)
        {
            _cache[(itemId, dataCenter)] = ConvertResponse(itemId, dataCenter, response, DateTimeOffset.UtcNow);
        }

        return responses.Count;
    }

    public void Seed(
        MarketDataFixture? fixture,
        BenchmarkCacheMode mode,
        IReadOnlyList<(int ItemId, string DataCenter)> requests,
        TimeSpan staleAge)
    {
        if (fixture == null || mode == BenchmarkCacheMode.Cold)
        {
            return;
        }

        var selected = mode == BenchmarkCacheMode.Partial
            ? requests.Where((_, index) => index % 2 == 0)
            : requests;
        var fetchedAt = mode == BenchmarkCacheMode.Stale
            ? DateTimeOffset.UtcNow - staleAge
            : DateTimeOffset.UtcNow;

        foreach (var request in selected.Distinct())
        {
            if (!fixture.TryGet(request.ItemId, request.DataCenter, out var entry))
            {
                continue;
            }

            _cache[(request.ItemId, request.DataCenter)] = entry.ToCachedMarketData(fetchedAt);
            SeededEntries++;
        }
    }

    public IReadOnlyList<CachedMarketData> Snapshot()
    {
        return _cache.Values.ToList();
    }

    public BenchmarkCacheResult ToResult()
    {
        return new BenchmarkCacheResult(
            SeededEntries,
            FreshCacheHits,
            StaleCacheHits,
            MissingCacheRequests,
            SourceFetchRequests,
            SourceFetchItems,
            _cache.Count,
            FetchElapsed,
            _regionDataCenterConcurrency,
            _adaptiveDataCenterConcurrency,
            _dataCenterOrder.ToString(),
            Volatile.Read(ref _minimumObservedDataCenterConcurrency) == int.MaxValue
                ? 0
                : Volatile.Read(ref _minimumObservedDataCenterConcurrency),
            Volatile.Read(ref _maximumObservedDataCenterConcurrency),
            Volatile.Read(ref _dataCenterConcurrencyReductions),
            Volatile.Read(ref _dataCenterConcurrencyIncreases));
    }

    private static bool IsStale(CachedMarketData data, TimeSpan? maxAge)
    {
        return maxAge.HasValue && data.IsOlderThan(maxAge.Value);
    }

    private static CachedMarketData ConvertResponse(
        int itemId,
        string dataCenter,
        UniversalisResponse response,
        DateTimeOffset fetchedAt)
    {
        return new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            FetchedAtUnix = fetchedAt.ToUnixTimeSeconds(),
            LastUploadTimeUnixMilliseconds = response.LastUploadTimeUnixMilliseconds > 0
                ? response.LastUploadTimeUnixMilliseconds
                : null,
            DCAveragePrice = (decimal)(response.AveragePriceNq > 0 ? response.AveragePriceNq : response.AveragePrice),
            HQAveragePrice = response.AveragePriceHq > 0 ? (decimal)response.AveragePriceHq : null,
            Worlds = response.Listings
                .GroupBy(listing => listing.WorldName ?? "Unknown")
                .Select(group => new CachedWorldData
                {
                    WorldName = group.Key,
                    LastUploadTimeUnixMilliseconds = response.LastUploadTimeUnixMilliseconds > 0
                        ? response.LastUploadTimeUnixMilliseconds
                        : null,
                    Listings = group.Select(listing => new CachedListing
                    {
                        Quantity = listing.Quantity,
                        PricePerUnit = listing.PricePerUnit,
                        RetainerName = listing.RetainerName ?? "Unknown",
                        IsHq = listing.IsHq,
                        LastReviewTimeUnix = listing.LastReviewTimeUnix > 0 ? listing.LastReviewTimeUnix : null
                    }).ToList()
                })
                .ToList()
        };
    }
}

internal sealed record BenchmarkRunResult(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string PlanPath,
    string PlanName,
    IReadOnlyList<string> RootItems,
    string Scope,
    IReadOnlyList<string> DataCenters,
    string CacheMode,
    string SourceMode,
    string? FixturePath,
    int PlanNodes,
    int UniquePlanItemIds,
    int MarketAnalysisCandidates,
    int Requests,
    TimeSpan TotalElapsed,
    IReadOnlyList<BenchmarkStageResult> Stages,
    IReadOnlyList<BenchmarkMemorySample> MemorySamples,
    IReadOnlyList<BenchmarkProgressEvent> ProgressEvents,
    BenchmarkCacheResult Cache,
    BenchmarkFetchMetrics Fetch,
    BenchmarkShapeMetrics Shape,
    BenchmarkPayloadMetrics Payload,
    BenchmarkRetentionMetrics Retention,
    int Fetched,
    int Plans,
    int Recommended,
    int Errors,
    decimal BestValueTotal,
    IReadOnlyList<BenchmarkRecommendedItem> TopRecommendedItems,
    IReadOnlyList<BenchmarkErrorItem> ErrorItems);

internal sealed record BenchmarkStageResult(string Name, TimeSpan Elapsed)
{
    public static BenchmarkStageResult FromStopwatch(string name, Stopwatch stopwatch)
    {
        return new BenchmarkStageResult(name, stopwatch.Elapsed);
    }
}

internal sealed record BenchmarkMemorySample(
    string Stage,
    long ManagedBytes,
    long WorkingSetBytes,
    long PrivateBytes);

internal sealed record BenchmarkProgressEvent(TimeSpan Elapsed, string Message);

internal sealed record BenchmarkCacheResult(
    int SeededEntries,
    int FreshCacheHits,
    int StaleCacheHits,
    int MissingCacheRequests,
    int SourceFetchRequests,
    int SourceFetchItems,
    int StoredEntries,
    TimeSpan FetchElapsed,
    int InitialRegionDataCenterConcurrency,
    bool AdaptiveDataCenterConcurrency,
    string DataCenterOrder,
    int MinimumObservedDataCenterConcurrency,
    int MaximumObservedDataCenterConcurrency,
    int DataCenterConcurrencyReductions,
    int DataCenterConcurrencyIncreases);

internal sealed record BenchmarkFetchMetrics(
    int RegionDataCenterConcurrency,
    int PerDataCenterChunkConcurrency,
    int InitialChunkSize,
    int MinChunkSize,
    bool RespectRetryAfter,
    int RequestedItems,
    int FetchedItems,
    int ChunkRequests,
    int RetryCount,
    int SplitCount,
    int RateLimit429Count,
    int GatewayTimeout504Count,
    int TimeoutCount,
    int MissingInResponseCount,
    int FinalMissingItemCount,
    int RetryAfterCount,
    TimeSpan RetryAfterDelay,
    TimeSpan BackoffDelay,
    IReadOnlyList<BenchmarkDataCenterFetchMetrics> DataCenters)
{
    public static BenchmarkFetchMetrics Empty { get; } = new(
        RegionDataCenterConcurrency: 1,
        PerDataCenterChunkConcurrency: 0,
        InitialChunkSize: 0,
        MinChunkSize: 0,
        RespectRetryAfter: false,
        RequestedItems: 0,
        FetchedItems: 0,
        ChunkRequests: 0,
        RetryCount: 0,
        SplitCount: 0,
        RateLimit429Count: 0,
        GatewayTimeout504Count: 0,
        TimeoutCount: 0,
        MissingInResponseCount: 0,
        FinalMissingItemCount: 0,
        RetryAfterCount: 0,
        RetryAfterDelay: TimeSpan.Zero,
        BackoffDelay: TimeSpan.Zero,
        DataCenters: Array.Empty<BenchmarkDataCenterFetchMetrics>());
}

internal sealed record BenchmarkDataCenterFetchMetrics(
    string DataCenter,
    TimeSpan Elapsed,
    int RequestedItems,
    int FetchedItems,
    int ChunkRequests,
    int RetryCount,
    int SplitCount,
    int RateLimit429Count,
    int GatewayTimeout504Count,
    int TimeoutCount,
    int MissingInResponseCount,
    int FinalMissingItems,
    int RetryAfterCount,
    TimeSpan RetryAfterDelay,
    TimeSpan BackoffDelay);

internal sealed record BenchmarkRecommendedItem(
    int ItemId,
    string Name,
    int QuantityNeeded,
    string WorldName,
    decimal TotalCost);

internal sealed record BenchmarkErrorItem(
    int ItemId,
    string Name,
    int QuantityNeeded,
    string Error);

internal sealed record BenchmarkShapeMetrics(
    int EvidenceEntries,
    int CachedListings,
    int Analyses,
    int AnalyzedWorlds,
    int AnalyzedListings,
    int PriceBands,
    int ShoppingPlans,
    int WorldOptions,
    int ShoppingListings)
{
    public static BenchmarkShapeMetrics FromExecutionResult(MarketAnalysisExecutionResult result)
    {
        var evidenceEntries = result.Evidence.Entries.Values.ToList();
        return new BenchmarkShapeMetrics(
            evidenceEntries.Count,
            evidenceEntries.Sum(entry => entry.Worlds.Sum(world => world.Listings.Count)),
            result.Analyses.Count,
            result.Analyses.Sum(analysis => analysis.Worlds.Count),
            result.Analyses.Sum(analysis => analysis.Worlds.Sum(world => world.Listings.Count)),
            result.Analyses.Sum(analysis => analysis.Worlds.Sum(world => world.PriceBands.Count)),
            result.ShoppingPlans.Count,
            result.ShoppingPlans.Sum(item => item.WorldOptions.Count),
            result.ShoppingPlans.Sum(item => item.WorldOptions.Sum(world => world.Listings.Count)));
    }
}

internal sealed record BenchmarkPayloadMetrics(
    long MarketIntelligenceJsonBytes,
    long CompactSummaryJsonBytes,
    long ColdDetailJsonBytes,
    long SourceFactJsonBytes,
    long LegacyPlansJsonBytes,
    long LegacyAnalysesJsonBytes,
    long RetainedDetailEstimateBytes)
{
    public static BenchmarkPayloadMetrics FromExecutionResult(MarketAnalysisExecutionResult result)
    {
        var intelligence = new MarketIntelligence(
            Guid.NewGuid(),
            result.Analyses,
            result.ShoppingPlans,
            Array.Empty<CoreMarketDataUnavailableItem>(),
            MarketIntelligencePublicationContext.UnknownLegacy(
                RecommendationMode.MinimizeTotalCost,
                MarketAcquisitionLens.MinimumUpfrontCost),
            null);
        var intelligenceJsonBytes = JsonSerializer
            .SerializeToUtf8Bytes(StoredMarketIntelligence.FromMarketIntelligence(intelligence), ProbeJson.Options)
            .LongLength;
        var projection = new MarketIntelligenceProjectionService().Project(
            new MarketIntelligenceProjectionRequest
            {
                ExecutionResult = result,
                PublicationContext = new MarketIntelligencePublicationContext(
                    MarketIntelligencePublicationContextKind.Known,
                    result.Evidence.Scope,
                    result.Evidence.SelectedDataCenter,
                    result.Evidence.SelectedRegion,
                    result.Evidence.DataCenters,
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                    null,
                    false,
                    RecommendationMode.MinimizeTotalCost,
                    MarketAcquisitionLens.MinimumUpfrontCost,
                    null,
                    null,
                    null,
                    result.Evidence.LoadedAtUtc),
                AnalyzerVersion = "market-analysis-probe",
                StartedAtUtc = result.Evidence.LoadedAtUtc,
                CompletedAtUtc = DateTime.UtcNow,
                MarketFetchDuration = TimeSpan.Zero,
                NetworkRequestCount = result.Evidence.FetchedCount
            });
        var compactSummaryJsonBytes = JsonSerializer
            .SerializeToUtf8Bytes(projection.Publication.Summary, ProbeJson.Options)
            .LongLength;
        var coldDetailJsonBytes = JsonSerializer
            .SerializeToUtf8Bytes(projection.Publication.Details, ProbeJson.Options)
            .LongLength;
        var sourceFactJsonBytes = JsonSerializer
            .SerializeToUtf8Bytes(projection.SourceFacts, ProbeJson.Options)
            .LongLength;
        var legacyPlansJsonBytes = JsonSerializer.SerializeToUtf8Bytes(result.ShoppingPlans, ProbeJson.Options).LongLength;
        var legacyAnalysesJsonBytes = JsonSerializer.SerializeToUtf8Bytes(result.Analyses, ProbeJson.Options).LongLength;

        return new BenchmarkPayloadMetrics(
            intelligenceJsonBytes,
            compactSummaryJsonBytes,
            coldDetailJsonBytes,
            sourceFactJsonBytes,
            legacyPlansJsonBytes,
            legacyAnalysesJsonBytes,
            coldDetailJsonBytes);
    }
}

internal sealed record BenchmarkRetentionMetrics(
    int ListingFacts,
    int WorldsWithListings,
    int ListingsWithPrice,
    int ListingsWithQuantity,
    int ListingsWithHqState,
    int ListingsWithRetainer,
    int ListingsWithReviewTime,
    int PriceBands,
    int ListingClassifications,
    int WorldsWithDataAge)
{
    public static BenchmarkRetentionMetrics FromExecutionResult(MarketAnalysisExecutionResult result)
    {
        var cachedWorlds = result.Evidence.Entries.Values
            .SelectMany(entry => entry.Worlds)
            .ToList();
        var cachedListings = cachedWorlds
            .SelectMany(world => world.Listings)
            .ToList();
        var analyzedWorlds = result.Analyses
            .SelectMany(analysis => analysis.Worlds)
            .ToList();

        return new BenchmarkRetentionMetrics(
            cachedListings.Count,
            cachedWorlds.Count(world => world.Listings.Count > 0),
            cachedListings.Count(listing => listing.PricePerUnit > 0),
            cachedListings.Count(listing => listing.Quantity > 0),
            cachedListings.Count,
            cachedListings.Count(listing => !string.IsNullOrWhiteSpace(listing.RetainerName)),
            cachedListings.Count(listing => listing.LastReviewTimeUnix.HasValue),
            analyzedWorlds.Sum(world => world.PriceBands.Count),
            analyzedWorlds.Sum(world => world.Listings.Count),
            analyzedWorlds.Count(world => world.DataAge.HasValue));
    }
}

internal sealed record MarketDataFixture(
    DateTimeOffset CreatedAtUtc,
    Dictionary<string, List<string>> WorldsByDataCenter,
    List<MarketDataFixtureEntry> Entries)
{
    public static async Task<MarketDataFixture?> LoadAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MarketDataFixture>(stream, ProbeJson.Options);
    }

    public static MarketDataFixture FromCache(
        IReadOnlyList<CachedMarketData> cache,
        IReadOnlyDictionary<string, IReadOnlyList<string>> worldsByDataCenter)
    {
        return new MarketDataFixture(
            DateTimeOffset.UtcNow,
            worldsByDataCenter.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList(),
                StringComparer.OrdinalIgnoreCase),
            cache.Select(MarketDataFixtureEntry.FromCachedMarketData).ToList());
    }

    public async Task SaveAsync(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(this, ProbeJson.Options));
    }

    public bool TryGet(int itemId, string dataCenter, out MarketDataFixtureEntry entry)
    {
        entry = Entries.FirstOrDefault(candidate =>
            candidate.ItemId == itemId &&
            string.Equals(candidate.DataCenter, dataCenter, StringComparison.OrdinalIgnoreCase))!;
        return entry != null;
    }
}

internal sealed record MarketDataFixtureEntry(
    int ItemId,
    string DataCenter,
    long LastUploadTimeUnixMilliseconds,
    decimal DCAveragePrice,
    decimal? HQAveragePrice,
    List<MarketDataFixtureWorld> Worlds)
{
    public static MarketDataFixtureEntry FromCachedMarketData(CachedMarketData data)
    {
        return new MarketDataFixtureEntry(
            data.ItemId,
            data.DataCenter,
            data.LastUploadTimeUnixMilliseconds ?? 0,
            data.DCAveragePrice,
            data.HQAveragePrice,
            data.Worlds
                .Select(world => new MarketDataFixtureWorld(
                    world.WorldName,
                    world.LastUploadTimeUnixMilliseconds ?? 0,
                    world.Listings
                        .Select(listing => new MarketDataFixtureListing(
                            listing.Quantity,
                            listing.PricePerUnit,
                            listing.RetainerName,
                            listing.IsHq,
                            listing.LastReviewTimeUnix ?? 0))
                        .ToList()))
                .ToList());
    }

    public CachedMarketData ToCachedMarketData(DateTimeOffset fetchedAt)
    {
        return new CachedMarketData
        {
            ItemId = ItemId,
            DataCenter = DataCenter,
            FetchedAtUnix = fetchedAt.ToUnixTimeSeconds(),
            LastUploadTimeUnixMilliseconds = LastUploadTimeUnixMilliseconds > 0
                ? LastUploadTimeUnixMilliseconds
                : null,
            DCAveragePrice = DCAveragePrice,
            HQAveragePrice = HQAveragePrice,
            Worlds = Worlds
                .Select(world => new CachedWorldData
                {
                    WorldName = world.WorldName,
                    LastUploadTimeUnixMilliseconds = world.LastUploadTimeUnixMilliseconds > 0
                        ? world.LastUploadTimeUnixMilliseconds
                        : null,
                    Listings = world.Listings
                        .Select(listing => new CachedListing
                        {
                            Quantity = listing.Quantity,
                            PricePerUnit = listing.PricePerUnit,
                            RetainerName = listing.RetainerName,
                            IsHq = listing.IsHq,
                            LastReviewTimeUnix = listing.LastReviewTimeUnix > 0
                                ? listing.LastReviewTimeUnix
                                : null
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    public UniversalisResponse ToUniversalisResponse()
    {
        return new UniversalisResponse
        {
            AveragePrice = (double)DCAveragePrice,
            AveragePriceNq = (double)DCAveragePrice,
            AveragePriceHq = HQAveragePrice.HasValue ? (double)HQAveragePrice.Value : 0,
            LastUploadTimeUnixMilliseconds = LastUploadTimeUnixMilliseconds,
            Listings = Worlds
                .SelectMany(world => world.Listings.Select(listing => new MarketListing
                {
                    WorldName = world.WorldName,
                    Quantity = listing.Quantity,
                    PricePerUnit = listing.PricePerUnit,
                    RetainerName = listing.RetainerName,
                    IsHq = listing.IsHq,
                    LastReviewTimeUnix = listing.LastReviewTimeUnix
                }))
                .ToList()
        };
    }
}

internal sealed record MarketDataFixtureWorld(
    string WorldName,
    long LastUploadTimeUnixMilliseconds,
    List<MarketDataFixtureListing> Listings);

internal sealed record MarketDataFixtureListing(
    int Quantity,
    long PricePerUnit,
    string RetainerName,
    bool IsHq,
    long LastReviewTimeUnix);

internal static class ProbeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
