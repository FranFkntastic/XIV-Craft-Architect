namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class HostedProfileConnectionSettings
{
    public string? HostUrl { get; set; }
    public string? AccessKey { get; set; }
    public bool RememberAccessKey { get; set; }
    public string? ConnectedProfileId { get; set; }
    public string? ConnectedProfileName { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(HostUrl) &&
        !string.IsNullOrWhiteSpace(AccessKey);
}
