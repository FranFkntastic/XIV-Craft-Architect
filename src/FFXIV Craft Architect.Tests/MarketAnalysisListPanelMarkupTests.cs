namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisListPanelMarkupTests
{
    [Fact]
    public void MarketAnalysisListPanel_UsesCachedOrderedPlansForRender()
    {
        var source = File.ReadAllText(GetListPanelPath());

        Assert.Contains("@foreach (var plan in _orderedPlans)", source);
        Assert.DoesNotContain("@foreach (var plan in GetGridPlans())", source);
    }

    [Fact]
    public void MarketAnalysisListPanel_DoesNotConsumeAutoExpandFromRenderLifecycle()
    {
        var source = File.ReadAllText(GetListPanelPath());

        Assert.DoesNotContain("OnAutoExpandConsumed", source);
        Assert.DoesNotContain("_autoExpandProcessed", source);
        Assert.DoesNotContain("AutoExpandItemId", source);
    }

    [Fact]
    public void MarketAnalysisListPanel_TotalCostUsesProjectionWarningHelpers()
    {
        var source = File.ReadAllText(GetListPanelPath());

        Assert.Contains("SortHeader(\"Calculated Total\", MarketAnalysisGridSortColumn.Total", source);
        Assert.DoesNotContain("SortHeader(\"Total\", MarketAnalysisGridSortColumn.Total", source);
        Assert.Contains("GetTotalCostClass(plan)", source);
        Assert.Contains("GetTotalCostTooltip(plan)", source);
        Assert.Contains("GetTotalCostClass(_selectedPlan)", source);
        Assert.Contains("GetTotalCostTooltip(_selectedPlan)", source);
        Assert.Contains("MarketAnalysisGridViewService.CalculatedTotalHeaderTooltip", source);
    }

    private static string GetListPanelPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Shared", "MarketAnalysisListPanel.razor");
    }
}
