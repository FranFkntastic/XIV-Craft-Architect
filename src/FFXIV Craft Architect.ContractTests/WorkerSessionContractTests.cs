using System.Text.Json;
using CraftArchitectEngineWorker;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class WorkerSessionContractTests
{
    private static readonly JsonSerializerOptions WireOptions =
        EngineJsonSerializerOptions.CreateWire();

    [Fact]
    public async Task RestorePublishesOneRevisionAndRejectsStaleReaders()
    {
        var plan = new CraftingPlan
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Worker plan",
            DataCenter = "Aether",
            RootItems =
            [
                new PlanNode
                {
                    NodeId = "root",
                    ItemId = 42,
                    Name = "Root",
                    Quantity = 2,
                    Source = AcquisitionSource.Craft,
                    CanCraft = true,
                    Children =
                    [
                        new PlanNode
                        {
                            NodeId = "child",
                            ItemId = 43,
                            Name = "Child",
                            Quantity = 4,
                            Source = AcquisitionSource.MarketBuyNq,
                            CanBuyFromMarket = true
                        },
                        new PlanNode
                        {
                            NodeId = "route-anchor",
                            ItemId = 45,
                            Name = "Route anchor",
                            Quantity = 1,
                            Source = AcquisitionSource.MarketBuyNq,
                            CanBuyFromMarket = true
                        }
                    ]
                }
            ]
        };
        var storedPlan = new StoredPlan
        {
            Id = "autosave",
            Name = "Autosave",
            DataCenter = "Aether",
            PlanJson = JsonSerializer.Serialize(plan),
            ProjectItems =
            [
                new StoredProjectItem { Id = 42, Name = "Root", Quantity = 2 }
            ]
        };

        var restored = await SendAsync(
            "restore",
            expectedRevision: 0,
            new WorkerSessionRestorePayload(
                Revision: 1,
                storedPlan,
                TrackStoredPlanIdentity: false,
                MigratedFromLegacy: true));

        Assert.Equal((true, 1L), (restored.Accepted, restored.Revision));
        var shell = restored.Projection.Deserialize<WorkerSessionShellProjection>(WireOptions);
        Assert.NotNull(shell);
        Assert.Equal((true, "Worker plan", 3, true), (shell.HasSession, shell.PlanName, shell.PlanNodeCount, shell.MigratedFromLegacy));

        var stale = await SendAsync("shell", expectedRevision: 0, new { });
        Assert.Equal((false, "stale-revision", 1L), (stale.Accepted, stale.RejectionCode, stale.Revision));

        var recipe = await SendAsync(
            WorkerSessionCommandKinds.RecipeProjection,
            expectedRevision: 1,
            new { });
        Assert.True(recipe.Accepted);
        var recipeProjection =
            recipe.Projection.Deserialize<WorkerRecipePlannerProjection>(WireOptions);
        Assert.NotNull(recipeProjection);
        Assert.Single(recipeProjection.ProjectItems);
        Assert.Single(recipeProjection.Roots);
        Assert.Equal(2, recipeProjection.Roots[0].Children.Count);

        var acquisition = await SendAsync(
            WorkerSessionCommandKinds.AcquisitionProjection,
            expectedRevision: 1,
            new WorkerAcquisitionProjectionRequest("All"));
        Assert.True(acquisition.Accepted);
        var acquisitionProjection =
            acquisition.Projection.Deserialize<WorkerAcquisitionProjection>(WireOptions);
        Assert.NotNull(acquisitionProjection);
        Assert.Equal("All", acquisitionProjection.Filter);
        Assert.NotEmpty(acquisitionProjection.Rows);
        Assert.DoesNotContain("\"node\":", acquisition.Projection.GetRawText());

        var mutated = await SendAsync(
            WorkerSessionCommandKinds.ProjectItemsMutation,
            expectedRevision: 1,
            new WorkerProjectItemsMutation(
                "add",
                Item: new ProjectItem
                {
                    Id = 44,
                    Name = "Second target",
                    Quantity = 3
                }));
        Assert.Equal((true, 2L), (mutated.Accepted, mutated.Revision));
        var mutation =
            mutated.Projection.Deserialize<WorkerSessionMutationProjection>(WireOptions);
        Assert.NotNull(mutation);
        Assert.Equal(2, mutation.Shell.Revision);
        Assert.Equal(2, mutation.Shell.ProjectItemCount);
        Assert.Null(mutation.DurableState);
        Assert.True(mutation.DurablePatch?.ReplaceProjectItems);
        Assert.False(mutation.DurablePatch?.ReplacePlanJson);
        var mutatedRecipe =
            mutation.PublicProjection.Deserialize<WorkerRecipePlannerProjection>(WireOptions);
        Assert.NotNull(mutatedRecipe);
        Assert.Equal(2, mutatedRecipe.ProjectItems.Count);

        var market = await SendAsync(
            WorkerSessionCommandKinds.MarketProjection,
            expectedRevision: 2,
            new { });
        Assert.True(market.Accepted);
        var marketProjection =
            market.Projection.Deserialize<WorkerMarketProjection>(WireOptions);
        Assert.NotNull(marketProjection);
        Assert.True(marketProjection.HasPlan);
        Assert.False(marketProjection.HasAnalysis);

        var marketOperationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var marketOperation = await SendAsync(
            WorkerSessionCommandKinds.OperationBegin,
            expectedRevision: 2,
            new WorkerSessionOperationBeginRequest(
                marketOperationId,
                WorkerSessionOperationKind.MarketAnalysis,
                "market:2",
                "Analyzing market prices..."),
            marketOperationId);
        Assert.True(marketOperation.Accepted);
        Assert.Equal(
            marketOperationId,
            marketOperation.Projection
                .Deserialize<WorkerSessionShellProjection>(WireOptions)?
                .Operation?
                .OperationId);
        var blockedOperation = await SendAsync(
            WorkerSessionCommandKinds.OperationBegin,
            expectedRevision: 2,
            new WorkerSessionOperationBeginRequest(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                WorkerSessionOperationKind.ProcurementAnalysis,
                "procurement:2",
                "Building your procurement route..."));
        var blockedShell = blockedOperation.Projection
            .Deserialize<WorkerSessionShellProjection>(WireOptions);
        Assert.Equal(WorkerSessionOperationDisposition.Busy, blockedShell?.Operation?.Disposition);
        Assert.Equal(marketOperationId, blockedShell?.Operation?.OperationId);

        var cheapChildWorld = World("Sargatanas", quantity: 4, unitPrice: 10);
        var expensiveChildWorld = World("Gilgamesh", quantity: 4, unitPrice: 100);
        var routeAnchorWorld = World("Gilgamesh", quantity: 1, unitPrice: 10);
        var unavailableWorld = World("Sargatanas", quantity: 0, unitPrice: 0);
        var staged = await SendAsync(
            WorkerSessionCommandKinds.MarketEvidencePublicationStage,
            expectedRevision: 2,
            new WorkerMarketEvidencePublicationRequest(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                2,
                MarketFetchScope.SelectedDataCenter,
                "Aether",
                "North America",
                MarketAcquisitionLens.MinimumUpfrontCost,
                [
                    Analysis(43, "Child", quantity: 4),
                    Analysis(45, "Route anchor", quantity: 1)
                ],
                [
                    ShoppingPlan(43, "Child", 4, cheapChildWorld, expensiveChildWorld),
                    ShoppingPlan(45, "Route anchor", 1, routeAnchorWorld),
                    ShoppingPlan(42, "Root", 2, unavailableWorld,
                        error: "Unavailable in the selected scope.")
                ],
                new HashSet<int> { 42 },
                FetchedCount: 2,
                ResetStaging: true,
                CompleteStaging: false),
            marketOperationId);
        Assert.Equal((true, 2L), (staged.Accepted, staged.Revision));

        var interleaved = await SendAsync(
            WorkerSessionCommandKinds.MarketEvidencePublicationStage,
            expectedRevision: 2,
            new WorkerMarketEvidencePublicationRequest(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                2,
                MarketFetchScope.SelectedDataCenter,
                "Aether",
                "North America",
                MarketAcquisitionLens.MinimumUpfrontCost,
                [],
                [],
                new HashSet<int>(),
                FetchedCount: 0,
                ResetStaging: true,
                CompleteStaging: false),
            marketOperationId);
        Assert.True(interleaved.Accepted);

        var completed = await SendAsync(
            WorkerSessionCommandKinds.MarketEvidencePublication,
            expectedRevision: 2,
            new WorkerMarketEvidencePublicationRequest(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                2,
                MarketFetchScope.SelectedDataCenter,
                "Aether",
                "North America",
                MarketAcquisitionLens.MinimumUpfrontCost,
                [],
                [],
                new HashSet<int>(),
                FetchedCount: 0,
                CompleteStaging: true),
            marketOperationId);
        Assert.Equal((true, 3L), (completed.Accepted, completed.Revision));
        var accepted =
            completed.Projection.Deserialize<WorkerSessionMutationProjection>(WireOptions);
        Assert.NotNull(accepted);
        Assert.Equal(2, accepted.Shell.MarketAnalysisCount);
        Assert.Equal(3, accepted.Shell.ShoppingPlanCount);
        Assert.True(accepted.DurablePatch?.ReplacePlanStateJson);
        Assert.False(accepted.DurablePatch?.ReplacePlanJson);
        Assert.True(accepted.DurablePatch?.ReplaceMarketEvidence);
        var published =
            accepted.PublicProjection.Deserialize<WorkerMarketEvidenceCommitProjection>(
                WireOptions);
        Assert.NotNull(published);
        Assert.Equal(3, published.AnalyzedCount);

        var marketOperationCompleted = await SendAsync(
            WorkerSessionCommandKinds.OperationComplete,
            expectedRevision: 3,
            new WorkerSessionOperationControlRequest(marketOperationId),
            marketOperationId);
        Assert.True(marketOperationCompleted.Accepted);

        var projectionStore = new WorkerProjectionStore();
        Assert.True(projectionStore.TryPublish(staged));
        var browserResult = completed with
        {
            Projection = JsonSerializer.SerializeToElement(
                new WorkerAcceptedMutationProjection(
                    accepted.Shell,
                    accepted.PublicProjection),
                WireOptions)
        };
        Assert.True(
            projectionStore.TryPublishMutation<WorkerMarketEvidenceCommitProjection>(
                browserResult,
                out var browserOutcome));
        Assert.NotNull(browserOutcome);
        Assert.Equal(3, browserOutcome.AnalyzedCount);

        var compactMarket = await SendAsync(
            WorkerSessionCommandKinds.MarketProjection,
            expectedRevision: 3,
            new WorkerMarketProjectionRequest(IncludeDetails: false));
        Assert.True(compactMarket.Accepted);
        var compactMarketProjection =
            compactMarket.Projection.Deserialize<WorkerMarketProjection>(WireOptions);
        Assert.NotNull(compactMarketProjection);
        Assert.True(compactMarketProjection.HasAnalysis);
        Assert.Empty(compactMarketProjection.ItemAnalyses);
        Assert.Empty(compactMarketProjection.ShoppingPlans);
        var compactWorldDetail = Assert.Single(
            compactMarketProjection.Items,
            item => item.Worlds.Count > 0);
        Assert.Equal(
            compactMarketProjection.Items.First().ItemId,
            compactWorldDetail.ItemId);
        Assert.All(
            compactMarketProjection.Items,
            item => Assert.True(item.WorldCount >= item.Worlds.Count));

        var targetedMarket = await SendAsync(
            WorkerSessionCommandKinds.MarketProjection,
            expectedRevision: 3,
            new WorkerMarketProjectionRequest(
                IncludeDetails: false,
                WorldDetailItemId: compactMarketProjection.Items.Last().ItemId));
        var targetedMarketProjection =
            targetedMarket.Projection.Deserialize<WorkerMarketProjection>(WireOptions);
        Assert.NotNull(targetedMarketProjection);
        Assert.NotEmpty(targetedMarketProjection.Items.Last().Worlds);
        Assert.All(
            targetedMarketProjection.Items.Take(targetedMarketProjection.Items.Count - 1),
            item => Assert.Empty(item.Worlds));

        var fullDetailMarket = await SendAsync(
            WorkerSessionCommandKinds.MarketProjection,
            expectedRevision: 3,
            new WorkerMarketProjectionRequest(IncludeDetails: true));
        var fullDetailProjection =
            fullDetailMarket.Projection.Deserialize<WorkerMarketProjection>(WireOptions);
        Assert.NotNull(fullDetailProjection);
        var selectedItemId = fullDetailProjection.ItemAnalyses.First().ItemId;
        var selectedDetailMarket = await SendAsync(
            WorkerSessionCommandKinds.MarketProjection,
            expectedRevision: 3,
            new WorkerMarketProjectionRequest(
                IncludeDetails: true,
                WorldDetailItemId: selectedItemId));
        var selectedDetailProjection =
            selectedDetailMarket.Projection.Deserialize<WorkerMarketProjection>(WireOptions);
        Assert.NotNull(selectedDetailProjection);
        Assert.Equal(
            selectedItemId,
            Assert.Single(selectedDetailProjection.ShoppingPlans).ItemId);
        Assert.Equal(
            selectedItemId,
            Assert.Single(selectedDetailProjection.ItemAnalyses).ItemId);
        Assert.NotEmpty(
            Assert.Single(
                selectedDetailProjection.Items,
                item => item.ItemId == selectedItemId).Worlds);
        Assert.All(
            selectedDetailProjection.Items.Where(item => item.ItemId != selectedItemId),
            item => Assert.Empty(item.Worlds));

        var procurement = await SendAsync(
            WorkerSessionCommandKinds.ProcurementProjection,
            expectedRevision: 3,
            new { });
        Assert.True(procurement.Accepted);
        var procurementProjection =
            procurement.Projection.Deserialize<WorkerProcurementProjection>(WireOptions);
        Assert.NotNull(procurementProjection);
        Assert.True(procurementProjection.HasPlan);
        Assert.False(procurementProjection.HasRoute);

        var procurementOperationId =
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var procurementOperation = await SendAsync(
            WorkerSessionCommandKinds.OperationBegin,
            expectedRevision: 3,
            new WorkerSessionOperationBeginRequest(
                procurementOperationId,
                WorkerSessionOperationKind.ProcurementAnalysis,
                "procurement:3",
                "Building your procurement route..."),
            procurementOperationId);
        Assert.True(procurementOperation.Accepted);

        var generatedRoute = await SendAsync(
            WorkerSessionCommandKinds.ProcurementRun,
            expectedRevision: 3,
            new WorkerProcurementRequest(
                MarketFetchScope.SelectedDataCenter,
                "Aether",
                "North America",
                MarketAcquisitionLens.MinimumUpfrontCost,
                TravelTolerance: 0,
                IncludeSplitPurchases: true,
                StartFromHomeDataCenter: false,
                MarketTravelPriority.DataCenterTransfersFirst),
            procurementOperationId);
        Assert.Equal((true, 4L), (generatedRoute.Accepted, generatedRoute.Revision));
        var generatedMutation =
            generatedRoute.Projection.Deserialize<WorkerSessionMutationProjection>(WireOptions);
        Assert.NotNull(generatedMutation);
        Assert.True(generatedMutation.DurablePatch?.ReplaceProcurementRoute);
        Assert.NotNull(generatedMutation.DurablePatch?.ProcurementRouteJson);
        Assert.Equal(0, generatedMutation.DurablePatch.ProcurementTravelTolerance);
        var generatedOutcome =
            generatedMutation.PublicProjection.Deserialize<WorkerProcurementOutcome>(WireOptions);
        Assert.NotNull(generatedOutcome);
        Assert.True(generatedOutcome.Procurement.IncludeSplitPurchases);
        var childRouteDecision = Assert.Single(
            generatedOutcome.Procurement.RouteDecision?.ItemPremiums ?? [], item => item.ItemId == 43);
        Assert.Equal((40L, 400L),
            (childRouteDecision.CheapestEligibleGilCost, childRouteDecision.SelectedGilCost));

        var routeAcquisition = await SendAsync(WorkerSessionCommandKinds.AcquisitionProjection,
            expectedRevision: 4, new WorkerAcquisitionProjectionRequest("All"));
        var routeAcquisitionProjection =
            routeAcquisition.Projection.Deserialize<WorkerAcquisitionProjection>(WireOptions);
        var acquisitionChild = Assert.Single(
            routeAcquisitionProjection?.Rows ?? [],
            row => row.ItemId == 43);
        Assert.Equal(40, acquisitionChild.CalculatedTotalCost);

        var routeTrade = await SendAsync(WorkerSessionCommandKinds.TradeProjection,
            expectedRevision: 4, new WorkerTradeProjectionRequest(IncludeCraftLabor: false));
        var routeTradeProjection =
            routeTrade.Projection.Deserialize<WorkerTradeProjection>(WireOptions);
        Assert.Null(routeTradeProjection?.MaterialQuote);
        Assert.Equal(2, routeTradeProjection?.MaterialLines.Count);
        var tradeChild = Assert.Single(
            routeTradeProjection?.MaterialLines ?? [],
            line => line.ItemId == 43);
        Assert.Equal((4, 10m), (tradeChild.Quantity, tradeChild.UnitCost));
        Assert.Contains(
            "current-region",
            routeTradeProjection?.MaterialQuoteFailureReason ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            routeTradeProjection?.Warnings ?? [],
            warning => warning.Contains("current-region", StringComparison.OrdinalIgnoreCase));

        var selectedTolerance = await SendAsync(
            WorkerSessionCommandKinds.ProcurementToleranceMutation,
            expectedRevision: 4,
            new WorkerProcurementToleranceMutation(11),
            procurementOperationId);
        Assert.Equal((true, 5L), (selectedTolerance.Accepted, selectedTolerance.Revision));
        var toleranceMutation =
            selectedTolerance.Projection.Deserialize<WorkerSessionMutationProjection>(WireOptions);
        Assert.NotNull(toleranceMutation);
        Assert.False(toleranceMutation.DurablePatch?.ReplaceProcurementRoute);
        Assert.Null(toleranceMutation.DurablePatch?.ProcurementRouteJson);
        Assert.Equal(11, toleranceMutation.DurablePatch?.ProcurementTravelTolerance);

        var procurementOperationCompleted = await SendAsync(
            WorkerSessionCommandKinds.OperationComplete,
            expectedRevision: 5,
            new WorkerSessionOperationControlRequest(procurementOperationId),
            procurementOperationId);
        Assert.True(procurementOperationCompleted.Accepted);

        var identified = await SendAsync(
            WorkerSessionCommandKinds.PlanIdentityMutation,
            expectedRevision: 5,
            new WorkerPlanIdentityMutation("named-plan", "Named worker plan"));
        Assert.Equal((true, 6L), (identified.Accepted, identified.Revision));
        var identityMutation =
            identified.Projection.Deserialize<WorkerSessionMutationProjection>(WireOptions);
        Assert.NotNull(identityMutation);
        Assert.True(identityMutation.DurablePatch?.ReplaceSourceIdentity);
        Assert.False(identityMutation.DurablePatch?.ReplacePlanJson);
        Assert.False(identityMutation.DurablePatch?.ReplaceMarketEvidence);
        Assert.False(identityMutation.DurablePatch?.ReplaceProcurementRoute);

        var tradeResult = await SendAsync(
            WorkerSessionCommandKinds.TradeProjection,
            expectedRevision: 6,
            new WorkerTradeProjectionRequest(IncludeCraftLabor: false));
        Assert.True(tradeResult.Accepted, tradeResult.Message);
        var trade = tradeResult.Projection.Deserialize<WorkerTradeProjection>(WireOptions);
        Assert.NotNull(trade);
        Assert.True(trade.HasPlan);
        Assert.Equal("named-plan", trade.PlanId);
        Assert.NotEmpty(trade.RootItems);
        Assert.NotEmpty(trade.AcquisitionRows);
        Assert.NotEmpty(trade.RequestedDataCenters);

        var exported = await SendAsync(
            "export",
            expectedRevision: 6,
            new WorkerSessionExportRequest(
                "autosave",
                "Autosave",
                IncludeSourcePlanIdentity: true));
        Assert.True(exported.Accepted);
        var export = exported.Projection.Deserialize<WorkerSessionExportProjection>(WireOptions);
        Assert.NotNull(export);
        Assert.Equal(6, export.Revision);
        Assert.NotNull(export.StoredPlan?.PlanJson);
        Assert.Equal(11, export.StoredPlan.ProcurementTravelTolerance);
        Assert.NotNull(export.StoredPlan.MarketIntelligenceJson);
        Assert.True(MarketIntelligencePayloadCodec.IsCompressed(
            export.StoredPlan.MarketIntelligenceJson));
        Assert.Null(export.StoredPlan.MarketPlansJson);
        Assert.Null(export.StoredPlan.MarketItemAnalysesJson);
        var storedMarket = MarketIntelligencePayloadCodec.Deserialize(
            export.StoredPlan.MarketIntelligenceJson);
        Assert.NotNull(storedMarket);
        Assert.Equal(2, storedMarket.ItemAnalyses.Count);
        Assert.Equal(3, storedMarket.Recommendations.Count);
        Assert.NotNull(storedMarket.RecipeBasis);
        Assert.Equal(3, storedMarket.RecipeBasis.MarketAnalysisDemandItems.Count);

        var reloaded = await SendAsync(
            "restore",
            expectedRevision: 6,
            new WorkerSessionRestorePayload(
                Revision: 7,
                export.StoredPlan,
                TrackStoredPlanIdentity: false,
                MigratedFromLegacy: false));
        Assert.True(reloaded.Accepted, reloaded.Message);

        var reloadedMarket = await SendAsync(
            WorkerSessionCommandKinds.MarketProjection,
            expectedRevision: 7,
            new WorkerMarketProjectionRequest(IncludeDetails: true));
        Assert.True(reloadedMarket.Accepted);
        var reloadedMarketProjection =
            reloadedMarket.Projection.Deserialize<WorkerMarketProjection>(WireOptions);
        Assert.NotNull(reloadedMarketProjection);
        Assert.True(
            reloadedMarketProjection.HasAnalysis,
            reloaded.Message ?? "Reloaded market projection did not contain analysis.");
        Assert.Equal(3, reloadedMarketProjection.Items.Count);
        Assert.Equal(2, reloadedMarketProjection.ItemAnalyses.Count);
        Assert.Equal(3, reloadedMarketProjection.ShoppingPlans.Count);

        var reexported = await SendAsync(
            "export",
            expectedRevision: 7,
            new WorkerSessionExportRequest(
                "autosave",
                "Autosave",
                IncludeSourcePlanIdentity: true));
        var reexport = reexported.Projection.Deserialize<WorkerSessionExportProjection>(WireOptions);
        Assert.NotNull(reexport?.StoredPlan);
        Assert.Equal(
            JsonSerializer.Serialize(export.StoredPlan.ProjectItems, WireOptions),
            JsonSerializer.Serialize(reexport.StoredPlan.ProjectItems, WireOptions));
        Assert.Equal(export.StoredPlan.PlanJson, reexport.StoredPlan.PlanJson);
        Assert.Equal(export.StoredPlan.PlanStateJson, reexport.StoredPlan.PlanStateJson);
        Assert.Equal(
            JsonSerializer.Serialize(
                MarketIntelligencePayloadCodec.Deserialize(
                    export.StoredPlan.MarketIntelligenceJson!),
                WireOptions),
            JsonSerializer.Serialize(
                MarketIntelligencePayloadCodec.Deserialize(
                    reexport.StoredPlan.MarketIntelligenceJson!),
                WireOptions));
        Assert.Equal(
            export.StoredPlan.MarketAnalysisRecipeBasisJson,
            reexport.StoredPlan.MarketAnalysisRecipeBasisJson);
        Assert.Equal(
            export.StoredPlan.ProcurementRouteJson,
            reexport.StoredPlan.ProcurementRouteJson);
        Assert.Equal(
            export.StoredPlan.ProcurementTravelTolerance,
            reexport.StoredPlan.ProcurementTravelTolerance);
        Assert.Equal(export.StoredPlan.SourcePlanId, reexport.StoredPlan.SourcePlanId);
        Assert.Equal(export.StoredPlan.SourcePlanName, reexport.StoredPlan.SourcePlanName);
        Assert.Equal(export.StoredPlan.DataCenter, reexport.StoredPlan.DataCenter);
    }

    private static MarketItemAnalysis Analysis(int id, string name, int quantity) => new()
    { ItemId = id, Name = name, QuantityNeeded = quantity, Scope = MarketFetchScope.SelectedDataCenter };
    private static DetailedShoppingPlan ShoppingPlan(
        int id, string name, int quantity, WorldShoppingSummary recommended,
        WorldShoppingSummary? alternate = null,
        string? error = null) => new()
        {
            ItemId = id,
            Name = name,
            QuantityNeeded = quantity,
            RecommendedWorld = recommended,
            WorldOptions = alternate == null ? [recommended] : [recommended, alternate],
            Error = error
        };
    private static WorldShoppingSummary World(string name, int quantity, long unitPrice) => new()
    {
        DataCenter = "Aether",
        WorldName = name,
        TotalCost = quantity * unitPrice,
        AveragePricePerUnit = unitPrice,
        TotalQuantityPurchased = quantity,
        HasSufficientStock = quantity > 0,
        Listings = quantity > 0
              ? [new ShoppingListingEntry { Quantity = quantity, NeededFromStack = quantity, PricePerUnit = unitPrice }]
              : []
    };
    private static async Task<WorkerSessionResultEnvelope> SendAsync<TPayload>(
        string commandKind,
        long expectedRevision,
        TPayload payload,
        Guid? operationId = null)
    {
        var commandId = Guid.NewGuid();
        var command = new WorkerSessionCommandEnvelope(
            WorkerSessionProtocol.ContractVersion,
            commandKind,
            expectedRevision,
            JsonSerializer.SerializeToElement(payload, WireOptions),
            operationId);
        var message = new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            WorkerSessionProtocol.CommandMessageKind,
            1,
            commandId,
            commandId,
            JsonSerializer.SerializeToElement(command, WireOptions));
        var responseJson = await ManagedHost.ExecuteSessionCommandJsonCore(
            JsonSerializer.Serialize(message, WireOptions));
        var response = JsonSerializer.Deserialize<EngineWorkerMessage>(responseJson, WireOptions);
        return response?.Payload?.Deserialize<WorkerSessionResultEnvelope>(WireOptions)
            ?? throw new InvalidOperationException("Worker session response was empty.");
    }
}
