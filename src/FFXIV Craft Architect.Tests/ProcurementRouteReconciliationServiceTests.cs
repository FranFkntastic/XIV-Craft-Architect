using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.Tests;

public class ProcurementRouteReconciliationServiceTests
{
    [Fact]
    public async Task ChangedRouteInputs_AreReconciledAutomatically()
    {
        var appState = CreateStateWithPublishedRoute();
        var workflow = new RecordingProcurementWorkflow(() =>
        {
            appState.ReplaceProcurementOverlay(appState.ProcurementShoppingPlans.ToArray());
            return new ProcurementWorkflowResult(ProcurementWorkflowStatus.Published, 1);
        });
        using var operations = new CancellableOperationService(appState);
        using var reconciliation = new ProcurementRouteReconciliationService(
            appState,
            workflow,
            operations,
            NullLogger<ProcurementRouteReconciliationService>.Instance,
            TimeSpan.FromMilliseconds(10));
        reconciliation.Start();

        appState.SetProcurementSettings(
            appState.ProcurementSearchEntireRegion,
            appState.ProcurementEnableSplitWorldPurchases,
            travelTolerance: 5,
            appState.TemporaryWorldBlacklistDurationMinutes);

        Assert.True(appState.IsProcurementRouteReconciling);
        await WaitUntilAsync(() => workflow.CallCount == 1 && !appState.IsProcurementRouteReconciling);

        Assert.Equal(ProcurementRoutePublicationValidity.Current, appState.ProcurementRouteValidity);
        Assert.False(appState.IsProcurementRouteStale);
        Assert.Null(appState.ProcurementRouteFailure);
    }

    [Fact]
    public async Task RapidClutchChanges_AreCoalescedIntoOneRepair()
    {
        var appState = CreateStateWithPublishedRoute();
        var workflow = new RecordingProcurementWorkflow(() =>
        {
            appState.ReplaceProcurementOverlay(appState.ProcurementShoppingPlans.ToArray());
            return new ProcurementWorkflowResult(ProcurementWorkflowStatus.Published, 1);
        });
        using var operations = new CancellableOperationService(appState);
        using var reconciliation = new ProcurementRouteReconciliationService(
            appState,
            workflow,
            operations,
            NullLogger<ProcurementRouteReconciliationService>.Instance,
            TimeSpan.FromMilliseconds(30));
        reconciliation.Start();

        foreach (var tolerance in new[] { 3, 4, 5, 6 })
        {
            appState.SetProcurementSettings(
                appState.ProcurementSearchEntireRegion,
                appState.ProcurementEnableSplitWorldPurchases,
                tolerance,
                appState.TemporaryWorldBlacklistDurationMinutes);
        }

        await WaitUntilAsync(() => workflow.CallCount == 1 && !appState.IsProcurementRouteReconciling);
        await Task.Delay(50);

        Assert.Equal(1, workflow.CallCount);
        Assert.Equal(6, appState.ProcurementRoutePublicationBasis?.TravelTolerance);
    }

    [Fact]
    public async Task FailedAutomaticRepair_SurfacesFailureWithoutRetryLoop()
    {
        var appState = CreateStateWithPublishedRoute();
        var workflow = new RecordingProcurementWorkflow(() =>
            new ProcurementWorkflowResult(
                ProcurementWorkflowStatus.NoCompleteRoute,
                0,
                "No complete route covers the current purchase list."));
        using var operations = new CancellableOperationService(appState);
        using var reconciliation = new ProcurementRouteReconciliationService(
            appState,
            workflow,
            operations,
            NullLogger<ProcurementRouteReconciliationService>.Instance,
            TimeSpan.FromMilliseconds(10));
        reconciliation.Start();

        appState.SetProcurementTravelPriority(MarketTravelPriority.WorldVisitsFirst);

        await WaitUntilAsync(() => workflow.CallCount == 1 && !appState.IsProcurementRouteReconciling);
        await Task.Delay(50);

        Assert.Equal(1, workflow.CallCount);
        Assert.True(appState.IsProcurementRouteStale);
        Assert.Equal("No complete route covers the current purchase list.", appState.ProcurementRouteFailure);
    }

    private static AppState CreateStateWithPublishedRoute()
    {
        var appState = new AppState();
        appState.ReplaceProcurementOverlay(
            [new DetailedShoppingPlan { ItemId = 100, Name = "Route Item" }]);
        return appState;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected reconciliation state was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class RecordingProcurementWorkflow : IProcurementWorkflowService
    {
        private readonly Func<ProcurementWorkflowResult> _run;

        public RecordingProcurementWorkflow(Func<ProcurementWorkflowResult> run)
        {
            _run = run;
        }

        public int CallCount { get; private set; }

        public Task<ProcurementWorkflowResult> RunAnalysisAsync(
            ProcurementWorkflowRequest request,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(_run());
        }
    }
}
