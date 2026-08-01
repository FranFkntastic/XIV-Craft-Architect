using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class TradeProcurementRowBuilderTests
{
    [Theory]
    [InlineData(PlanRowScenario.SuppressedPrecraftVisible)]
    [InlineData(PlanRowScenario.MarketSourcedPrecraft)]
    [InlineData(PlanRowScenario.OnHandPrecraft)]
    [InlineData(PlanRowScenario.SuppressedPrecraftSourceIntent)]
    [InlineData(PlanRowScenario.OrdinaryOrderLivePlan)]
    [InlineData(PlanRowScenario.ReadOnlyCommissionLivePlan)]
    [InlineData(PlanRowScenario.EditableCommissionLivePlan)]
    public async Task PlanRowsPreserveSourceIntentAndCanonicalEditability(PlanRowScenario scenario)
    {
        switch (scenario)
        {
            case PlanRowScenario.SuppressedPrecraftVisible:
                FullySuppressedPrecraftRemainsInPlan();
                break;
            case PlanRowScenario.MarketSourcedPrecraft:
                DirectlySourcedPrecraftRemainsRequiredWhileItsIngredientsAreSuppressed(
                    AcquisitionSource.MarketBuyNq);
                break;
            case PlanRowScenario.OnHandPrecraft:
                DirectlySourcedPrecraftRemainsRequiredWhileItsIngredientsAreSuppressed(
                    AcquisitionSource.OnHand);
                break;
            case PlanRowScenario.SuppressedPrecraftSourceIntent:
                FullySuppressedPrecraftCanRecordWholeRowSourceIntent();
                break;
            case PlanRowScenario.OrdinaryOrderLivePlan:
                LivePlanMutationFollowsCanonicalWorkPackageEditability(false, false, true);
                break;
            case PlanRowScenario.ReadOnlyCommissionLivePlan:
                LivePlanMutationFollowsCanonicalWorkPackageEditability(true, false, false);
                ReadOnlyCommissionStillConsumesLivePlanStructure();
                await ProfileSyncAuthorityScenarios.LegacyStateMigratesOnceUnderExactAuthorityPath();
                await ProfileSyncAuthorityScenarios.CanonicalAdoptionIsAuthenticatedAndAdoptsReturnedIdentity(
                    System.Net.HttpStatusCode.Unauthorized,
                    shouldAdopt: false);
                await ProfileSyncAuthorityScenarios.CanonicalAdoptionIsAuthenticatedAndAdoptsReturnedIdentity(
                    System.Net.HttpStatusCode.OK,
                    shouldAdopt: true);
                ProfileSyncAuthorityScenarios.CommissionedLocalResidueRemainsVisibleButOutsideCanonicalOrders();
                break;
            case PlanRowScenario.EditableCommissionLivePlan:
                LivePlanMutationFollowsCanonicalWorkPackageEditability(true, true, true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static void FullySuppressedPrecraftRemainsInPlan()
    {
        var row = Row(
            source: AcquisitionSource.Craft,
            isActiveProcurement: false,
            isFullySuppressed: true,
            suppressedBy: ["Purchased assembly"],
            hasEditableOccurrences: false);

        Assert.True(TradeProcurementRowBuilder.IsPlanPrecraftRow(row));
        Assert.True(TradeProcurementRowBuilder.ShouldIncludePlanRow(row));
        Assert.Equal(
            "Not currently required because Purchased assembly is sourced directly",
            TradeProcurementRowBuilder.GetPlanRouteDescription(row));
    }

    private static void DirectlySourcedPrecraftRemainsRequiredWhileItsIngredientsAreSuppressed(
        AcquisitionSource source)
    {
        var row = Row(
            source,
            isActiveProcurement: true,
            isFullySuppressed: false,
            suppressedBy: [],
            hasEditableOccurrences: true);

        var description = TradeProcurementRowBuilder.GetPlanRouteDescription(row);

        Assert.StartsWith("Required item; its ingredients are not required", description);
        Assert.DoesNotContain("Not currently required", description);
    }

    private static void FullySuppressedPrecraftCanRecordWholeRowSourceIntent()
    {
        var row = Row(
            source: AcquisitionSource.MarketBuyNq,
            isActiveProcurement: false,
            isFullySuppressed: true,
            suppressedBy: ["Purchased assembly"],
            hasEditableOccurrences: false);

        Assert.True(TradeProcurementSourceMutationPolicy.CanChangeSource(row));
        Assert.False(TradeProcurementSourceMutationPolicy.CanChangeSource(
            row with { HasChildren = false }));
    }

    private static void LivePlanMutationFollowsCanonicalWorkPackageEditability(
        bool hasCanonicalCommission,
        bool canEditCanonicalWorkPackage,
        bool expected)
    {
        Assert.Equal(
            expected,
            TradeProcurementSourceMutationPolicy.CanMutateLivePlan(
                hasCanonicalCommission,
                canEditCanonicalWorkPackage));
    }

    private static void ReadOnlyCommissionStillConsumesLivePlanStructure()
    {
        var order = new TradeOrder
        {
            CraftPlanId = "plan-1",
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                Materials =
                [
                    new TradeOrderMaterialSnapshot(
                        999,
                        "Incomplete canonical leaf",
                        1,
                        false,
                        25,
                        25)
                ]
            }
        };
        var snapshot = new WorkerTradeProjection(
            Revision: 1,
            HasPlan: true,
            PlanId: "plan-1",
            PlanName: "Cobalt Joint Plate",
            SelectedDataCenter: "Aether",
            SelectedRegion: "North America",
            MarketFetchScope: MarketFetchScope.SelectedDataCenter,
            RequestedDataCenters: ["Aether"],
            MarketLens: MarketAcquisitionLens.MinimumUpfrontCost,
            PlanSessionVersion: 1,
            MarketAnalysisVersion: 1,
            CraftedItems: [],
            RootItems: [],
            MaterialLines: [],
            ActiveProcurementItems: [],
            AcquisitionRows:
            [
                ProjectionRow(
                    itemId: 1_000,
                    itemName: "Cobalt Ingot",
                    source: AcquisitionSource.MarketBuyNq,
                    hasChildren: true,
                    isActiveProcurement: true,
                    isFullySuppressed: false,
                    suppressedBy: []),
                ProjectionRow(
                    itemId: 1_001,
                    itemName: "Cobalt Rivets",
                    source: AcquisitionSource.Craft,
                    hasChildren: true,
                    isActiveProcurement: false,
                    isFullySuppressed: true,
                    suppressedBy: ["Purchased assembly"])
            ],
            CraftLabor: [],
            Warnings: []);

        Assert.True(TradeProcurementSourceMutationPolicy.CanReadLivePlan(
            order.CraftPlanId,
            "plan-1",
            snapshot.HasPlan,
            snapshot.PlanId));
        Assert.False(TradeProcurementSourceMutationPolicy.CanMutateLivePlan(
            hasCanonicalCommission: true,
            canEditCanonicalWorkPackage: false));
        Assert.Empty(TradeProcurementRowBuilder.BuildRows(
            order,
            draft: null,
            activePlanId: "plan-1",
            liveSnapshot: snapshot with { PlanId = "another-plan" }));

        var rows = TradeProcurementRowBuilder.BuildRows(
            order,
            draft: null,
            activePlanId: "plan-1",
            liveSnapshot: snapshot);

        Assert.Collection(
            rows.OrderBy(row => row.ItemId),
            ingot =>
            {
                Assert.Equal("Cobalt Ingot", ingot.ItemName);
                Assert.True(TradeProcurementRowBuilder.ShouldIncludePlanRow(ingot));
            },
            suppressedPrecraft =>
            {
                Assert.Equal("Cobalt Rivets", suppressedPrecraft.ItemName);
                Assert.True(suppressedPrecraft.IsFullySuppressed);
                Assert.True(TradeProcurementRowBuilder.ShouldIncludePlanRow(suppressedPrecraft));
            });
    }

    private static WorkerAcquisitionRowProjection ProjectionRow(
        int itemId,
        string itemName,
        AcquisitionSource source,
        bool hasChildren,
        bool isActiveProcurement,
        bool isFullySuppressed,
        IReadOnlyList<string> suppressedBy) =>
        new(
            NodeId: itemId.ToString(),
            ItemId: itemId,
            ItemName: itemName,
            IconId: 0,
            Source: source,
            SourceReason: AcquisitionSourceReason.UserSelected,
            MustBeHq: false,
            HasChildren: hasChildren,
            CanCraft: true,
            CanBeHq: false,
            CanBuyFromMarket: true,
            CanBuyFromVendor: false,
            TotalQuantity: 10,
            ActiveQuantity: isActiveProcurement ? 10 : 0,
            UsedIn: "Cobalt Joint Plate",
            HasSuppressedOccurrences: isFullySuppressed,
            IsFullySuppressed: isFullySuppressed,
            SuppressedBy: suppressedBy,
            IsActiveProcurement: isActiveProcurement,
            HasEditableOccurrences: !isFullySuppressed,
            IsMarketCandidate: true,
            MarketEvidence: "Test evidence",
            EstimatedCost: "100 gil each",
            IsMarketUnavailable: false,
            UnitPrice: 100,
            CalculatedTotalCost: 1_000,
            AvailableSources: [AcquisitionSource.Craft, AcquisitionSource.MarketBuyNq],
            Options: []);

    private static TradeOrderProcurementRow Row(
        AcquisitionSource source,
        bool isActiveProcurement,
        bool isFullySuppressed,
        IReadOnlyList<string> suppressedBy,
        bool hasEditableOccurrences) =>
        new(
            RowKey: "20:false",
            ItemId: 20,
            ItemName: "Cobalt Ingot",
            Quantity: 4_995,
            RequiresHq: false,
            SourceLabel: source.ToString(),
            UnitCost: 100,
            TotalCost: 499_500,
            Responsibility: CommissionMaterialResponsibility.Crafter,
            EvidenceSource: "Test evidence",
            EvidenceStatus: "Priced",
            UnitCostExplanation: "Fixture",
            WarningSummary: string.Empty,
            Warnings: [],
            IsLiveAcquisitionRow: true,
            IsActiveProcurement: isActiveProcurement,
            HasSuppressedOccurrences: isFullySuppressed,
            IsFullySuppressed: isFullySuppressed,
            SuppressedBy: suppressedBy,
            ActiveQuantity: isActiveProcurement ? 4_995 : 0,
            UsedIn: "Cobalt Joint Plate",
            HasEditableOccurrences: hasEditableOccurrences,
            Source: source,
            HasChildren: true,
            AvailableSources: [
                AcquisitionSource.Craft,
                AcquisitionSource.MarketBuyNq,
                AcquisitionSource.OnHand
            ]);

    public enum PlanRowScenario
    {
        SuppressedPrecraftVisible,
        MarketSourcedPrecraft,
        OnHandPrecraft,
        SuppressedPrecraftSourceIntent,
        OrdinaryOrderLivePlan,
        ReadOnlyCommissionLivePlan,
        EditableCommissionLivePlan
    }
}
