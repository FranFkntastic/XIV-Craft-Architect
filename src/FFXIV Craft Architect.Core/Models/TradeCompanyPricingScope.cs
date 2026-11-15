namespace FFXIV_Craft_Architect.Core.Models;

public sealed record TradeCompanyPricingScope(
    string Region,
    string? DataCenter = null,
    MarketFetchScope MarketFetchScope = MarketFetchScope.EntireRegion)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid =>
        MarketFetchScopeResolver.GetRegions().Contains(Region, StringComparer.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(DataCenter) ||
         MarketFetchScopeResolver.IsDataCenterInRegion(DataCenter, Region)) &&
        (MarketFetchScope == MarketFetchScope.EntireRegion ||
         MarketFetchScope == MarketFetchScope.SelectedDataCenter && !string.IsNullOrWhiteSpace(DataCenter));

    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveDataCenter =>
        MarketFetchScopeResolver.ResolveValidDataCenter(Region, DataCenter);
}
