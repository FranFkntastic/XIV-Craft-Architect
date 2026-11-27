namespace FFXIV_Craft_Architect.Core.Integrations.XivData;

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
