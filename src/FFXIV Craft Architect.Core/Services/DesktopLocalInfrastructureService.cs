using System.Diagnostics;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Desktop.Services;

public sealed class DesktopLocalInfrastructureService
{
    private readonly CoreStoredPlanStoreOptions _planStoreOptions;
    private readonly DesktopSettingsStore _settingsStore;
    private readonly IVendorCacheService _vendorCache;
    private readonly IMarketCacheService _marketCache;
    private readonly DesktopJsonMarketCacheService _desktopMarketCache;
    private readonly DesktopActivityLogStore _activityLogStore;
    private readonly DesktopLogStore _logStore;

    public DesktopLocalInfrastructureService(
        CoreStoredPlanStoreOptions planStoreOptions,
        DesktopSettingsStore settingsStore,
        IVendorCacheService vendorCache,
        IMarketCacheService marketCache,
        DesktopJsonMarketCacheService desktopMarketCache,
        DesktopActivityLogStore activityLogStore,
        DesktopLogStore logStore)
    {
        _planStoreOptions = planStoreOptions ?? throw new ArgumentNullException(nameof(planStoreOptions));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _vendorCache = vendorCache ?? throw new ArgumentNullException(nameof(vendorCache));
        _marketCache = marketCache ?? throw new ArgumentNullException(nameof(marketCache));
        _desktopMarketCache = desktopMarketCache ?? throw new ArgumentNullException(nameof(desktopMarketCache));
        _activityLogStore = activityLogStore ?? throw new ArgumentNullException(nameof(activityLogStore));
        _logStore = logStore ?? throw new ArgumentNullException(nameof(logStore));
    }

    public string LocalDataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIV_Craft_Architect");

    public async Task<DesktopLocalInfrastructureSnapshot> InspectAsync()
    {
        Directory.CreateDirectory(LocalDataRoot);
        Directory.CreateDirectory(_planStoreOptions.RootDirectory);
        Directory.CreateDirectory(_settingsStore.RootDirectory);
        var marketCacheStats = await _marketCache.GetStatsAsync();

        return new DesktopLocalInfrastructureSnapshot(
            LocalDataRoot,
            _planStoreOptions.RootDirectory,
            _settingsStore.SettingsPath,
            _desktopMarketCache.CachePath,
            _activityLogStore.LogPath,
            _logStore.LogPath,
            Directory.Exists(_planStoreOptions.RootDirectory),
            File.Exists(_settingsStore.SettingsPath),
            _vendorCache.Count,
            marketCacheStats.TotalEntries,
            marketCacheStats.ValidEntries,
            marketCacheStats.StaleEntries,
            marketCacheStats.OldestEntry,
            marketCacheStats.NewestEntry,
            marketCacheStats.ApproximateSizeBytes);
    }

    public void OpenLocalDataRoot()
    {
        Directory.CreateDirectory(LocalDataRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = LocalDataRoot,
            UseShellExecute = true
        });
    }

    public Task<int> CleanupStaleMarketCacheAsync(TimeSpan maxAge) =>
        _marketCache.CleanupStaleAsync(maxAge);

    public Task<int> ResetMarketCacheAsync() =>
        _desktopMarketCache.ClearAsync();
}

public sealed record DesktopLocalInfrastructureSnapshot(
    string LocalDataRoot,
    string SessionStorePath,
    string SettingsPath,
    string MarketCachePath,
    string ActivityLogPath,
    string DiagnosticLogPath,
    bool SessionStoreAvailable,
    bool SettingsFileExists,
    int VendorCacheEntryCount,
    int MarketCacheEntryCount,
    int MarketCacheValidEntryCount,
    int MarketCacheStaleEntryCount,
    DateTime? MarketCacheOldestEntryUtc,
    DateTime? MarketCacheNewestEntryUtc,
    long MarketCacheApproximateSizeBytes)
{
    public string MarketCacheAgeText =>
        MarketCacheEntryCount == 0
            ? "empty"
            : $"oldest {FormatAge(MarketCacheOldestEntryUtc)}, newest {FormatAge(MarketCacheNewestEntryUtc)}";

    public string MarketCacheHealthText =>
        MarketCacheEntryCount == 0
            ? "Market cache is empty."
            : MarketCacheStaleEntryCount == 0
                ? $"Market cache healthy: {MarketCacheValidEntryCount:N0} fresh entr{(MarketCacheValidEntryCount == 1 ? "y" : "ies")}."
                : MarketCacheValidEntryCount == 0
                    ? $"Market cache is stale: {MarketCacheStaleEntryCount:N0} stale entr{(MarketCacheStaleEntryCount == 1 ? "y" : "ies")}."
                    : $"Market cache mixed: {MarketCacheValidEntryCount:N0} fresh, {MarketCacheStaleEntryCount:N0} stale.";

    public string MarketCacheRecommendedAction =>
        MarketCacheEntryCount == 0
            ? "Refresh market evidence for the active plan to populate local cache."
            : MarketCacheStaleEntryCount > 0
                ? "Clear stale cache entries or refresh market evidence before procurement."
                : "No cache repair needed.";

    public string Summary =>
        $"Local data: {LocalDataRoot}; sessions: {(SessionStoreAvailable ? "ready" : "missing")}; settings: {(SettingsFileExists ? "saved" : "defaults")}; vendor cache: {VendorCacheEntryCount:N0} item{(VendorCacheEntryCount == 1 ? string.Empty : "s")}; market cache: {MarketCacheValidEntryCount:N0} fresh, {MarketCacheStaleEntryCount:N0} stale ({MarketCacheEntryCount:N0} total, {MarketCacheApproximateSizeBytes:N0} bytes, {MarketCacheAgeText}).";

    private static string FormatAge(DateTime? timestampUtc)
    {
        if (timestampUtc == null)
        {
            return "unknown";
        }

        var age = DateTime.UtcNow - timestampUtc.Value.ToUniversalTime();
        if (age < TimeSpan.Zero)
        {
            return "now";
        }

        return age switch
        {
            { TotalDays: >= 1 } => $"{age.TotalDays:0.#}d old",
            { TotalHours: >= 1 } => $"{age.TotalHours:0.#}h old",
            { TotalMinutes: >= 1 } => $"{age.TotalMinutes:0.#}m old",
            _ => "now"
        };
    }
}
