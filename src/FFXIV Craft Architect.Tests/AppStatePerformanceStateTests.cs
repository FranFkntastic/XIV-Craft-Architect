using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AppStatePerformanceStateTests
{
    [Fact]
    public void Constructor_DefaultMarketSearchScopeIsEntireRegion()
    {
        var appState = new AppState();

        Assert.Equal(MarketFetchScope.EntireRegion, appState.DefaultMarketFetchScope);
        Assert.True(appState.SearchEntireRegion);
    }

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
        var appState = new AppState();
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 100, Name = "Market Item" }],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Market Item" }]);
        appState.ReplaceProcurementOverlay([new DetailedShoppingPlan { ItemId = 100, Name = "Route Item" }]);
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
    public void ApplyPlanDecisionChange_RebuildsShoppingItemsClearsProcurementAndPublishesDecisionScope()
    {
        var appState = new AppState();
        appState.ReplaceProcurementOverlay([new DetailedShoppingPlan { ItemId = 300, Name = "Old Route" }]);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ApplyPlanDecisionChange(
            [new MaterialAggregate { ItemId = 200, Name = "Active Item", TotalQuantity = 4 }],
            clearProcurementOverlay: true);

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.PlanDecision));
        Assert.True(change.HasScope(AppStateChangeScope.ShoppingItems));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        var item = Assert.Single(appState.ShoppingItems);
        Assert.Equal(200, item.Id);
        Assert.Empty(appState.ProcurementShoppingPlans);
    }

    [Fact]
    public void ApplyMarketAnalysisPublication_WithDecisionChanges_ReplacesEvidenceAndPublishesDecisionScope()
    {
        var appState = new AppState();
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ApplyMarketAnalysisPublication(
            [new MarketItemAnalysis { ItemId = 200, Name = "Analysis Item" }],
            [new DetailedShoppingPlan { ItemId = 200, Name = "Analysis Item" }],
            [new MaterialAggregate { ItemId = 300, Name = "Active Item", TotalQuantity = 5 }],
            acquisitionDecisionsChanged: true);

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.True(change.HasScope(AppStateChangeScope.ShoppingItems));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.True(change.HasScope(AppStateChangeScope.PlanDecision));
        Assert.False(change.HasScope(AppStateChangeScope.PlanStructure));
        Assert.Single(appState.ShoppingPlans);
        Assert.Equal(300, Assert.Single(appState.ShoppingItems).Id);
    }

    [Fact]
    public void ApplyMarketAnalysisPublication_WithoutDecisionChanges_RefreshesShoppingItemsWithoutDirtyingPlanCore()
    {
        var appState = new AppState();
        appState.NotifyPlanChanged();
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ApplyMarketAnalysisPublication(
            [new MarketItemAnalysis { ItemId = 200, Name = "Analysis Item" }],
            [new DetailedShoppingPlan { ItemId = 200, Name = "Analysis Item" }],
            [new MaterialAggregate { ItemId = 300, Name = "Active Item", TotalQuantity = 5 }],
            acquisitionDecisionsChanged: false);

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.True(change.HasScope(AppStateChangeScope.ShoppingItems));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.False(change.HasScope(AppStateChangeScope.PlanStructure));
        Assert.False(change.HasScope(AppStateChangeScope.PlanDecision));
        Assert.True(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.PlanCore));
        Assert.Equal(300, Assert.Single(appState.ShoppingItems).Id);
    }

    [Fact]
    public void RequestPlanAndMarketRefresh_PublishesShoppingAndPlanScopes()
    {
        var appState = new AppState();
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.RequestPlanAndMarketRefresh();

        Assert.Collection(
            changes,
            change =>
            {
                Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
                Assert.True(change.HasScope(AppStateChangeScope.ShoppingItems));
            },
            change => Assert.True(change.HasScope(AppStateChangeScope.PlanStructure)));
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
        var source = new List<CoreMarketDataUnavailableItem>
        {
            new(123, "Unavailable Item")
        };

        appState.SetUnavailableMarketItems(source);
        source.Add(new CoreMarketDataUnavailableItem(456, "Late Mutation"));

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.True(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.PlanCore));
        Assert.Equal(123, Assert.Single(appState.UnavailableMarketItems).ItemId);
    }

    [Fact]
    public void ReplaceMarketAnalysis_ReplacesAnalysisProjectionAndClearsProcurementOverlay()
    {
        var appState = new AppState();
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 100, Name = "Old Analysis" }],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Old Plan" }]);
        appState.ReplaceProcurementOverlay([new DetailedShoppingPlan { ItemId = 100, Name = "Old Route" }]);
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
    public void MarketAnalysisViewStateChanges_EmitViewScopeWithoutDirtyingPersistedBucketsOrCoreVersions()
    {
        var appState = new AppState();
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 100, Name = "Market Item" }],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Market Item" }]);
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);
        var beforeVersions = appState.CurrentVersions;
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.SelectMarketAnalysisItem(100);
        appState.ToggleMarketAnalysisWorld(100, "Aether", "Siren");
        appState.SetMarketAnalysisGridSort(MarketAnalysisGridSortColumn.Total, descending: true);
        appState.SetMarketAnalysisWorldGridSort(MarketAnalysisWorldGridSortColumn.StockDepth, descending: true);

        Assert.Equal(4, changes.Count);
        Assert.All(changes, change => Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysisView)));
        Assert.All(changes, change => Assert.False(change.HasScope(AppStateChangeScope.MarketAnalysis)));
        Assert.Equal(beforeVersions, appState.CurrentVersions);
        Assert.Equal(100, appState.SelectedMarketAnalysisItemId);
        Assert.Contains(new MarketAnalysisExpandedWorldKey(100, "Aether", "Siren"), appState.ExpandedMarketAnalysisWorlds);
        Assert.Equal(MarketAnalysisGridSortColumn.Total, appState.MarketAnalysisGridSortColumn);
        Assert.True(appState.MarketAnalysisGridSortDescending);
        Assert.Equal(MarketAnalysisWorldGridSortColumn.StockDepth, appState.MarketAnalysisWorldGridSortColumn);
        Assert.True(appState.MarketAnalysisWorldGridSortDescending);
        Assert.Equal(PersistedStateBucket.None, appState.GetDirtyPersistedBuckets());
    }

    [Fact]
    public void ClearMarketAnalysisState_ClearsMarketAnalysisViewState()
    {
        var appState = new AppState();
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 100, Name = "Market Item" }],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Market Item" }]);
        appState.SelectMarketAnalysisItem(100);
        appState.ToggleMarketAnalysisWorld(100, "Aether", "Siren");
        appState.SetMarketAnalysisGridSort(MarketAnalysisGridSortColumn.Total, descending: true);
        appState.SetMarketAnalysisWorldGridSort(MarketAnalysisWorldGridSortColumn.StockDepth, descending: true);

        appState.ClearMarketAnalysisState();

        Assert.Null(appState.SelectedMarketAnalysisItemId);
        Assert.Empty(appState.ExpandedMarketAnalysisWorlds);
        Assert.Null(appState.MarketAnalysisGridSortColumn);
        Assert.False(appState.MarketAnalysisGridSortDescending);
        Assert.Null(appState.MarketAnalysisWorldGridSortColumn);
        Assert.False(appState.MarketAnalysisWorldGridSortDescending);
    }

    [Fact]
    public void SetMarketEvidenceSettings_WhenMarketContextChanges_PreservesMarketEvidenceAndMarksScopeStale()
    {
        var appState = new AppState();
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis
                {
                    ItemId = 100,
                    Name = "Market Item",
                    Worlds = [new WorldMarketAnalysis { DataCenter = "Aether", WorldName = "Siren" }]
                }
            ],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Market Item" }],
            publishedScope: new PublishedMarketAnalysisScopeSnapshot(
                MarketFetchScope.EntireRegion,
                "Aether",
                "North America",
                ["Aether", "Primal", "Crystal", "Dynamis"],
                MarketAcquisitionLens.MinimumUpfrontCost,
                appState.PlanSessionVersion,
                new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc)));
        appState.SelectMarketAnalysisItem(100);
        appState.ToggleMarketAnalysisWorld(100, "Aether", "Siren");
        appState.SetMarketAnalysisGridSort(MarketAnalysisGridSortColumn.Total, descending: true);
        appState.SetMarketAnalysisWorldGridSort(MarketAnalysisWorldGridSortColumn.StockDepth, descending: true);

        appState.SetMarketEvidenceSettings(
            "Primal",
            "North America",
            MarketFetchScope.SelectedDataCenter,
            searchEntireRegion: false);

        Assert.Single(appState.MarketItemAnalyses);
        Assert.Single(appState.ShoppingPlans);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.Contains("Entire Region: North America", appState.MarketAnalysisScopeWarning);
        Assert.Contains("Selected Data Center: Primal", appState.MarketAnalysisScopeWarning);
        Assert.Null(appState.SelectedMarketAnalysisItemId);
        Assert.Empty(appState.ExpandedMarketAnalysisWorlds);
        Assert.Null(appState.MarketAnalysisGridSortColumn);
        Assert.False(appState.MarketAnalysisGridSortDescending);
        Assert.Null(appState.MarketAnalysisWorldGridSortColumn);
        Assert.False(appState.MarketAnalysisWorldGridSortDescending);
    }

    [Fact]
    public void ReplaceMarketAnalysis_PreservesValidMarketAnalysisViewStateAndPrunesInvalidKeys()
    {
        var appState = new AppState();
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis
                {
                    ItemId = 100,
                    Name = "Keep",
                    Worlds = [new WorldMarketAnalysis { DataCenter = "Aether", WorldName = "Siren" }]
                },
                new MarketItemAnalysis
                {
                    ItemId = 200,
                    Name = "Remove",
                    Worlds = [new WorldMarketAnalysis { DataCenter = "Aether", WorldName = "Faerie" }]
                }
            ],
            [
                new DetailedShoppingPlan { ItemId = 100, Name = "Keep" },
                new DetailedShoppingPlan { ItemId = 200, Name = "Remove" }
            ]);
        appState.SelectMarketAnalysisItem(100);
        appState.ToggleMarketAnalysisWorld(100, "Aether", "Siren");
        appState.ToggleMarketAnalysisWorld(200, "Aether", "Faerie");
        appState.SetMarketAnalysisGridSort(MarketAnalysisGridSortColumn.Total, descending: true);
        appState.SetMarketAnalysisWorldGridSort(MarketAnalysisWorldGridSortColumn.StockDepth, descending: true);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis
                {
                    ItemId = 100,
                    Name = "Keep Updated",
                    Worlds = [new WorldMarketAnalysis { DataCenter = "Aether", WorldName = "Siren" }]
                }
            ],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Keep Updated" }]);

        Assert.Equal(100, appState.SelectedMarketAnalysisItemId);
        Assert.Equal([new MarketAnalysisExpandedWorldKey(100, "Aether", "Siren")], appState.ExpandedMarketAnalysisWorlds);
        Assert.Equal(MarketAnalysisGridSortColumn.Total, appState.MarketAnalysisGridSortColumn);
        Assert.True(appState.MarketAnalysisGridSortDescending);
        Assert.Equal(MarketAnalysisWorldGridSortColumn.StockDepth, appState.MarketAnalysisWorldGridSortColumn);
        Assert.True(appState.MarketAnalysisWorldGridSortDescending);
        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysisView));
    }

    [Fact]
    public void ReplaceMarketAnalysisItem_ReplacesSingleAnalysisPlanAndClearsProcurementOverlay()
    {
        var appState = new AppState();
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis { ItemId = 100, Name = "Keep Analysis" },
                new MarketItemAnalysis { ItemId = 200, Name = "Old Analysis" }
            ],
            [
                new DetailedShoppingPlan { ItemId = 100, Name = "Keep Plan" },
                new DetailedShoppingPlan { ItemId = 200, Name = "Old Plan" }
            ]);
        appState.ReplaceProcurementOverlay([new DetailedShoppingPlan { ItemId = 200, Name = "Old Route" }]);
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
        var appState = new AppState();
        appState.ReplaceProcurementOverlay([new DetailedShoppingPlan { ItemId = 100, Name = "Route Item" }]);
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
        var appState = new AppState();
        appState.ReplaceProcurementOverlay([new DetailedShoppingPlan { ItemId = 100, Name = "Old Route Item" }]);
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
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(new CraftingPlan { RootItems = [root] });
        appState.SyncProjectToShopping();
        appState.NotifyShoppingListChanged();
        appState.MarkPersisted(PersistedStateBucket.All, appState.CurrentVersions);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ReplaceShoppingItemsFromActivePlan(
            AppStateRecipeLayerTestExtensions.BuildActiveProcurementItems(appState.CurrentPlan));

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
    public void ReplaceShoppingItemsFromActivePlan_RequiresExplicitActiveProcurementItems()
    {
        var method = typeof(AppState).GetMethod(
            nameof(AppState.ReplaceShoppingItemsFromActivePlan),
            [typeof(IReadOnlyList<MaterialAggregate>)]);

        Assert.NotNull(method);
        var parameter = Assert.Single(method.GetParameters());
        Assert.False(parameter.IsOptional);
    }

    [Fact]
    public void ApplyBuiltRecipePlan_ReplacesPlanClearsDerivedMarketStateAndAdvancesSession()
    {
        var appState = new AppState();
        var oldPlan = new CraftingPlan { RootItems = [new PlanNode { ItemId = 1, Name = "Old", Quantity = 1 }] };
        var newPlan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 100,
                    Name = "Built Root",
                    Quantity = 1,
                    Source = AcquisitionSource.Craft,
                    Children =
                    [
                        new PlanNode
                        {
                            ItemId = 200,
                            Name = "Bought Child",
                            Quantity = 3,
                            Source = AcquisitionSource.MarketBuyNq,
                            CanBuyFromMarket = true
                        }
                    ]
                }
            ]
        };
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 100, Name = "Old Analysis" }],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Old Plan" }]);
        appState.ReplaceProcurementOverlay([new DetailedShoppingPlan { ItemId = 100, Name = "Old Route" }]);
        appState.SetUnavailableMarketItems([new CoreMarketDataUnavailableItem(100, "Old Unavailable")]);
        appState.RequestMarketItemAutoExpand(100);
        appState.ApplyBuiltRecipePlanWithActiveItems(oldPlan);
        var previousSession = appState.PlanSessionVersion;
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ApplyBuiltRecipePlanWithActiveItems(newPlan);

        var change = Assert.Single(changes);
        Assert.Same(newPlan, appState.CurrentPlan);
        Assert.True(appState.PlanSessionVersion > previousSession);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.Empty(appState.UnavailableMarketItems);
        Assert.Null(appState.AutoExpandItemId);
        Assert.Equal(200, Assert.Single(appState.ShoppingItems).Id);
        Assert.True(change.HasScope(AppStateChangeScope.PlanStructure));
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.True(change.HasScope(AppStateChangeScope.ShoppingItems));
    }

    [Fact]
    public void TrackCurrentPlanIdentity_DoesNotAdvancePlanSession()
    {
        var appState = new AppState();
        var session = appState.PlanSessionVersion;

        appState.TrackCurrentPlanIdentity("plan-1", "Plan 1");
        appState.RenameCurrentPlanIdentity("plan-1", "Renamed");
        appState.ClearCurrentPlanId();

        Assert.Equal(session, appState.PlanSessionVersion);
    }

    [Fact]
    public void IsCurrentPlanSession_RejectsOldPlanAfterNewSession()
    {
        var appState = new AppState();
        var oldPlan = new CraftingPlan { RootItems = [new PlanNode { ItemId = 1, Name = "Old", Quantity = 1 }] };
        var newPlan = new CraftingPlan { RootItems = [new PlanNode { ItemId = 2, Name = "New", Quantity = 1 }] };
        appState.ApplyBuiltRecipePlanWithActiveItems(oldPlan);
        var oldSession = appState.PlanSessionVersion;

        appState.ApplyBuiltRecipePlanWithActiveItems(newPlan);

        Assert.False(appState.IsCurrentPlanSession(oldPlan, oldSession));
        Assert.True(appState.IsCurrentPlanSession(newPlan, appState.PlanSessionVersion));
    }

    [Fact]
    public void ApplyImportedProjectItems_ClearsActivePlanIdentityAndDerivedState()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(new CraftingPlan { RootItems = [new PlanNode { ItemId = 1, Name = "Old", Quantity = 1 }] });
        appState.TrackCurrentPlanIdentity("old-plan", "Old Plan");
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 100, Name = "Old Analysis" }],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Old Plan" }]);
        var previousSession = appState.PlanSessionVersion;
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ApplyImportedProjectItems(
        [
            new ProjectItem { Id = 200, Name = "Imported Item", Quantity = 4 }
        ]);

        var change = Assert.Single(changes);
        Assert.Null(appState.CurrentPlan);
        Assert.Null(appState.CurrentPlanId);
        Assert.Null(appState.CurrentPlanName);
        Assert.True(appState.PlanSessionVersion > previousSession);
        Assert.Equal(200, Assert.Single(appState.ProjectItems).Id);
        Assert.Empty(appState.ShoppingItems);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.True(change.HasScope(AppStateChangeScope.PlanStructure));
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
    }

    [Fact]
    public void ActivateRecipePlan_ReplacesPlanProjectItemsMarketContextAndAdvancesSession()
    {
        var plan = new CraftingPlan
        {
            DataCenter = "Primal",
            RootItems = [new PlanNode { ItemId = 100, Name = "Native Root", Quantity = 2 }]
        };
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(new CraftingPlan { RootItems = [new PlanNode { ItemId = 1, Name = "Old", Quantity = 1 }] });
        appState.TrackCurrentPlanIdentity("old-plan", "Old Plan");
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 100, Name = "Old Analysis" }],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Old Plan" }]);
        var previousSession = appState.PlanSessionVersion;
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ActivateRecipePlanWithActiveItems(
            plan,
            [new ProjectItem { Id = 100, Name = "Native Root", Quantity = 2 }],
            plan.DataCenter,
            clearCurrentPlanId: true);

        var change = Assert.Single(changes);
        Assert.Same(plan, appState.CurrentPlan);
        Assert.Equal("Primal", appState.SelectedDataCenter);
        Assert.Null(appState.CurrentPlanId);
        Assert.Null(appState.CurrentPlanName);
        Assert.True(appState.PlanSessionVersion > previousSession);
        Assert.Equal(100, Assert.Single(appState.ProjectItems).Id);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.True(change.HasScope(AppStateChangeScope.PlanStructure));
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
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
        var appState = new AppState();
        appState.ReplaceProjectItems([new ProjectItem { Id = 100, Name = "Test Item", Quantity = 1 }]);
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
        var appState = new AppState();
        appState.ReplaceProjectItems([new ProjectItem { Id = 100, Name = "Old", Quantity = 1 }]);
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
    public void UpdateProjectItemQuantity_ClampsQuantityAndPublishesPlanStructure()
    {
        var appState = new AppState();
        var item = new ProjectItem { Id = 100, Name = "Target", Quantity = 1 };
        appState.ReplaceProjectItems([item]);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        var changed = appState.UpdateProjectItemQuantity(item, 20_000);
        var unchanged = appState.UpdateProjectItemQuantity(item, 9_999);

        Assert.True(changed);
        Assert.False(unchanged);
        Assert.Equal(9_999, Assert.Single(appState.ProjectItems).Quantity);
        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.PlanStructure));
    }

    [Fact]
    public void ProjectItems_ReturnSnapshotsThatCannotMutateBackingState()
    {
        var appState = new AppState();
        var sourceItem = new ProjectItem { Id = 100, Name = "Target", Quantity = 1, MustBeHq = false };
        appState.ReplaceProjectItems([sourceItem]);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        sourceItem.Quantity = 99;
        sourceItem.MustBeHq = true;
        var publishedItem = Assert.Single(appState.ProjectItems);
        publishedItem.Quantity = 123;
        publishedItem.MustBeHq = true;

        var currentItem = Assert.Single(appState.ProjectItems);
        Assert.Equal(1, currentItem.Quantity);
        Assert.False(currentItem.MustBeHq);
        Assert.Empty(changes);
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
        Assert.True(change.HasScope(AppStateChangeScope.Settings));
    }

    [Fact]
    public void AutoSaveGuard_SkipsWhenCleanAndBlocksConcurrentSave()
    {
        var appState = new AppState();
        appState.ReplaceProjectItems([new ProjectItem { Id = 123, Name = "Current Item", Quantity = 10 }]);

        Assert.True(appState.TryBeginAutoSave(out var capturedVersions, out var dirtyBuckets));
        Assert.Equal(PersistedStateBucket.PlanCore, dirtyBuckets);
        Assert.False(appState.TryBeginAutoSave(out _, out _));

        appState.CompleteAutoSave(succeeded: true, capturedVersions, dirtyBuckets);

        Assert.False(appState.TryBeginAutoSave(out _, out _));
    }

    [Fact]
    public void RecordAutoSaveCompleted_UpdatesLastAutoSave()
    {
        var appState = new AppState();
        var completedAt = new DateTime(2026, 5, 30, 12, 34, 56, DateTimeKind.Local);

        appState.RecordAutoSaveCompleted(completedAt);

        Assert.Equal(completedAt, appState.LastAutoSave);
    }

    [Fact]
    public void SetMarketEvidenceSettings_WhenLocationChanges_PreservesMarketAnalysisAndClearsProcurementOverlay()
    {
        var appState = new AppState();
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 100, Name = "Market Item" }],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Market Item" }],
            publishedScope: appState.CreateCurrentMarketAnalysisScopeSnapshot(new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc)));
        appState.ReplaceProcurementOverlay([new DetailedShoppingPlan { ItemId = 100, Name = "Route Item" }]);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.SetMarketEvidenceSettings(
            dataCenter: "Primal",
            region: "North America",
            defaultFetchScope: MarketFetchScope.SelectedDataCenter,
            searchEntireRegion: false);

        var change = Assert.Single(changes);
        Assert.Equal("Primal", appState.SelectedDataCenter);
        Assert.Single(appState.ShoppingPlans);
        Assert.Single(appState.MarketItemAnalyses);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.NotNull(appState.MarketAnalysisScopeWarning);
        Assert.True(change.HasScope(AppStateChangeScope.Settings));
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
    }

    [Fact]
    public void SetProcurementSettings_WhenRouteMeaningChanges_ClearsProcurementOverlayOnly()
    {
        var appState = new AppState();
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 100, Name = "Market Item" }],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Market Item" }]);
        appState.ReplaceProcurementOverlay([new DetailedShoppingPlan { ItemId = 100, Name = "Route Item" }]);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.SetProcurementSettings(
            searchEntireRegion: true,
            enableSplitWorldPurchases: true,
            travelTolerance: 7,
            temporaryWorldBlacklistDurationMinutes: 60);

        var change = Assert.Single(changes);
        Assert.True(appState.ProcurementSearchEntireRegion);
        Assert.True(appState.ProcurementEnableSplitWorldPurchases);
        Assert.Equal(7, appState.ProcurementTravelTolerance);
        Assert.Single(appState.ShoppingPlans);
        Assert.Single(appState.MarketItemAnalyses);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.True(change.HasScope(AppStateChangeScope.Settings));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.False(change.HasScope(AppStateChangeScope.MarketAnalysis));
    }

    [Fact]
    public void SettingsOnlyMutators_PublishSettingsWithoutInvalidatingMarketOrProcurementState()
    {
        AssertSettingsOnlyChange(
            state => state.SetMarketAnalysisLens(MarketAcquisitionLens.BulkValue),
            state => state.SetMarketAnalysisLens(MarketAcquisitionLens.BulkValue),
            state => Assert.Equal(MarketAcquisitionLens.BulkValue, state.MarketAnalysisLens));
        AssertSettingsOnlyChange(
            state => state.SetAutoSaveEnabled(false),
            state => state.SetAutoSaveEnabled(false),
            state => Assert.False(state.IsAutoSaveEnabled));
        AssertSettingsOnlyChange(
            state => state.SetMarketSortPreference(MarketSortOption.Alphabetical),
            state => state.SetMarketSortPreference(MarketSortOption.Alphabetical),
            state => Assert.Equal(MarketSortOption.Alphabetical, state.MarketSortPreference));
    }

    [Fact]
    public void ReplaceSavedPlans_CopiesIncomingSequenceAndRaisesLegacyEventOnly()
    {
        var appState = new AppState();
        var source = new List<StoredPlanSummary>
        {
            new() { Id = "plan-1", Name = "Plan 1" }
        };
        var legacyEventCount = 0;
        var stateChanges = new List<AppStateChange>();
        appState.OnSavedPlansChanged += () => legacyEventCount++;
        appState.OnStateChanged += stateChanges.Add;

        appState.ReplaceSavedPlans(source);
        source.Add(new StoredPlanSummary { Id = "plan-2", Name = "Plan 2" });

        Assert.Equal(1, legacyEventCount);
        Assert.Empty(stateChanges);
        Assert.Equal("plan-1", Assert.Single(appState.SavedPlans).Id);
    }

    [Fact]
    public void ClearSavedPlans_RaisesLegacyEventOnly()
    {
        var appState = new AppState();
        appState.ReplaceSavedPlans([new StoredPlanSummary { Id = "plan-1", Name = "Plan 1" }]);
        var legacyEventCount = 0;
        var stateChanges = new List<AppStateChange>();
        appState.OnSavedPlansChanged += () => legacyEventCount++;
        appState.OnStateChanged += stateChanges.Add;

        appState.ClearSavedPlans();

        Assert.Equal(1, legacyEventCount);
        Assert.Empty(stateChanges);
        Assert.Empty(appState.SavedPlans);
    }

    [Fact]
    public void RequestMarketItemAutoExpand_PersistsUntilTargetConsumed()
    {
        var appState = new AppState();

        appState.RequestMarketItemAutoExpand(123);

        Assert.Equal(123, appState.AutoExpandItemId);
        Assert.False(appState.ConsumeMarketItemAutoExpand(456));
        Assert.Equal(123, appState.AutoExpandItemId);
        Assert.True(appState.ConsumeMarketItemAutoExpand(123));
        Assert.Null(appState.AutoExpandItemId);
    }

    [Fact]
    public void RequestMarketItemAutoExpand_SelectsExistingMarketAnalysisItem()
    {
        var appState = new AppState();
        appState.ReplaceMarketAnalysis(
            Array.Empty<MarketItemAnalysis>(),
            [new DetailedShoppingPlan { ItemId = 123, Name = "Target" }]);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.RequestMarketItemAutoExpand(123);

        Assert.Equal(123, appState.SelectedMarketAnalysisItemId);
        Assert.Null(appState.AutoExpandItemId);
        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.MarketAnalysisView));
    }

    [Fact]
    public void ReplaceMarketAnalysis_AppliesPendingMarketItemAutoExpandWhenItemArrives()
    {
        var appState = new AppState();
        appState.RequestMarketItemAutoExpand(123);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        appState.ReplaceMarketAnalysis(
            Array.Empty<MarketItemAnalysis>(),
            [new DetailedShoppingPlan { ItemId = 123, Name = "Target" }]);

        Assert.Equal(123, appState.SelectedMarketAnalysisItemId);
        Assert.Null(appState.AutoExpandItemId);
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.MarketAnalysisView));
    }

    private static void AssertSettingsOnlyChange(
        Func<AppState, bool> change,
        Func<AppState, bool> unchanged,
        Action<AppState> assertValue)
    {
        var appState = new AppState();
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;

        Assert.True(change(appState));
        Assert.False(unchanged(appState));

        assertValue(appState);
        var stateChange = Assert.Single(changes);
        Assert.True(stateChange.HasScope(AppStateChangeScope.Settings));
        Assert.False(stateChange.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.False(stateChange.HasScope(AppStateChangeScope.ProcurementOverlay));
    }
}
