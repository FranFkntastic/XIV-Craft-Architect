using System.IO;
using System.IO.Compression;
using System.Text.Json;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// SQLite implementation of the market cache service for WPF desktop app.
/// Stores compressed Universalis responses for efficient retrieval.
/// </summary>
public class SqliteMarketCacheService : Core.Services.IMarketCacheService, IDisposable
{
    private readonly ILogger<SqliteMarketCacheService> _logger;
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly TimeSpan _defaultMaxAge = TimeSpan.FromHours(1);
    private readonly JsonSerializerOptions _jsonOptions;

    public SqliteMarketCacheService(ILogger<SqliteMarketCacheService> logger)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions 
        { 
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        };
        
        // Use LocalApplicationData for better persistence across updates
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIVCraftArchitect",
            "Cache");
        Directory.CreateDirectory(cacheDir);
        _dbPath = Path.Combine(cacheDir, "market_cache.db");
        
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        
        InitializeDatabase();
        MigrateFromJsonIfNeeded();
        
        _logger.LogInformation("[SqliteMarketCache] Initialized at {Path}", _dbPath);
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS market_data (
                item_id INTEGER NOT NULL,
                data_center TEXT NOT NULL,
                fetched_at TEXT NOT NULL,
                dc_avg_price REAL NOT NULL,
                hq_avg_price REAL,
                compressed_data BLOB NOT NULL,
                PRIMARY KEY (item_id, data_center)
            );
            
            CREATE INDEX IF NOT EXISTS idx_fetched_at ON market_data(fetched_at);
            
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// One-time migration from old JSON cache format.
    /// </summary>
    private void MigrateFromJsonIfNeeded()
    {
        // Check both old and new locations for JSON cache
        var oldJsonPath = Path.Combine(AppContext.BaseDirectory, "Cache", "market_cache.json");
        var jsonPath = oldJsonPath;
        
        if (!File.Exists(jsonPath))
        {
            // Also check LocalApplicationData location
            var newCacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FFXIVCraftArchitect",
                "Cache");
            jsonPath = Path.Combine(newCacheDir, "market_cache.json");
            if (!File.Exists(jsonPath)) return;
        }
        
        _logger.LogInformation("[SqliteMarketCache] Found legacy JSON cache at {Path}", jsonPath);

        try
        {
            _logger.LogInformation("[SqliteMarketCache] Migrating from JSON cache...");
            
            var json = File.ReadAllText(jsonPath);
            var data = JsonSerializer.Deserialize<LegacyMarketCacheFile>(json, _jsonOptions);
            
            if (data?.Entries != null)
            {
                foreach (var (key, entry) in data.Entries)
                {
                    // Skip stale entries during migration
                    if (DateTime.UtcNow - entry.FetchedAt > TimeSpan.FromHours(2))
                        continue;
                        
                    var cacheData = new Core.Services.CachedMarketData
                    {
                        ItemId = entry.ItemId,
                        DataCenter = entry.DataCenter,
                        FetchedAt = entry.FetchedAt,
                        DCAveragePrice = entry.DCAveragePrice,
                        HQAveragePrice = entry.HQAveragePrice,
                        Worlds = entry.Worlds.Select(w => new Core.Services.CachedWorldData
                        {
                            WorldName = w.WorldName,
                            Listings = w.Listings.Select(l => new Core.Services.CachedListing
                            {
                                Quantity = l.Quantity,
                                PricePerUnit = l.PricePerUnit,
                                RetainerName = l.RetainerName,
                                IsHq = l.IsHq
                            }).ToList()
                        }).ToList()
                    };
                    
                    _ = SetAsync(entry.ItemId, entry.DataCenter, cacheData);
                }
                
                _logger.LogInformation("[SqliteMarketCache] Migrated {Count} entries", data.Entries.Count);
            }
            
            // Rename old file to prevent re-migration
            File.Move(jsonPath, jsonPath + ".migrated", overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SqliteMarketCache] Failed to migrate from JSON");
        }
    }

    public async Task<Core.Services.CachedMarketData?> GetAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        var cutoff = DateTime.UtcNow - (maxAge ?? _defaultMaxAge);
        
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT fetched_at, dc_avg_price, hq_avg_price, compressed_data
            FROM market_data
            WHERE item_id = @itemId AND data_center = @dataCenter AND fetched_at > @cutoff
        ";
        cmd.Parameters.AddWithValue("@itemId", itemId);
        cmd.Parameters.AddWithValue("@dataCenter", dataCenter);
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
        
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            _logger.LogDebug("[SqliteMarketCache] MISS for {ItemId}@{DataCenter}", itemId, dataCenter);
            return null;
        }
        
        var compressedData = (byte[])reader.GetValue(3);
        var json = Decompress(compressedData);
        var worlds = JsonSerializer.Deserialize<List<Core.Services.CachedWorldData>>(json, _jsonOptions);
        
        _logger.LogDebug("[SqliteMarketCache] HIT for {ItemId}@{DataCenter}", itemId, dataCenter);
        
        return new Core.Services.CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            FetchedAt = reader.GetDateTime(0),
            DCAveragePrice = reader.GetDecimal(1),
            HQAveragePrice = reader.IsDBNull(2) ? null : reader.GetDecimal(2),
            Worlds = worlds ?? new List<Core.Services.CachedWorldData>()
        };
    }

    public async Task SetAsync(int itemId, string dataCenter, Core.Services.CachedMarketData data)
    {
        var json = JsonSerializer.Serialize(data.Worlds, _jsonOptions);
        var compressed = Compress(json);
        
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO market_data 
            (item_id, data_center, fetched_at, dc_avg_price, hq_avg_price, compressed_data)
            VALUES (@itemId, @dataCenter, @fetchedAt, @dcAvgPrice, @hqAvgPrice, @compressedData)
        ";
        cmd.Parameters.AddWithValue("@itemId", itemId);
        cmd.Parameters.AddWithValue("@dataCenter", dataCenter);
        cmd.Parameters.AddWithValue("@fetchedAt", data.FetchedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@dcAvgPrice", data.DCAveragePrice);
        cmd.Parameters.AddWithValue("@hqAvgPrice", data.HQAveragePrice ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@compressedData", compressed);
        
        await cmd.ExecuteNonQueryAsync();
        _logger.LogDebug("[SqliteMarketCache] Stored {ItemId}@{DataCenter}", itemId, dataCenter);
    }

    public async Task<bool> HasValidCacheAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        return await GetAsync(itemId, dataCenter, maxAge) != null;
    }

    public async Task<List<(int itemId, string dataCenter)>> GetMissingAsync(
        List<(int itemId, string dataCenter)> requests, 
        TimeSpan? maxAge = null)
    {
        var missing = new List<(int, string)>();
        
        foreach (var (itemId, dataCenter) in requests)
        {
            if (!await HasValidCacheAsync(itemId, dataCenter, maxAge))
            {
                missing.Add((itemId, dataCenter));
            }
        }
        
        return missing;
    }

    public async Task<int> CleanupStaleAsync(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM market_data
            WHERE fetched_at < @cutoff
        ";
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
        
        var deleted = await cmd.ExecuteNonQueryAsync();
        _logger.LogInformation("[SqliteMarketCache] Cleaned up {Count} stale entries", deleted);
        
        // Vacuum to reclaim space
        using var vacuumCmd = _connection.CreateCommand();
        vacuumCmd.CommandText = "VACUUM";
        await vacuumCmd.ExecuteNonQueryAsync();
        
        return deleted;
    }

    public async Task<Core.Services.CacheStats> GetStatsAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                COUNT(*) as total,
                SUM(CASE WHEN fetched_at > @cutoff THEN 1 ELSE 0 END) as valid,
                SUM(CASE WHEN fetched_at <= @cutoff THEN 1 ELSE 0 END) as stale,
                MIN(fetched_at) as oldest,
                MAX(fetched_at) as newest,
                SUM(LENGTH(compressed_data)) as size
            FROM market_data
        ";
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddHours(-1).ToString("O"));
        
        using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        
        return new Core.Services.CacheStats
        {
            TotalEntries = reader.GetInt32(0),
            ValidEntries = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            StaleEntries = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            OldestEntry = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            NewestEntry = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            ApproximateSizeBytes = reader.IsDBNull(5) ? 0 : reader.GetInt64(5)
        };
    }

    private static byte[] Compress(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    private static string Decompress(byte[] input)
    {
        using var source = new MemoryStream(input);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }

    // Legacy model for JSON migration
    private class LegacyMarketCacheFile
    {
        public DateTime SavedAt { get; set; }
        public Dictionary<string, LegacyCachedMarketData> Entries { get; set; } = new();
    }

    private class LegacyCachedMarketData
    {
        public int ItemId { get; set; }
        public string DataCenter { get; set; } = string.Empty;
        public DateTime FetchedAt { get; set; }
        public decimal DCAveragePrice { get; set; }
        public decimal? HQAveragePrice { get; set; }
        public List<LegacyCachedWorldData> Worlds { get; set; } = new();
    }

    private class LegacyCachedWorldData
    {
        public string WorldName { get; set; } = string.Empty;
        public List<LegacyCachedListing> Listings { get; set; } = new();
    }

    private class LegacyCachedListing
    {
        public int Quantity { get; set; }
        public long PricePerUnit { get; set; }
        public string RetainerName { get; set; } = string.Empty;
        public bool IsHq { get; set; }
    }
}
