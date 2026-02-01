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
            ["include_cross_world"] = true
        }
    };

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        _settingsPath = Path.Combine(
            AppContext.BaseDirectory,
            "settings.json"
        );
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        _settings = LoadSettings();
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
        current[keys[^1]] = value!;
        
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
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
            if (kvp.Value is Dictionary<string, object> loadedDict &&
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
