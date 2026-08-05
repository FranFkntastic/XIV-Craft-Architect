using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

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
    public void GetDataCenters_IncludesEverySelectedComparisonRegion()
    {
        var dataCenters = MarketFetchScopeResolver.GetDataCenters(MarketFetchScope.EntireRegion, "Aether", "North America", ["Europe", "Japan", "Oceania"]);
        Assert.Equal(["Aether", "Primal", "Crystal", "Dynamis", "Chaos", "Light", "Elemental", "Gaia", "Mana", "Meteor", "Materia"], dataCenters);
        Assert.Equal(["North America", "Japan", "Europe", "Oceania"], MarketFetchScopeResolver.NormalizeSelectedRegions("North America", ["Japan", "Europe", "Japan", "Unknown", "Oceania"]));
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

    [Fact]
    public void RequestedOrderDraftCapturesTheCurrentRegionalMarketContext()
    {
        var createdAt = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        var requestedDataCenters = new[] { "Aether", "Primal", "Crystal", "Dynamis" };
        var result = new TradeOrderDraftFactory().CreateFromRequestedOutputs(
            new TradeRequestedOrderCreateRequest(
                Guid.NewGuid(),
                AssignedCrafterId: null,
                Title: null,
                Outputs: [new TradeRequestedOrderOutput(2, "Fire Shard", 10, false, 0)],
                DataCenter: "Aether",
                Region: "North America",
                MarketFetchScope: MarketFetchScope.EntireRegion,
                RequestedDataCenters: requestedDataCenters,
                World: null,
                Notes: null,
                CreatedAtUtc: createdAt));

        Assert.True(result.CanCreate);
        Assert.NotNull(result.Order);
        Assert.Equal(MarketFetchScope.EntireRegion, result.Order.SourceSnapshot.MarketFetchScope);
        Assert.Equal("North America", result.Order.SourceSnapshot.Region);
        Assert.Equal("Aether", result.Order.SourceSnapshot.DataCenter);
        Assert.Equal(requestedDataCenters, result.Order.SourceSnapshot.RequestedDataCenters);
    }
    [Fact]
    public void MarketConsumersUseWorkerContextWithoutIndependentScopeSettings()
    {
        var web = Path.Combine(LocateRepositoryRoot(), "src", "FFXIV Craft Architect.Web");
        var sources = new[]
        {
            Path.Combine(web, "Pages", "MarketAnalysis.razor"),
            Path.Combine(web, "Pages", "ProcurementPlan.razor"),
            Path.Combine(web, "Services", "WebSettingsService.cs"),
            Path.Combine(web, "Services", "ProfileHosting", "ProfileSyncLocalStateService.cs")
        }.Select(File.ReadAllText);

        foreach (var source in sources)
        {
            Assert.DoesNotContain("market.search_entire_region", source, StringComparison.Ordinal);
            Assert.DoesNotContain("procurement.search_entire_region", source, StringComparison.Ordinal);
        }

        var pricing = File.ReadAllText(Path.Combine(web, "Services", "TradeOrderPricingWorkflowService.cs"));
        var lifecycle = File.ReadAllText(Path.Combine(web, "Services", "PlanLifecycleWorkflowService.cs"));
        var procurement = File.ReadAllText(Path.Combine(web, "Pages", "ProcurementPlan.razor"));
        var tradeProcurement = File.ReadAllText(Path.Combine(web, "Pages", "TradeOrders.Procurement.cs"));

        Assert.Contains("_viewSettings.DefaultMarketFetchScope", pricing, StringComparison.Ordinal);
        Assert.Contains("useCurrentSettingsContext: true", pricing, StringComparison.Ordinal);
        Assert.DoesNotContain("order.SourceSnapshot.MarketFetchScope ??", pricing, StringComparison.Ordinal);
        Assert.Contains("var requestedScope = marketScope;", lifecycle, StringComparison.Ordinal);
        Assert.Contains("_route.Scope", procurement, StringComparison.Ordinal);
        Assert.Contains("MarketFetchScope.EntireRegion", tradeProcurement, StringComparison.Ordinal);
        Assert.Contains("$\"{order.SourceSnapshot.Region} region\"", tradeProcurement, StringComparison.Ordinal);

        var options = File.ReadAllText(Path.Combine(web, "Dialogs", "OptionsDialog.razor")); var controls = File.ReadAllText(Path.Combine(web, "Shared", "MarketAnalysisControlsPanel.razor"));
        Assert.Contains("CompactMultiSelectField", options, StringComparison.Ordinal);
        Assert.Contains("market.comparison_regions", options, StringComparison.Ordinal);
        Assert.DoesNotContain("Search entire region", controls, StringComparison.Ordinal);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
