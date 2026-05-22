using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FFXIV_Craft_Architect.Models;

internal static class MarketDataStatusColorHex
{
    internal const string Pending = "#888888";
    internal const string NoCache = "#f59e0b";
    internal const string Fetching = "#4ecdc4";
    internal const string Success = "#4ade80";
    internal const string Failed = "#f87171";
    internal const string Skipped = "#94a3b8";
    internal const string Cached = "#fbbf24";
}

/// <summary>
/// Represents the status of a single item's market data fetch operation.
/// Used for real-time visualization in the Market Data Status window.
/// </summary>
public class MarketDataStatusItem : INotifyPropertyChanged
{
    private MarketDataFetchStatus _status = MarketDataFetchStatus.Pending;
    private decimal _unitPrice;
    private string _errorMessage = "";
    private string _sourceDetails = "";
    private string _dataScopeText = "";
    private string _retrievalSourceText = "";
    private string _dataTypeText = "";

    public int ItemId { get; set; }
    public string ItemName { get; set; } = "";
    public int Quantity { get; set; }

    public MarketDataFetchStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
        }
    }

    public decimal UnitPrice
    {
        get => _unitPrice;
        set
        {
            _unitPrice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PriceDisplay));
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            _errorMessage = value;
            OnPropertyChanged();
        }
    }

    public string SourceDetails
    {
        get => _sourceDetails;
        set
        {
            _sourceDetails = value;
            OnPropertyChanged();
        }
    }

    public string DataScopeText
    {
        get => _dataScopeText;
        set
        {
            _dataScopeText = value;
            OnPropertyChanged();
        }
    }

    public string RetrievalSourceText
    {
        get => _retrievalSourceText;
        set
        {
            _retrievalSourceText = value;
            OnPropertyChanged();
        }
    }

    public string DataTypeText
    {
        get => _dataTypeText;
        set
        {
            _dataTypeText = value;
            OnPropertyChanged();
        }
    }

    private DateTime _cacheTimestamp;
    
    /// <summary>
    /// When this price was last fetched from the API.
    /// </summary>
    public DateTime CacheTimestamp
    {
        get => _cacheTimestamp;
        set
        {
            _cacheTimestamp = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CacheAgeText));
        }
    }
    
    /// <summary>
    /// Human-readable age of the cached data (e.g., "5m ago", "2h ago").
    /// </summary>
    public string CacheAgeText
    {
        get
        {
            if (CacheTimestamp == default) return "";
            
            var age = DateTime.Now - CacheTimestamp;
            if (age.TotalMinutes < 1)
                return "just now";
            if (age.TotalHours < 1)
                return $"{(int)age.TotalMinutes}m ago";
            if (age.TotalDays < 1)
                return $"{(int)age.TotalHours}h {(int)age.TotalMinutes % 60}m ago";
            return $"{(int)age.TotalDays}d ago";
        }
    }

    public string StatusText => Status switch
    {
        MarketDataFetchStatus.Pending => "⏳ Waiting...",
        MarketDataFetchStatus.NoCache => "∅ No Cache",
        MarketDataFetchStatus.Fetching => "🔄 Fetching...",
        MarketDataFetchStatus.Success => "✓ Success",
        MarketDataFetchStatus.Failed => "✗ Failed",
        MarketDataFetchStatus.Skipped => "↷ Skipped",
        MarketDataFetchStatus.Cached => $"📋 Cached ({CacheAgeText})",
        _ => "Unknown"
    };

    public string StatusColor => Status switch
    {
        MarketDataFetchStatus.Pending => MarketDataStatusColorHex.Pending,
        MarketDataFetchStatus.NoCache => MarketDataStatusColorHex.NoCache,
        MarketDataFetchStatus.Fetching => MarketDataStatusColorHex.Fetching,
        MarketDataFetchStatus.Success => MarketDataStatusColorHex.Success,
        MarketDataFetchStatus.Failed => MarketDataStatusColorHex.Failed,
        MarketDataFetchStatus.Skipped => MarketDataStatusColorHex.Skipped,
        MarketDataFetchStatus.Cached => MarketDataStatusColorHex.Cached,
        _ => MarketDataStatusColorHex.Pending
    };

    public string PriceDisplay => UnitPrice > 0 ? $"{UnitPrice:N0}g" : "-";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum MarketDataFetchStatus
{
    Pending,
    NoCache,
    Fetching,
    Success,
    Failed,
    Skipped,
    Cached
}
