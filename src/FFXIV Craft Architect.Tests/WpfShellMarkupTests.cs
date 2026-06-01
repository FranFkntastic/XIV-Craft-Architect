using System.Text.RegularExpressions;

namespace FFXIV_Craft_Architect.Tests;

public class WpfShellMarkupTests
{
    [Fact]
    public void MainWindow_TabsMatchWebWorkflowOrder()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect", "MainWindow.xaml"));

        var recipeIndex = source.IndexOf("RECIPE PLANNER", StringComparison.Ordinal);
        var marketIndex = source.IndexOf("MARKET ANALYSIS", StringComparison.Ordinal);
        var acquisitionIndex = source.IndexOf("ACQUISITION EVALUATION", StringComparison.Ordinal);
        var procurementIndex = source.IndexOf("PROCUREMENT PLAN", StringComparison.Ordinal);

        Assert.True(recipeIndex >= 0);
        Assert.True(marketIndex > recipeIndex);
        Assert.True(acquisitionIndex > marketIndex);
        Assert.True(procurementIndex > acquisitionIndex);
        Assert.Contains("OnAcquisitionEvaluationTabClick", source);
        Assert.Contains("AcquisitionEvaluationModule", source);
        Assert.Contains("AcquisitionEvaluationSidebarModule", source);
    }

    [Fact]
    public void NavigationModels_IncludeAcquisitionEvaluationTab()
    {
        var navigationSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect", "MainWindow.Navigation.cs"));
        var serviceSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect", "Services", "Interfaces", "INavigationService.cs"));

        Assert.Contains("AcquisitionEvaluation", navigationSource);
        Assert.Contains("OnAcquisitionEvaluationTabClick", navigationSource);
        Assert.Contains("AcquisitionEvaluation.RefreshCommand.Execute", navigationSource);
        Assert.Contains("AcquisitionEvaluation", serviceSource);
    }

    [Fact]
    public void MainWindow_StartsWithOnlyRecipePlannerModuleVisible()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect", "MainWindow.xaml"));

        AssertModuleVisibility(source, "RecipePlannerModule", "Visible");
        AssertModuleVisibility(source, "MarketAnalysisModule", "Collapsed");
        AssertModuleVisibility(source, "AcquisitionEvaluationModule", "Collapsed");
        AssertModuleVisibility(source, "ProcurementPlannerModule", "Collapsed");
    }

    [Fact]
    public void AcquisitionEvaluationView_UsesEditableDecisionControls()
    {
        var source = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect",
            "Views",
            "Modules",
            "AcquisitionEvaluationView.xaml"));

        Assert.Contains("SourceOptions", source);
        Assert.Contains("SelectedSource", source);
        Assert.Contains("CanChangeSource", source);
        Assert.Contains("IsMarketHq", source);
        Assert.Contains("CanChangeMarketHq", source);
        Assert.Contains("SelectedRow", source);
        Assert.Contains("OptionRows", source);
    }

    [Fact]
    public void MarketAnalysisSidebar_UsesWebLensAndScopeLanguage()
    {
        var source = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect",
            "Views",
            "Modules",
            "MarketAnalysisSidebarView.xaml"));

        Assert.Contains("Acquisition Lens", source);
        Assert.Contains("Minimum Upfront Cost", source);
        Assert.Contains("Best Value / Bulk Acquisition", source);
        Assert.Contains("Search entire region", source);
    }

    [Fact]
    public void MainWindow_BuildProjectPlan_UsesCoreRecipePlannerCommandService()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect", "MainWindow.xaml.cs"));
        var method = ExtractMethodBody(source, "OnBuildProjectPlanAsync");

        Assert.Contains("CoreRecipePlannerCommandService", source);
        Assert.Contains("_recipePlannerCommands.BuildPlanAsync", method);
        Assert.DoesNotContain("_recipeCalcService.BuildPlanAsync", method);
    }

    private static void AssertModuleVisibility(string source, string moduleName, string expectedVisibility)
    {
        var pattern = $@"<modules:[^>]*x:Name=""{Regex.Escape(moduleName)}""[^>]*Visibility=""{Regex.Escape(expectedVisibility)}""";
        Assert.Matches(pattern, source);
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var signatureIndex = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0);

        var bodyStart = source.IndexOf('{', signatureIndex);
        Assert.True(bodyStart >= 0);

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[bodyStart..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Could not parse method body for {methodName}.");
    }

    private static string GetWorkspacePath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
    }
}
