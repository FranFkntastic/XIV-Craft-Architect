namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed class TradeMembershipOptions
{
    public string DatabasePath { get; set; } = Path.Combine(
        AppContext.BaseDirectory,
        "trade-memberships.db");
}
