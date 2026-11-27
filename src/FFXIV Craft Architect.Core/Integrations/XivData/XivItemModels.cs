namespace FFXIV_Craft_Architect.Core.Integrations.XivData;

public sealed record XivItemSummary(
    int ItemId,
    string Name,
    string ItemType,
    int IconId);

public sealed record XivItemSearchResponse(
    IReadOnlyList<XivItemSummary> Items);

public sealed record XivDataErrorResponse(
    string ErrorCode,
    string Message);
