using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Service for fetching and caching FFXIV world status from the Lodestone.
/// Scrapes the official world status page to get congestion data.
/// </summary>
public class WorldStatusService : IWorldStatusService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WorldStatusService> _logger;
    private readonly TimeSpan _cacheValidity = TimeSpan.FromHours(6);
    private readonly bool _ownsHttpClient;
    
    protected readonly string? CacheFilePath;
    protected WorldStatusData? CachedData;
    
    private const string LodestoneWorldStatusUrl = "https://na.finalfantasyxiv.com/lodestone/worldstatus/";

    /// <summary>
    /// Creates a new WorldStatusService with an HttpClient (owned by this service).
    /// </summary>
    public WorldStatusService(
        HttpClient httpClient, 
        ILogger<WorldStatusService> logger,
        string? cacheFilePath = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _ownsHttpClient = true;
        CacheFilePath = cacheFilePath ?? GetDefaultCachePath();
        
        InitializeCache();
    }
    
    /// <summary>
    /// Creates a new WorldStatusService using IHttpClientFactory.
    /// </summary>
    public WorldStatusService(
        IHttpClientFactory httpClientFactory,
        ILogger<WorldStatusService> logger,
        string? cacheFilePath = null,
        string httpClientName = "WorldStatus")
    {
        _httpClient = httpClientFactory.CreateClient(httpClientName);
        _logger = logger;
        _ownsHttpClient = false;
        CacheFilePath = cacheFilePath ?? GetDefaultCachePath();
        
        InitializeCache();
    }
    
    private void InitializeCache()
    {
        if (!string.IsNullOrEmpty(CacheFilePath))
        {
            var dir = Path.GetDirectoryName(CacheFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            LoadCachedData();
        }
    }
    
    /// <summary>
    /// Gets the default cache file path. Can be overridden for different platforms.
    /// </summary>
    protected virtual string GetDefaultCachePath()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appDataPath, "FFXIV_Craft_Architect");
        return Path.Combine(dir, "world_status.json");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public WorldStatus? GetWorldStatus(string worldName)
    {
        if (CachedData?.Worlds == null) return null;
        
        var kvp = CachedData.Worlds.FirstOrDefault(w => 
            w.Value.Name.Equals(worldName, StringComparison.OrdinalIgnoreCase));
        
        return kvp.Value;
    }

    public bool IsWorldCongested(string worldName)
    {
        var status = GetWorldStatus(worldName);
        return status?.IsCongested ?? false;
    }

    public Dictionary<string, WorldStatus> GetAllWorldStatuses()
    {
        return CachedData?.Worlds ?? new Dictionary<string, WorldStatus>();
    }

    public DateTime? LastUpdated => CachedData?.LastUpdated;

    public bool NeedsRefresh()
    {
        if (CachedData == null) return true;
        return DateTime.UtcNow - CachedData.LastUpdated > _cacheValidity;
    }

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
            
            CachedData = new WorldStatusData
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

    protected List<WorldStatus> ParseWorldStatusHtml(string html)
    {
        var worlds = new List<WorldStatus>();
        
        try
        {
            var worldItemPattern = "<li[^>]*class=\"item-list[^\"]*\"[^>]*>.*?<div class=\"world-list__world_name\">\\s*<p>([^<]+)</p>.*?<div class=\"world-list__world_category\">\\s*<p>([^<]+)</p>.*?<div class=\"world-list__create_character\">(.*?)</div>.*?</li>";
            
            var matches = Regex.Matches(html, worldItemPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            
            foreach (Match match in matches)
            {
                if (match.Groups.Count >= 4)
                {
                    var worldName = match.Groups[1].Value.Trim();
                    var categoryText = match.Groups[2].Value.Trim();
                    var characterCreationHtml = match.Groups[3].Value.Trim();
                    
                    var classification = ParseClassification(categoryText);
                    var canCreateCharacter = characterCreationHtml.Contains("world-ic__available") &&
                                            !characterCreationHtml.Contains("world-ic__unavailable");
                    
                    worlds.Add(new WorldStatus
                    {
                        Name = worldName,
                        Classification = classification,
                        Category = categoryText,
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

    protected WorldClassification ParseClassification(string text)
    {
        return text.ToLowerInvariant() switch
        {
            "congested" => WorldClassification.Congested,
            "preferred" => WorldClassification.Preferred,
            "preferred+" => WorldClassification.PreferredPlus,
            _ => WorldClassification.Standard
        };
    }

    protected virtual void LoadCachedData()
    {
        try
        {
            if (!string.IsNullOrEmpty(CacheFilePath) && File.Exists(CacheFilePath))
            {
                var json = File.ReadAllText(CacheFilePath);
                CachedData = JsonSerializer.Deserialize<WorldStatusData>(json);
                _logger.LogInformation("[WorldStatus] Loaded {Count} worlds from cache", 
                    CachedData?.Worlds?.Count ?? 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WorldStatus] Failed to load cached data");
            CachedData = null;
        }
    }

    protected virtual void SaveCachedData()
    {
        try
        {
            if (!string.IsNullOrEmpty(CacheFilePath) && CachedData != null)
            {
                var json = JsonSerializer.Serialize(CachedData, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(CacheFilePath, json);
                _logger.LogDebug("[WorldStatus] Saved cache to {Path}", CacheFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WorldStatus] Failed to save cache");
        }
    }
}
