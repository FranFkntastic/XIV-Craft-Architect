namespace FFXIV_Craft_Architect.Core.Models;

public sealed record LodestoneCrafterSearchRequest(
    string CharacterName,
    string? WorldName,
    string? DataCenter,
    string? Region = null);

public sealed record LodestoneCrafterSearchCandidate(
    string LodestoneCharacterId,
    string DisplayName,
    string? WorldName,
    string? DataCenter,
    string LodestoneProfileUrl);

public sealed record LodestoneCrafterImportPreview(
    string LodestoneCharacterId,
    string DisplayName,
    string? WorldName,
    string? DataCenter,
    string LodestoneProfileUrl,
    string? AvatarUrl,
    string? PortraitUrl,
    string? FreeCompanyName,
    string? Race,
    string? Clan,
    string? Gender,
    DateTime RetrievedAtUtc,
    IReadOnlyList<TradeCraftingJobLevel> JobLevels);

public enum LodestoneCrafterLookupFailureKind
{
    None,
    InvalidRequest,
    NotFound,
    NetworkUnavailable,
    BrowserCorsBlocked,
    ParseFailed,
    Unknown
}

public sealed class LodestoneCrafterLookupResult<T>
{
    public T? Value { get; init; }
    public LodestoneCrafterLookupFailureKind FailureKind { get; init; }
    public string? ErrorMessage { get; init; }
    public bool Succeeded => FailureKind == LodestoneCrafterLookupFailureKind.None;

    public static LodestoneCrafterLookupResult<T> Success(T value)
    {
        return new LodestoneCrafterLookupResult<T>
        {
            Value = value,
            FailureKind = LodestoneCrafterLookupFailureKind.None
        };
    }

    public static LodestoneCrafterLookupResult<T> Failure(
        LodestoneCrafterLookupFailureKind failureKind,
        string errorMessage)
    {
        return new LodestoneCrafterLookupResult<T>
        {
            FailureKind = failureKind,
            ErrorMessage = errorMessage
        };
    }
}
