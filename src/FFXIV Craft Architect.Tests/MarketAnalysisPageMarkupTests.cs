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

    [Fact]
    public void MarketAnalysisPage_RefreshSelectedItemUsesItemRefreshWorkflow()
    {
        var source = File.ReadAllText(GetMarketAnalysisPagePath());

        Assert.Contains("@inject MarketAnalysisItemRefreshService MarketAnalysisItemRefreshService", source);
        Assert.Contains("OnRefreshItemRequested=\"RefreshSelectedMarketItemAsync\"", source);
        Assert.Contains("IsRefreshingItem=\"_isRefreshingSelectedItem || _isAnalyzing || _isRefreshingPrices\"", source);
        Assert.Contains("private CancellableOperationLease? _itemRefreshOperation", source);
        Assert.Contains("CancellableOperationWorkflow.ItemMarketRefresh", source);
        Assert.Contains("new MarketAnalysisItemRefreshWorkflowRequest", source);
        Assert.Contains("MarketAnalysisExecutionOptions.Interactive", source);
        Assert.Contains("Scope = refreshScope", source);
        Assert.Contains("IsCurrentConfiguration = () =>", source);
        Assert.Contains("MarketAnalysisItemRefreshStatus.Refreshed", source);
        Assert.Contains("throw new InvalidOperationException(\"Item refresh reported success without a refreshed item name.\")", source);
        Assert.DoesNotContain("result.ItemName ?? plan.Name", source);
        Assert.Contains("IsDiscardedItemRefreshStatus(result.Status)", source);
        Assert.Contains("Snackbar.Add($\"Item refresh was discarded:", source);
        Assert.Contains("Refresh market data failed", source);
        Assert.Contains("CancellableOperations.Cancel(CancellableOperationWorkflow.ItemMarketRefresh)", source);
    }

    [Fact]
    public void ProcurementPage_ItemRefreshSuccessDoesNotFallbackToRequestedItemName()
    {
        var source = File.ReadAllText(GetProcurementPagePath());

        Assert.Contains("throw new InvalidOperationException(\"Item refresh reported success without a refreshed item name.\")", source);
        Assert.DoesNotContain("result.ItemName ?? action.Item.ItemName", source);
        Assert.Contains("Snackbar.Add($\"Refreshed {result.ItemName}; re-running procurement.\"", source);
    }

    private static string GetMarketAnalysisPagePath()
    {
        return GetPagePath("MarketAnalysis.razor");
    }

    private static string GetProcurementPagePath()
    {
        return GetPagePath("ProcurementPlan.razor");
    }

    private static string GetPagePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Pages", fileName);
    }
}
