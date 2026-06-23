using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.Tests;

[Collection(DesktopTestCollection.Name)]
public sealed class DesktopLocalInfrastructureServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"craft-architect-desktop-{Guid.NewGuid():N}");

    [Fact]
    public async Task InspectAsync_CreatesDesktopDirectoriesAndReportsCacheCounts()
    {
        var sessionRoot = Path.Combine(_root, "sessions");
        var settingsRoot = Path.Combine(_root, "settings");
        var marketCacheRoot = Path.Combine(_root, "market");
        var settingsStore = new DesktopSettingsStore(settingsRoot);
        var vendorCache = new RecordingVendorCacheService();
        vendorCache.Set(42, new VendorCacheEntry(42, [], 0, DateTime.UtcNow));
        var marketCache = new DesktopJsonMarketCacheService(
            new UniversalisService(new HttpClient()),
            marketCacheRoot);
        var activityLogStore = new DesktopActivityLogStore(Path.Combine(_root, "logs"));
        var diagnosticLogStore = new DesktopLogStore(Path.Combine(_root, "logs"));
        await marketCache.SetAsync(100, "Aether", new CachedMarketData
        {
            ItemId = 100,
            DataCenter = "Aether",
            FetchedAt = DateTime.UtcNow,
            DCAveragePrice = 12
        });
        var service = new DesktopLocalInfrastructureService(
            new CoreStoredPlanStoreOptions(sessionRoot),
            settingsStore,
            vendorCache,
            marketCache,
            marketCache,
            activityLogStore,
            diagnosticLogStore);

        var snapshot = await service.InspectAsync();

        Assert.True(Directory.Exists(sessionRoot));
        Assert.True(Directory.Exists(settingsRoot));
        Assert.True(snapshot.SessionStoreAvailable);
        Assert.False(snapshot.SettingsFileExists);
        Assert.Equal(1, snapshot.VendorCacheEntryCount);
        Assert.Equal(1, snapshot.MarketCacheEntryCount);
        Assert.Equal(1, snapshot.MarketCacheValidEntryCount);
        Assert.Equal(Path.Combine(marketCacheRoot, "desktop-market-cache.json"), snapshot.MarketCachePath);
        Assert.Equal(Path.Combine(_root, "logs", "desktop-activity.jsonl"), snapshot.ActivityLogPath);
        Assert.Equal(diagnosticLogStore.LogPath, snapshot.DiagnosticLogPath);
        Assert.Contains("vendor cache: 1 item", snapshot.Summary);
        Assert.Contains("market cache: 1 fresh", snapshot.Summary);
        Assert.Contains("Market cache healthy", snapshot.MarketCacheHealthText);
        Assert.Equal("No cache repair needed.", snapshot.MarketCacheRecommendedAction);
        Assert.NotEqual("empty", snapshot.MarketCacheAgeText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingVendorCacheService : IVendorCacheService
    {
        private readonly Dictionary<int, VendorCacheEntry> _entries = new();

        public int Count => _entries.Count;

        public VendorCacheEntry? Get(int itemId) =>
            _entries.TryGetValue(itemId, out var entry) ? entry : null;

        public Task<VendorCacheEntry?> GetOrFetchAsync(int itemId, CancellationToken ct = default) =>
            Task.FromResult(Get(itemId));

        public Task<Dictionary<int, VendorCacheEntry>> GetOrFetchBatchAsync(IEnumerable<int> itemIds, CancellationToken ct = default) =>
            Task.FromResult(itemIds.Where(_entries.ContainsKey).ToDictionary(itemId => itemId, itemId => _entries[itemId]));

        public void Set(int itemId, VendorCacheEntry entry) =>
            _entries[itemId] = entry;

        public void Clear() =>
            _entries.Clear();

        public Task SaveAsync(CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task LoadAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
