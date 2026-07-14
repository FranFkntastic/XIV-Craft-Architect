using System.IO;
using System.IO.Compression;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Services;

/// <summary>
/// SQLite implementation of the market cache service for WPF desktop app.
/// Stores compressed Universalis responses for efficient retrieval.
/// Also acts as the orchestrator for fetching missing data from Universalis API.
/// </summary>
public class SqliteMarketCacheService : Core.Services.IMarketCacheService, IDisposable
{
    private const int BulkReadChunkSize = 400;
    private const int MaxDataCenterFetchConcurrency = 2;

    private readonly ILogger<SqliteMarketCacheService> _logger;
    private readonly UniversalisService _universalisService;
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly TimeSpan _defaultMaxAge = MarketEvidencePolicyDefaults.ReusableCacheMaxAge;
    private readonly JsonSerializerOptions _jsonOptions;

    public SqliteMarketCacheService(
        ILogger<SqliteMarketCacheService> logger,
        UniversalisService universalisService)
    {
        _logger = logger;
        _universalisService = universalisService;
        _jsonOptions = new JsonSerializerOptions 
        { 
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        };
        
        // Use LocalApplicationData for better persistence across updates
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIV_Craft_Architect",
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
                last_upload_time_unix_ms INTEGER,
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
        EnsureColumnExists("market_data", "last_upload_time_unix_ms", "INTEGER");
    }

    private void EnsureColumnExists(string tableName, string columnName, string definition)
    {
        using var checkCmd = _connection.CreateCommand();
        checkCmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = checkCmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alterCmd = _connection.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}";
        alterCmd.ExecuteNonQuery();
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
                "FFXIV_Craft_Architect",
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
            SELECT fetched_at, last_upload_time_unix_ms, dc_avg_price, hq_avg_price, compressed_data
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
        
        var compressedData = (byte[])reader.GetValue(4);
        var json = Decompress(compressedData);
        var worlds = JsonSerializer.Deserialize<List<Core.Services.CachedWorldData>>(json, _jsonOptions);
        
        _logger.LogDebug("[SqliteMarketCache] HIT for {ItemId}@{DataCenter}", itemId, dataCenter);
        
        return new Core.Services.CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            FetchedAt = CacheTimeHelper.ParseFetchedAt(reader.GetValue(0)),
            LastUploadTimeUnixMilliseconds = reader.IsDBNull(1) ? null : reader.GetInt64(1),
            DCAveragePrice = reader.GetDecimal(2),
            HQAveragePrice = reader.IsDBNull(3) ? null : reader.GetDecimal(3),
            Worlds = worlds ?? new List<Core.Services.CachedWorldData>()
        };
    }

    public async Task<(Core.Services.CachedMarketData? Data, bool IsStale)> GetWithStaleAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        var cutoff = DateTime.UtcNow - (maxAge ?? _defaultMaxAge);
        
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT fetched_at, last_upload_time_unix_ms, dc_avg_price, hq_avg_price, compressed_data
            FROM market_data
            WHERE item_id = @itemId AND data_center = @dataCenter
        ";
        cmd.Parameters.AddWithValue("@itemId", itemId);
        cmd.Parameters.AddWithValue("@dataCenter", dataCenter);
        
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            _logger.LogDebug("[SqliteMarketCache] NO DATA for {ItemId}@{DataCenter}", itemId, dataCenter);
            return (null, false);
        }
        
        var fetchedAt = CacheTimeHelper.ParseFetchedAt(reader.GetValue(0));
        var isStale = fetchedAt <= cutoff;
        
        var compressedData = (byte[])reader.GetValue(4);
        var json = Decompress(compressedData);
        var worlds = JsonSerializer.Deserialize<List<Core.Services.CachedWorldData>>(json, _jsonOptions);
        
        _logger.LogDebug("[SqliteMarketCache] {Status} for {ItemId}@{DataCenter} (fetched {Hours:F1}h ago)", 
            isStale ? "STALE" : "FRESH", itemId, dataCenter, (DateTime.UtcNow - fetchedAt).TotalHours);
        
        var data = new Core.Services.CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            FetchedAt = fetchedAt,
            LastUploadTimeUnixMilliseconds = reader.IsDBNull(1) ? null : reader.GetInt64(1),
            DCAveragePrice = reader.GetDecimal(2),
            HQAveragePrice = reader.IsDBNull(3) ? null : reader.GetDecimal(3),
            Worlds = worlds ?? new List<Core.Services.CachedWorldData>()
        };
        
        return (data, isStale);
    }

    public async Task<IReadOnlyDictionary<(int itemId, string dataCenter), Core.Services.CachedMarketData>> GetManyAsync(
        IReadOnlyCollection<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null)
    {
        var result = new Dictionary<(int itemId, string dataCenter), Core.Services.CachedMarketData>();
        if (requests.Count == 0)
        {
            return result;
        }

        var cutoff = DateTime.UtcNow - (maxAge ?? _defaultMaxAge);
        foreach (var chunk in requests.Distinct().Chunk(BulkReadChunkSize))
        {
            using var cmd = _connection.CreateCommand();
            var values = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                values.Add($"(@itemId{i}, @dataCenter{i})");
                cmd.Parameters.AddWithValue($"@itemId{i}", chunk[i].itemId);
                cmd.Parameters.AddWithValue($"@dataCenter{i}", chunk[i].dataCenter);
            }

            cmd.CommandText = $@"
                WITH requested(item_id, data_center) AS (
                    VALUES {string.Join(", ", values)}
                )
                SELECT md.item_id, md.data_center, md.fetched_at, md.last_upload_time_unix_ms, md.dc_avg_price, md.hq_avg_price, md.compressed_data
                FROM market_data md
                INNER JOIN requested r ON r.item_id = md.item_id AND r.data_center = md.data_center
                WHERE md.fetched_at > @cutoff
            ";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var itemId = reader.GetInt32(0);
                var dataCenter = reader.GetString(1);
                result[(itemId, dataCenter)] = ReadCachedData(reader, itemId, dataCenter, fetchedAtOrdinal: 2);
            }
        }

        _logger.LogDebug(
            "[SqliteMarketCache] Bulk loaded {HitCount}/{RequestCount} valid entries",
            result.Count,
            requests.Count);

        return result;
    }

    public async Task SetAsync(int itemId, string dataCenter, Core.Services.CachedMarketData data)
    {
        var age = DateTime.UtcNow - data.FetchedAt;
        _logger.LogDebug("[SqliteMarketCache] Storing {ItemId}@{DataCenter} with FetchedAt={FetchedAt} (age={Age:F1}min)", 
            itemId, dataCenter, data.FetchedAt, age.TotalMinutes);
        
        var json = JsonSerializer.Serialize(data.Worlds, _jsonOptions);
        var compressed = Compress(json);
        
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO market_data 
            (item_id, data_center, fetched_at, last_upload_time_unix_ms, dc_avg_price, hq_avg_price, compressed_data)
            VALUES (@itemId, @dataCenter, @fetchedAt, @lastUploadTimeUnixMs, @dcAvgPrice, @hqAvgPrice, @compressedData)
        ";
        cmd.Parameters.AddWithValue("@itemId", itemId);
        cmd.Parameters.AddWithValue("@dataCenter", dataCenter);
        var fetchedAtUtc = CacheTimeHelper.NormalizeToUtc(data.FetchedAt);
        cmd.Parameters.AddWithValue("@fetchedAt", fetchedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@lastUploadTimeUnixMs", data.LastUploadTimeUnixMilliseconds ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@dcAvgPrice", data.DCAveragePrice);
        cmd.Parameters.AddWithValue("@hqAvgPrice", data.HQAveragePrice ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@compressedData", compressed);
        
        var rowsAffected = await cmd.ExecuteNonQueryAsync();
        _logger.LogDebug("[SqliteMarketCache] Stored {ItemId}@{DataCenter} - {Rows} rows affected", itemId, dataCenter, rowsAffected);
    }

    public async Task<bool> HasValidCacheAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        return await GetAsync(itemId, dataCenter, maxAge) != null;
    }

    public async Task<List<(int itemId, string dataCenter)>> GetMissingAsync(
        List<(int itemId, string dataCenter)> requests, 
        TimeSpan? maxAge = null)
    {
        if (requests.Count == 0)
        {
            return new List<(int itemId, string dataCenter)>();
        }

        var validEntries = await GetManyAsync(requests, maxAge);
        var missing = requests
            .Where(request => !validEntries.ContainsKey(request))
            .ToList();
        
        _logger.LogInformation("[SqliteMarketCache] Checked {Checked}, Hits {Hits}, Missing {Missing}", 
            requests.Count, requests.Count - missing.Count, missing.Count);
        
        return missing;
    }

    public async Task<int> EnsurePopulatedAsync(
        List<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAge),
                maxAge,
                "Use RefreshRequestedAsync when fresh data is required for specific pairs.");
        }

        var effectiveMaxAge = maxAge ?? _defaultMaxAge;
        var cutoff = DateTime.UtcNow - effectiveMaxAge;
        
        _logger.LogInformation("[SqliteMarketCache] EnsurePopulatedAsync START - {Count} requests, maxAge={MaxAge}, cutoff={Cutoff}", 
            requests.Count, effectiveMaxAge, cutoff);
        
        if (requests.Count == 0) return 0;
        
        // Check what's missing from cache (using provided maxAge or default)
        var missing = await GetMissingAsync(requests, maxAge);
        if (missing.Count == 0)
        {
            _logger.LogInformation("[SqliteMarketCache] All {Count} items already in cache (maxAge={MaxAge})", 
                requests.Count, effectiveMaxAge);
            return 0;
        }

        return await FetchAndStoreAsync(missing, progress, ct);
    }

    public async Task<int> RefreshRequestedAsync(
        List<(int itemId, string dataCenter)> requests,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[SqliteMarketCache] RefreshRequestedAsync START - {Count} requests", requests.Count);
        if (requests.Count == 0)
        {
            return 0;
        }

        return await FetchAndStoreAsync(requests, progress, ct);
    }

    private async Task<int> FetchAndStoreAsync(
        List<(int itemId, string dataCenter)> requests,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (requests.Count == 0)
        {
            return 0;
        }
        
        _logger.LogInformation("[SqliteMarketCache] Fetching {MissingCount}/{TotalCount} items from Universalis", 
            requests.Count, requests.Count);
        progress?.Report($"Fetching market data for {requests.Count} items...");
        
        // Group by data center for efficient bulk fetching
        var byDataCenter = requests.GroupBy(x => x.dataCenter).ToList();
        int fetchedCount = 0;

        var fetchResults = await FetchDataCentersAsync(byDataCenter, progress, ct);
        var shouldLoadWorldData = fetchResults.Any(result => result.FetchedData.Count > 0);
        var worldData = shouldLoadWorldData
            ? await GetWorldDataForCachingAsync(ct)
            : null;

        foreach (var result in fetchResults)
        {
            // Store each result in cache
            foreach (var kvp in result.FetchedData)
            {
                var cachedData = ConvertUniversalisResponseToCachedData(kvp.Key, result.DataCenter, kvp.Value, worldData);
                await SetAsync(kvp.Key, result.DataCenter, cachedData);
                fetchedCount++;
            }

            _logger.LogInformation("[SqliteMarketCache] Fetched and cached {FetchedCount}/{RequestedCount} items from {DC}",
                result.FetchedData.Count, result.RequestedItemIds.Count, result.DataCenter);
        }
        
        return fetchedCount;
    }

    private async Task<List<DataCenterFetchResult>> FetchDataCentersAsync(
        IReadOnlyCollection<IGrouping<string, (int itemId, string dataCenter)>> dataCenterGroups,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(MaxDataCenterFetchConcurrency);
        var tasks = dataCenterGroups.Select(async dcGroup =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var dc = dcGroup.Key;
                var itemIds = dcGroup.Select(x => x.itemId).ToList();
                progress?.Report($"Fetching {itemIds.Count} items from {dc}...");

                var fetchedData = await _universalisService.GetMarketDataBulkAsync(dc, itemIds, useParallel: true, ct: ct);
                return new DataCenterFetchResult(dc, itemIds, fetchedData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SqliteMarketCache] Failed to fetch items from {DC}", dcGroup.Key);
                // Continue with other DCs - partial failure is acceptable
                return new DataCenterFetchResult(dcGroup.Key, dcGroup.Select(x => x.itemId).ToList(), new Dictionary<int, UniversalisResponse>());
            }
            finally
            {
                semaphore.Release();
            }
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    private async Task<WorldData?> GetWorldDataForCachingAsync(CancellationToken ct)
    {
        try
        {
            return await _universalisService.GetWorldDataAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SqliteMarketCache] World metadata unavailable; caching data without per-world upload mapping");
            return null;
        }
    }

    private sealed record DataCenterFetchResult(
        string DataCenter,
        IReadOnlyCollection<int> RequestedItemIds,
        Dictionary<int, UniversalisResponse> FetchedData);
    
    private Core.Services.CachedMarketData ConvertUniversalisResponseToCachedData(
        int itemId,
        string dataCenter,
        UniversalisResponse response,
        WorldData? worldData)
    {
        _logger.LogDebug("[SqliteMarketCache] Converting response for {ItemId}@{DC} with {ListingCount} listings", 
            itemId, dataCenter, response.Listings.Count);

        var now = DateTime.UtcNow;
        _logger.LogDebug("[SqliteMarketCache] Setting FetchedAt={Now} for {ItemId}@{DC}", now, itemId, dataCenter);

        return UniversalisMarketDataMapper.ToCachedMarketData(itemId, dataCenter, response, worldData, now);
    }

    private Core.Services.CachedMarketData ReadCachedData(
        SqliteDataReader reader,
        int itemId,
        string dataCenter,
        int fetchedAtOrdinal)
    {
        var compressedData = (byte[])reader.GetValue(fetchedAtOrdinal + 4);
        var json = Decompress(compressedData);
        var worlds = JsonSerializer.Deserialize<List<Core.Services.CachedWorldData>>(json, _jsonOptions);

        return new Core.Services.CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            FetchedAt = CacheTimeHelper.ParseFetchedAt(reader.GetValue(fetchedAtOrdinal)),
            LastUploadTimeUnixMilliseconds = reader.IsDBNull(fetchedAtOrdinal + 1) ? null : reader.GetInt64(fetchedAtOrdinal + 1),
            DCAveragePrice = reader.GetDecimal(fetchedAtOrdinal + 2),
            HQAveragePrice = reader.IsDBNull(fetchedAtOrdinal + 3) ? null : reader.GetDecimal(fetchedAtOrdinal + 3),
            Worlds = worlds ?? new List<Core.Services.CachedWorldData>()
        };
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
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.Subtract(_defaultMaxAge).ToString("O"));
        
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
