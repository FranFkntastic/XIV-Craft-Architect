using System.Diagnostics;
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
        var universalis = new UniversalisService(httpClient);
        IMarketDataSource marketSource = fixture == null
            ? new LiveMarketDataSource(universalis)
            : new FixtureMarketDataSource(fixture);
        var cache = new BenchmarkMarketCache(marketSource);
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
            SourceMode: fixture == null ? "LiveUniversalis" : "Fixture",
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
        Console.WriteLine($"  Legacy plans JSON bytes: {run.Payload.LegacyPlansJsonBytes:N0}");
        Console.WriteLine($"  Legacy analyses JSON bytes: {run.Payload.LegacyAnalysesJsonBytes:N0}");
        Console.WriteLine($"  Full legacy market JSON bytes: {run.Payload.FullLegacyMarketJsonBytes:N0}");
        Console.WriteLine("Cache:");
        Console.WriteLine(
            $"  Seeded={run.Cache.SeededEntries}; freshHits={run.Cache.FreshCacheHits}; " +
            $"staleHits={run.Cache.StaleCacheHits}; missing={run.Cache.MissingCacheRequests}; " +
            $"sourceFetches={run.Cache.SourceFetchRequests}");
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
        if (options.SearchRegion)
        {
            return ["Aether", "Primal", "Crystal", "Dynamis"];
        }

        if (!string.IsNullOrWhiteSpace(options.DataCenter))
        {
            return [options.DataCenter];
        }

        if (!string.IsNullOrWhiteSpace(plan.DataCenter))
        {
            return [plan.DataCenter];
        }

        return ["Aether"];
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
}

internal enum BenchmarkCacheMode
{
    Cold,
    Warm,
    Partial,
    Stale
}

internal interface IMarketDataSource
{
    Task<Dictionary<int, UniversalisResponse>> GetMarketDataBulkAsync(
        string dataCenter,
        IReadOnlyList<int> itemIds,
        CancellationToken ct);
}

internal sealed class LiveMarketDataSource : IMarketDataSource
{
    private readonly UniversalisService _universalis;

    public LiveMarketDataSource(UniversalisService universalis)
    {
        _universalis = universalis;
    }

    public Task<Dictionary<int, UniversalisResponse>> GetMarketDataBulkAsync(
        string dataCenter,
        IReadOnlyList<int> itemIds,
        CancellationToken ct)
    {
        return _universalis.GetMarketDataBulkAsync(dataCenter, itemIds, useParallel: true, ct);
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
}

internal sealed class BenchmarkMarketCache : IMarketCacheService
{
    private readonly Dictionary<(int ItemId, string DataCenter), CachedMarketData> _cache = new();
    private readonly IMarketDataSource _marketSource;
    private readonly Stopwatch _fetchStopwatch = new();

    public BenchmarkMarketCache(IMarketDataSource marketSource)
    {
        _marketSource = marketSource;
    }

    public int SeededEntries { get; private set; }

    public int FreshCacheHits { get; private set; }

    public int StaleCacheHits { get; private set; }

    public int MissingCacheRequests { get; private set; }

    public int SourceFetchRequests { get; private set; }

    public int SourceFetchItems { get; private set; }

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
        var missing = await GetMissingAsync(requests, maxAge);
        var fetched = 0;

        foreach (var dcGroup in missing.GroupBy(request => request.dataCenter))
        {
            var dataCenter = dcGroup.Key;
            var itemIds = dcGroup.Select(request => request.itemId).Distinct().ToList();
            progress?.Report($"Fetching {itemIds.Count} items from {dataCenter}...");
            SourceFetchRequests++;
            SourceFetchItems += itemIds.Count;

            _fetchStopwatch.Start();
            var responses = await _marketSource.GetMarketDataBulkAsync(dataCenter, itemIds, ct);
            _fetchStopwatch.Stop();

            foreach (var (itemId, response) in responses)
            {
                _cache[(itemId, dataCenter)] = ConvertResponse(itemId, dataCenter, response, DateTimeOffset.UtcNow);
                fetched++;
            }
        }

        return fetched;
    }

    public async Task<int> RefreshRequestedAsync(
        List<(int itemId, string dataCenter)> requests,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        foreach (var request in requests.Distinct())
        {
            _cache.Remove((request.itemId, request.dataCenter));
        }

        return await EnsurePopulatedAsync(requests, TimeSpan.Zero, progress, ct);
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
            FetchElapsed);
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
    TimeSpan FetchElapsed);

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
    long LegacyPlansJsonBytes,
    long LegacyAnalysesJsonBytes,
    long FullLegacyMarketJsonBytes)
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
        var legacyPlansJsonBytes = JsonSerializer.SerializeToUtf8Bytes(result.ShoppingPlans, ProbeJson.Options).LongLength;
        var legacyAnalysesJsonBytes = JsonSerializer.SerializeToUtf8Bytes(result.Analyses, ProbeJson.Options).LongLength;

        return new BenchmarkPayloadMetrics(
            intelligenceJsonBytes,
            legacyPlansJsonBytes,
            legacyAnalysesJsonBytes,
            legacyPlansJsonBytes + legacyAnalysesJsonBytes);
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
