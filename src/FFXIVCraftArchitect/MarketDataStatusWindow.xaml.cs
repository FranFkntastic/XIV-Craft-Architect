using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using FFXIVCraftArchitect.Models;
using Microsoft.Win32;

namespace FFXIVCraftArchitect;

/// <summary>
/// Window for visualizing real-time market data population status.
/// Shows each item's fetch status: Pending, Fetching, Success, Failed, or Cached.
/// </summary>
public partial class MarketDataStatusWindow : Window
{
    public ObservableCollection<MarketDataStatusItem> StatusItems { get; } = new();
    private ICollectionView? _filteredView;

    public MarketDataStatusWindow()
    {
        InitializeComponent();
        SetupDataGrid();
    }

    private void SetupDataGrid()
    {
        _filteredView = CollectionViewSource.GetDefaultView(StatusItems);
        StatusDataGrid.ItemsSource = _filteredView;
        UpdateStats();
    }

    /// <summary>
    /// Initialize with a list of items to track.
    /// </summary>
    public void InitializeItems(IEnumerable<(int itemId, string name, int quantity)> items)
    {
        StatusItems.Clear();
        
        foreach (var (itemId, name, quantity) in items)
        {
            StatusItems.Add(new MarketDataStatusItem
            {
                ItemId = itemId,
                ItemName = name,
                Quantity = quantity,
                Status = MarketDataFetchStatus.Pending
            });
        }
        
        UpdateStats();
    }

    /// <summary>
    /// Update the status of a specific item.
    /// </summary>
    public void UpdateItemStatus(int itemId, MarketDataFetchStatus status, decimal price = 0, string sourceDetails = "", string errorMessage = "")
    {
        var item = StatusItems.FirstOrDefault(i => i.ItemId == itemId);
        if (item != null)
        {
            Dispatcher.Invoke(() =>
            {
                item.Status = status;
                if (price > 0) item.UnitPrice = price;
                if (!string.IsNullOrEmpty(sourceDetails)) item.SourceDetails = sourceDetails;
                if (!string.IsNullOrEmpty(errorMessage)) item.ErrorMessage = errorMessage;
                UpdateStats();
            });
        }
    }

    /// <summary>
    /// Mark all pending items as fetching.
    /// </summary>
    public void SetAllFetching()
    {
        Dispatcher.Invoke(() =>
        {
            foreach (var item in StatusItems.Where(i => i.Status == MarketDataFetchStatus.Pending))
            {
                item.Status = MarketDataFetchStatus.Fetching;
            }
            UpdateStats();
        });
    }

    /// <summary>
    /// Mark a specific item as currently being fetched.
    /// </summary>
    public void SetItemFetching(int itemId)
    {
        UpdateItemStatus(itemId, MarketDataFetchStatus.Fetching);
    }

    /// <summary>
    /// Mark a specific item as successfully fetched.
    /// </summary>
    public void SetItemSuccess(int itemId, decimal price, string sourceDetails)
    {
        UpdateItemStatus(itemId, MarketDataFetchStatus.Success, price, sourceDetails);
        
        // Record timestamp for cache age tracking
        var item = StatusItems.FirstOrDefault(i => i.ItemId == itemId);
        if (item != null)
        {
            item.CacheTimestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Mark a specific item as failed.
    /// </summary>
    public void SetItemFailed(int itemId, string errorMessage)
    {
        UpdateItemStatus(itemId, MarketDataFetchStatus.Failed, errorMessage: errorMessage);
    }

    /// <summary>
    /// Mark a specific item as using cached data.
    /// </summary>
    public void SetItemCached(int itemId, decimal price, string sourceDetails)
    {
        UpdateItemStatus(itemId, MarketDataFetchStatus.Cached, price, sourceDetails);
        
        // For cached items, try to extract age from the source details if available
        // Otherwise assume it was cached just now (this is a simplification)
        var item = StatusItems.FirstOrDefault(i => i.ItemId == itemId);
        if (item != null)
        {
            // If we have a way to determine original fetch time, use it
            // Otherwise use a time in the past (we'll use "1 hour ago" as default assumption)
            item.CacheTimestamp = DateTime.Now.AddHours(-1);
        }
    }

    private void UpdateStats()
    {
        Dispatcher.Invoke(() =>
        {
            var pending = StatusItems.Count(i => i.Status == MarketDataFetchStatus.Pending);
            var fetching = StatusItems.Count(i => i.Status == MarketDataFetchStatus.Fetching);
            var success = StatusItems.Count(i => i.Status == MarketDataFetchStatus.Success);
            var failed = StatusItems.Count(i => i.Status == MarketDataFetchStatus.Failed);
            var cached = StatusItems.Count(i => i.Status == MarketDataFetchStatus.Cached);
            var total = StatusItems.Count;
            var completed = success + failed + cached;

            PendingCount.Text = $"⏳ Pending: {pending}";
            FetchingCount.Text = $"🔄 Fetching: {fetching}";
            SuccessCount.Text = $"✓ Success: {success}";
            FailedCount.Text = $"✗ Failed: {failed}";
            CachedCount.Text = $"📋 Cached: {cached}";

            if (completed >= total)
            {
                ProgressText.Text = $" - Complete ({completed}/{total})";
                ProgressText.Foreground = failed > 0 
                    ? System.Windows.Media.Brushes.Orange 
                    : System.Windows.Media.Brushes.LightGreen;
            }
            else
            {
                ProgressText.Text = $" - In Progress ({completed}/{total})";
                ProgressText.Foreground = System.Windows.Media.Brushes.LightBlue;
            }
        });
    }

    #region Event Handlers

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnFilterAll(object sender, RoutedEventArgs e)
    {
        if (_filteredView != null)
        {
            _filteredView.Filter = null;
        }
    }

    private void OnFilterPending(object sender, RoutedEventArgs e)
    {
        if (_filteredView != null)
        {
            _filteredView.Filter = obj => obj is MarketDataStatusItem item && 
                (item.Status == MarketDataFetchStatus.Pending || item.Status == MarketDataFetchStatus.Fetching);
        }
    }

    private void OnFilterFailed(object sender, RoutedEventArgs e)
    {
        if (_filteredView != null)
        {
            _filteredView.Filter = obj => obj is MarketDataStatusItem item && item.Status == MarketDataFetchStatus.Failed;
        }
    }

    private void OnFilterSuccess(object sender, RoutedEventArgs e)
    {
        if (_filteredView != null)
        {
            _filteredView.Filter = obj => obj is MarketDataStatusItem item && 
                (item.Status == MarketDataFetchStatus.Success || item.Status == MarketDataFetchStatus.Cached);
        }
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = "csv",
            FileName = $"MarketDataStatus_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var lines = new List<string>
                {
                    "ItemId,ItemName,Quantity,Status,UnitPrice,SourceDetails,ErrorMessage"
                };

                foreach (var item in StatusItems)
                {
                    lines.Add($"{item.ItemId},\"{item.ItemName.Replace("\"", "\"\"")}\",{item.Quantity},{item.Status},{item.UnitPrice},\"{item.SourceDetails.Replace("\"", "\"\"")}\",\"{item.ErrorMessage.Replace("\"", "\"\"")}\"");
                }

                File.WriteAllLines(dialog.FileName, lines);
                MessageBox.Show("Export complete!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    #endregion
}

/// <summary>
/// Converter to convert color strings to SolidColorBrush for DataGrid binding.
/// </summary>
public class StringToBrushConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string colorString)
        {
            try
            {
                return new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorString));
            }
            catch
            {
                return System.Windows.Media.Brushes.Gray;
            }
        }
        return System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
