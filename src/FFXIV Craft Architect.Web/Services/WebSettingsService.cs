using System.Text.Json;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.BrowserPersistence;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Web implementation of ISettingsService using IndexedDB for persistence.
/// </summary>
public class WebSettingsService : ISettingsService
{
    private const string RegionalProcurementDefaultMigrationKey = "migration.regional_procurement_default";
    private readonly IndexedDbService _indexedDb;
    private readonly ILogger<WebSettingsService>? _logger;
    private readonly Dictionary<string, object> _cache = new();
    private readonly object _loadSync = new();
    private Task? _loadTask;
    private bool _isLoaded = false;

    private static readonly Dictionary<string, object> DefaultSettings = new()
    {
        ["market.default_datacenter"] = "Aether",
        ["market.region"] = "North America",
        ["market.comparison_region"] = "",
        ["market.home_world"] = "",
        ["market.default_search_scope"] = "EntireRegion",
        ["market.include_cross_world"] = true,
        ["market.exclude_congested_worlds"] = true,
        ["procurement.search_entire_region"] = true,
        ["procurement.region"] = "North America",
        ["procurement.enable_split_world_purchases"] = true,
        ["procurement.travel_tolerance"] = 0,
        ["procurement.world_exclusion_duration_minutes"] = 60,
        ["procurement.start_from_home_data_center"] = false,
        ["procurement.travel_priority"] = "DataCenterTransfersFirst",
        ["marketmafioso.workshop_host_url"] = "",
        ["marketmafioso.api_key"] = "",
        ["marketmafioso.target_character"] = "",
        ["marketmafioso.target_world"] = "",
        ["marketmafioso.auto_sync_evidence"] = true,
        ["marketmafioso.pending_submission"] = MarketMafiosoPendingSubmission.Empty,
        ["marketmafioso.active_handoff"] = new MarketMafiosoActiveHandoffState(),
        ["marketmafioso.active_request_id"] = "",
        ["marketmafioso.active_item_id"] = 0,
        ["marketmafioso.active_data_center"] = "",
        ["marketmafioso.active_purchase_world"] = "",
        ["ui.accent_color"] = "#d4af37",
        ["ui.use_split_pane_market_view"] = true,
        ["planning.default_recommendation_mode"] = "MinimizeTotalCost",
        ["debug.enable_diagnostic_logging"] = false,
        ["debug.secret_tools_enabled"] = false
    };

    public WebSettingsService(
        IndexedDbService indexedDb,
        ILogger<WebSettingsService>? logger = null)
    {
        _indexedDb = indexedDb;
        _logger = logger;
    }

    public event Action? PortableSettingsApplied;

    private Task EnsureLoadedAsync()
    {
        if (_isLoaded)
        {
            return Task.CompletedTask;
        }

        lock (_loadSync)
        {
            return _loadTask ??= LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            await _indexedDb.EnsureSpecializedStorageAsync();
            await ApplyMigrationsAsync();

            var storedSettings = await _indexedDb.LoadAllSettingsAsync();
            foreach (var (key, defaultValue) in DefaultSettings)
            {
                if (storedSettings.TryGetValue(key, out var serialized) &&
                    !string.IsNullOrWhiteSpace(serialized))
                {
                    _cache[key] = JsonSerializer.Deserialize<object>(serialized)
                        ?? defaultValue;
                }
                else
                {
                    _cache[key] = defaultValue;
                }
            }
            _isLoaded = true;
            _logger?.LogInformation("[WebSettingsService] Loaded {Count} settings from IndexedDB", _cache.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[WebSettingsService] Failed to load settings");
            throw new InvalidOperationException(
                "Browser settings storage is unavailable or uses an incompatible schema.",
                ex);
        }
    }

    private async Task ApplyMigrationsAsync()
    {
        if (await _indexedDb.LoadSettingAsync(RegionalProcurementDefaultMigrationKey, false))
        {
            return;
        }

        if (await _indexedDb.SaveSettingAsync("procurement.search_entire_region", true))
        {
            await _indexedDb.SaveSettingAsync(RegionalProcurementDefaultMigrationKey, true);
        }
    }

    public T? Get<T>(string keyPath, T? defaultValue = default)
    {
        // Sync wrapper - uses cached value
        if (_cache.TryGetValue(keyPath, out var value))
        {
            return ConvertValue<T>(value, defaultValue);
        }
        return defaultValue;
    }

    public void Set<T>(string keyPath, T value)
    {
        // Sync wrapper - updates cache, fire-and-forget save
        _cache[keyPath] = value!;
        _ = SaveToIndexedDb(keyPath, value, throwOnFailure: false);
    }

    public async Task<T?> GetAsync<T>(string keyPath, T? defaultValue = default)
    {
        await EnsureLoadedAsync();

        if (_cache.TryGetValue(keyPath, out var value))
        {
            return ConvertValue<T>(value, defaultValue);
        }
        return defaultValue;
    }

    public async Task SetAsync<T>(string keyPath, T value)
    {
        await EnsureLoadedAsync();
        var hadPrevious = _cache.TryGetValue(keyPath, out var previous);
        _cache[keyPath] = value!;
        try
        {
            await SaveToIndexedDb(keyPath, value, throwOnFailure: true);
        }
        catch
        {
            if (hadPrevious)
            {
                _cache[keyPath] = previous!;
            }
            else
            {
                _cache.Remove(keyPath);
            }
            throw;
        }
    }

    public async Task ResetToDefaultsAsync()
    {
        _cache.Clear();
        foreach (var kvp in DefaultSettings)
        {
            _cache[kvp.Key] = kvp.Value;
            await SaveToIndexedDb(kvp.Key, kvp.Value, throwOnFailure: true);
        }
        _logger?.LogInformation("[WebSettingsService] Reset all settings to defaults");
    }

    public async Task ApplyPortableSettingsAsync(
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureLoadedAsync();
        var invalidKey = settings.Keys.FirstOrDefault(
            key => !PortableOperatorSettingKeys.IsPortable(key));
        if (invalidKey != null)
        {
            throw new InvalidOperationException(
                $"Browser-local setting '{invalidKey}' cannot be hydrated from company storage.");
        }

        if (!await _indexedDb.SaveSettingsBatchAsync(
                settings.ToDictionary(pair => pair.Key, pair => pair.Value)))
        {
            throw new InvalidOperationException(
                "The browser could not persist canonical operator settings.");
        }

        foreach (var key in PortableOperatorSettingKeys.All)
        {
            if (settings.ContainsKey(key))
            {
                continue;
            }

            if (DefaultSettings.TryGetValue(key, out var defaultValue))
            {
                _cache[key] = defaultValue;
            }
            else
            {
                _cache.Remove(key);
            }
        }
        foreach (var (key, serialized) in settings)
        {
            _cache[key] = JsonSerializer.Deserialize<object>(serialized)
                ?? throw new InvalidOperationException(
                    $"Portable setting '{key}' contains an empty value.");
        }
        PortableSettingsApplied?.Invoke();
    }

    public async Task ApplyHostedSettingAsync(
        string key,
        string serialized,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureLoadedAsync();
        if (!ProfileSyncLocalStateService.IsSyncedSetting(key))
        {
            throw new InvalidOperationException(
                $"Browser-local setting '{key}' cannot be hydrated from hosted profile sync.");
        }

        if (!await _indexedDb.SaveSettingsBatchAsync(new Dictionary<string, string>
            {
                [key] = serialized
            }))
        {
            throw new InvalidOperationException(
                $"The browser could not persist hosted setting '{key}'.");
        }

        if (string.Equals(serialized, "null", StringComparison.OrdinalIgnoreCase))
        {
            if (DefaultSettings.TryGetValue(key, out var defaultValue))
            {
                _cache[key] = defaultValue;
            }
            else
            {
                _cache.Remove(key);
            }
        }
        else
        {
            _cache[key] = JsonSerializer.Deserialize<object>(serialized)
                ?? throw new InvalidOperationException(
                    $"Hosted setting '{key}' contains an empty value.");
        }

        PortableSettingsApplied?.Invoke();
    }

    private async Task SaveToIndexedDb<T>(string key, T value, bool throwOnFailure)
    {
        try
        {
            if (!await _indexedDb.SaveSettingAsync(key, value))
            {
                throw new InvalidOperationException(
                    $"Browser settings storage rejected '{key}'.");
            }

            _logger?.LogDebug("[WebSettingsService] Saved setting '{Key}'", key);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[WebSettingsService] Failed to save setting '{Key}'", key);
            if (throwOnFailure)
            {
                throw;
            }
        }
    }

    private static T? ConvertValue<T>(object value, T? defaultValue)
    {
        if (value is T typedValue)
        {
            return typedValue;
        }

        if (value is JsonElement jsonElement)
        {
            try
            {
                var deserialized = jsonElement.Deserialize<T>();
                if (deserialized is not null)
                {
                    return deserialized;
                }
            }
            catch (JsonException)
            {
                // Fall through to the primitive compatibility conversions below.
            }

            var targetType = typeof(T);
            var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (underlyingType == typeof(string))
            {
                return (T?)(object?)jsonElement.GetString();
            }
            if (underlyingType == typeof(bool))
            {
                return (T?)(object?)(jsonElement.ValueKind == JsonValueKind.True);
            }
            if (underlyingType == typeof(int))
            {
                return (T?)(object?)jsonElement.GetInt32();
            }
            if (underlyingType == typeof(double))
            {
                return (T?)(object?)jsonElement.GetDouble();
            }
        }

        try
        {
            return (T?)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }
}
