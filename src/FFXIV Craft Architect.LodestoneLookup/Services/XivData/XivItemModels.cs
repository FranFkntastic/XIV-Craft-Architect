namespace FFXIV_Craft_Architect.LodestoneLookup.Services.XivData;

public sealed record XivItemSummary
{
    public uint ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int? IconId { get; init; }
    public string? ItemType { get; init; }
    public bool? CanBeHq { get; init; }
    public bool? IsMarketable { get; init; }
    public uint? StackSize { get; init; }
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset SourceUpdatedAtUtc { get; init; }
}

public sealed record XivItemSearchResponse
{
    public string Query { get; init; } = string.Empty;
    public IReadOnlyList<XivItemSummary> Items { get; init; } = [];
}

public sealed record XivDataErrorResponse
{
    public string ErrorCode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
