using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.XivData;

public sealed class GarlandXivItemDataProvider : IXivItemDataProvider
{
    private const string DefaultItemType = "Item";

    private readonly IGarlandService _garland;

    public GarlandXivItemDataProvider(IGarlandService garland)
    {
        _garland = garland;
    }

    public async Task<IReadOnlyList<XivItemSummary>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var results = await _garland.SearchAsync(query, cancellationToken);
        return results
            .Where(result => result.Id > 0 && result.Object != null)
            .Take(limit)
            .Select(result => new XivItemSummary(
                result.Id,
                result.Object.Name,
                DefaultItemType,
                result.Object.IconId))
            .ToList();
    }

    public async Task<XivItemSummary?> GetItemAsync(
        int itemId,
        CancellationToken cancellationToken)
    {
        var item = await _garland.GetItemAsync(itemId, cancellationToken);
        return item == null
            ? null
            : new XivItemSummary(
                item.Id,
                item.Name,
                DefaultItemType,
                item.IconId);
    }
}
