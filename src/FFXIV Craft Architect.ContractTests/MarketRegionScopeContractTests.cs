using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Tests;

public sealed class MarketRegionScopeContractTests
{
    [Fact]
    public void ResolveValidDataCenter_ChangesInvalidDefaultWithItsRegion()
    {
        Assert.Equal("Chaos", MarketFetchScopeResolver.ResolveValidDataCenter("Europe", "Aether"));
        Assert.Equal("Light", MarketFetchScopeResolver.ResolveValidDataCenter("Europe", "Light"));
    }

    [Fact]
    public void GetDataCenters_CapsAnalysisAtPrimaryPlusOneComparisonRegion()
    {
        var dataCenters = MarketFetchScopeResolver.GetDataCenters(
            MarketFetchScope.EntireRegion,
            "Aether",
            "North America",
            ["Europe", "Japan"]);

        Assert.Equal(
            ["Aether", "Primal", "Crystal", "Dynamis", "Chaos", "Light"],
            dataCenters);
    }

    [Fact]
    public void GetDataCenters_SelectedDataCenterNeverExpandsAcrossRegions()
    {
        var dataCenters = MarketFetchScopeResolver.GetDataCenters(
            MarketFetchScope.SelectedDataCenter,
            "Light",
            "Europe",
            ["North America"]);

        Assert.Equal(["Light"], dataCenters);
    }
}
