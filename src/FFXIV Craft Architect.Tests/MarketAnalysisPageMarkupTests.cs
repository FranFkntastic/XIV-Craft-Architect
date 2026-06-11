namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisPageMarkupTests
{
    [Fact]
    public void MarketAnalysisPage_RefreshPricesRequestsForceRefresh()
    {
        var source = File.ReadAllText(GetMarketAnalysisPagePath());

        Assert.Contains("new RefreshRecipePlanPricesRequest", source);
        Assert.Contains("ForceRefreshData: true", source);
    }

    private static string GetMarketAnalysisPagePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Pages", "MarketAnalysis.razor");
    }
}
