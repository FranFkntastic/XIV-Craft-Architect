using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for managing application settings.
/// Ported from Python: settings_manager.py
/// </summary>
public class SettingsService
{
    private readonly string _settingsPath;
    private readonly Dictionary<string, object> _settings;
    private readonly ILogger<SettingsService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    // Default settings matching Python edition
    private static readonly Dictionary<string, object> DefaultSettings = new()
    {
        ["application"] = new Dictionary<string, object>
        {
            ["version"] = "0.1.0",
            ["auto_check_updates"] = false
        },
        ["live_mode"] = new Dictionary<string, object>
        {
            ["auto_elevate"] = true,
            ["show_admin_warning"] = true
        },
        ["inventory"] = new Dictionary<string, object>
        {
            ["show_icons"] = true,
            ["group_by_container"] = true
        },
        ["market"] = new Dictionary<string, object>
        {
            ["default_datacenter"] = "Aether",
            ["home_world"] = "",  // User's home world - persists across sessions
            ["include_cross_world"] = true,
            ["auto_fetch_prices"] = true,
            ["exclude_congested_worlds"] = true,  // Skip congested worlds (except home) to save API calls
            ["exclude_blacklisted_worlds"] = true,  // Skip user-blacklisted worlds (except home)
            ["parallel_api_requests"] = true  // Enable parallel API requests for faster price fetching
        },
        ["ui"] = new Dictionary<string, object>
        {
            ["accent_color"] = "#d4af37",  // Gold default
            ["use_split_pane_market_view"] = true  // Modern split-pane layout (false = legacy view)
        },
        ["planning"] = new Dictionary<string, object>
        {
            ["default_recommendation_mode"] = "MinimizeTotalCost"  // or "MaximizeValue"
        },
        ["debug"] = new Dictionary<string, object>
        {
            ["enable_diagnostic_logging"] = false,
            ["log_level"] = 1  // Info = 1
        }
    };

    /// <summary>
    /// Gets the full path to the settings file.
    /// </summary>
    public string SettingsFilePath => _settingsPath;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        // Use LocalApplicationData for settings to persist across app updates
        // and avoid issues with AppContext.BaseDirectory changing between dev/publish
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVCraftArchitect"
        );
        Directory.CreateDirectory(appDataDir);
        _settingsPath = Path.Combine(appDataDir, "settings.json");
        
        _logger.LogInformation("Settings file path: {Path}", _settingsPath);
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        _settings = LoadSettings();
        
        // Log loaded settings for debugging
        _logger.LogInformation("Loaded settings - market.default_datacenter: {DC}, market.home_world: {Home}, market.exclude_congested_worlds: {Exclude}",
            Get<string>("market.default_datacenter", "NOT_SET"),
            Get<string>("market.home_world", "NOT_SET"),
            Get<bool>("market.exclude_congested_worlds", false));
    }

    /// <summary>
    /// Get a setting value by dot-separated path.
    /// </summary>
    public T? Get<T>(string keyPath, T? defaultValue = default)
    {
        try
        {
            var keys = keyPath.Split('.');
            object? current = _settings;

            foreach (var key in keys)
            {
                if (current is not Dictionary<string, object> dict)
                    return defaultValue;
                
                if (!dict.TryGetValue(key, out var value))
                    return defaultValue;
                
                current = value;
            }

            // Handle JsonElement (values loaded from JSON are stored as JsonElement)
            if (current is JsonElement jsonElement)
            {
                return ConvertJsonElement<T>(jsonElement, defaultValue);
            }

            if (current is T typedValue)
                return typedValue;

            // Try JSON conversion for nested objects
            var json = JsonSerializer.Serialize(current);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get setting: {KeyPath}", keyPath);
            return defaultValue;
        }
    }

    /// <summary>
    /// Converts a JsonElement to the target type T.
    /// </summary>
    private static T? ConvertJsonElement<T>(JsonElement element, T? defaultValue = default)
    {
        try
        {
            var targetType = typeof(T);
            
            // Handle nullable types
            if (Nullable.GetUnderlyingType(targetType) != null)
            {
                targetType = Nullable.GetUnderlyingType(targetType)!;
            }

            // Handle common primitive types
            if (targetType == typeof(string))
            {
                if (element.ValueKind == JsonValueKind.String)
                    return (T)(object)element.GetString()!;
                return (T)(object)element.ToString()!;
            }
            
            if (targetType == typeof(bool))
            {
                if (element.ValueKind == JsonValueKind.True)
                    return (T)(object)true;
                if (element.ValueKind == JsonValueKind.False)
                    return (T)(object)false;
                return defaultValue;
            }
            
            if (targetType == typeof(int) || targetType == typeof(long))
            {
                if (element.TryGetInt64(out var longVal))
                    return (T)Convert.ChangeType(longVal, targetType);
                return defaultValue;
            }
            
            if (targetType == typeof(double))
            {
                if (element.TryGetDouble(out var doubleVal))
                    return (T)(object)doubleVal;
                return defaultValue;
            }

            // For other types (enums, complex objects), use JSON deserialization
            var json = element.GetRawText();
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception)
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Set a setting value by dot-separated path.
    /// </summary>
    public void Set<T>(string keyPath, T value)
    {
        var keys = keyPath.Split('.');
        var current = _settings;

        // Navigate to the parent of the target key
        for (int i = 0; i < keys.Length - 1; i++)
        {
            if (!current.TryGetValue(keys[i], out var child))
            {
                child = new Dictionary<string, object>();
                current[keys[i]] = child;
            }
            
            if (child is not Dictionary<string, object> childDict)
            {
                childDict = new Dictionary<string, object>();
                current[keys[i]] = childDict;
            }
            
            current = childDict;
        }

        // Set the value
        var oldValue = current.TryGetValue(keys[^1], out var existing) ? existing : null;
        current[keys[^1]] = value!;
        
        _logger.LogDebug("Setting {Key}: {OldValue} -> {NewValue}", keyPath, oldValue, value);
        
        // Save to disk
        SaveSettings();
    }

    /// <summary>
    /// Reset all settings to defaults.
    /// </summary>
    public void ResetToDefaults()
    {
        _settings.Clear();
        foreach (var kvp in DefaultSettings)
        {
            _settings[kvp.Key] = kvp.Value;
        }
        SaveSettings();
        _logger.LogInformation("Settings reset to defaults");
    }

    private Dictionary<string, object> LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                _logger.LogInformation("Settings file not found, creating with defaults");
                var defaults = DeepClone(DefaultSettings);
                SaveSettings(defaults);
                return defaults;
            }

            var json = File.ReadAllText(_settingsPath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, object>>(json, _jsonOptions);
            
            if (loaded == null)
            {
                _logger.LogWarning("Failed to parse settings, using defaults");
                return DeepClone(DefaultSettings);
            }

            // Merge with defaults to ensure all keys exist
            return MergeWithDefaults(loaded);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
            return DeepClone(DefaultSettings);
        }
    }

    private void SaveSettings(Dictionary<string, object>? settings = null)
    {
        settings ??= _settings;
        
        try
        {
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            File.WriteAllText(_settingsPath, json);
            
            // Verify file was written
            var fileInfo = new FileInfo(_settingsPath);
            if (fileInfo.Exists)
            {
                _logger.LogDebug("Settings saved to {Path} ({Bytes} bytes)", _settingsPath, fileInfo.Length);
            }
            else
            {
                _logger.LogWarning("Settings file not found after save: {Path}", _settingsPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", _settingsPath);
        }
    }

    private static Dictionary<string, object> DeepClone(Dictionary<string, object> source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
    }

    private static Dictionary<string, object> MergeWithDefaults(Dictionary<string, object> loaded)
    {
        var merged = DeepClone(DefaultSettings);
        
        foreach (var kvp in loaded)
        {
            // Handle JsonElement by converting to proper dictionary
            Dictionary<string, object>? loadedDict = null;
            
            if (kvp.Value is Dictionary<string, object> dict)
            {
                loadedDict = dict;
            }
            else if (kvp.Value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Object)
            {
                // Convert JsonElement to dictionary
                loadedDict = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText());
            }
            
            if (loadedDict != null &&
                merged.TryGetValue(kvp.Key, out var defaultValue) &&
                defaultValue is Dictionary<string, object> defaultDict)
            {
                // Deep merge nested dictionaries
                foreach (var nestedKvp in loadedDict)
                {
                    defaultDict[nestedKvp.Key] = nestedKvp.Value;
                }
            }
            else
            {
                merged[kvp.Key] = kvp.Value;
            }
        }
        
        return merged;
    }
}
