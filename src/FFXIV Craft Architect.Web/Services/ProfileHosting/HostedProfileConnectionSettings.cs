namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class HostedProfileConnectionSettings
{
    public string? HostUrl { get; set; }
    public string? AccessKey { get; set; }
    public bool RememberAccessKey { get; set; }
    public string? ConnectedProfileId { get; set; }
    public string? ConnectedProfileName { get; set; }
    public long ConnectedProfileMetadataRevision { get; set; }

    public string? ProfileScopeId =>
        Guid.TryParse(ConnectedProfileId, out var profileId) &&
        profileId != Guid.Empty
            ? profileId.ToString("D")
            : null;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(HostUrl) &&
        !string.IsNullOrWhiteSpace(AccessKey) &&
        ProfileScopeId != null;

    public string? ConnectionScopeId => IsConfigured
        ? $"{ProfileHostClient.NormalizeHostUrl(HostUrl!)}|{ProfileScopeId}"
        : null;

    public HostedProfileConnectionSettings Snapshot() =>
        new()
        {
            HostUrl = HostUrl,
            AccessKey = AccessKey,
            RememberAccessKey = RememberAccessKey,
            ConnectedProfileId = ProfileScopeId,
            ConnectedProfileName = ConnectedProfileName,
            ConnectedProfileMetadataRevision = ConnectedProfileMetadataRevision
        };
}

public enum ConnectedProfileNameSaveResult
{
    ConnectionChanged = 0,
    Saved = 1,
    Stale = 2
}
