namespace FFXIV_Craft_Architect.ContractTests;

public sealed class MarketAnalysisPresentationContractTests
{
    [Fact]
    public void MarketAnalysis_UsesExpandableCardsInsteadOfTheCompactSplitLedger()
    {
        var root = LocateRepositoryRoot();
        var web = Path.Combine(root, "src", "FFXIV Craft Architect.Web");
        var page = File.ReadAllText(Path.Combine(web, "Pages", "MarketAnalysis.razor"));
        var panel = File.ReadAllText(Path.Combine(
            web,
            "Shared",
            "MarketAnalysisListPanel.razor"));
        var styles = File.ReadAllText(Path.Combine(
            web,
            "Shared",
            "MarketAnalysisListPanel.razor.css"));

        Assert.Contains("class=\"ma-market-card-grid\"", panel, StringComparison.Ordinal);
        Assert.Contains("class=\"ma-expanded-item\"", panel, StringComparison.Ordinal);
        Assert.Contains("CloseSelectedItemAsync", panel, StringComparison.Ordinal);
        Assert.Contains("return item.RecommendedWorld;", panel, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(auto-fill, minmax(285px, 1fr))", styles, StringComparison.Ordinal);
        Assert.Contains("max-height: min(52vh, 680px)", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("ma-ledger-workspace", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("ma-ledger-table", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectedItemId ??=", page, StringComparison.Ordinal);
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
