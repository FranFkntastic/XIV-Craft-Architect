namespace FFXIV_Craft_Architect.Tests;

public class CrossTabNavigationMarkupTests
{
    [Fact]
    public void RecipeNodeView_PriceLinkHasConcreteAcquisitionEvaluationHref()
    {
        var source = File.ReadAllText(GetWebPath("Shared", "RecipeNodeView.razor"));

        Assert.Contains("__builder.OpenElement(19, \"a\");", source);
        Assert.Contains("href", source);
        Assert.Contains("GetAcquisitionEvaluationHref()", source);
        Assert.Contains("Open in Acquisition Evaluation", source);
    }

    [Fact]
    public void AcquisitionEvaluation_AcceptsNodeIdQuerySelection()
    {
        var source = File.ReadAllText(GetWebPath("Pages", "AcquisitionEvaluation.razor"));

        Assert.Contains("[SupplyParameterFromQuery(Name = \"nodeId\")]", source);
        Assert.Contains("SelectRequestedNodeFromSnapshot", source);
    }

    [Fact]
    public void AcquisitionDetailsPanel_UsesMarketAutoExpandRequestForMarketNavigation()
    {
        var source = File.ReadAllText(GetWebPath("Shared", "AcquisitionDetailsPanel.razor"));

        Assert.Contains("AppState.RequestMarketItemAutoExpand(ItemId);", source);
        Assert.Contains("NavigationManager.NavigateTo(\"market\");", source);
    }

    private static string GetWebPath(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(
            [directory.FullName, "src", "FFXIV Craft Architect.Web", .. segments]);
    }
}
