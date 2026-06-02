namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisWorldGridMarkupTests
{
    [Fact]
    public void MarketAnalysisWorldGrid_PercentDiffCell_UsesCompetitiveValueTooltip()
    {
        var source = File.ReadAllText(GetWorldGridPath());

        Assert.Contains("WebDataTable", source);
        Assert.Contains("Header = \"% Diff\"", source);
        Assert.Contains("CellTitleSelector = MarketAnalysisGridViewService.FormatCompetitiveValueTooltip", source);
        Assert.Contains("MarketAnalysisGridViewService.FormatCompetitiveValue(world)", source);
    }

    private static string GetWorldGridPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Shared", "MarketAnalysisWorldGrid.razor");
    }
}
