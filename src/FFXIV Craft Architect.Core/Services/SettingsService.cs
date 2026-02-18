using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Service for managing application settings.
/// Ported from Python: settings_manager.py
/// </summary>
public class SettingsService : ISettingsService
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
            ["home_world"] = "",
            ["include_cross_world"] = true,
            ["auto_fetch_prices"] = true,
            ["exclude_congested_worlds"] = true,
            ["exclude_blacklisted_worlds"] = true,
            ["parallel_api_requests"] = true,
            ["cache_ttl_hours"] = 3.0,
            ["warm_cache_for_crafted_items"] = false
        },
        ["ui"] = new Dictionary<string, object>
        {
            ["accent_color"] = "#d4af37",
            ["use_split_pane_market_view"] = true
        },
        ["planning"] = new Dictionary<string, object>
        {
            ["default_recommendation_mode"] = "MinimizeTotalCost"
        },
        ["debug"] = new Dictionary<string, object>
        {
            ["enable_diagnostic_logging"] = false,
            ["log_level"] = 1
        }
    };

    /// <summary>
    /// Gets the full path to the settings file.
    /// </summary>
    public string SettingsFilePath => _settingsPath;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        
        // Get the LocalApplicationData folder (e.g., C:\Users\User\AppData\Local)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _logger.LogInformation("[SettingsService] LocalApplicationData = '{LocalAppData}'", localAppData);
        
        var appDataDir = Path.Combine(localAppData, "FFXIV_Craft_Architect");
        _logger.LogInformation("[SettingsService] AppDataDir = '{AppDataDir}'", appDataDir);
        
        Directory.CreateDirectory(appDataDir);
        _settingsPath = Path.Combine(appDataDir, "settings.json");
        
        _logger.LogInformation("[SettingsService] Settings file path: {Path}", _settingsPath);
        _logger.LogInformation("[SettingsService] Current working directory: {CWD}", Environment.CurrentDirectory);
        _logger.LogInformation("[SettingsService] Executable directory: {ExeDir}", Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location));
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        _settings = LoadSettings();
        
        _logger.LogInformation("Loaded settings - market.default_datacenter: {DC}, market.home_world: {Home}",
            Get<string>("market.default_datacenter", "NOT_SET"),
            Get<string>("market.home_world", "NOT_SET"));
    }

    /// <summary>
    /// Get a setting value by dot-separated path.
    /// </summary>
    public virtual T? Get<T>(string keyPath, T? defaultValue = default)
    {
        try
        {
            var keys = keyPath.Split('.');
            object? current = _settings;

            foreach (var key in keys)
            {
                if (current is not Dictionary<string, object> dict)
                {
                    _logger.LogWarning("[Get] Key '{Key}' not found in dictionary - returning default for '{KeyPath}'", key, keyPath);
                    return defaultValue;
                }
                
                if (!dict.TryGetValue(key, out var value))
                {
                    _logger.LogWarning("[Get] Key '{Key}' not found - returning default '{Default}' for '{KeyPath}'", 
                        key, defaultValue, keyPath);
                    return defaultValue;
                }
                
                current = value;
            }

            T? result;
            if (current is JsonElement jsonElement)
            {
                result = ConvertJsonElement<T>(jsonElement, defaultValue);
            }
            else if (current is T typedValue)
            {
                result = typedValue;
            }
            else
            {
                var json = JsonSerializer.Serialize(current);
                result = JsonSerializer.Deserialize<T>(json);
            }
            
            _logger.LogDebug("[Get] '{KeyPath}' = '{Result}'", keyPath, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Get] EXCEPTION for '{KeyPath}': {Message}", keyPath, ex.Message);
            return defaultValue;
        }
    }

    private static T? ConvertJsonElement<T>(JsonElement element, T? defaultValue = default)
    {
        try
        {
            var targetType = typeof(T);
            
            if (Nullable.GetUnderlyingType(targetType) != null)
            {
                targetType = Nullable.GetUnderlyingType(targetType)!;
            }

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

        var oldValue = current.TryGetValue(keys[^1], out var existing) ? existing : null;
        if (value is null)
        {
            _logger.LogWarning("Attempted to set null value for key '{Key}', skipping", keyPath);
            return;
        }
        current[keys[^1]] = value;

        _logger.LogDebug("Setting {Key}: {OldValue} -> {NewValue}", keyPath, oldValue, value);
        
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
    
    // ISettingsService async implementations (wrapper around sync methods)
    
    public Task<T?> GetAsync<T>(string keyPath, T? defaultValue = default)
    {
        return Task.FromResult(Get(keyPath, defaultValue));
    }
    
    public Task SetAsync<T>(string keyPath, T value)
    {
        Set(keyPath, value);
        return Task.CompletedTask;
    }
    
    public Task ResetToDefaultsAsync()
    {
        ResetToDefaults();
        return Task.CompletedTask;
    }

    private Dictionary<string, object> LoadSettings()
    {
        _logger.LogInformation("[LoadSettings] START - Looking for settings at: {Path}", _settingsPath);
        
        try
        {
            if (!File.Exists(_settingsPath))
            {
                _logger.LogWarning("[LoadSettings] Settings file NOT FOUND at: {Path}", _settingsPath);
                _logger.LogInformation("[LoadSettings] Creating new settings file with defaults");
                var defaults = DeepClone(DefaultSettings);
                SaveSettings(defaults);
                return defaults;
            }

            var fileInfo = new FileInfo(_settingsPath);
            _logger.LogInformation("[LoadSettings] Settings file FOUND: {Path} ({Bytes} bytes, LastModified: {Modified})", 
                _settingsPath, fileInfo.Length, fileInfo.LastWriteTime);

            var json = File.ReadAllText(_settingsPath);
            _logger.LogDebug("[LoadSettings] Raw JSON content:\n{Json}", json);
            
            var loaded = JsonSerializer.Deserialize<Dictionary<string, object>>(json, _jsonOptions);
            
            if (loaded == null)
            {
                _logger.LogError("[LoadSettings] Deserialization returned NULL, using defaults");
                return DeepClone(DefaultSettings);
            }

            _logger.LogInformation("[LoadSettings] Deserialized {Count} top-level keys: {Keys}", 
                loaded.Count, string.Join(", ", loaded.Keys));
            
            var merged = MergeWithDefaults(loaded);
            
            _logger.LogInformation("[LoadSettings] After merge - Settings contains:");
            foreach (var key in merged.Keys)
            {
                var value = merged[key];
                if (value is Dictionary<string, object> dict)
                {
                    _logger.LogInformation("[LoadSettings]   [{Key}]: {NestedKeys}", key, string.Join(", ", dict.Keys.Select(k => $"{k}={dict[k]}")));
                }
                else
                {
                    _logger.LogInformation("[LoadSettings]   [{Key}]: {Value}", key, value);
                }
            }
            
            return merged;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LoadSettings] EXCEPTION - Failed to load settings: {Message}", ex.Message);
            return DeepClone(DefaultSettings);
        }
    }

    private void SaveSettings(Dictionary<string, object>? settings = null)
    {
        settings ??= _settings;
        
        try
        {
            _logger.LogInformation("[SaveSettings] Saving settings to: {Path}", _settingsPath);
            
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            _logger.LogDebug("[SaveSettings] JSON to save:\n{Json}", json);
            
            File.WriteAllText(_settingsPath, json);
            
            var fileInfo = new FileInfo(_settingsPath);
            if (fileInfo.Exists)
            {
                _logger.LogInformation("[SaveSettings] SUCCESS - Saved {Bytes} bytes to {Path}", 
                    fileInfo.Length, _settingsPath);
            }
            else
            {
                _logger.LogError("[SaveSettings] FAILED - File does not exist after write: {Path}", _settingsPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SaveSettings] EXCEPTION - Failed to save to {Path}: {Message}", _settingsPath, ex.Message);
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
            Dictionary<string, object>? loadedDict = null;
            
            if (kvp.Value is Dictionary<string, object> dict)
            {
                loadedDict = dict;
            }
            else if (kvp.Value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Object)
            {
                loadedDict = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText());
            }
            
            // Get or create the target dictionary in merged
            if (!merged.TryGetValue(kvp.Key, out var targetValue))
            {
                // Key doesn't exist in defaults, add it
                merged[kvp.Key] = kvp.Value;
                continue;
            }
            
            // Convert target to Dictionary<string, object> if needed
            Dictionary<string, object>? targetDict = null;
            if (targetValue is Dictionary<string, object> targetDictObj)
            {
                targetDict = targetDictObj;
            }
            else if (targetValue is JsonElement targetJson && targetJson.ValueKind == JsonValueKind.Object)
            {
                targetDict = JsonSerializer.Deserialize<Dictionary<string, object>>(targetJson.GetRawText());
                merged[kvp.Key] = targetDict; // Replace with proper dictionary
            }
            
            if (loadedDict != null && targetDict != null)
            {
                // Merge loaded values into target
                foreach (var nestedKvp in loadedDict)
                {
                    targetDict[nestedKvp.Key] = nestedKvp.Value;
                }
            }
            else
            {
                // Can't merge, just replace
                merged[kvp.Key] = kvp.Value;
            }
        }
        
        return merged;
    }
}
