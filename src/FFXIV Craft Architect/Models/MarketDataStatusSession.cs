using System.Collections.ObjectModel;

namespace FFXIV_Craft_Architect.Models;

/// <summary>
/// Session-scoped store for market data population status.
/// Lives outside the status window so closing/reopening the window does not reset data.
/// </summary>
public class MarketDataStatusSession
{
    public ObservableCollection<MarketDataStatusItem> Items { get; } = new();

    public int PendingCount => Items.Count(i => i.Status == MarketDataFetchStatus.Pending);
    public int NoCacheCount => Items.Count(i => i.Status == MarketDataFetchStatus.NoCache);
    public int FetchingCount => Items.Count(i => i.Status == MarketDataFetchStatus.Fetching);
    public int SuccessCount => Items.Count(i => i.Status == MarketDataFetchStatus.Success);
    public int FailedCount => Items.Count(i => i.Status == MarketDataFetchStatus.Failed);
    public int SkippedCount => Items.Count(i => i.Status == MarketDataFetchStatus.Skipped);
    public int CachedCount => Items.Count(i => i.Status == MarketDataFetchStatus.Cached);
    public int TotalCount => Items.Count;
    public int CompletedCount => SuccessCount + FailedCount + SkippedCount + CachedCount + NoCacheCount;

    public void InitializeItems(IEnumerable<(int itemId, string name, int quantity)> items)
    {
        Items.Clear();

        foreach (var (itemId, name, quantity) in items)
        {
            Items.Add(new MarketDataStatusItem
            {
                ItemId = itemId,
                ItemName = name,
                Quantity = quantity,
                Status = MarketDataFetchStatus.Pending,
                DataScopeText = "-",
                RetrievalSourceText = "Pending",
                DataTypeText = "Pending"
            });
        }
    }

    public void SetItemFetching(int itemId)
    {
        UpdateItemStatus(itemId, MarketDataFetchStatus.Fetching, retrievalSourceText: "Resolving", dataTypeText: "Resolving");
    }

    public void SetItemNoCache(int itemId, string sourceDetails)
    {
        UpdateItemStatus(
            itemId,
            MarketDataFetchStatus.NoCache,
            sourceDetails: sourceDetails,
            dataScopeText: "-",
            retrievalSourceText: "Cache Check",
            dataTypeText: "No Cache");
    }

    public void SetItemSuccess(int itemId, decimal price, string sourceDetails, string dataScopeText, string retrievalSourceText, string dataTypeText)
    {
        UpdateItemStatus(itemId, MarketDataFetchStatus.Success, price, sourceDetails, dataScopeText: dataScopeText, retrievalSourceText: retrievalSourceText, dataTypeText: dataTypeText);

        var item = Items.FirstOrDefault(i => i.ItemId == itemId);
        if (item != null)
        {
            item.CacheTimestamp = DateTime.Now;
        }
    }

    public void SetItemFailed(int itemId, string errorMessage, string dataTypeText = "Unknown")
    {
        UpdateItemStatus(itemId, MarketDataFetchStatus.Failed, errorMessage: errorMessage, retrievalSourceText: "N/A", dataTypeText: dataTypeText);
    }

    public void SetItemSkipped(int itemId, string reason)
    {
        UpdateItemStatus(itemId, MarketDataFetchStatus.Skipped, sourceDetails: reason, dataScopeText: "N/A", retrievalSourceText: "N/A", dataTypeText: "Skipped");
    }

    public void SetItemCached(int itemId, decimal price, string sourceDetails, string dataScopeText, string dataTypeText, DateTime? fetchedAt = null)
    {
        UpdateItemStatus(itemId, MarketDataFetchStatus.Cached, price, sourceDetails, dataScopeText: dataScopeText, retrievalSourceText: "Cache", dataTypeText: dataTypeText);

        var item = Items.FirstOrDefault(i => i.ItemId == itemId);
        if (item != null)
        {
            item.CacheTimestamp = fetchedAt?.ToLocalTime() ?? DateTime.Now;
        }
    }

    private void UpdateItemStatus(
        int itemId,
        MarketDataFetchStatus status,
        decimal price = 0,
        string sourceDetails = "",
        string errorMessage = "",
        string dataScopeText = "",
        string retrievalSourceText = "",
        string dataTypeText = "")
    {
        var item = Items.FirstOrDefault(i => i.ItemId == itemId);
        if (item == null)
        {
            return;
        }

        item.Status = status;
        if (price > 0)
        {
            item.UnitPrice = price;
        }

        if (!string.IsNullOrEmpty(sourceDetails))
        {
            item.SourceDetails = sourceDetails;
        }

        if (!string.IsNullOrEmpty(errorMessage))
        {
            item.ErrorMessage = errorMessage;
        }

        if (!string.IsNullOrEmpty(dataScopeText))
        {
            item.DataScopeText = dataScopeText;
        }

        if (!string.IsNullOrEmpty(retrievalSourceText))
        {
            item.RetrievalSourceText = retrievalSourceText;
        }

        if (!string.IsNullOrEmpty(dataTypeText))
        {
            item.DataTypeText = dataTypeText;
        }
    }
}
