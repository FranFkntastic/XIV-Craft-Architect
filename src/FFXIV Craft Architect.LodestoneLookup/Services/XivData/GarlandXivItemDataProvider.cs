using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.XivData;

public sealed class GarlandXivItemDataProvider : IXivItemDataProvider
{
    private const string SourceName = "garland";
    private readonly IGarlandService garlandService;

    public GarlandXivItemDataProvider(IGarlandService garlandService)
    {
        this.garlandService = garlandService;
    }

    public async Task<IReadOnlyList<XivItemSummary>> SearchItemsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Item search query is required.", nameof(query));

        var clampedLimit = Math.Clamp(limit, 1, 50);
        var results = await garlandService.SearchAsync(query.Trim(), cancellationToken).ConfigureAwait(false);
        var fetchedAtUtc = DateTimeOffset.UtcNow;

        return results
            .Where(IsUsableItemResult)
            .Take(clampedLimit)
            .Select(result => new XivItemSummary
            {
                ItemId = (uint)result.Id,
                Name = result.Object.Name.Trim(),
                Source = SourceName,
                SourceUpdatedAtUtc = fetchedAtUtc,
            })
            .ToList();
    }

    public async Task<XivItemSummary?> GetItemAsync(
        uint itemId,
        CancellationToken cancellationToken)
    {
        if (itemId == 0)
            throw new ArgumentException("Item id is required.", nameof(itemId));

        var item = await garlandService.GetItemAsync((int)itemId, cancellationToken).ConfigureAwait(false);
        return item == null || item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name)
            ? null
            : MapItem(item, DateTimeOffset.UtcNow);
    }

    private static bool IsUsableItemResult(GarlandSearchResult result) =>
        result.Type == "item" &&
        result.Id > 0 &&
        result.Object != null &&
        !string.IsNullOrWhiteSpace(result.Object.Name);

    private static XivItemSummary MapItem(GarlandItem item, DateTimeOffset fetchedAtUtc) =>
        new()
        {
            ItemId = (uint)item.Id,
            Name = item.Name.Trim(),
            IconId = item.IconId == 0 ? null : item.IconId,
            IsMarketable = item.CanListOnMarket,
            Source = SourceName,
            SourceUpdatedAtUtc = fetchedAtUtc,
        };
}
