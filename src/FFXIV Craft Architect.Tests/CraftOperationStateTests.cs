using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class CraftOperationStateTests
{
    [Fact]
    public void StartOperation_AssignsIdentityAndReportsBusyProgress()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var state = new CraftOperationState();
        var coordinator = new CraftOperationCoordinator(session, state);

        using var lease = coordinator.Start(
            CraftOperationWorkflow.MarketAnalysis,
            "Market Analysis",
            "Analyzing");

        Assert.NotEqual(Guid.Empty, lease.OperationId);
        Assert.True(state.IsBusy);
        Assert.Equal(lease.OperationId, state.CurrentOperationId);
        Assert.Equal("Analyzing", state.StatusMessage);

        Assert.True(lease.ReportProgress(42, "Halfway"));

        Assert.Equal(42, state.ProgressPercent);
        Assert.Equal("Halfway", state.StatusMessage);
    }

    [Fact]
    public void Changed_NotifiesWithSnapshotsForCurrentOperationTransitions()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var state = new CraftOperationState();
        var coordinator = new CraftOperationCoordinator(session, state);
        var snapshots = new List<CraftOperationSnapshot>();
        state.Changed += snapshot => snapshots.Add(snapshot);

        using var lease = coordinator.Start(
            CraftOperationWorkflow.PriceRefresh,
            "Price Refresh",
            "Refreshing");
        Assert.True(lease.ReportProgress(50, "Halfway"));
        Assert.True(lease.CompleteIfCurrent(() => { }, "Done"));

        Assert.Equal(["Refreshing", "Halfway", "Done"], snapshots.Select(snapshot => snapshot.StatusMessage).ToArray());
        Assert.True(snapshots[0].IsBusy);
        Assert.Equal(50, snapshots[1].ProgressPercent);
        Assert.False(snapshots[2].IsBusy);
    }

    [Fact]
    public void SupersededOperation_CannotPublishCompletion()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var state = new CraftOperationState();
        var coordinator = new CraftOperationCoordinator(session, state);

        using var first = coordinator.Start(CraftOperationWorkflow.MarketAnalysis, "Market", "first");
        using var second = coordinator.Start(CraftOperationWorkflow.MarketAnalysis, "Market", "second");
        var published = false;

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(first.IsCurrent);
        Assert.False(first.CompleteIfCurrent(() => published = true, "first complete"));
        Assert.True(second.CompleteIfCurrent(() => published = true, "second complete"));
        Assert.True(published);
        Assert.False(state.IsBusy);
        Assert.Equal("second complete", state.StatusMessage);
    }

    [Fact]
    public void StaleSessionOperation_CannotPublishCompletion()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var state = new CraftOperationState();
        var coordinator = new CraftOperationCoordinator(session, state);

        using var lease = coordinator.Start(CraftOperationWorkflow.ProcurementAnalysis, "Procurement", "Routing");
        session.MarkPlanStructureChanged("new plan activated");
        var published = false;

        Assert.False(lease.CompleteIfCurrent(() => published = true, "route complete"));

        Assert.False(published);
        Assert.False(state.IsBusy);
        Assert.Equal("Ready", state.StatusMessage);
        Assert.True(lease.Token.IsCancellationRequested);
    }

    [Fact]
    public void ActivePlanReplacement_CannotPublishCompletionFromOlderOperationLease()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var state = new CraftOperationState();
        var coordinator = new CraftOperationCoordinator(session, state);
        var originalPlan = new CraftingPlan();
        var replacementPlan = new CraftingPlan { Id = originalPlan.Id };
        session.ActivatePlan(originalPlan, [], new CraftSessionActiveContext(null, null, null, null), "original");
        using var lease = coordinator.Start(CraftOperationWorkflow.RecipeBuild, "Build", "Building");
        session.ActivatePlan(replacementPlan, [], new CraftSessionActiveContext(null, null, null, null), "replacement");
        var published = false;

        Assert.False(lease.IsCurrent);
        Assert.False(lease.ReportProgress(50, "stale progress"));
        Assert.False(state.IsBusy);
        Assert.False(lease.CompleteIfCurrent(() => published = true, "complete"));

        Assert.False(published);
        Assert.False(state.IsBusy);
        Assert.True(lease.Token.IsCancellationRequested);
    }

    [Fact]
    public void Start_DifferentWorkflowCancelsPreviousLeaseBecauseOperationStateIsGlobal()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var state = new CraftOperationState();
        var coordinator = new CraftOperationCoordinator(session, state);

        using var market = coordinator.Start(CraftOperationWorkflow.MarketAnalysis, "Market", "Analyzing");
        using var procurement = coordinator.Start(CraftOperationWorkflow.ProcurementAnalysis, "Procurement", "Routing");

        Assert.True(market.Token.IsCancellationRequested);
        Assert.False(market.IsCurrent);
        Assert.True(procurement.IsCurrent);
        Assert.Equal(procurement.OperationId, state.CurrentOperationId);
    }

    [Fact]
    public void Cancel_ActiveOperationUsesNeutralStatus()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var state = new CraftOperationState();
        var coordinator = new CraftOperationCoordinator(session, state);
        using var lease = coordinator.Start(CraftOperationWorkflow.PriceRefresh, "Price Refresh", "Refreshing");

        Assert.True(lease.Cancel());

        Assert.True(lease.Token.IsCancellationRequested);
        Assert.False(state.IsBusy);
        Assert.Equal("Ready", state.StatusMessage);
    }

    [Fact]
    public void PublishFailure_ClearsBusyStateAndDoesNotDispatchDeferredSessionChangesLater()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var state = new CraftOperationState();
        var coordinator = new CraftOperationCoordinator(session, state);
        var changeCount = 0;
        session.Changed += (_, _) => changeCount++;
        using var lease = coordinator.Start(CraftOperationWorkflow.MarketAnalysis, "Market", "Analyzing");
        var before = session.CaptureVersionStamp();
        var changesBefore = session.Changes.Count;

        Assert.Throws<InvalidOperationException>(() =>
            lease.CompleteIfCurrent(
                () =>
                {
                    session.MarkViewStateChanged("partial publication");
                    throw new InvalidOperationException("publish failed");
                },
                "complete"));

        Assert.False(state.IsBusy);
        Assert.Equal("Ready", state.StatusMessage);
        Assert.True(lease.Token.IsCancellationRequested);
        Assert.Equal(0, changeCount);
        Assert.True(session.IsCurrent(before));
        Assert.False(session.IsDirty(CraftSessionDirtyBucket.ViewState));
        Assert.Equal(changesBefore, session.Changes.Count);

        session.MarkViewStateChanged("later change");

        Assert.Equal(1, changeCount);
    }
}
