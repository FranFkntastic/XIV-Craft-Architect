namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed class DiscordCommissionOptions
{
    public bool Enabled { get; init; }
    public string PublicKey { get; init; } = string.Empty;
    public string AllowedGuildId { get; init; } = string.Empty;
    public string AllowedChannelId { get; init; } = string.Empty;
    public string CommissionBaseUrl { get; init; } = string.Empty;

    public bool CanVerifyInteractions =>
        Enabled &&
        PublicKey.Length == 64;

    public bool IsConfigured =>
        CanVerifyInteractions &&
        !string.IsNullOrWhiteSpace(AllowedGuildId) &&
        !string.IsNullOrWhiteSpace(AllowedChannelId) &&
        Uri.TryCreate(CommissionBaseUrl, UriKind.Absolute, out _);
}
