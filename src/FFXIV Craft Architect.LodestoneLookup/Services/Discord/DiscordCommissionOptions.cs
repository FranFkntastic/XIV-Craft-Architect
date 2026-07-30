namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed class DiscordCommissionOptions
{
    public bool Enabled { get; init; }
    public string ApplicationId { get; init; } = string.Empty;
    public string PublicKey { get; init; } = string.Empty;
    public string BotToken { get; init; } = string.Empty;
    public string AllowedGuildId { get; init; } = string.Empty;
    public string AllowedChannelId { get; init; } = string.Empty;
    public string CommissionBaseUrl { get; init; } = string.Empty;
    public string ApiBaseUrl { get; init; } = "https://discord.com/api/v10/";
    public string DatabasePath { get; init; } = Path.Combine(
        AppContext.BaseDirectory,
        "discord-collaboration.db");
    public int OutboxMaximumAttempts { get; init; } = 5;
    public TimeSpan OutboxLeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan OutboxPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public bool CanVerifyInteractions =>
        Enabled &&
        PublicKey.Length == 64;

    public bool IsConfigured =>
        CanVerifyInteractions &&
        !string.IsNullOrWhiteSpace(AllowedGuildId) &&
        !string.IsNullOrWhiteSpace(AllowedChannelId) &&
        Uri.TryCreate(CommissionBaseUrl, UriKind.Absolute, out _);

    public bool CanPublishDirectly =>
        Enabled &&
        !string.IsNullOrWhiteSpace(BotToken) &&
        Uri.TryCreate(CommissionBaseUrl, UriKind.Absolute, out _) &&
        Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out _);
}
