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
    public void ClearMarketAnalysisState_EmitsMarketAndProcurementScopesWithoutDirtyingPlanCore()
    {
        var appState = new AppState
        {
            ShoppingPlans =
            [
                new DetailedShoppingPlan { ItemId = 100, Name = "Market Item" }
            ],
            MarketItemAnalyses =
            [
                new MarketItemAnalysis { ItemId = 100, Name = "Market Item" }
            ],
            ProcurementShoppingPlans =
            [
                new DetailedShoppingPlan { ItemId = 100, Name = "Route Item" }
            ]
        };
        appState.NotifyPlanChanged();
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ClearMarketAnalysisState();

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.False(change.HasScope(AppStateChangeScope.PlanStructure));
        Assert.False(change.HasScope(AppStateChangeScope.PlanDecision));
        Assert.True(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.PlanCore));
    }

    [Fact]
    public void BatchedMarketAndDecisionChanges_EmitSingleCombinedChangeWithExpectedVersions()
    {
        var appState = new AppState();
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        using (appState.BeginStateChangeBatch())
        {
            appState.NotifyShoppingListChanged();
            appState.NotifyProcurementOverlayChanged();
            appState.NotifyPlanDecisionChanged();
        }

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.True(change.HasScope(AppStateChangeScope.PlanDecision));
        Assert.Equal(1, change.Versions.MarketAnalysisVersion);
        Assert.Equal(1, change.Versions.ProcurementOverlayVersion);
        Assert.Equal(1, change.Versions.PlanDecisionVersion);
    }

    [Fact]
    public void BatchedDuplicateScopes_IncrementVersionsOnceAndEmitOneChange()
    {
        var appState = new AppState();
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        using (appState.BeginStateChangeBatch())
        {
            appState.NotifyPlanDecisionChanged();
            appState.NotifyPlanDecisionChanged();
            appState.NotifyProcurementOverlayChanged();
            appState.NotifyProcurementOverlayChanged();
        }

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.PlanDecision));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.Equal(1, change.Versions.PlanDecisionVersion);
        Assert.Equal(1, change.Versions.ProcurementOverlayVersion);
    }

    [Fact]
    public void ShoppingItemsChange_EmitsShoppingScopeWithoutDirtyingMarketAnalysis()
    {
        var appState = new AppState();
        appState.NotifyPlanChanged();
        appState.NotifyShoppingListChanged();
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);
        var beforeVersions = appState.CurrentVersions;
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.NotifyShoppingItemsChanged();

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.ShoppingItems));
        Assert.False(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.Equal(beforeVersions.MarketAnalysisVersion, change.Versions.MarketAnalysisVersion);
        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
    }

    [Fact]
    public void UnavailableMarketItemsChange_DirtiesMarketAnalysisWithoutDirtyingPlanCore()
    {
        var appState = new AppState();
        appState.NotifyPlanChanged();
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.SetUnavailableMarketItems(
        [
            new MarketDataUnavailableItem(123, "Unavailable Item")
        ]);

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.True(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.PlanCore));
        Assert.Equal(123, Assert.Single(appState.UnavailableMarketItems).ItemId);
    }

    [Fact]
    public void ReplaceMarketAnalysis_ReplacesAnalysisProjectionAndClearsProcurementOverlay()
    {
        var appState = new AppState
        {
            ShoppingPlans =
            [
                new DetailedShoppingPlan { ItemId = 100, Name = "Old Plan" }
            ],
            MarketItemAnalyses =
            [
                new MarketItemAnalysis { ItemId = 100, Name = "Old Analysis" }
            ],
            ProcurementShoppingPlans =
            [
                new DetailedShoppingPlan { ItemId = 100, Name = "Old Route" }
            ]
        };
        appState.NotifyPlanChanged();
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 200, Name = "New Analysis" }],
            [new DetailedShoppingPlan { ItemId = 200, Name = "New Plan" }]);

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.False(change.HasScope(AppStateChangeScope.PlanStructure));
        Assert.Equal(200, Assert.Single(appState.MarketItemAnalyses).ItemId);
        Assert.Equal(200, Assert.Single(appState.ShoppingPlans).ItemId);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.True(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.PlanCore));
    }

    [Fact]
    public void ReplaceMarketAnalysisItem_ReplacesSingleAnalysisPlanAndClearsProcurementOverlay()
    {
        var appState = new AppState
        {
            MarketItemAnalyses =
            [
                new MarketItemAnalysis { ItemId = 100, Name = "Keep Analysis" },
                new MarketItemAnalysis { ItemId = 200, Name = "Old Analysis" }
            ],
            ShoppingPlans =
            [
                new DetailedShoppingPlan { ItemId = 100, Name = "Keep Plan" },
                new DetailedShoppingPlan { ItemId = 200, Name = "Old Plan" }
            ],
            ProcurementShoppingPlans =
            [
                new DetailedShoppingPlan { ItemId = 200, Name = "Old Route" }
            ]
        };
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ReplaceMarketAnalysisItem(
            new MarketItemAnalysis { ItemId = 200, Name = "New Analysis" },
            new DetailedShoppingPlan { ItemId = 200, Name = "New Plan" });

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.Equal(["Keep Analysis", "New Analysis"], appState.MarketItemAnalyses.Select(analysis => analysis.Name));
        Assert.Equal(["Keep Plan", "New Plan"], appState.ShoppingPlans.Select(plan => plan.Name));
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.True(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.PlanCore));
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
    public void OperationProgress_WhenTokenIsCurrent_UpdatesStatus()
    {
        var appState = new AppState();
        var operation = appState.BeginOperation("Market Analysis", "Starting market analysis...");

        var updated = appState.SetStatusForOperation(operation, "Fetching market data...", progress: 25);

        Assert.True(updated);
        Assert.True(appState.IsBusy);
        Assert.Equal("Market Analysis", appState.CurrentOperation);
        Assert.Equal("Fetching market data...", appState.StatusMessage);
        Assert.Equal(25, appState.ProgressPercent);
    }

    [Fact]
    public void OperationProgress_WhenTokenIsStale_DoesNotOverwriteNewerStatus()
    {
        var appState = new AppState();
        var staleOperation = appState.BeginOperation("Market Analysis", "Starting market analysis...");
        var currentOperation = appState.BeginOperation("Procurement Analysis", "Starting procurement...");

        var updated = appState.SetStatusForOperation(staleOperation, "Late market progress...");

        Assert.False(updated);
        Assert.True(appState.IsBusy);
        Assert.Equal("Procurement Analysis", appState.CurrentOperation);
        Assert.Equal("Starting procurement...", appState.StatusMessage);
        Assert.True(appState.EndOperation(currentOperation, "Procurement complete."));
    }

    [Fact]
    public void EndOperation_WhenTokenIsStale_DoesNotClearNewerOperation()
    {
        var appState = new AppState();
        var staleOperation = appState.BeginOperation("Market Analysis", "Starting market analysis...");
        appState.BeginOperation("Procurement Analysis", "Starting procurement...");

        var ended = appState.EndOperation(staleOperation, "Market complete.");

        Assert.False(ended);
        Assert.True(appState.IsBusy);
        Assert.Equal("Procurement Analysis", appState.CurrentOperation);
        Assert.Equal("Starting procurement...", appState.StatusMessage);
    }

    [Fact]
    public void CancelOperation_WhenTokenIsCurrent_ClearsBusyWithoutFailureStatus()
    {
        var appState = new AppState();
        var operation = appState.BeginOperation("Market Analysis", "Starting market analysis...");

        var canceled = appState.CancelOperation(operation, "Market analysis canceled.");

        Assert.True(canceled);
        Assert.False(appState.IsBusy);
        Assert.Null(appState.CurrentOperation);
        Assert.Equal(0, appState.ProgressPercent);
        Assert.Equal("Market analysis canceled.", appState.StatusMessage);
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
    public void ClearProcurementOverlay_ClearsOverlayAndPublishesOnlyOverlayScope()
    {
        var appState = new AppState
        {
            ProcurementShoppingPlans =
            [
                new DetailedShoppingPlan { ItemId = 100, Name = "Route Item" }
            ]
        };
        appState.NotifyShoppingListChanged();
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ClearProcurementOverlay();

        var change = Assert.Single(changes);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.False(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
    }

    [Fact]
    public void ReplaceProcurementOverlay_ReplacesOverlayAndPublishesOnlyOverlayScope()
    {
        var appState = new AppState
        {
            ProcurementShoppingPlans =
            [
                new DetailedShoppingPlan { ItemId = 100, Name = "Old Route Item" }
            ]
        };
        var replacement = new List<DetailedShoppingPlan>
        {
            new() { ItemId = 200, Name = "New Route Item" }
        };
        appState.NotifyShoppingListChanged();
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ReplaceProcurementOverlay(replacement);
        replacement.Clear();

        var change = Assert.Single(changes);
        var routePlan = Assert.Single(appState.ProcurementShoppingPlans);
        Assert.Equal(200, routePlan.ItemId);
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.False(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
    }

    [Fact]
    public void ReplaceShoppingItemsFromActivePlan_RebuildsItemsFromAcquisitionDecisionsWithoutDirtyingMarketAnalysis()
    {
        var child = new PlanNode
        {
            ItemId = 200,
            Name = "Bought Child",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            IconId = 456
        };
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Crafted Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Children = [child]
        };
        child.Parent = root;
        var appState = new AppState
        {
            CurrentPlan = new CraftingPlan { RootItems = [root] },
            ShoppingItems =
            [
                new MarketShoppingItem { Id = 999, Name = "Stale Item", Quantity = 1 }
            ]
        };
        appState.NotifyShoppingListChanged();
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ReplaceShoppingItemsFromActivePlan();

        var change = Assert.Single(changes);
        var shoppingItem = Assert.Single(appState.ShoppingItems);
        Assert.Equal(200, shoppingItem.Id);
        Assert.Equal("Bought Child", shoppingItem.Name);
        Assert.Equal(3, shoppingItem.Quantity);
        Assert.True(change.HasScope(AppStateChangeScope.ShoppingItems));
        Assert.False(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
    }

    [Fact]
    public void AddProjectItem_WhenItemIsNew_AddsItemAndPublishesPlanStructure()
    {
        var appState = new AppState();
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        var added = appState.AddProjectItem(new ProjectItem
        {
            Id = 100,
            Name = "Test Item",
            Quantity = 1
        });

        var change = Assert.Single(changes);
        Assert.True(added);
        Assert.Equal(100, Assert.Single(appState.ProjectItems).Id);
        Assert.True(change.HasScope(AppStateChangeScope.PlanStructure));
        Assert.Equal(1, appState.CurrentVersions.PlanStructureVersion);
    }

    [Fact]
    public void AddProjectItem_WhenItemAlreadyExists_DoesNotPublish()
    {
        var appState = new AppState
        {
            ProjectItems =
            [
                new ProjectItem
                {
                    Id = 100,
                    Name = "Test Item",
                    Quantity = 1
                }
            ]
        };
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        var added = appState.AddProjectItem(new ProjectItem
        {
            Id = 100,
            Name = "Duplicate",
            Quantity = 2
        });

        Assert.False(added);
        Assert.Single(appState.ProjectItems);
        Assert.Empty(changes);
    }

    [Fact]
    public void ReplaceProjectItems_ReplacesItemsAndPublishesPlanStructure()
    {
        var appState = new AppState
        {
            ProjectItems =
            [
                new ProjectItem
                {
                    Id = 100,
                    Name = "Old",
                    Quantity = 1
                }
            ]
        };
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ReplaceProjectItems(
        [
            new ProjectItem
            {
                Id = 200,
                Name = "New",
                Quantity = 3
            }
        ]);

        var change = Assert.Single(changes);
        Assert.Equal(200, Assert.Single(appState.ProjectItems).Id);
        Assert.True(change.HasScope(AppStateChangeScope.PlanStructure));
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
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
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
