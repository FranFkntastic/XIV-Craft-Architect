using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.JSInterop;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class ProcurementWorkflowServiceTests
{
    [Fact]
    public async Task RunAnalysisAsync_PublishesProcurementOverlay()
    {
        var appState = CreateAppState();
        appState.ReplaceMarketAnalysis([], [ShoppingPlan(101), ShoppingPlan(202)]);
        var execution = new Mock<IProcurementRouteExecutionService>();
        execution.Setup(e => e.AnalyzeAsync(
                It.IsAny<ProcurementRouteExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(new ProcurementRouteExecutionResult(
                [ShoppingPlan(101, "Siren"), ShoppingPlan(202, "Faerie")],
                [],
                [],
                [],
                []));
        var service = CreateService(appState, procurementExecution: execution.Object);
        var before = appState.CurrentVersions;

        var result = await service.RunAnalysisAsync(
            new ProcurementWorkflowRequest(() => true, MarketAnalysisExecutionOptions.Synchronous));

        Assert.Equal(ProcurementWorkflowStatus.Published, result.Status);
        Assert.Equal(2, result.ShoppingPlanCount);
        Assert.Equal([101, 202], appState.ProcurementShoppingPlans.Select(plan => plan.ItemId));
        Assert.Equal(before.PlanDecisionVersion, appState.CurrentVersions.PlanDecisionVersion);
        Assert.Equal(before.MarketAnalysisVersion, appState.CurrentVersions.MarketAnalysisVersion);
        Assert.Equal(before.ProcurementOverlayVersion + 1, appState.CurrentVersions.ProcurementOverlayVersion);
        execution.Verify(e => e.AnalyzeAsync(
            It.Is<ProcurementRouteExecutionRequest>(request =>
                ReferenceEquals(request.Plan, appState.CurrentPlan) &&
                request.SourceShoppingPlans.Count == 2 &&
                request.Scope == MarketFetchScope.SelectedDataCenter &&
                request.SelectedDataCenter == "Aether" &&
                request.SelectedRegion == "North America" &&
                request.Lens == appState.MarketAnalysisLens &&
                request.ProcurementConfig.TravelTolerance == appState.ProcurementTravelTolerance),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<MarketAnalysisExecutionOptions?>()));
    }

    [Fact]
    public async Task RunAnalysisAsync_DisplayOnlyScopeFields_DoNotChangeSourcePlans()
    {
        var appState = CreateAppState();
        var sourcePlan = ShoppingPlan(101, "Siren");
        sourcePlan.RecommendedWorld!.TotalCost = 1010;
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis
                {
                    ItemId = 101,
                    Name = "Item 101",
                    QuantityNeeded = 5,
                    AnalysisScopeBaselineUnitPrice = 100,
                    SaneThresholdUnitPrice = 200,
                    Worlds =
                    [
                        new WorldMarketAnalysis
                        {
                            DataCenter = "Aether",
                            WorldName = "Siren",
                            QuantityNeeded = 5,
                            AnalysisScopeBaselineUnitPrice = 100,
                            SaneThresholdUnitPrice = 200,
                            ScopeSaneQuantity = 5,
                            ScopeInsaneQuantity = 99,
                            ScopeCompetitiveQuantity = 1
                        }
                    ]
                }
            ],
            [sourcePlan]);
        ProcurementRouteExecutionRequest? capturedRequest = null;
        var execution = new Mock<IProcurementRouteExecutionService>();
        execution.Setup(e => e.AnalyzeAsync(
                It.IsAny<ProcurementRouteExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback<ProcurementRouteExecutionRequest, IProgress<string>?, CancellationToken, MarketAnalysisExecutionOptions?>(
                (request, _, _, _) => capturedRequest = request)
            .ReturnsAsync(new ProcurementRouteExecutionResult([ShoppingPlan(101, "Faerie")], [], [], [], []));
        var service = CreateService(appState, procurementExecution: execution.Object);

        var result = await service.RunAnalysisAsync(
            new ProcurementWorkflowRequest(() => true, MarketAnalysisExecutionOptions.Synchronous));

        Assert.Equal(ProcurementWorkflowStatus.Published, result.Status);
        Assert.NotNull(capturedRequest);
        var requestPlan = Assert.Single(capturedRequest.SourceShoppingPlans);
        Assert.Same(sourcePlan, requestPlan);
        Assert.Equal("Siren", requestPlan.RecommendedWorld?.WorldName);
        Assert.Equal(1010, requestPlan.RecommendedWorld?.TotalCost);
    }

    [Fact]
    public async Task RunAnalysisAsync_DisplayOnlyScopeFields_DoNotChangeRealRouteOutput()
    {
        var withoutDisplayFields = await RunRealRouteWithDisplayFieldsAsync(includeDisplayFields: false);
        var withDisplayFields = await RunRealRouteWithDisplayFieldsAsync(includeDisplayFields: true);

        Assert.Equal(withoutDisplayFields.Status, withDisplayFields.Status);
        Assert.Equal(withoutDisplayFields.ShoppingPlanCount, withDisplayFields.ShoppingPlanCount);
        Assert.Equal(
            withoutDisplayFields.Output.Select(ToRouteSummary).ToArray(),
            withDisplayFields.Output.Select(ToRouteSummary).ToArray());
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenDecisionVersionChanges_DoesNotPublish()
    {
        var appState = CreateAppState();
        var execution = new Mock<IProcurementRouteExecutionService>();
        execution.Setup(e => e.AnalyzeAsync(
                It.IsAny<ProcurementRouteExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => appState.NotifyPlanDecisionChanged())
            .ReturnsAsync(new ProcurementRouteExecutionResult([ShoppingPlan(101)], [], [], [], []));
        var service = CreateService(appState, procurementExecution: execution.Object);
        var beforeOverlayVersion = appState.CurrentVersions.ProcurementOverlayVersion;

        var result = await service.RunAnalysisAsync(
            new ProcurementWorkflowRequest(() => true, MarketAnalysisExecutionOptions.Synchronous));

        Assert.Equal(ProcurementWorkflowStatus.StaleDecision, result.Status);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.Equal(beforeOverlayVersion, appState.CurrentVersions.ProcurementOverlayVersion);
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenPlanChanges_DoesNotPublish()
    {
        var appState = CreateAppState();
        var replacementPlan = CreatePlan(303);
        var execution = new Mock<IProcurementRouteExecutionService>();
        execution.Setup(e => e.AnalyzeAsync(
                It.IsAny<ProcurementRouteExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => appState.ApplyBuiltRecipePlan(replacementPlan))
            .ReturnsAsync(new ProcurementRouteExecutionResult([ShoppingPlan(101)], [], [], [], []));
        var service = CreateService(appState, procurementExecution: execution.Object);

        var result = await service.RunAnalysisAsync(
            new ProcurementWorkflowRequest(() => true, MarketAnalysisExecutionOptions.Synchronous));

        Assert.Equal(ProcurementWorkflowStatus.StalePlan, result.Status);
        Assert.Empty(appState.ProcurementShoppingPlans);
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenOperationNoLongerCurrent_DoesNotPublish()
    {
        var appState = CreateAppState();
        var isCurrent = true;
        var execution = new Mock<IProcurementRouteExecutionService>();
        execution.Setup(e => e.AnalyzeAsync(
                It.IsAny<ProcurementRouteExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => isCurrent = false)
            .ReturnsAsync(new ProcurementRouteExecutionResult([ShoppingPlan(101)], [], [], [], []));
        var service = CreateService(appState, procurementExecution: execution.Object);

        var result = await service.RunAnalysisAsync(
            new ProcurementWorkflowRequest(() => isCurrent, MarketAnalysisExecutionOptions.Synchronous));

        Assert.Equal(ProcurementWorkflowStatus.Superseded, result.Status);
        Assert.Empty(appState.ProcurementShoppingPlans);
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenProcurementSettingsChange_DoesNotPublish()
    {
        var appState = CreateAppState();
        var execution = new Mock<IProcurementRouteExecutionService>();
        execution.Setup(e => e.AnalyzeAsync(
                It.IsAny<ProcurementRouteExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => appState.SetProcurementSettings(
                searchEntireRegion: true,
                enableSplitWorldPurchases: true,
                travelTolerance: 11,
                temporaryWorldBlacklistDurationMinutes: appState.TemporaryWorldBlacklistDurationMinutes))
            .ReturnsAsync(new ProcurementRouteExecutionResult([ShoppingPlan(101)], [], [], [], []));
        var service = CreateService(appState, procurementExecution: execution.Object);

        var result = await service.RunAnalysisAsync(
            new ProcurementWorkflowRequest(() => true, MarketAnalysisExecutionOptions.Synchronous));

        Assert.Equal(ProcurementWorkflowStatus.StaleConfiguration, result.Status);
        Assert.Empty(appState.ProcurementShoppingPlans);
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenMarketEvidenceChanges_DoesNotPublish()
    {
        var appState = CreateAppState();
        var execution = new Mock<IProcurementRouteExecutionService>();
        execution.Setup(e => e.AnalyzeAsync(
                It.IsAny<ProcurementRouteExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => appState.ReplaceMarketAnalysis(
                [new MarketItemAnalysis { ItemId = 303, Name = "New Evidence" }],
                [ShoppingPlan(303)]))
            .ReturnsAsync(new ProcurementRouteExecutionResult([ShoppingPlan(101)], [], [], [], []));
        var service = CreateService(appState, procurementExecution: execution.Object);

        var result = await service.RunAnalysisAsync(
            new ProcurementWorkflowRequest(() => true, MarketAnalysisExecutionOptions.Synchronous));

        Assert.Equal(ProcurementWorkflowStatus.StaleConfiguration, result.Status);
        Assert.Empty(appState.ProcurementShoppingPlans);
    }

    [Fact]
    public async Task RefreshItemMarketDataAsync_ReplacesMarketAnalysisItemAndSavesCapturedPlan()
    {
        var appState = CreateAppState();
        appState.TrackCurrentPlanIdentity("plan-1", "Saved Plan");
        var jsRuntime = new RecordingJsRuntime();
        var marketExecution = new Mock<IMarketAnalysisExecutionService>();
        marketExecution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEmptyEvidence(),
                [new MarketItemAnalysis { ItemId = 101, Name = "Item 101", QuantityNeeded = 5 }],
                [ShoppingPlan(101, "Siren")]));
        var service = CreateService(appState, marketExecution: marketExecution.Object, jsRuntime: jsRuntime);

        var result = await service.RefreshItemMarketDataAsync(
            new ProcurementItemRefreshWorkflowRequest(
                ItemId: 101,
                ItemName: "Item 101",
                IsCurrentOperation: () => true,
                ExecutionOptions: MarketAnalysisExecutionOptions.Synchronous));

        Assert.Equal(ProcurementItemRefreshStatus.Refreshed, result.Status);
        Assert.Equal(101, Assert.Single(appState.MarketItemAnalyses).ItemId);
        Assert.Equal(101, Assert.Single(appState.ShoppingPlans).ItemId);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.Equal(["plan-1"], jsRuntime.PatchedPlanIds);
        marketExecution.Verify(e => e.ExecuteAsync(
            It.Is<MarketAnalysisExecutionRequest>(request =>
                request.Items.Count == 1 &&
                request.Items[0].ItemId == 101 &&
                request.MaxAge == TimeSpan.Zero),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<MarketAnalysisExecutionOptions?>()));
    }

    [Fact]
    public async Task RefreshItemMarketDataAsync_WhenPlanChangesAfterFetch_DoesNotPublishOrSave()
    {
        var appState = CreateAppState();
        appState.TrackCurrentPlanIdentity("plan-1", null);
        var jsRuntime = new RecordingJsRuntime();
        var marketExecution = new Mock<IMarketAnalysisExecutionService>();
        marketExecution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => appState.ApplyBuiltRecipePlan(CreatePlan(303)))
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEmptyEvidence(),
                [new MarketItemAnalysis { ItemId = 101, Name = "Item 101", QuantityNeeded = 5 }],
                [ShoppingPlan(101, "Siren")]));
        var service = CreateService(appState, marketExecution: marketExecution.Object, jsRuntime: jsRuntime);

        var result = await service.RefreshItemMarketDataAsync(
            new ProcurementItemRefreshWorkflowRequest(
                ItemId: 101,
                ItemName: "Item 101",
                IsCurrentOperation: () => true,
                ExecutionOptions: MarketAnalysisExecutionOptions.Synchronous));

        Assert.Equal(ProcurementItemRefreshStatus.StalePlan, result.Status);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(jsRuntime.PatchedPlanIds);
    }

    [Fact]
    public async Task RefreshItemMarketDataAsync_WhenDecisionVersionChanges_DoesNotPublishOrSave()
    {
        var appState = CreateAppState();
        appState.TrackCurrentPlanIdentity("plan-1", null);
        var jsRuntime = new RecordingJsRuntime();
        var marketExecution = new Mock<IMarketAnalysisExecutionService>();
        marketExecution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => appState.NotifyPlanDecisionChanged())
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEmptyEvidence(),
                [new MarketItemAnalysis { ItemId = 101, Name = "Item 101", QuantityNeeded = 5 }],
                [ShoppingPlan(101, "Siren")]));
        var service = CreateService(appState, marketExecution: marketExecution.Object, jsRuntime: jsRuntime);

        var result = await service.RefreshItemMarketDataAsync(
            new ProcurementItemRefreshWorkflowRequest(
                ItemId: 101,
                ItemName: "Item 101",
                IsCurrentOperation: () => true,
                ExecutionOptions: MarketAnalysisExecutionOptions.Synchronous));

        Assert.Equal(ProcurementItemRefreshStatus.StaleDecision, result.Status);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(jsRuntime.PatchedPlanIds);
    }

    [Fact]
    public async Task RefreshItemMarketDataAsync_WhenOperationNoLongerCurrent_DoesNotPublishOrSave()
    {
        var appState = CreateAppState();
        appState.TrackCurrentPlanIdentity("plan-1", null);
        var jsRuntime = new RecordingJsRuntime();
        var isCurrent = true;
        var marketExecution = new Mock<IMarketAnalysisExecutionService>();
        marketExecution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => isCurrent = false)
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEmptyEvidence(),
                [new MarketItemAnalysis { ItemId = 101, Name = "Item 101", QuantityNeeded = 5 }],
                [ShoppingPlan(101, "Siren")]));
        var service = CreateService(appState, marketExecution: marketExecution.Object, jsRuntime: jsRuntime);

        var result = await service.RefreshItemMarketDataAsync(
            new ProcurementItemRefreshWorkflowRequest(
                ItemId: 101,
                ItemName: "Item 101",
                IsCurrentOperation: () => isCurrent,
                ExecutionOptions: MarketAnalysisExecutionOptions.Synchronous));

        Assert.Equal(ProcurementItemRefreshStatus.Superseded, result.Status);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(jsRuntime.PatchedPlanIds);
    }

    [Fact]
    public async Task RefreshItemMarketDataAsync_WhenProcurementSettingsChange_DoesNotPublishOrSave()
    {
        var appState = CreateAppState();
        appState.TrackCurrentPlanIdentity("plan-1", null);
        var jsRuntime = new RecordingJsRuntime();
        var marketExecution = new Mock<IMarketAnalysisExecutionService>();
        marketExecution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => appState.SetProcurementSettings(
                searchEntireRegion: true,
                enableSplitWorldPurchases: true,
                travelTolerance: 11,
                temporaryWorldBlacklistDurationMinutes: appState.TemporaryWorldBlacklistDurationMinutes))
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEmptyEvidence(),
                [new MarketItemAnalysis { ItemId = 101, Name = "Item 101", QuantityNeeded = 5 }],
                [ShoppingPlan(101, "Siren")]));
        var service = CreateService(appState, marketExecution: marketExecution.Object, jsRuntime: jsRuntime);

        var result = await service.RefreshItemMarketDataAsync(
            new ProcurementItemRefreshWorkflowRequest(
                ItemId: 101,
                ItemName: "Item 101",
                IsCurrentOperation: () => true,
                ExecutionOptions: MarketAnalysisExecutionOptions.Synchronous));

        Assert.Equal(ProcurementItemRefreshStatus.StaleConfiguration, result.Status);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(jsRuntime.PatchedPlanIds);
    }

    [Fact]
    public async Task RefreshItemMarketDataAsync_WhenMarketEvidenceChanges_DoesNotPublishOrSave()
    {
        var appState = CreateAppState();
        appState.TrackCurrentPlanIdentity("plan-1", null);
        var jsRuntime = new RecordingJsRuntime();
        var marketExecution = new Mock<IMarketAnalysisExecutionService>();
        marketExecution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() =>
            {
                appState.ReplaceMarketAnalysis(
                    [
                        new MarketItemAnalysis
                        {
                            ItemId = 303,
                            Name = "New Evidence",
                            QuantityNeeded = 5,
                            Worlds =
                            [
                                new WorldMarketAnalysis
                                {
                                    DataCenter = "Aether",
                                    WorldName = "Siren"
                                }
                            ]
                        }
                    ],
                    [ShoppingPlan(303)]);
                appState.SelectMarketAnalysisItem(303);
                appState.ToggleMarketAnalysisWorld(303, "Aether", "Siren");
                appState.SetMarketAnalysisGridSort(MarketAnalysisGridSortColumn.Total, descending: true);
                appState.ReplaceProcurementOverlay([ShoppingPlan(303)]);
            })
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEmptyEvidence(),
                [new MarketItemAnalysis { ItemId = 101, Name = "Item 101", QuantityNeeded = 5 }],
                [ShoppingPlan(101, "Siren")]));
        var service = CreateService(appState, marketExecution: marketExecution.Object, jsRuntime: jsRuntime);

        var result = await service.RefreshItemMarketDataAsync(
            new ProcurementItemRefreshWorkflowRequest(
                ItemId: 101,
                ItemName: "Item 101",
                IsCurrentOperation: () => true,
                ExecutionOptions: MarketAnalysisExecutionOptions.Synchronous));

        Assert.Equal(ProcurementItemRefreshStatus.StaleConfiguration, result.Status);
        Assert.Equal(303, Assert.Single(appState.MarketItemAnalyses).ItemId);
        Assert.Equal(303, Assert.Single(appState.ShoppingPlans).ItemId);
        Assert.Equal(303, Assert.Single(appState.ProcurementShoppingPlans).ItemId);
        Assert.Equal(303, appState.SelectedMarketAnalysisItemId);
        Assert.Equal([new MarketAnalysisExpandedWorldKey(303, "Aether", "Siren")], appState.ExpandedMarketAnalysisWorlds);
        Assert.Equal(MarketAnalysisGridSortColumn.Total, appState.MarketAnalysisGridSortColumn);
        Assert.Empty(jsRuntime.PatchedPlanIds);
    }

    private static ProcurementWorkflowService CreateService(
        AppState appState,
        IProcurementRouteExecutionService? procurementExecution = null,
        IMarketAnalysisExecutionService? marketExecution = null,
        RecordingJsRuntime? jsRuntime = null)
    {
        jsRuntime ??= new RecordingJsRuntime();
        var indexedDb = new IndexedDbService(jsRuntime);
        var persistence = new WebPlanPersistenceService(
            indexedDb,
            new StoredPlanSnapshotBuilder(appState),
            new PlanSessionLoadService(appState));

        return new ProcurementWorkflowService(
            appState,
            procurementExecution ?? Mock.Of<IProcurementRouteExecutionService>(),
            marketExecution ?? Mock.Of<IMarketAnalysisExecutionService>(),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()),
            persistence);
    }

    private static AppState CreateAppState(params int[] itemIds)
    {
        var appState = new AppState();
        appState.SetProcurementSettings(
            searchEntireRegion: false,
            enableSplitWorldPurchases: false,
            travelTolerance: 7,
            temporaryWorldBlacklistDurationMinutes: appState.TemporaryWorldBlacklistDurationMinutes);
        appState.ApplyBuiltRecipePlan(CreatePlan(itemIds));
        return appState;
    }

    private static CraftingPlan CreatePlan(params int[] itemIds)
    {
        if (itemIds.Length == 0)
        {
            itemIds = [101, 202];
        }

        return new CraftingPlan
        {
            RootItems = itemIds
                .Select(itemId => new PlanNode
                {
                    ItemId = itemId,
                    Name = $"Item {itemId}",
                    Quantity = 5,
                    Source = AcquisitionSource.MarketBuyNq,
                    CanBuyFromMarket = true
                })
                .ToList()
        };
    }

    private static DetailedShoppingPlan ShoppingPlan(int itemId, string worldName = "Siren")
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = $"Item {itemId}",
            QuantityNeeded = 5,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = worldName,
                TotalCost = itemId * 10,
                TotalQuantityPurchased = 5
            }
        };
    }

    private static DetailedShoppingPlan CompleteShoppingPlan(int itemId, string worldName, long totalCost)
    {
        var world = new WorldShoppingSummary
        {
            DataCenter = "Aether",
            WorldName = worldName,
            TotalCost = totalCost,
            AveragePricePerUnit = totalCost / 5m,
            TotalQuantityPurchased = 5,
            HasSufficientStock = true,
            Listings =
            [
                new ShoppingListingEntry
                {
                    Quantity = 5,
                    NeededFromStack = 5,
                    PricePerUnit = totalCost / 5,
                    RetainerName = $"{worldName} Retainer"
                }
            ]
        };

        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = $"Item {itemId}",
            QuantityNeeded = 5,
            WorldOptions = [world],
            RecommendedWorld = world
        };
    }

    private static async Task<RealRouteResult> RunRealRouteWithDisplayFieldsAsync(bool includeDisplayFields)
    {
        var appState = CreateAppState(101);
        var sourcePlan = CompleteShoppingPlan(101, "Siren", totalCost: 500);
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis
                {
                    ItemId = 101,
                    Name = "Item 101",
                    QuantityNeeded = 5,
                    AnalysisScopeBaselineUnitPrice = includeDisplayFields ? 100 : 0,
                    SaneThresholdUnitPrice = includeDisplayFields ? 200 : 0,
                    Worlds =
                    [
                        new WorldMarketAnalysis
                        {
                            DataCenter = "Aether",
                            WorldName = "Siren",
                            QuantityNeeded = 5,
                            AnalysisScopeBaselineUnitPrice = includeDisplayFields ? 100 : 0,
                            SaneThresholdUnitPrice = includeDisplayFields ? 200 : 0,
                            ScopeSaneQuantity = includeDisplayFields ? 5 : 0,
                            ScopeInsaneQuantity = includeDisplayFields ? 99 : 0,
                            ScopeCompetitiveQuantity = includeDisplayFields ? 1 : 0
                        }
                    ]
                }
            ],
            [sourcePlan]);
        var marketExecution = new Mock<IMarketAnalysisExecutionService>(MockBehavior.Strict);
        var routeExecution = new ProcurementRouteExecutionService(
            marketExecution.Object,
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));
        var service = CreateService(appState, procurementExecution: routeExecution);

        var result = await service.RunAnalysisAsync(
            new ProcurementWorkflowRequest(() => true, MarketAnalysisExecutionOptions.Synchronous));

        marketExecution.VerifyNoOtherCalls();
        return new RealRouteResult(result.Status, result.ShoppingPlanCount, appState.ProcurementShoppingPlans.ToList());
    }

    private static object ToRouteSummary(DetailedShoppingPlan plan)
    {
        return new
        {
            plan.ItemId,
            plan.QuantityNeeded,
            RecommendedWorld = plan.RecommendedWorld?.WorldName,
            RecommendedDataCenter = plan.RecommendedWorld?.DataCenter,
            RecommendedCost = plan.RecommendedWorld?.TotalCost,
            SplitWorlds = plan.RecommendedSplit?
                .Select(split => new { split.DataCenter, split.WorldName, split.QuantityToBuy, split.TotalCost })
                .ToArray()
        };
    }

    private sealed record RealRouteResult(
        ProcurementWorkflowStatus Status,
        int ShoppingPlanCount,
        IReadOnlyList<DetailedShoppingPlan> Output);

    private static MarketEvidenceSet CreateEmptyEvidence()
    {
        return new MarketEvidenceSet(
            new Dictionary<(int itemId, string dataCenter), CachedMarketData>(),
            [],
            MarketFetchScope.SelectedDataCenter,
            ["Aether"],
            "Aether",
            "North America",
            maxAge: TimeSpan.Zero,
            fetchedCount: 0,
            loadedAtUtc: DateTime.UtcNow);
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public List<string> PatchedPlanIds { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "IndexedDB.patchMarketAnalysis")
            {
                PatchedPlanIds.Add((string)args![0]!);
                return new ValueTask<TValue>((TValue)(object)true);
            }

            throw new InvalidOperationException($"Unexpected JS invocation: {identifier}");
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, args);
        }
    }
}
