using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Coordinators;

/// <summary>
/// State of a watched item.
/// </summary>
public enum WatchItemState
{
    Idle,
    Refreshing,
    Updated,
    Failed
}

/// <summary>
/// Represents an item in the watch list for price monitoring.
/// </summary>
public class WatchItem
{
    /// <summary>
    /// Unique identifier for the item.
    /// </summary>
    public int ItemId { get; set; }

    /// <summary>
    /// Name of the item.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Current unit price of the item.
    /// </summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Previous unit price for tracking changes.
    /// </summary>
    public decimal PreviousPrice { get; set; }

    /// <summary>
    /// World or data center where the price was fetched.
    /// </summary>
    public string WorldOrDc { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of the last refresh.
    /// </summary>
    public DateTimeOffset LastRefreshed { get; set; }

    /// <summary>
    /// Current state of the watch item.
    /// </summary>
    public WatchItemState State { get; set; } = WatchItemState.Idle;

    /// <summary>
    /// Error message if the last refresh failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Price source (Market, Vendor, etc.).
    /// </summary>
    public PriceSource Source { get; set; } = PriceSource.Unknown;
}

/// <summary>
/// Serializable state for the watch list.
/// Used for persisting watch list across sessions.
/// </summary>
public class WatchListState
{
    /// <summary>
    /// List of watched items.
    /// </summary>
    public List<WatchItemStateData> Items { get; set; } = new();

    /// <summary>
    /// Timestamp when state was saved.
    /// </summary>
    public DateTimeOffset SavedAt { get; set; }

    /// <summary>
    /// Auto-refresh interval in minutes.
    /// </summary>
    public int AutoRefreshIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Whether auto-refresh is enabled.
    /// </summary>
    public bool AutoRefreshEnabled { get; set; }
}

/// <summary>
/// Serializable data for a single watch item.
/// </summary>
public class WatchItemStateData
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public string WorldOrDc { get; set; } = string.Empty;
    public DateTimeOffset LastRefreshed { get; set; }
    public PriceSource Source { get; set; }
}

/// <summary>
/// Event arguments for when a watch item's price changes.
/// </summary>
public class WatchItemChangedEventArgs : EventArgs
{
    /// <summary>
    /// The watch item that changed.
    /// </summary>
    public WatchItem Item { get; }

    /// <summary>
    /// The old price before the change.
    /// </summary>
    public decimal OldPrice { get; }

    /// <summary>
    /// The new price after the change.
    /// </summary>
    public decimal NewPrice { get; }

    /// <summary>
    /// Price change delta (positive = price increased, negative = price decreased).
    /// </summary>
    public decimal PriceDelta => NewPrice - OldPrice;

    /// <summary>
    /// Whether the price increased.
    /// </summary>
    public bool PriceIncreased => NewPrice > OldPrice;

    /// <summary>
    /// Whether the price decreased.
    /// </summary>
    public bool PriceDecreased => NewPrice < OldPrice;

    public WatchItemChangedEventArgs(WatchItem item, decimal oldPrice, decimal newPrice)
    {
        Item = item;
        OldPrice = oldPrice;
        NewPrice = newPrice;
    }
}

/// <summary>
/// Event arguments for when a watch list refresh completes.
/// </summary>
public class WatchRefreshCompletedEventArgs : EventArgs
{
    /// <summary>
    /// Number of items successfully refreshed.
    /// </summary>
    public int SuccessCount { get; }

    /// <summary>
    /// Number of items that failed to refresh.
    /// </summary>
    public int FailedCount { get; }

    /// <summary>
    /// Total number of items in the watch list.
    /// </summary>
    public int TotalCount { get; }

    /// <summary>
    /// Whether all items were refreshed successfully.
    /// </summary>
    public bool AllSucceeded => FailedCount == 0;

    /// <summary>
    /// Whether any items were refreshed.
    /// </summary>
    public bool AnyRefreshed => SuccessCount > 0;

    public WatchRefreshCompletedEventArgs(int successCount, int failedCount, int totalCount)
    {
        SuccessCount = successCount;
        FailedCount = failedCount;
        TotalCount = totalCount;
    }
}

/// <summary>
/// Defines the contract for coordinating the watch list functionality.
/// Manages a list of items to monitor prices for, with auto-refresh capabilities.
/// </summary>
public interface IWatchListCoordinator
{
    /// <summary>
    /// Collection of watched items.
    /// </summary>
    IReadOnlyList<WatchItem> Items { get; }

    /// <summary>
    /// Event raised when a watch item's price changes.
    /// </summary>
    event EventHandler<WatchItemChangedEventArgs>? ItemChanged;

    /// <summary>
    /// Event raised when the watch list refresh completes.
    /// </summary>
    event EventHandler<WatchRefreshCompletedEventArgs>? RefreshCompleted;

    /// <summary>
    /// Event raised when the watch list changes (items added/removed/cleared).
    /// </summary>
    event EventHandler? ItemsChanged;

    /// <summary>
    /// Event raised when auto-refresh state changes.
    /// </summary>
    event EventHandler<bool>? AutoRefreshStateChanged;

    /// <summary>
    /// Whether auto-refresh is currently active.
    /// </summary>
    bool IsAutoRefreshActive { get; }

    /// <summary>
    /// Adds an item to the watch list.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <param name="name">The item name.</param>
    /// <param name="worldOrDc">The world or data center to monitor.</param>
    /// <returns>True if the item was added, false if already exists.</returns>
    bool AddItem(int itemId, string name, string worldOrDc);

    /// <summary>
    /// Removes an item from the watch list.
    /// </summary>
    /// <param name="itemId">The item ID to remove.</param>
    /// <returns>True if the item was removed, false if not found.</returns>
    bool RemoveItem(int itemId);

    /// <summary>
    /// Clears all items from the watch list.
    /// </summary>
    void ClearItems();

    /// <summary>
    /// Checks if an item is in the watch list.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <returns>True if the item is being watched.</returns>
    bool IsWatching(int itemId);

    /// <summary>
    /// Refreshes prices for all items in the watch list.
    /// </summary>
    /// <returns>Result of the refresh operation.</returns>
    Task<WatchRefreshResult> RefreshAllAsync();

    /// <summary>
    /// Refreshes price for a single item.
    /// </summary>
    /// <param name="itemId">The item ID to refresh.</param>
    /// <returns>Result of the refresh operation.</returns>
    Task<WatchRefreshResult> RefreshItemAsync(int itemId);

    /// <summary>
    /// Starts auto-refresh with the specified interval.
    /// </summary>
    /// <param name="intervalMinutes">Refresh interval in minutes.</param>
    void StartAutoRefresh(int intervalMinutes);

    /// <summary>
    /// Stops auto-refresh.
    /// </summary>
    void StopAutoRefresh();

    /// <summary>
    /// Loads watch list state from persistent storage.
    /// </summary>
    Task LoadStateAsync();

    /// <summary>
    /// Saves watch list state to persistent storage.
    /// </summary>
    Task SaveStateAsync();
}

/// <summary>
/// Result of a watch list refresh operation.
/// </summary>
public class WatchRefreshResult
{
    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public bool Success { get; }

    /// <summary>
    /// Status message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Number of items successfully refreshed.
    /// </summary>
    public int SuccessCount { get; }

    /// <summary>
    /// Number of items that failed to refresh.
    /// </summary>
    public int FailedCount { get; }

    /// <summary>
    /// Total number of items processed.
    /// </summary>
    public int TotalCount { get; }

    public WatchRefreshResult(bool success, string message, int successCount, int failedCount, int totalCount)
    {
        Success = success;
        Message = message;
        SuccessCount = successCount;
        FailedCount = failedCount;
        TotalCount = totalCount;
    }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static WatchRefreshResult SuccessResult(int successCount, int totalCount) =>
        new(true, $"Refreshed {successCount} of {totalCount} items", successCount, 0, totalCount);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static WatchRefreshResult FailedResult(string message, int totalCount) =>
        new(false, message, 0, totalCount, totalCount);

    /// <summary>
    /// Creates a partial success result.
    /// </summary>
    public static WatchRefreshResult PartialResult(int successCount, int failedCount, int totalCount) =>
        new(true, $"Refreshed {successCount} of {totalCount} items ({failedCount} failed)", successCount, failedCount, totalCount);
}
