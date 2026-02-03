using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using FFXIVCraftArchitect.Models;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for fetching and caching FFXIV world status from the Lodestone.
/// Scrapes the official world status page to get congestion data.
/// </summary>
public class WorldStatusService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WorldStatusService> _logger;
    private readonly string _cacheFilePath;
    private WorldStatusData? _cachedData;
    
    private const string LodestoneWorldStatusUrl = "https://na.finalfantasyxiv.com/lodestone/worldstatus/";
    private readonly TimeSpan _cacheValidity = TimeSpan.FromHours(6); // Refresh every 6 hours
    
    public WorldStatusService(ILogger<WorldStatusService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "FFXIV Craft Architect/1.0");
        
        // Store cache in app data folder
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVCraftArchitect");
        Directory.CreateDirectory(appDataPath);
        _cacheFilePath = Path.Combine(appDataPath, "world_status.json");
        
        // Load cached data on startup
        LoadCachedData();
    }
    
    /// <summary>
    /// Gets the status for a specific world.
    /// </summary>
    public WorldStatus? GetWorldStatus(string worldName)
    {
        if (_cachedData?.Worlds == null) return null;
        
        // Case-insensitive lookup
        var kvp = _cachedData.Worlds.FirstOrDefault(w => 
            w.Value.Name.Equals(worldName, StringComparison.OrdinalIgnoreCase));
        
        return kvp.Value;
    }
    
    /// <summary>
    /// Checks if a world is congested.
    /// </summary>
    public bool IsWorldCongested(string worldName)
    {
        var status = GetWorldStatus(worldName);
        return status?.IsCongested ?? false;
    }
    
    /// <summary>
    /// Gets all world statuses.
    /// </summary>
    public Dictionary<string, WorldStatus> GetAllWorldStatuses()
    {
        return _cachedData?.Worlds ?? new Dictionary<string, WorldStatus>();
    }
    
    /// <summary>
    /// Gets the last time the status was updated.
    /// </summary>
    public DateTime? LastUpdated => _cachedData?.LastUpdated;
    
    /// <summary>
    /// Checks if the cache needs refreshing.
    /// </summary>
    public bool NeedsRefresh()
    {
        if (_cachedData == null) return true;
        return DateTime.UtcNow - _cachedData.LastUpdated > _cacheValidity;
    }
    
    /// <summary>
    /// Fetches world status from the Lodestone and updates the cache.
    /// </summary>
    public async Task<bool> RefreshStatusAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("[WorldStatus] Fetching world status from Lodestone...");
            
            var html = await _httpClient.GetStringAsync(LodestoneWorldStatusUrl, ct);
            var worlds = ParseWorldStatusHtml(html);
            
            if (worlds.Count == 0)
            {
                _logger.LogWarning("[WorldStatus] No worlds found in HTML - parsing may have failed");
                return false;
            }
            
            _cachedData = new WorldStatusData
            {
                Worlds = worlds.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase),
                LastUpdated = DateTime.UtcNow,
                Source = "Lodestone"
            };
            
            SaveCachedData();
            
            var congestedCount = worlds.Count(w => w.IsCongested);
            _logger.LogInformation("[WorldStatus] Updated {Count} worlds ({Congested} congested)", 
                worlds.Count, congestedCount);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WorldStatus] Failed to refresh world status");
            return false;
        }
    }
    
    /// <summary>
    /// Parses the Lodestone HTML to extract world status information.
    /// </summary>
    private List<WorldStatus> ParseWorldStatusHtml(string html)
    {
        var worlds = new List<WorldStatus>();
        
        try
        {
            // The HTML structure is:
            // <div class="world-list__world_name"><p>WorldName</p></div>
            // <div class="world-list__world_category"><p>Congested|Standard|Preferred|Preferred+</p></div>
            // <div class="world-list__create_character">
            //   <i class="world-ic__available ..."> or <i class="world-ic__unavailable ...">
            
            // Find all world list items
            // Using a simpler approach: extract world names and their categories
            var worldItemPattern = "<li[^>]*class=\"item-list[^\"]*\"[^>]*>.*?<div class=\"world-list__world_name\">\\s*<p>([^<]+)</p>.*?<div class=\"world-list__world_category\">\\s*<p>([^<]+)</p>.*?<div class=\"world-list__create_character\">(.*?)</div>.*?</li>";
            
            var matches = Regex.Matches(html, worldItemPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            
            foreach (Match match in matches)
            {
                if (match.Groups.Count >= 4)
                {
                    var worldName = match.Groups[1].Value.Trim();
                    var categoryText = match.Groups[2].Value.Trim();
                    var characterCreationHtml = match.Groups[3].Value.Trim();
                    
                    // Parse classification
                    var classification = ParseClassification(categoryText);
                    
                    // Check if character creation is available
                    var canCreateCharacter = characterCreationHtml.Contains("world-ic__available") &&
                                            !characterCreationHtml.Contains("world-ic__unavailable");
                    
                    worlds.Add(new WorldStatus
                    {
                        Name = worldName,
                        Classification = classification,
                        CanCreateCharacter = canCreateCharacter,
                        LastUpdated = DateTime.UtcNow
                    });
                }
            }
            
            _logger.LogDebug("[WorldStatus] Parsed {Count} worlds from HTML", worlds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WorldStatus] Error parsing HTML");
        }
        
        return worlds;
    }
    
    private WorldClassification ParseClassification(string text)
    {
        return text.ToLowerInvariant() switch
        {
            "congested" => WorldClassification.Congested,
            "preferred" => WorldClassification.Preferred,
            "preferred+" => WorldClassification.PreferredPlus,
            _ => WorldClassification.Standard
        };
    }
    
    private void LoadCachedData()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                var json = File.ReadAllText(_cacheFilePath);
                _cachedData = System.Text.Json.JsonSerializer.Deserialize<WorldStatusData>(json);
                _logger.LogInformation("[WorldStatus] Loaded {Count} worlds from cache", 
                    _cachedData?.Worlds?.Count ?? 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WorldStatus] Failed to load cached data");
            _cachedData = null;
        }
    }
    
    private void SaveCachedData()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_cachedData, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_cacheFilePath, json);
            _logger.LogDebug("[WorldStatus] Saved cache to {Path}", _cacheFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WorldStatus] Failed to save cache");
        }
    }
}
