namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisWorldGridMarkupTests
{
    [Fact]
    public void MarketAnalysisWorldGrid_UsesWebDataTableWithOriginalWorldTableContract()
    {
        var source = File.ReadAllText(GetWorldGridPath());
        var styles = File.ReadAllText(GetWorldGridStylesPath());

        Assert.Contains("<WebDataTable", source);
        Assert.Contains("Header = \"% Diff\"", source);
        Assert.Contains("Header = \"Data\"", source);
        Assert.Contains("WebTableColumnSize.Percent", source);
        Assert.DoesNotContain("nth-child", styles);
        Assert.Contains("CellTitleSelector = MarketAnalysisGridViewService.FormatCompetitiveValueTooltip", source);
        Assert.Contains("RenderExpandedWorld", source);
        Assert.Contains("ma-world-score", source);
        Assert.Contains("GetScoreClass(scoreBucket)", source);
        Assert.Contains("ma-world-muted", source);
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

    private static string GetWorldGridStylesPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Shared", "MarketAnalysisWorldGrid.razor.css");
    }
}
