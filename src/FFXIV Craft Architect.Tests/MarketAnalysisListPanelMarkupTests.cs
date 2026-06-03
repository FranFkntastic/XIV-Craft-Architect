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

    [Fact]
    public void MarketAnalysisListPanel_DetailHeaderAlignsTitleAndSupportsCopyName()
    {
        var source = File.ReadAllText(GetListPanelPath());
        var styles = File.ReadAllText(GetListPanelStylePath());

        Assert.Contains("@inject IJSRuntime JSRuntime", source);
        Assert.Contains("@inject ISnackbar Snackbar", source);
        Assert.Contains("class=\"ma-detail-title-block\"", source);
        Assert.Contains("class=\"ma-detail-title-row\"", source);
        Assert.Contains("Class=\"ma-detail-copy-button\"", source);
        Assert.Contains("Icons.Material.Filled.ContentCopy", source);
        Assert.Contains("OnClick=\"() => CopySelectedItemNameAsync(_selectedPlan.Name)\"", source);
        Assert.Contains("OnClick:StopPropagation=\"true\"", source);
        Assert.Contains("navigator.clipboard.writeText", source);
        Assert.Contains("Snackbar.Add(\"Item name copied\", Severity.Success)", source);

        Assert.Contains(".ma-detail-title-block", styles);
        Assert.Contains(".ma-detail-title-row", styles);
        Assert.Contains(".ma-detail-copy-button", styles);
    }

    private static string GetListPanelPath()
    {
        return GetSharedPath("MarketAnalysisListPanel.razor");
    }

    private static string GetListPanelStylePath()
    {
        return GetSharedPath("MarketAnalysisListPanel.razor.css");
    }

    private static string GetSharedPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Shared", fileName);
    }
}
