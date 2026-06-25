namespace FFXIV_Craft_Architect.LodestoneLookup.Services.XivData;

public interface IXivItemDataProvider
{
    Task<IReadOnlyList<XivItemSummary>> SearchItemsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);

    Task<XivItemSummary?> GetItemAsync(
        uint itemId,
        CancellationToken cancellationToken);
}
