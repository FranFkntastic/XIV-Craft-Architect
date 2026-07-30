using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Franthropy.FFXIV.Market;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Service for interacting with the Universalis API.
/// </summary>
public class UniversalisService : IUniversalisService
{
    internal const int MaxConcurrentApiRequests = UniversalisBulkClient.DefaultMaxConcurrentRequests;

    private const string UniversalisMarketUrl = "https://universalis.app/market/{0}";
    private readonly UniversalisBulkClient bulkClient;
    private readonly ILogger<UniversalisService>? logger;
    private readonly PackagedWorldDirectoryService packagedWorldDirectory;
    private WorldData? worldDataCache;

    public UniversalisService(
        HttpClient httpClient,
        ILogger<UniversalisService>? logger = null,
        PackagedWorldDirectoryService? packagedWorldDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        bulkClient = new UniversalisBulkClient(httpClient);
        this.logger = logger;
        this.packagedWorldDirectory = packagedWorldDirectory ?? new PackagedWorldDirectoryService();
    }

    public WorldData? GetCachedWorldData() => worldDataCache;

    public void SeedWorldData(WorldData worldData)
    {
        ArgumentNullException.ThrowIfNull(worldData);
        worldDataCache = worldData;
    }

    /// <summary>
    /// Get market board listings for an item.
    /// </summary>
    [Obsolete("Use GetMarketDataBulkAsync instead. This method will be removed in a future version.")]
    public async Task<UniversalisResponse> GetMarketDataAsync(
        string worldOrDc,
        int itemId,
        bool hqOnly = false,
        int entries = 10,
        CancellationToken ct = default)
    {
        var result = await bulkClient.FetchAsync<UniversalisResponse>(
            new UniversalisBulkRequest
            {
                WorldOrDataCenter = worldOrDc,
                ItemIds = [(uint)itemId],
                ListingsPerItem = entries,
                HqOnly = hqOnly,
                UseParallelRequests = false,
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (result.Items.TryGetValue((uint)itemId, out var item))
            return item;

        throw BuildIncompleteFetchException(result.MissingItemIds, result.Failures);
    }

    /// <summary>
    /// Get market data for multiple items at once through Franthropy's shared,
    /// bounded Universalis transport.
    /// </summary>
    public async Task<Dictionary<int, UniversalisResponse>> GetMarketDataBulkAsync(
        string worldOrDc,
        IEnumerable<int> itemIds,
        bool useParallel = true,
        CancellationToken ct = default)
    {
        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
            return [];
        if (ids.Any(itemId => itemId <= 0))
            throw new ArgumentOutOfRangeException(nameof(itemIds), "Universalis item IDs must be positive.");

        logger?.LogInformation(
            "Fetching bulk market data for {Count} items from {WorldOrDc} (parallel={UseParallel})",
            ids.Length,
            worldOrDc,
            useParallel);

        var result = await bulkClient.FetchAsync<UniversalisResponse>(
            new UniversalisBulkRequest
            {
                WorldOrDataCenter = worldOrDc,
                ItemIds = ids.Select(itemId => (uint)itemId).ToArray(),
                UseParallelRequests = useParallel,
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (result.MissingItemIds.Count > 0)
        {
            logger?.LogWarning(
                "Failed to fetch {MissingCount} Universalis items: {ItemIds}",
                result.MissingItemIds.Count,
                string.Join(", ", result.MissingItemIds));
        }

        foreach (var failure in result.Failures)
        {
            logger?.LogWarning(
                "Universalis request for items {ItemIds} failed: {Message}",
                string.Join(", ", failure.ItemIds),
                failure.Message);
        }

        return result.Items.ToDictionary(
            pair => checked((int)pair.Key),
            pair => pair.Value);
    }

    public Task<WorldData> GetWorldDataAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (worldDataCache != null)
            return Task.FromResult(worldDataCache);

        logger?.LogDebug("Loading packaged world directory");
        worldDataCache = packagedWorldDirectory.LoadWorldData();
        return Task.FromResult(worldDataCache);
    }

    public static string GetMarketUrl(int itemId) =>
        string.Format(UniversalisMarketUrl, itemId);

    public ShoppingPlan CalculateShoppingPlan(
        string itemName,
        int itemId,
        int quantityNeeded,
        List<MarketListing> listings)
    {
        var plan = new ShoppingPlan
        {
            ItemId = itemId,
            Name = itemName,
            QuantityNeeded = quantityNeeded,
        };

        var remaining = quantityNeeded;
        long totalCost = 0;
        foreach (var listing in listings.OrderBy(listing => listing.PricePerUnit))
        {
            if (remaining <= 0)
                break;

            var toBuy = Math.Min(listing.Quantity, remaining);
            totalCost += toBuy * listing.PricePerUnit;
            remaining -= toBuy;
            plan.Entries.Add(new ShoppingPlanEntry
            {
                Quantity = toBuy,
                PricePerUnit = listing.PricePerUnit,
                WorldName = listing.WorldName,
                RetainerName = listing.RetainerName,
            });
        }

        plan.TotalCost = totalCost;
        return plan;
    }

    private static HttpRequestException BuildIncompleteFetchException(
        IReadOnlyList<uint> missingItemIds,
        IReadOnlyList<UniversalisBulkFailure> failures)
    {
        var detail = failures.FirstOrDefault()?.Message;
        var message = $"Universalis did not return item(s) {string.Join(", ", missingItemIds)}.";
        if (!string.IsNullOrWhiteSpace(detail))
            message += $" {detail}";
        return new HttpRequestException(message);
    }
}
