using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class RecipePlanDiagnosticContractTests
{
    [Fact]
    public void Dump_IsAWorkerSnapshotWithoutAnalysisOrProcurementCommands()
    {
        var root = LocateRepositoryRoot();
        var layout = File.ReadAllText(Path.Combine(
            root,
            "src",
            "FFXIV Craft Architect.Web",
            "Shared",
            "MainLayout.razor"));
        var start = layout.IndexOf(
            "private async Task OnDumpRecipePlanDiagnostics()",
            StringComparison.Ordinal);
        var end = layout.IndexOf(
            "private async Task LoadNativePlanAsync(",
            start,
            StringComparison.Ordinal);
        var dumpHandler = layout[start..end];

        Assert.Contains("WorkerSession.ExportStoredPlanAsync(", dumpHandler, StringComparison.Ordinal);
        Assert.Contains("Session = stored", dumpHandler, StringComparison.Ordinal);
        Assert.Contains("Recipe = WorkerProjections.Recipe", dumpHandler, StringComparison.Ordinal);
        Assert.Contains("Market = WorkerProjections.Market", dumpHandler, StringComparison.Ordinal);
        Assert.Contains("Procurement = WorkerProjections.Procurement", dumpHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("RunMarketAnalysis", dumpHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("RunProcurement", dumpHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceStoredPlan", dumpHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("AppState.CurrentPlan", dumpHandler, StringComparison.Ordinal);
        Assert.Equal(
            "recipe-plan-Crasher_Plan-20260724-123456.json",
            RecipePlanDiagnosticFileName.Create(
                "Crasher/Plan",
                new DateTime(2026, 7, 24, 12, 34, 56, DateTimeKind.Utc)));
    }

    [Fact]
    public void Composition_DoesNotRegisterRetiredMainThreadPlannerPipelines()
    {
        var root = LocateRepositoryRoot();
        var web = Path.Combine(root, "src", "FFXIV Craft Architect.Web");
        var program = File.ReadAllText(Path.Combine(
            web,
            "Program.cs"));
        var options = File.ReadAllText(Path.Combine(
            web,
            "Dialogs",
            "OptionsDialog.razor"));
        var planner = File.ReadAllText(Path.Combine(web, "Pages", "Index.razor"));
        var marketPage = File.ReadAllText(Path.Combine(
            web,
            "Pages",
            "MarketAnalysis.razor"));
        var workflow = File.ReadAllText(Path.Combine(
            web,
            "Services",
            "PlanLifecycleWorkflowService.cs"));
        var tradePricing = File.ReadAllText(Path.Combine(
            web,
            "Services",
            "TradeOrderPricingWorkflowService.cs"));
        var layout = File.ReadAllText(Path.Combine(
            web,
            "Shared",
            "MainLayout.razor"));
        var planBrowser = File.ReadAllText(Path.Combine(
            web,
            "Dialogs",
            "PlanBrowserDialog.razor"));
        var tradeOrders = File.ReadAllText(Path.Combine(
            web,
            "Pages",
            "TradeOrders.CraftPlan.cs"));
        var retiredTypes = new[]
        {
            "RecipePlannerCommandService",
            "MarketAnalysisAutoRunner",
            "MarketEvidenceHydrationService",
            "ProcurementRouteReconciliationService",
            "RecipePlanDiagnosticDumpService",
            "AcquisitionEvaluationItemDiagnosticDumpService"
        };

        foreach (var retiredType in retiredTypes)
        {
            Assert.DoesNotContain(retiredType, program, StringComparison.Ordinal);
            Assert.DoesNotContain(retiredType, options, StringComparison.Ordinal);
        }

        Assert.Contains(
            "AddScoped<PlanLifecycleWorkflowService>()",
            program,
            StringComparison.Ordinal);
        Assert.Contains("PlanLifecycle.BuildRecipeAsync(", planner, StringComparison.Ordinal);
        Assert.Contains("PlanLifecycle.ReplaceStoredPlanAsync(", planner, StringComparison.Ordinal);
        Assert.Contains("_planLifecycle.EnsureDerivedAsync(", tradePricing, StringComparison.Ordinal);
        Assert.Contains(
            "if (WorkerProjections.Shell.HasSession)",
            layout,
            StringComparison.Ordinal);
        Assert.Contains("PlanLifecycle.Schedule();", layout, StringComparison.Ordinal);
        Assert.True(
            workflow.IndexOf("_worker.RunProcurementAsync(", StringComparison.Ordinal) >
            workflow.IndexOf("_worker.RunMarketAnalysisAsync(", StringComparison.Ordinal));
        foreach (var entryPoint in new[] { planner, layout, planBrowser, tradeOrders })
        {
            Assert.DoesNotContain(
                "WorkerSession.BuildRecipeAsync(",
                entryPoint,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "WorkerSession.ReplaceStoredPlanAsync(",
                entryPoint,
                StringComparison.Ordinal);
        }
        Assert.DoesNotContain("_worker.BuildRecipeAsync(", tradePricing, StringComparison.Ordinal);
        Assert.DoesNotContain("_worker.ReplaceStoredPlanAsync(", tradePricing, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshMarketEvidenceAsync(", tradePricing, StringComparison.Ordinal);
        Assert.Contains(
            "_market?.Revision != revision",
            marketPage,
            StringComparison.Ordinal);
        Assert.Contains(
            "RefreshProjectionAsync(revision)",
            marketPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("OnStateChanged +=", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkerProjections.Changed +=", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Architecture_AppStateCannotBecomeASecondDomainAuthority()
    {
        var root = LocateRepositoryRoot();
        var web = Path.Combine(root, "src", "FFXIV Craft Architect.Web");
        var appState = File.ReadAllText(Path.Combine(web, "Services", "AppState.cs"));
        var program = File.ReadAllText(Path.Combine(web, "Program.cs"));
        var engineHost = File.ReadAllText(Path.Combine(web, "Services", "CraftArchitectEngineHost.cs"));
        var engineComposition = File.ReadAllText(Path.Combine(
            web,
            "Services",
            "WorkerEngineServiceCollectionExtensions.cs"));
        var workerCoordinator = File.ReadAllText(Path.Combine(
            web,
            "Services",
            "WorkerSessionCoordinator.cs"));
        var planner = File.ReadAllText(Path.Combine(web, "Pages", "Index.razor"));
        var marketResults = File.ReadAllText(Path.Combine(
            web,
            "Shared",
            "MarketAnalysisResultsPanel.razor"));
        var marketResultsCss = File.ReadAllText(Path.Combine(
            web,
            "Shared",
            "MarketAnalysisResultsPanel.razor.css"));
        var retiredAuthority = new[]
        {
            "CurrentPlan",
            "MarketItemAnalyses",
            "ShoppingPlans",
            "ProcurementShoppingPlans",
            "BeginAutoSaveAsync",
            "BeginEngineMemoryPressureLeaseAsync"
        };
        var retiredPipelines = new[]
        {
            "MarketAnalysisWorkflowService",
            "ProcurementWorkflowService",
            "PlanSessionLoadService",
            "StoredPlanSnapshotBuilder",
            "WebMarketAnalysisEngineSettlement",
            "WebProcurementEngineSettlement"
        };

        foreach (var member in retiredAuthority)
        {
            Assert.DoesNotContain(member, appState, StringComparison.Ordinal);
        }

        foreach (var type in retiredPipelines)
        {
            Assert.DoesNotContain(type, program, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("CreateExecution(", engineHost, StringComparison.Ordinal);
        Assert.DoesNotContain("AppState", engineHost, StringComparison.Ordinal);
        Assert.Contains("AddWorkerEngine", program, StringComparison.Ordinal);
        Assert.Contains("WorkerSessionCoordinator", engineComposition, StringComparison.Ordinal);

        var initializationStart = planner.IndexOf(
            "protected override async Task OnInitializedAsync()",
            StringComparison.Ordinal);
        var initializationEnd = planner.IndexOf(
            "public void Dispose()",
            initializationStart,
            StringComparison.Ordinal);
        var initialization = planner[initializationStart..initializationEnd];
        Assert.True(
            initialization.IndexOf("WorkerProjections.Recipe", StringComparison.Ordinal) <
            initialization.IndexOf("RefreshSavedPlansListAsync()", StringComparison.Ordinal));
        Assert.Contains(
            "if (_recipe?.Revision != WorkerProjections.Shell.Revision)",
            initialization,
            StringComparison.Ordinal);
        Assert.Contains(
            "_recipe = WorkerProjections.Recipe;",
            initialization,
            StringComparison.Ordinal);
        Assert.Contains(
            "await RefreshRecipeProjectionAsync(cancellationToken);",
            workerCoordinator,
            StringComparison.Ordinal);

        Assert.Contains(
            "ProjectedItems=\"ProjectedItems\"",
            marketResults,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ma-restored-", marketResults, StringComparison.Ordinal);
        Assert.DoesNotContain("ma-restored-", marketResultsCss, StringComparison.Ordinal);
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
