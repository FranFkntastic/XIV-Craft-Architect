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
    public void ReplaceMarketAnalysisItem_ReplacesSingleAnalysisPlanAndMarksProcurementOverlayStale()
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
        Assert.Single(appState.ProcurementShoppingPlans);
        Assert.True(appState.IsProcurementRouteStale);
        Assert.Equal("Market evidence changed.", appState.ProcurementRouteStaleReason);
        Assert.True(appState.IsPersistedBucketDirty(PersistedStateBucket.MarketAnalysis));
        Assert.False(appState.IsPersistedBucketDirty(PersistedStateBucket.PlanCore));
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
    public void SetProcurementSettings_WhenRouteMeaningChanges_PreservesAndMarksProcurementOverlayStale()
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
        Assert.Single(appState.ProcurementShoppingPlans);
        Assert.True(appState.IsProcurementRouteStale);
        Assert.Equal("Route settings changed.", appState.ProcurementRouteStaleReason);
        Assert.True(change.HasScope(AppStateChangeScope.Settings));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.False(change.HasScope(AppStateChangeScope.MarketAnalysis));
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
