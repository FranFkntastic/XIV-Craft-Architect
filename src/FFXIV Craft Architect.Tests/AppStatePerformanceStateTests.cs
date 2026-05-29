using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AppStatePerformanceStateTests
{
    [Fact]
    public void NotifyPlanChanged_EmitsScopedChangeAndMarksPlanCoreDirty()
    {
        var appState = new AppState();
        var changes = new List<AppStateChange>();

        appState.OnStateChanged += changes.Add;

        appState.NotifyPlanChanged();

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.PlanStructure));
        Assert.Equal(1, change.Versions.PlanStructureVersion);
        Assert.True(appState.IsPersistedBucketDirty(PersistedStateBucket.PlanCore));
        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
    }

    [Fact]
    public void MarketAnalysisChange_DirtiesOnlyMarketAnalysisBucket()
    {
        var appState = new AppState();
        appState.NotifyPlanChanged();
        var baseline = appState.CurrentVersions;
        appState.MarkPersisted(PersistedStateBucket.All, baseline);

        appState.NotifyShoppingListChanged();

        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.PlanCore));
        Assert.True(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
    }

    [Fact]
    public void StatusChange_DoesNotDirtyPersistedBuckets()
    {
        var appState = new AppState();
        appState.NotifyPlanChanged();
        appState.NotifyShoppingListChanged();
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);

        appState.NotifyStatusChanged();

        Assert.Equal(PersistedStateBucket.None, appState.GetDirtyPersistedBuckets());
    }

    [Fact]
    public void PlanDecisionChange_DirtiesPlanCoreAfterHigherStructureVersionWasPersisted()
    {
        var appState = new AppState();
        appState.NotifyPlanChanged();
        appState.NotifyPlanChanged();
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);

        appState.NotifyPlanDecisionChanged();

        Assert.True(appState.IsPersistedBucketDirty(PersistedStateBucket.PlanCore));
    }

    [Fact]
    public void TemporaryWorldBlacklist_DirtiesProcurementOverlayWithoutDirtyingMarketAnalysis()
    {
        var appState = new AppState();
        appState.NotifyShoppingListChanged();
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);

        appState.BlacklistMarketWorldTemporarily(new MarketWorldKey("Aether", "Siren"));

        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
        Assert.Equal(1, appState.CurrentVersions.ProcurementOverlayVersion);
    }

    [Fact]
    public void ProcurementOverlayChange_EmitsOverlayScopeWithoutDirtyingMarketAnalysis()
    {
        var appState = new AppState();
        var changes = new List<AppStateChange>();
        appState.NotifyShoppingListChanged();
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);

        appState.OnStateChanged += changes.Add;
        appState.NotifyProcurementOverlayChanged();

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
    }

    [Fact]
    public void LoadStoredPlan_EmitsSingleBatchedStateChange()
    {
        var appState = new AppState();
        var changes = new List<AppStateChange>();
        var storedPlan = new StoredPlan
        {
            Id = "plan",
            Name = "Plan",
            DataCenter = "Aether",
            ProjectItems =
            [
                new StoredProjectItem
                {
                    Id = 123,
                    Name = "Current Item",
                    Quantity = 10
                }
            ]
        };

        appState.OnStateChanged += changes.Add;

        appState.LoadStoredPlan(storedPlan, deserializedPlan: null);

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.PlanStructure));
        Assert.True(change.HasScope(AppStateChangeScope.ShoppingItems));
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
    }

    [Fact]
    public void AutoSaveGuard_SkipsWhenCleanAndBlocksConcurrentSave()
    {
        var appState = new AppState
        {
            ProjectItems =
            [
                new ProjectItem
                {
                    Id = 123,
                    Name = "Current Item",
                    Quantity = 10
                }
            ]
        };

        Assert.True(appState.TryBeginAutoSave(out var capturedVersions, out var dirtyBuckets));
        Assert.Equal(PersistedStateBucket.PlanCore, dirtyBuckets);
        Assert.False(appState.TryBeginAutoSave(out _, out _));

        appState.CompleteAutoSave(succeeded: true, capturedVersions, dirtyBuckets);

        Assert.False(appState.TryBeginAutoSave(out _, out _));
    }
}
