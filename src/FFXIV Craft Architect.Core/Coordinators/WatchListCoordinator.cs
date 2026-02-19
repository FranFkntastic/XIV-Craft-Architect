using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Timers;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Timer = System.Timers.Timer;

namespace FFXIV_Craft_Architect.Core.Coordinators;

/// <summary>
/// Coordinates the watch list functionality.
/// Manages a list of items to monitor prices for, with auto-refresh capabilities.
/// </summary>
public class WatchListCoordinator : IWatchListCoordinator, IDisposable
{
    private readonly IPriceRefreshCoordinator _priceRefreshCoordinator;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<WatchListCoordinator> _logger;

    private readonly ObservableCollection<WatchItem> _watchedItems;
    private readonly Timer _refreshTimer;
    private readonly object _refreshLock = new();
    private readonly string _stateFilePath;

    private bool _isRefreshing;
    private bool _isDisposed;

    /// <inheritdoc />
    public IReadOnlyList<WatchItem> Items => _watchedItems;

    /// <inheritdoc />
    public bool IsAutoRefreshActive => _refreshTimer.Enabled;

    /// <inheritdoc />
    public event EventHandler<WatchItemChangedEventArgs>? ItemChanged;

    /// <inheritdoc />
    public event EventHandler<WatchRefreshCompletedEventArgs>? RefreshCompleted;

    /// <inheritdoc />
    public event EventHandler? ItemsChanged;

    /// <inheritdoc />
    public event EventHandler<bool>? AutoRefreshStateChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchListCoordinator"/> class.
    /// </summary>
    /// <param name="priceRefreshCoordinator">Service for refreshing prices.</param>
    /// <param name="settingsService">Service for accessing application settings.</param>
    /// <param name="logger">Logger instance.</param>
    public WatchListCoordinator(
        IPriceRefreshCoordinator priceRefreshCoordinator,
        ISettingsService settingsService,
        ILogger<WatchListCoordinator> logger)
    {
        _priceRefreshCoordinator = priceRefreshCoordinator;
        _settingsService = settingsService;
        _logger = logger;

        _watchedItems = new ObservableCollection<WatchItem>();
        _watchedItems.CollectionChanged += (s, e) => ItemsChanged?.Invoke(this, EventArgs.Empty);

        _refreshTimer = new Timer();
        _refreshTimer.Elapsed += OnRefreshTimerElapsed;
        _refreshTimer.AutoReset = true;
        _refreshTimer.Enabled = false;

        _stateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FFXIV_Craft_Architect",
            "watchlist.json");

        // Ensure directory exists
        var directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _logger.LogInformation("[WatchList] Initialized with state file: {StateFilePath}", _stateFilePath);
    }

    /// <inheritdoc />
    public bool AddItem(int itemId, string name, string worldOrDc)
    {
        if (_watchedItems.Any(i => i.ItemId == itemId))
        {
            _logger.LogDebug("[WatchList] Item {ItemId} ({Name}) already in watch list", itemId, name);
            return false;
        }

        var item = new WatchItem
        {
            ItemId = itemId,
            Name = name,
            WorldOrDc = worldOrDc,
            State = WatchItemState.Idle,
            LastRefreshed = DateTimeOffset.MinValue
        };

        _watchedItems.Add(item);
        _logger.LogInformation("[WatchList] Added item {ItemId} ({Name}) for {WorldOrDc}", itemId, name, worldOrDc);

        return true;
    }

    /// <inheritdoc />
    public bool RemoveItem(int itemId)
    {
        var item = _watchedItems.FirstOrDefault(i => i.ItemId == itemId);
        if (item == null)
        {
            return false;
        }

        _watchedItems.Remove(item);
        _logger.LogInformation("[WatchList] Removed item {ItemId} ({Name})", itemId, item.Name);

        return true;
    }

    /// <inheritdoc />
    public void ClearItems()
    {
        var count = _watchedItems.Count;
        _watchedItems.Clear();
        _logger.LogInformation("[WatchList] Cleared {Count} items from watch list", count);
    }

    /// <inheritdoc />
    public bool IsWatching(int itemId)
    {
        return _watchedItems.Any(i => i.ItemId == itemId);
    }

    /// <inheritdoc />
    public async Task<WatchRefreshResult> RefreshAllAsync()
    {
        if (_watchedItems.Count == 0)
        {
            return WatchRefreshResult.SuccessResult(0, 0);
        }

        lock (_refreshLock)
        {
            if (_isRefreshing)
            {
                _logger.LogWarning("[WatchList] Refresh already in progress, skipping");
                return WatchRefreshResult.FailedResult("Refresh already in progress", _watchedItems.Count);
            }
            _isRefreshing = true;
        }

        try
        {
            _logger.LogInformation("[WatchList] Starting refresh for {Count} items", _watchedItems.Count);

            int successCount = 0;
            int failedCount = 0;

            foreach (var item in _watchedItems)
            {
                var result = await RefreshSingleItemInternalAsync(item);
                if (result)
                {
                    successCount++;
                }
                else
                {
                    failedCount++;
                }
            }

            var refreshResult = successCount == _watchedItems.Count
                ? WatchRefreshResult.SuccessResult(successCount, _watchedItems.Count)
                : WatchRefreshResult.PartialResult(successCount, failedCount, _watchedItems.Count);

            RefreshCompleted?.Invoke(this, new WatchRefreshCompletedEventArgs(successCount, failedCount, _watchedItems.Count));

            _logger.LogInformation("[WatchList] Refresh completed: {Success} success, {Failed} failed", successCount, failedCount);

            return refreshResult;
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <inheritdoc />
    public async Task<WatchRefreshResult> RefreshItemAsync(int itemId)
    {
        var item = _watchedItems.FirstOrDefault(i => i.ItemId == itemId);
        if (item == null)
        {
            return WatchRefreshResult.FailedResult($"Item {itemId} not found in watch list", 0);
        }

        lock (_refreshLock)
        {
            if (_isRefreshing)
            {
                return WatchRefreshResult.FailedResult("Refresh already in progress", 1);
            }
            _isRefreshing = true;
        }

        try
        {
            var success = await RefreshSingleItemInternalAsync(item);

            var refreshResult = success
                ? WatchRefreshResult.SuccessResult(1, 1)
                : WatchRefreshResult.PartialResult(0, 1, 1);

            RefreshCompleted?.Invoke(this, new WatchRefreshCompletedEventArgs(success ? 1 : 0, success ? 0 : 1, 1));

            return refreshResult;
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>
    /// Refreshes a single item's price.
    /// </summary>
    /// <param name="item">The watch item to refresh.</param>
    /// <returns>True if refresh succeeded, false otherwise.</returns>
    private async Task<bool> RefreshSingleItemInternalAsync(WatchItem item)
    {
        item.State = WatchItemState.Refreshing;
        item.ErrorMessage = null;

        try
        {
            var result = await _priceRefreshCoordinator.RefreshItemAsync(
                item.ItemId,
                item.Name,
                item.WorldOrDc);

            if (result.Status == PriceRefreshStatus.Success && result.Prices.TryGetValue(item.ItemId, out var priceInfo))
            {
                var oldPrice = item.UnitPrice;
                item.UnitPrice = priceInfo.UnitPrice;
                item.PreviousPrice = oldPrice;
                item.Source = priceInfo.Source;
                item.LastRefreshed = DateTimeOffset.UtcNow;
                item.State = WatchItemState.Updated;
                item.ErrorMessage = null;

                // Fire event if price changed
                if (oldPrice != item.UnitPrice)
                {
                    ItemChanged?.Invoke(this, new WatchItemChangedEventArgs(item, oldPrice, item.UnitPrice));
                }

                return true;
            }
            else
            {
                item.State = WatchItemState.Failed;
                item.ErrorMessage = result.Message;
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WatchList] Failed to refresh item {ItemId}", item.ItemId);
            item.State = WatchItemState.Failed;
            item.ErrorMessage = ex.Message;
            return false;
        }
    }

    /// <inheritdoc />
    public void StartAutoRefresh(int intervalMinutes)
    {
        if (intervalMinutes < 1)
        {
            throw new ArgumentException("Interval must be at least 1 minute", nameof(intervalMinutes));
        }

        _refreshTimer.Interval = intervalMinutes * 60 * 1000; // Convert to milliseconds
        _refreshTimer.Enabled = true;

        _logger.LogInformation("[WatchList] Auto-refresh started with {Interval} minute interval", intervalMinutes);
        AutoRefreshStateChanged?.Invoke(this, true);
    }

    /// <inheritdoc />
    public void StopAutoRefresh()
    {
        _refreshTimer.Enabled = false;
        _logger.LogInformation("[WatchList] Auto-refresh stopped");
        AutoRefreshStateChanged?.Invoke(this, false);
    }

    /// <summary>
    /// Handles the refresh timer elapsed event.
    /// </summary>
    private async void OnRefreshTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WatchList] Auto-refresh failed");
        }
    }

    /// <inheritdoc />
    public async Task LoadStateAsync()
    {
        if (!File.Exists(_stateFilePath))
        {
            _logger.LogDebug("[WatchList] No state file found at {StateFilePath}", _stateFilePath);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_stateFilePath);
            var state = JsonSerializer.Deserialize<WatchListState>(json, GetJsonOptions());

            if (state == null)
            {
                _logger.LogWarning("[WatchList] Failed to deserialize state file");
                return;
            }

            _watchedItems.Clear();

            foreach (var itemData in state.Items)
            {
                var item = new WatchItem
                {
                    ItemId = itemData.ItemId,
                    Name = itemData.Name,
                    UnitPrice = itemData.UnitPrice,
                    WorldOrDc = itemData.WorldOrDc,
                    LastRefreshed = itemData.LastRefreshed,
                    Source = itemData.Source,
                    State = WatchItemState.Idle
                };
                _watchedItems.Add(item);
            }

            // Restore auto-refresh if it was enabled
            if (state.AutoRefreshEnabled && state.AutoRefreshIntervalMinutes > 0)
            {
                StartAutoRefresh(state.AutoRefreshIntervalMinutes);
            }

            _logger.LogInformation("[WatchList] Loaded {Count} items from state file", _watchedItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WatchList] Failed to load state from {StateFilePath}", _stateFilePath);
        }
    }

    /// <inheritdoc />
    public async Task SaveStateAsync()
    {
        try
        {
            var state = new WatchListState
            {
                SavedAt = DateTimeOffset.UtcNow,
                AutoRefreshEnabled = _refreshTimer.Enabled,
                AutoRefreshIntervalMinutes = (int)(_refreshTimer.Interval / 60 / 1000),
                Items = _watchedItems.Select(i => new WatchItemStateData
                {
                    ItemId = i.ItemId,
                    Name = i.Name,
                    UnitPrice = i.UnitPrice,
                    WorldOrDc = i.WorldOrDc,
                    LastRefreshed = i.LastRefreshed,
                    Source = i.Source
                }).ToList()
            };

            var json = JsonSerializer.Serialize(state, GetJsonOptions());
            await File.WriteAllTextAsync(_stateFilePath, json);

            _logger.LogDebug("[WatchList] Saved {Count} items to state file", state.Items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WatchList] Failed to save state to {StateFilePath}", _stateFilePath);
        }
    }

    /// <summary>
    /// Gets JSON serialization options.
    /// </summary>
    private static JsonSerializerOptions GetJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Disposes the coordinator resources.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();
        _isDisposed = true;

        GC.SuppressFinalize(this);
    }
}
