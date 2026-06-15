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
        Assert.Contains("MarketAnalysisGridViewService.FormatWorldPriceBandRole(world)", source);
        Assert.Contains("MarketAnalysisGridViewService.FormatWorldPriceBandSummary(world)", source);
        Assert.Contains("MarketAnalysisGridViewService.GetWorldPriceBandScoreClass(world)", source);
        Assert.DoesNotContain("\"ItemId\", ItemId", source);
        Assert.DoesNotContain("\"PublicationId\", PublicationId", source);
    }

    [Fact]
    public void MarketAnalysisWorldGrid_PassesEvidenceOverlayOnlyToExpandedListingLadder()
    {
        var source = File.ReadAllText(GetWorldGridPath());
        var styles = File.ReadAllText(GetWorldGridStylesPath());
        var ladderSource = File.ReadAllText(GetSharedPath("MarketAnalysisListingLadder.razor"));
        var ladderStyles = File.ReadAllText(GetSharedPath("MarketAnalysisListingLadder.razor.css"));
        var resultsSource = File.ReadAllText(GetSharedPath("MarketAnalysisResultsPanel.razor"));
        var listSource = File.ReadAllText(GetSharedPath("MarketAnalysisListPanel.razor"));
        var pageSource = File.ReadAllText(GetPagePath("MarketAnalysis.razor"));

        Assert.Contains("[Parameter] public MarketAnalysisEvidenceOverlay EvidenceOverlay", source);
        Assert.Contains("builder.AddAttribute(2, \"EvidenceOverlay\", EvidenceOverlay)", source);
        Assert.Contains("EvidenceOverlay=\"AppState.MarketAnalysisEvidenceOverlay\"", pageSource);
        Assert.Contains("EvidenceOverlay=\"EvidenceOverlay\"", resultsSource);
        Assert.Contains("EvidenceOverlay=\"EvidenceOverlay\"", listSource);
        Assert.Contains("GetListingRowClass(World, listing, EvidenceOverlay)", ladderSource);
        Assert.Contains("GetListingPriceBandTooltip(World, listing, EvidenceOverlay)", ladderSource);
        Assert.Contains(".ma-ladder-table tr.ma-band-tone-low td", ladderStyles);
        Assert.DoesNotContain("ma-band-tone", styles);
        Assert.DoesNotContain("ma-band-edge", styles);
    }

    private static string GetWorldGridPath()
    {
        return GetSharedPath("MarketAnalysisWorldGrid.razor");
    }

    private static string GetWorldGridStylesPath()
    {
        return GetSharedPath("MarketAnalysisWorldGrid.razor.css");
    }

    private static string GetSharedPath(string fileName)
    {
        var directory = GetRepositoryRoot();
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Shared", fileName);
    }

    private static string GetPagePath(string fileName)
    {
        var directory = GetRepositoryRoot();
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Pages", fileName);
    }

    private static DirectoryInfo GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory;
    }
}
