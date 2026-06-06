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
