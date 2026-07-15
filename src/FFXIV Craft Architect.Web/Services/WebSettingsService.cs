using System.Text.Json;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Web implementation of ISettingsService using IndexedDB for persistence.
/// </summary>
public class WebSettingsService : ISettingsService
{
    private readonly IndexedDbService _indexedDb;
    private readonly ILogger<WebSettingsService>? _logger;
    private readonly Dictionary<string, object> _cache = new();
    private bool _isLoaded = false;

    private static readonly Dictionary<string, object> DefaultSettings = new()
    {
        ["market.default_datacenter"] = "Aether",
        ["market.region"] = "North America",
        ["market.home_world"] = "",
        ["market.default_search_scope"] = "EntireRegion",
        ["market.include_cross_world"] = true,
        ["market.exclude_congested_worlds"] = true,
        ["procurement.search_entire_region"] = false,
        ["procurement.enable_split_world_purchases"] = true,
        ["procurement.travel_tolerance"] = 0,
        ["procurement.world_exclusion_duration_minutes"] = 60,
        ["procurement.start_from_home_data_center"] = false,
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
        ["ui.auto_save_enabled"] = true,
        ["ui.accent_color"] = "#d4af37",
        ["ui.use_split_pane_market_view"] = true,
        ["planning.default_recommendation_mode"] = "MinimizeTotalCost",
        ["debug.enable_diagnostic_logging"] = false,
        ["debug.secret_tools_enabled"] = false
    };

    public WebSettingsService(IndexedDbService indexedDb, ILogger<WebSettingsService>? logger = null)
    {
        _indexedDb = indexedDb;
        _logger = logger;
    }

    private async Task EnsureLoadedAsync()
    {
        if (_isLoaded)
        {
            return;
        }

        try
        {
            // Load all settings from IndexedDB
            foreach (var key in DefaultSettings.Keys)
            {
                var value = await _indexedDb.LoadSettingAsync<object>(key);
                if (value != null)
                {
                    _cache[key] = value;
                }
                else
                {
                    _cache[key] = DefaultSettings[key];
                }
            }
            _isLoaded = true;
            _logger?.LogInformation("[WebSettingsService] Loaded {Count} settings from IndexedDB", _cache.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[WebSettingsService] Failed to load settings");
            // Fall back to defaults
            foreach (var kvp in DefaultSettings)
            {
                _cache[kvp.Key] = kvp.Value;
            }
            _isLoaded = true;
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
                _cache[keyPath] = previous!;
            else
                _cache.Remove(keyPath);
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

    private async Task SaveToIndexedDb<T>(string key, T value, bool throwOnFailure)
    {
        try
        {
            await _indexedDb.SaveSettingAsync(key, value);
            _logger?.LogDebug("[WebSettingsService] Saved setting '{Key}'", key);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[WebSettingsService] Failed to save setting '{Key}'", key);
            if (throwOnFailure)
                throw;
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
                    return deserialized;
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
