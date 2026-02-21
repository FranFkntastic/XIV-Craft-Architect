using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using FFXIV_Craft_Architect.Models;
using FFXIV_Craft_Architect.Services;
using FFXIV_Craft_Architect.Services.Interfaces;
using Microsoft.Win32;

namespace FFXIV_Craft_Architect;

/// <summary>
/// Window for visualizing real-time market data population status.
/// Shows each item's fetch/cache status: Pending, NoCache, Fetching, Success, Failed, or Cached.
/// </summary>
public partial class MarketDataStatusWindow : Window
{
    private readonly MarketDataStatusSession _session;
    private ICollectionView? _filteredView;
    private readonly IDialogService _dialogs;

    /// <summary>
    /// Event raised when the user requests a fresh fetch of market data.
    /// </summary>
    public event EventHandler? RefreshMarketDataRequested;

    /// <summary>
    /// Event raised when the user requests cache inspection without fetching.
    /// </summary>
    public event EventHandler? CacheCheckRequested;

    public MarketDataStatusWindow(DialogServiceFactory dialogFactory, MarketDataStatusSession session)
    {
        InitializeComponent();
        _session = session;
        _dialogs = dialogFactory.CreateForWindow(this);
        SetupDataGrid();
    }

    private void SetupDataGrid()
    {
        _filteredView = CollectionViewSource.GetDefaultView(_session.Items);
        StatusDataGrid.ItemsSource = _filteredView;
        UpdateStats();
    }

    public void RefreshView()
    {
        Dispatcher.Invoke(UpdateStats);
    }

    private void UpdateStats()
    {
        var pending = _session.PendingCount;
        var noCache = _session.NoCacheCount;
        var fetching = _session.FetchingCount;
        var success = _session.SuccessCount;
        var failed = _session.FailedCount;
        var skipped = _session.SkippedCount;
        var cached = _session.CachedCount;
        var total = _session.TotalCount;
        var completed = _session.CompletedCount;

        PendingCount.Text = $"⏳ Pending: {pending}";
        NoCacheCount.Text = $"∅ No Cache: {noCache}";
        FetchingCount.Text = $"🔄 Fetching: {fetching}";
        SuccessCount.Text = $"✓ Success: {success}";
        FailedCount.Text = $"✗ Failed: {failed}";
        SkippedCount.Text = $"↷ Skipped: {skipped}";
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
            _filteredView.Filter = obj => obj is MarketDataStatusItem item && item.Status == MarketDataFetchStatus.Pending;
        }
    }

    private void OnFilterFetching(object sender, RoutedEventArgs e)
    {
        if (_filteredView != null)
        {
            _filteredView.Filter = obj => obj is MarketDataStatusItem item && item.Status == MarketDataFetchStatus.Fetching;
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

    private void OnRefreshMarketData(object sender, RoutedEventArgs e)
    {
        // Raise event to notify MainWindow to perform a force refresh
        RefreshMarketDataRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCheckCache(object sender, RoutedEventArgs e)
    {
        CacheCheckRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnExportCsv(object sender, RoutedEventArgs e)
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
                    "ItemId,ItemName,Quantity,Status,UnitPrice,DataScope,RetrievalSource,DataType,Details,ErrorMessage"
                };

                foreach (var item in _session.Items)
                {
                    lines.Add($"{item.ItemId},\"{item.ItemName.Replace("\"", "\"\"")}\",{item.Quantity},{item.Status},{item.UnitPrice},\"{item.DataScopeText.Replace("\"", "\"\"")}\",\"{item.RetrievalSourceText.Replace("\"", "\"\"")}\",\"{item.DataTypeText.Replace("\"", "\"\"")}\",\"{item.SourceDetails.Replace("\"", "\"\"")}\",\"{item.ErrorMessage.Replace("\"", "\"\"")}\"");
                }

                File.WriteAllLines(dialog.FileName, lines);
                await _dialogs.ShowInfoAsync("Export complete!", "Success");
            }
            catch (Exception ex)
            {
                await _dialogs.ShowErrorAsync($"Failed to export: {ex.Message}", ex);
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
