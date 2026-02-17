using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect;

public partial class CacheDiagnosticsWindow : Window
{
    private readonly string _dbPath;
    private List<CacheEntryViewModel> _currentEntries = new();

    public CacheDiagnosticsWindow()
    {
        InitializeComponent();
        
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FFXIV_Craft_Architect",
            "Cache");
        _dbPath = Path.Combine(cacheDir, "market_cache.db");
        
        DbPathText.Text = _dbPath;
        RefreshData();
    }

    private void RefreshData()
    {
        try
        {
            if (!File.Exists(_dbPath))
            {
                EntriesCount.Text = "Entries: 0";
                DbSizeText.Text = "DB: not found";
                WalSizeText.Text = "WAL: -";
                OldestText.Text = "Oldest: -";
                NewestText.Text = "Newest: -";
                CacheDataGrid.ItemsSource = null;
                return;
            }

            _currentEntries = new List<CacheEntryViewModel>();
            long dbSize = new FileInfo(_dbPath).Length;
            
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            
            // Check if table exists
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='market_data'";
                var tableExists = cmd.ExecuteScalar() != null;
                if (!tableExists)
                {
                    EntriesCount.Text = "Entries: 0";
                    DbSizeText.Text = $"DB: {dbSize:N0} bytes";
                    WalSizeText.Text = "WAL: -";
                    OldestText.Text = "Oldest: -";
                    NewestText.Text = "Newest: -";
                    CacheDataGrid.ItemsSource = null;
                    return;
                }
            }
            
            // Get stats
            int totalCount = 0;
            DateTime? oldest = null;
            DateTime? newest = null;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*), MIN(fetched_at), MAX(fetched_at) FROM market_data";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    totalCount = reader.GetInt32(0);
                    if (!reader.IsDBNull(1)) oldest = reader.GetDateTime(1);
                    if (!reader.IsDBNull(2)) newest = reader.GetDateTime(2);
                }
            }
            
            // Get entries with decompressed data
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT item_id, data_center, fetched_at, dc_avg_price, hq_avg_price, compressed_data FROM market_data ORDER BY fetched_at DESC";
                
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var itemId = reader.GetInt32(0);
                    var dataCenter = reader.GetString(1);
                    var fetchedAt = reader.GetDateTime(2);
                    var dcAvgPrice = reader.GetDecimal(3);
                    var hqAvgPrice = reader.IsDBNull(4) ? (decimal?)null : reader.GetDecimal(4);
                    var compressedData = (byte[])reader.GetValue(5);
                    
                    int worldsCount = 0;
                    int listingsCount = 0;
                    List<CachedWorldData>? worlds = null;
                    
                    try
                    {
                        var json = Decompress(compressedData);
                        worlds = JsonSerializer.Deserialize<List<CachedWorldData>>(json);
                        if (worlds != null)
                        {
                            worldsCount = worlds.Count;
                            listingsCount = worlds.Sum(w => w.Listings.Count);
                        }
                    }
                    catch
                    {
                        worldsCount = -1;
                    }
                    
                    var age = DateTime.UtcNow - fetchedAt;
                    
                    _currentEntries.Add(new CacheEntryViewModel
                    {
                        ItemId = itemId,
                        DataCenter = dataCenter,
                        FetchedAt = fetchedAt.ToLocalTime().ToString("g"),
                        FetchedAtRaw = fetchedAt,
                        AgeText = age.TotalMinutes < 1 ? "just now" : 
                                  age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago" :
                                  age.TotalDays < 1 ? $"{(int)age.TotalHours}h {(int)age.TotalMinutes % 60}m ago" :
                                  $"{(int)age.TotalDays}d ago",
                        AgeMinutes = age.TotalMinutes,
                        DCAveragePrice = dcAvgPrice,
                        HQAveragePrice = hqAvgPrice?.ToString() ?? "-",
                        WorldsCount = worldsCount,
                        ListingsCount = listingsCount,
                        DataSize = compressedData.Length,
                        DataSizeText = FormatBytes(compressedData.Length),
                        Worlds = worlds
                    });
                }
            }
            
            var walPath = _dbPath + "-wal";
            long walSize = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
            
            EntriesCount.Text = $"Entries: {totalCount:N0}";
            DbSizeText.Text = $"DB: {FormatBytes(dbSize)}";
            WalSizeText.Text = $"WAL: {FormatBytes(walSize)}";
            OldestText.Text = oldest.HasValue ? $"Oldest: {(DateTime.UtcNow - oldest.Value).TotalHours:F0}h ago" : "Oldest: -";
            NewestText.Text = newest.HasValue ? $"Newest: {(DateTime.UtcNow - newest.Value).TotalHours:F0}h ago" : "Newest: -";
            
            CacheDataGrid.ItemsSource = _currentEntries;
        }
        catch (Exception ex)
        {
            EntriesCount.Text = "Error";
            DbPathText.Text = $"Error: {ex.Message}";
            CacheDataGrid.ItemsSource = null;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F1} MB";
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CacheDataGrid.SelectedItem is CacheEntryViewModel entry && entry.Worlds != null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Item ID: {entry.ItemId}");
            sb.AppendLine($"Data Center: {entry.DataCenter}");
            sb.AppendLine($"Fetched At: {entry.FetchedAt}");
            sb.AppendLine($"DC Average: {entry.DCAveragePrice:N0} gil");
            if (entry.HQAveragePrice != "-")
                sb.AppendLine($"HQ Average: {entry.HQAveragePrice} gil");
            sb.AppendLine();
            sb.AppendLine($"Worlds ({entry.WorldsCount}):");
            
            foreach (var world in entry.Worlds)
            {
                sb.AppendLine($"  {world.WorldName} ({world.Listings.Count} listings)");
                foreach (var listing in world.Listings.Take(3))
                {
                    sb.AppendLine($"    {listing.Quantity}x @ {listing.PricePerUnit:N0} gil {(listing.IsHq ? "[HQ]" : "")}");
                }
                if (world.Listings.Count > 3)
                {
                    sb.AppendLine($"    ... and {world.Listings.Count - 3} more");
                }
            }
            
            DetailsHeader.Text = $"Details for Item {entry.ItemId} @ {entry.DataCenter}";
            DetailsText.Text = sb.ToString();
        }
        else
        {
            DetailsHeader.Text = "Entry Details (select an item)";
            DetailsText.Text = "No item selected";
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshData();
    }

    private void OnCheckpointClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            cmd.ExecuteNonQuery();
            MessageBox.Show("WAL checkpoint completed! Changes flushed to main database.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshData();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Checkpoint failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnVacuumClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "VACUUM";
            cmd.ExecuteNonQuery();
            MessageBox.Show("Database vacuumed successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshData();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Vacuum failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Clear all cached market data?\n\nThis cannot be undone.",
            "Confirm Clear Cache",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        
        if (result == MessageBoxResult.Yes)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM market_data";
                var deleted = cmd.ExecuteNonQuery();
                
                using var vacuumCmd = connection.CreateCommand();
                vacuumCmd.CommandText = "VACUUM";
                vacuumCmd.ExecuteNonQuery();
                
                MessageBox.Show($"Cache cleared. {deleted:N0} entries deleted.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshData();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Clear failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string Decompress(byte[] input)
    {
        using var source = new MemoryStream(input);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }
}

public class CacheEntryViewModel
{
    public int ItemId { get; set; }
    public string DataCenter { get; set; } = "";
    public string FetchedAt { get; set; } = "";
    public DateTime FetchedAtRaw { get; set; }
    public string AgeText { get; set; } = "";
    public double AgeMinutes { get; set; }
    public decimal DCAveragePrice { get; set; }
    public string HQAveragePrice { get; set; } = "";
    public int WorldsCount { get; set; }
    public int ListingsCount { get; set; }
    public long DataSize { get; set; }
    public string DataSizeText { get; set; } = "";
    public List<CachedWorldData>? Worlds { get; set; }
}
