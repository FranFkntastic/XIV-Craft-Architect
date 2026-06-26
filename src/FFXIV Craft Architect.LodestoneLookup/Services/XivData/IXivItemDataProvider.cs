namespace FFXIV_Craft_Architect.LodestoneLookup.Services.XivData;

public interface IXivItemDataProvider
{
    Task<IReadOnlyList<XivItemSummary>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);

    Task<XivItemSummary?> GetItemAsync(
        int itemId,
        CancellationToken cancellationToken);
}
