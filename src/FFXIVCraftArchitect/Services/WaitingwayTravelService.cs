using System.Net.Http;
using System.Net.Http.Json;
using FFXIVCraftArchitect.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for fetching and caching FFXIV world travel status from Waitingway API.
/// Tracks which worlds currently have travel prohibited (congested worlds at capacity).
/// </summary>
public class WaitingwayTravelService : IWaitingwayTravelService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WaitingwayTravelService> _logger;
    private readonly TimeSpan _cacheValidity = TimeSpan.FromMinutes(1); // Travel status changes frequently
    
    private TravelStatusData? _cachedData;
    private DateTime _lastFetchTime = DateTime.MinValue;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    
    private const string WaitingwayTravelApiUrl = "https://waiting.camora.dev/api/v1/travel/";
    private const string WaitingwayWorldStatusApiUrl = "https://waiting.camora.dev/api/v1/world_status/";
    
    public WaitingwayTravelService(IHttpClientFactory httpClientFactory, ILogger<WaitingwayTravelService> logger)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("Waitingway");
    }
    
    /// <summary>
    /// Disposes the HttpClient instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
        _fetchLock.Dispose();
    }
    
    /// <summary>
    /// Gets the current travel prohibition status for all worlds.
    /// Returns cached data if fresh, otherwise fetches from API.
    /// </summary>
    public async Task<Dictionary<int, bool>> GetTravelProhibitionsAsync(CancellationToken ct = default)
    {
        // Return cached data if fresh
        if (_cachedData != null && DateTime.UtcNow - _lastFetchTime < _cacheValidity)
        {
            _logger.LogDebug("[Waitingway] Returning cached travel status (age: {Age}s)", 
                (DateTime.UtcNow - _lastFetchTime).TotalSeconds);
            return _cachedData.Prohibited;
        }
        
        await _fetchLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedData != null && DateTime.UtcNow - _lastFetchTime < _cacheValidity)
            {
                return _cachedData.Prohibited;
            }
            
            return await FetchTravelStatusAsync(ct);
        }
        finally
        {
            _fetchLock.Release();
        }
    }
    
    /// <summary>
    /// Checks if travel to a specific world is currently prohibited.
    /// </summary>
    public async Task<bool> IsTravelProhibitedAsync(int worldId, CancellationToken ct = default)
    {
        var prohibitions = await GetTravelProhibitionsAsync(ct);
        return prohibitions.TryGetValue(worldId, out var prohibited) && prohibited;
    }
    
    /// <summary>
    /// Checks if travel to a specific world is currently prohibited.
    /// World name lookup requires world data to be loaded.
    /// </summary>
    public async Task<bool> IsTravelProhibitedAsync(string worldName, Dictionary<string, int> worldNameToId, CancellationToken ct = default)
    {
        if (!worldNameToId.TryGetValue(worldName, out var worldId))
        {
            _logger.LogWarning("[Waitingway] Unknown world name: {WorldName}", worldName);
            return false; // Default to allowing travel if we can't look up
        }
        
        return await IsTravelProhibitedAsync(worldId, ct);
    }
    
    /// <summary>
    /// Gets the current average DC travel time in seconds.
    /// </summary>
    public async Task<int?> GetAverageTravelTimeAsync(CancellationToken ct = default)
    {
        var data = await GetTravelStatusDataAsync(ct);
        return data?.TravelTime;
    }
    
    /// <summary>
    /// Gets the full travel status data including metadata.
    /// </summary>
    public async Task<TravelStatusData?> GetTravelStatusDataAsync(CancellationToken ct = default)
    {
        if (_cachedData != null && DateTime.UtcNow - _lastFetchTime < _cacheValidity)
        {
            return _cachedData;
        }
        
        await _fetchLock.WaitAsync(ct);
        try
        {
            if (_cachedData != null && DateTime.UtcNow - _lastFetchTime < _cacheValidity)
            {
                return _cachedData;
            }
            
            await FetchTravelStatusAsync(ct);
            return _cachedData;
        }
        finally
        {
            _fetchLock.Release();
        }
    }
    
    /// <summary>
    /// Forces a refresh of the travel status from the API.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        await _fetchLock.WaitAsync(ct);
        try
        {
            return await FetchTravelStatusAsync(ct) != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Waitingway] Failed to refresh travel status");
            return false;
        }
        finally
        {
            _fetchLock.Release();
        }
    }
    
    private async Task<Dictionary<int, bool>?> FetchTravelStatusAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[Waitingway] Fetching travel status from API...");
            
            var response = await _httpClient.GetFromJsonAsync<WaitingwayTravelResponse>(WaitingwayTravelApiUrl, ct);
            
            if (response == null)
            {
                _logger.LogWarning("[Waitingway] API returned null response");
                return null;
            }
            
            // Convert string keys to int keys
            var prohibited = response.Prohibited.ToDictionary(
                kvp => int.Parse(kvp.Key),
                kvp => kvp.Value
            );
            
            _cachedData = new TravelStatusData
            {
                Prohibited = prohibited,
                TravelTime = response.TravelTime,
                FetchedAt = DateTime.UtcNow
            };
            
            _lastFetchTime = DateTime.UtcNow;
            
            var prohibitedCount = prohibited.Count(p => p.Value);
            _logger.LogInformation("[Waitingway] Fetched travel status: {Prohibited}/{Total} worlds prohibited, avg travel time: {TravelTime}s",
                prohibitedCount, prohibited.Count, response.TravelTime);
            
            return prohibited;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[Waitingway] HTTP error fetching travel status");
            return _cachedData?.Prohibited;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Waitingway] Unexpected error fetching travel status");
            return _cachedData?.Prohibited;
        }
    }
    
    /// <summary>
    /// Gets world status information (category, classification) from Waitingway.
    /// This is separate from travel prohibition status.
    /// </summary>
    public async Task<List<WorldStatusInfo>?> GetWorldStatusAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("[Waitingway] Fetching world status from API...");
            
            var response = await _httpClient.GetFromJsonAsync<WaitingwayWorldStatusResponse>(WaitingwayWorldStatusApiUrl, ct);
            
            if (response?.Value == null)
            {
                _logger.LogWarning("[Waitingway] World status API returned null response");
                return null;
            }
            
            return response.Value.Select(w => new WorldStatusInfo
            {
                WorldId = w.WorldId,
                Status = w.Status,
                Category = w.Category,
                CanCreateCharacter = w.CanCreate
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Waitingway] Error fetching world status");
            return null;
        }
    }
}

/// <summary>
/// Response from Waitingway travel API.
/// </summary>
public class WaitingwayTravelResponse
{
    public int TravelTime { get; set; }
    public Dictionary<string, bool> Prohibited { get; set; } = new();
}

/// <summary>
/// Response from Waitingway world status API.
/// </summary>
public class WaitingwayWorldStatusResponse
{
    public List<WaitingwayWorldStatusEntry> Value { get; set; } = new();
}

public class WaitingwayWorldStatusEntry
{
    public int WorldId { get; set; }
    public int Status { get; set; }
    public int Category { get; set; }
    public bool CanCreate { get; set; }
}

/// <summary>
/// Cached travel status data.
/// </summary>
public class TravelStatusData
{
    public Dictionary<int, bool> Prohibited { get; set; } = new();
    public int TravelTime { get; set; }
    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// World status information from Waitingway.
/// </summary>
public class WorldStatusInfo
{
    public int WorldId { get; set; }
    public int Status { get; set; }
    public int Category { get; set; }
    public bool CanCreateCharacter { get; set; }
    
    /// <summary>
    /// World category classification (0=Standard, 1=Preferred, 2=Preferred+, 3=Congested).
    /// </summary>
    public WorldCategory Classification => Category switch
    {
        0 => WorldCategory.Standard,
        1 => WorldCategory.Preferred,
        2 => WorldCategory.PreferredPlus,
        3 => WorldCategory.Congested,
        _ => WorldCategory.Standard
    };
}

public enum WorldCategory
{
    Standard,
    Preferred,
    PreferredPlus,
    Congested
}
