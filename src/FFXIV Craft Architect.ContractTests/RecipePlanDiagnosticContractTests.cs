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
        var program = File.ReadAllText(Path.Combine(
            root,
            "src",
            "FFXIV Craft Architect.Web",
            "Program.cs"));
        var options = File.ReadAllText(Path.Combine(
            root,
            "src",
            "FFXIV Craft Architect.Web",
            "Dialogs",
            "OptionsDialog.razor"));
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
