using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Tests;

public sealed class WebProcurementEngineSettlementTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task RouteSettlementNeverOwnsAcquisitionDecisionsOrOverwritesUserEdits(
        bool failDuringPersistence,
        bool userChangedPlan,
        bool newerPublication)
    {
        var appState = new AppState();
        var item = new MaterialAggregate { ItemId = 101, Name = "Route Item", TotalQuantity = 2 };
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = item.ItemId,
                    Name = item.Name,
                    Quantity = item.TotalQuantity,
                    Source = AcquisitionSource.MarketBuyNq
                }
            ]
        };
        appState.ActivateRecipePlan(
            plan,
            [new ProjectItem { Id = item.ItemId, Name = item.Name, Quantity = item.TotalQuantity }],
            "Aether",
            clearCurrentPlanId: true,
            [item]);
        var analysis = new MarketItemAnalysis
        {
            ItemId = item.ItemId,
            Name = item.Name,
            QuantityNeeded = item.TotalQuantity
        };
        var shoppingPlan = new DetailedShoppingPlan
        {
            ItemId = item.ItemId,
            Name = item.Name,
            QuantityNeeded = item.TotalQuantity,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = "Siren",
                TotalCost = 200,
                TotalQuantityPurchased = item.TotalQuantity
            }
        };
        appState.ReplaceMarketAnalysis([analysis], [shoppingPlan]);
        var routeBasis = appState.CreateCurrentProcurementRouteBasis();
        var decision = new MarketRouteDecision(0, null, 200, 200, 0, 1, 1, 0, 0, false, null);
        var route = new ProcurementRouteExecutionResult(
            [shoppingPlan],
            [],
            [],
            [],
            [],
            decision,
            ActiveProcurementItems: [item]);
        var snapshots = new ReferenceEngineSemanticSnapshotProvider();
        var routeSnapshot = snapshots.CaptureRoute(route);
        var transactionId = Guid.NewGuid();
        var request = new EngineRequestEnvelope(
            "1",
            transactionId,
            EngineInputKind.RootIntent,
            JsonSerializer.SerializeToElement(new ReferenceEngineInput(null, null)),
            new EngineBasisSet(
                EngineBasisIdentity.Empty("plan"),
                EngineBasisIdentity.Empty("session"),
                EngineBasisIdentity.Empty("publication"),
                EngineBasisIdentity.Empty("route")),
            EngineDeterministicSettings.Default,
            EngineExecutionBudgets.Default,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
        var result = JsonSerializer.SerializeToElement(
            new ReferenceEngineResultSnapshot(null, routeSnapshot, route),
            EngineJsonSerializerOptions.CreateWire());
        var computation = new EngineComputationResult(
            "1",
            1,
            Guid.NewGuid(),
            transactionId,
            EngineComputationStatus.Completed,
            EnginePhase.Reconciling,
            result,
            request.Basis,
            string.Empty,
            request.Budgets,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            EngineSemanticSnapshotHash.Route(routeSnapshot),
            string.Empty,
            new Dictionary<string, string>());
        var gateHeld = true;
        var settlement = new WebProcurementEngineSettlement(
            appState,
            new IndexedDbService(new FailedSaveJsRuntime()),
            snapshots,
            new WebProcurementSettlementRegistration(
                request,
                plan,
                appState.PlanSessionVersion,
                appState.CurrentVersions.PlanDecisionVersion,
                appState.CurrentVersions.MarketAnalysisVersion,
                routeBasis,
                OperationGateLease.Create(() => gateHeld, () => !(gateHeld = false))));
        var requestHash = EngineCanonicalHash.ComputeRequestIdentity(request);
        const string invocationToken = "invocation-token";
        const string claimToken = "claim-token";
        Assert.NotNull(settlement.TryRegisterInvocationCleanupOwnership(
            new EngineInvocationCleanupRegistration(request, invocationToken)));
        settlement.RegisterExecutionContext(new EngineExecutionContextRegistration(
            computation.Generation,
            computation.ExecutionId,
            request,
            requestHash,
            invocationToken,
            claimToken));

        EngineSettlementContext Context(EnginePhase phase) => new(
            request,
            computation,
            requestHash,
            EngineCanonicalHash.Compute(new
            {
                Domain = "engine-settlement-delivery-v1",
                request.TransactionId,
                RequestHash = requestHash,
                Phase = phase
            }),
            invocationToken,
            claimToken);

        var publishing = Context(EnginePhase.Publishing);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settlement.SettleAsync(
            EnginePhase.Publishing,
            publishing with { PhaseDeliveryId = "wrong" },
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settlement.SettleAsync(
            EnginePhase.Publishing,
            publishing with { InvocationToken = "wrong" },
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settlement.SettleAsync(
            EnginePhase.Publishing,
            publishing with { ClaimToken = "wrong" },
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settlement.SettleAsync(
            EnginePhase.Publishing,
            publishing with { RequestHash = new string('0', 64) },
            CancellationToken.None));

        await settlement.SettleAsync(EnginePhase.Publishing, publishing, CancellationToken.None);
        await settlement.SettleAsync(EnginePhase.SettlingRoute, Context(EnginePhase.SettlingRoute), CancellationToken.None);
        Assert.Equal(ProcurementRoutePublicationValidity.Current, appState.ProcurementRouteValidity);
        Assert.Equal(AcquisitionSource.MarketBuyNq, appState.CurrentPlan!.RootItems[0].Source);
        var optimizedPlan = appState.CurrentPlan;
        Assert.Contains(EnginePhase.Publishing, settlement.SettlementPhaseElapsedMilliseconds.Keys);
        Assert.Contains(EnginePhase.SettlingRoute, settlement.SettlementPhaseElapsedMilliseconds.Keys);

        if (userChangedPlan)
        {
            optimizedPlan.RootItems[0].Source = AcquisitionSource.Craft;
            appState.NotifyPlanDecisionChanged();
        }
        if (newerPublication)
        {
            var replacementPlan = new DetailedShoppingPlan
            {
                ItemId = item.ItemId,
                Name = item.Name,
                QuantityNeeded = item.TotalQuantity,
                RecommendedWorld = new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Replacement",
                    TotalCost = 150,
                    TotalQuantityPurchased = item.TotalQuantity
                }
            };
            appState.ReplaceMarketAnalysis([analysis], [replacementPlan]);
            appState.ReplaceProcurementOverlay([replacementPlan], decision with { SelectedGilCost = 150 });
        }

        if (failDuringPersistence)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                settlement.SettleAsync(
                    EnginePhase.Persisting,
                    Context(EnginePhase.Persisting),
                    CancellationToken.None));
            Assert.NotEmpty(appState.ProcurementShoppingPlans);
            Assert.Null(appState.ProcurementRouteFailure);
        }

        var released = await settlement.SettleAsync(
            EnginePhase.ReleasingGate,
            Context(EnginePhase.ReleasingGate),
            CancellationToken.None);

        Assert.Equal(EngineSettlementOutcome.Applied, released.Outcome);
        Assert.Equal(
            !userChangedPlan && !newerPublication,
            released.Evidence.Contains("route-rolled-back", StringComparison.Ordinal));
        Assert.False(gateHeld);
        Assert.Same(plan, appState.CurrentPlan);
        Assert.Equal(
            userChangedPlan
                ? AcquisitionSource.Craft
                : AcquisitionSource.MarketBuyNq,
            appState.CurrentPlan.RootItems[0].Source);
        if (newerPublication)
        {
            Assert.Equal("Replacement", Assert.Single(appState.ProcurementShoppingPlans).RecommendedWorld?.WorldName);
        }
        Assert.Equal(
            failDuringPersistence && !userChangedPlan && !newerPublication,
            appState.ProcurementRouteFailure is not null);
    }

    private sealed class FailedSaveJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult((TValue)(object)false);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

}
