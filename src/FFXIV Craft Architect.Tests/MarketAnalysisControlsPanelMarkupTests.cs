namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisControlsPanelMarkupTests
{
    [Fact]
    public void MarketAnalysisControlsPanel_BenchmarkHooks_AreDeveloperModeGated()
    {
        var source = File.ReadAllText(GetMarketAnalysisControlsPanelPath());

        Assert.Contains("BenchmarkHook(", source);
        Assert.Contains("AppState.SecretDebugToolsEnabled ? id : null", source);
        Assert.Contains("data-benchmark-id=\"@BenchmarkHook(\"", source);
        Assert.Contains("market-analysis-run", source);
        Assert.Contains("market-analysis-refresh-prices", source);
    }

    [Fact]
    public void MarketAnalysisControlsPanel_ReplacesSortPreferenceWithEvidenceOverlaySelector()
    {
        var source = File.ReadAllText(GetMarketAnalysisControlsPanelPath());

        Assert.Contains("Evidence Overlay", source);
        Assert.Contains("MarketAnalysisEvidenceOverlay", source);
        Assert.Contains("AppState.MarketAnalysisEvidenceOverlay", source);
        Assert.Contains("Shelf Overlay", source);
        Assert.Contains("Price Band Overlay", source);
        Assert.Contains("SetMarketAnalysisEvidenceOverlay", source);

        Assert.DoesNotContain("<div class=\"ma-field-label\">Sort</div>", source);
        Assert.DoesNotContain("MudSelect T=\"MarketSortOption\"", source);
        Assert.DoesNotContain("By Lens Score", source);
        Assert.DoesNotContain("Alphabetical", source);
    }

    [Fact]
    public void MarketAnalysisControlsPanel_AcquisitionLensOptionsHaveClarifyingTooltips()
    {
        var source = File.ReadAllText(GetMarketAnalysisControlsPanelPath());

        Assert.Contains("Minimum upfront cost ranks worlds by needed-quantity coverage and estimated cost to cover the plan need.", source);
        Assert.Contains("Best value / bulk acquisition favors deeper competitive stock and partial shelves that may still be useful.", source);
        Assert.Contains("Title=\"@LensSelectorTooltip\"", source);
        Assert.Contains("Title=\"@MinimumUpfrontCostLensTooltip\"", source);
        Assert.Contains("Title=\"@BulkValueLensTooltip\"", source);
        Assert.Contains("Minimum Upfront Cost", source);
        Assert.Contains("Best Value / Bulk Acquisition", source);
    }

    private static string GetMarketAnalysisControlsPanelPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(
            directory.FullName,
            "src",
            "FFXIV Craft Architect.Web",
            "Shared",
            "MarketAnalysisControlsPanel.razor");
    }
}
