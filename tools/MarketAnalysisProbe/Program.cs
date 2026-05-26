using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project tools/MarketAnalysisProbe -- <plan.craftplan> [--region] [--dc Aether]");
    return args.Length == 0 ? 1 : 0;
}

var planPath = args[0];
var searchRegion = args.Contains("--region", StringComparer.OrdinalIgnoreCase);
var dc = GetOption(args, "--dc");

if (!File.Exists(planPath))
{
    Console.WriteLine($"Plan not found: {planPath}");
    return 1;
}

string[] dataCenters = searchRegion
    ? ["Aether", "Primal", "Crystal", "Dynamis"]
    : [dc ?? "Aether"];

var planJson = await File.ReadAllTextAsync(planPath);
var recipeService = new RecipeCalculationService(null!, null!);
var plan = recipeService.DeserializePlan(planJson);
if (plan == null)
{
    Console.WriteLine("Failed to deserialize plan.");
    return 1;
}

if (!searchRegion && string.IsNullOrWhiteSpace(dc) && !string.IsNullOrWhiteSpace(plan.DataCenter))
{
    dataCenters = [plan.DataCenter];
}

var candidates = AcquisitionPlanningService.GetMarketAnalysisCandidates(plan)
    .Where(item => item.TotalQuantity > 0)
    .ToList();

Console.WriteLine($"Plan: {plan.Name}");
Console.WriteLine($"Roots: {string.Join(", ", plan.RootItems.Select(root => $"{root.Name} x{root.Quantity}"))}");
Console.WriteLine($"Market analysis candidates: {candidates.Count}");
Console.WriteLine($"Scope: {(searchRegion ? "North America" : dataCenters[0])}");
Console.WriteLine($"Requests: {candidates.Count * dataCenters.Length}");

var cache = new InMemoryMarketCache();
using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var universalis = new UniversalisService(httpClient);
var shoppingService = new MarketShoppingService(cache);
var progress = new Progress<string>(message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"));
var startedAt = DateTime.UtcNow;
var fetched = 0;

foreach (var dataCenter in dataCenters)
{
    var requests = candidates.Select(item => (item.ItemId, dataCenter)).ToList();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Ensuring {requests.Count} candidates on {dataCenter}...");
    fetched += await cache.EnsurePopulatedAsync(requests, universalis, progress);
}

List<DetailedShoppingPlan> plans = searchRegion
    ? await shoppingService.CalculateDetailedShoppingPlansMultiDCAsync(
        candidates,
        progress,
        mode: RecommendationMode.MinimizeTotalCost)
    : await shoppingService.CalculateDetailedShoppingPlansAsync(
        candidates,
        dataCenters[0],
        progress,
        mode: RecommendationMode.MinimizeTotalCost);

var elapsed = DateTime.UtcNow - startedAt;
var errors = plans.Count(item => !string.IsNullOrWhiteSpace(item.Error));
var recommended = plans.Count(item => item.RecommendedWorld != null);
var totalCost = plans.Sum(item => item.RecommendedWorld?.TotalCost ?? 0);

Console.WriteLine("Analysis complete.");
Console.WriteLine($"Elapsed: {elapsed:mm\\:ss}");
Console.WriteLine($"Fetched: {fetched}");
Console.WriteLine($"Plans: {plans.Count}; Recommended: {recommended}; Errors: {errors}");
Console.WriteLine($"Best value total: {totalCost:N0}g");

foreach (var item in plans
    .Where(item => item.RecommendedWorld != null)
    .OrderByDescending(item => item.RecommendedWorld!.TotalCost)
    .Take(10))
{
    Console.WriteLine($"{item.Name} x{item.QuantityNeeded}: {item.RecommendedWorld!.WorldName} {item.RecommendedWorld.TotalCost:N0}g");
}

foreach (var item in plans.Where(item => !string.IsNullOrWhiteSpace(item.Error)).Take(10))
{
    Console.WriteLine($"ERROR {item.Name} x{item.QuantityNeeded}: {item.Error}");
}

return errors == 0 ? 0 : 2;

static string? GetOption(string[] args, string name)
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

sealed class InMemoryMarketCache : IMarketCacheService
{
    private readonly Dictionary<(int ItemId, string DataCenter), CachedMarketData> _cache = new();

    public Task<CachedMarketData?> GetAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        _cache.TryGetValue((itemId, dataCenter), out var value);
        return Task.FromResult(value);
    }

    public Task<(CachedMarketData? Data, bool IsStale)> GetWithStaleAsync(
        int itemId,
        string dataCenter,
        TimeSpan? maxAge = null)
    {
        _cache.TryGetValue((itemId, dataCenter), out var value);
        return Task.FromResult<(CachedMarketData?, bool)>((value, false));
    }

    public Task SetAsync(int itemId, string dataCenter, CachedMarketData data)
    {
        _cache[(itemId, dataCenter)] = data;
        return Task.CompletedTask;
    }

    public Task<bool> HasValidCacheAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        return Task.FromResult(_cache.ContainsKey((itemId, dataCenter)));
    }

    public Task<List<(int itemId, string dataCenter)>> GetMissingAsync(
        List<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null)
    {
        return Task.FromResult(requests
            .Distinct()
            .Where(request => !_cache.ContainsKey((request.itemId, request.dataCenter)))
            .ToList());
    }

    public Task<int> CleanupStaleAsync(TimeSpan maxAge) => Task.FromResult(0);

    public Task<CacheStats> GetStatsAsync() => Task.FromResult(new CacheStats
    {
        TotalEntries = _cache.Count,
        ValidEntries = _cache.Count
    });

    public Task<int> EnsurePopulatedAsync(
        List<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Use the overload that accepts UniversalisService.");
    }

    public async Task<int> EnsurePopulatedAsync(
        List<(int itemId, string dataCenter)> requests,
        UniversalisService universalis,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var fetched = 0;

        foreach (var dcGroup in (await GetMissingAsync(requests)).GroupBy(request => request.dataCenter))
        {
            var dataCenter = dcGroup.Key;
            var itemIds = dcGroup.Select(request => request.itemId).Distinct().ToList();
            progress?.Report($"Fetching {itemIds.Count} items from {dataCenter}...");
            var responses = await universalis.GetMarketDataBulkAsync(dataCenter, itemIds, useParallel: true, ct);

            foreach (var (itemId, response) in responses)
            {
                _cache[(itemId, dataCenter)] = ConvertResponse(itemId, dataCenter, response);
                fetched++;
            }
        }

        return fetched;
    }

    private static CachedMarketData ConvertResponse(int itemId, string dataCenter, UniversalisResponse response)
    {
        return new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            FetchedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DCAveragePrice = (decimal)(response.AveragePriceNq > 0 ? response.AveragePriceNq : response.AveragePrice),
            HQAveragePrice = response.AveragePriceHq > 0 ? (decimal)response.AveragePriceHq : null,
            Worlds = response.Listings
                .GroupBy(listing => listing.WorldName ?? "Unknown")
                .Select(group => new CachedWorldData
                {
                    WorldName = group.Key,
                    Listings = group.Select(listing => new CachedListing
                    {
                        Quantity = listing.Quantity,
                        PricePerUnit = listing.PricePerUnit,
                        RetainerName = listing.RetainerName ?? "Unknown",
                        IsHq = listing.IsHq
                    }).ToList()
                })
                .ToList()
        };
    }
}
