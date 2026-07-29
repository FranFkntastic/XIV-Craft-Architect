namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed class TradeCompanyOptions
{
    public bool Enabled { get; init; }
    public string DatabasePath { get; init; } =
        Path.Combine(AppContext.BaseDirectory, "trade-company.db");
    public string EnvironmentId { get; init; } = "unconfigured";
    public string ProvisioningKey { get; init; } = string.Empty;

    public bool IsReady =>
        Enabled &&
        !string.IsNullOrWhiteSpace(EnvironmentId) &&
        !string.Equals(EnvironmentId, "unconfigured", StringComparison.OrdinalIgnoreCase);

    public bool CanProvision => IsReady && !string.IsNullOrWhiteSpace(ProvisioningKey);
}
