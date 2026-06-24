using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradeProcurementRowBuilderTests
{
    [Fact]
    public void BuildRows_UsesLiveLinkedPlanProjectionIncludingSuppressedRows()
    {
        var order = new TradeOrder
        {
            CraftPlanId = "linked-plan",
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                RootItems =
                [
                    new TradeOrderRootItemSnapshot(100, "Finished Commission", 1, MustBeHq: false, EstimatedSaleValue: 1000m)
                ],
                Materials =
                [
                    new TradeOrderMaterialSnapshot(200, "Stale Intermediate", 1, RequiresHq: false, UnitCost: 1m, TotalCost: 1m)
                ]
            }
        };
        var plan = CreatePlanWithSuppressedVendorChild();
        var snapshot = BuildSnapshot(plan, [new CoreMarketDataUnavailableItem(200, "Selected Intermediate")]);
        var draft = new TradePayrollWorkflowDraft
        {
            Responsibilities =
            [
                new TradePayrollResponsibilityLine(300, RequiresHq: false, CommissionMaterialResponsibility.Provided)
            ]
        };

        var rows = TradeProcurementRowBuilder.BuildRows(
            order,
            draft,
            activePlanId: "linked-plan",
            snapshot);

        Assert.Contains(rows, row => row.ItemId == 100 && row.SourceLabel == "Craft");

        var active = Assert.Single(rows, row => row.ItemId == 200);
        Assert.True(active.IsLiveAcquisitionRow);
        Assert.True(active.IsActiveProcurement);
        Assert.False(active.IsFullySuppressed);
        Assert.Equal("Buy NQ", active.SourceLabel);
        Assert.Equal(2, active.ActiveQuantity);
        Assert.True(active.HasEditableOccurrences);
        Assert.Equal("Needs data", active.EvidenceSource);

        var suppressed = Assert.Single(rows, row => row.ItemId == 300);
        Assert.True(suppressed.IsLiveAcquisitionRow);
        Assert.False(suppressed.IsActiveProcurement);
        Assert.True(suppressed.HasSuppressedOccurrences);
        Assert.True(suppressed.IsFullySuppressed);
        Assert.False(suppressed.HasEditableOccurrences);
        Assert.Equal("Vendor", suppressed.SourceLabel);
        Assert.Equal("Suppressed", suppressed.EvidenceStatus);
        Assert.Equal(CommissionMaterialResponsibility.Provided, suppressed.Responsibility);
        Assert.Contains("Selected Intermediate", suppressed.SuppressedBy);
        Assert.Equal(0, suppressed.ActiveQuantity);
        Assert.Equal("Selected Intermediate x2", suppressed.UsedIn);
    }

    [Fact]
    public void BuildRows_WhenLinkedPlanIsNotActive_UsesSavedOrderEvidenceSnapshot()
    {
        var order = new TradeOrder
        {
            CraftPlanId = "linked-plan",
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                Materials =
                [
                    new TradeOrderMaterialSnapshot(
                        200,
                        "Snapshot Ore",
                        4,
                        RequiresHq: false,
                        UnitCost: 50m,
                        TotalCost: 200m,
                        EvidenceSource: "Market recommendation")
                ]
            }
        };

        var rows = TradeProcurementRowBuilder.BuildRows(
            order,
            draft: null,
            activePlanId: "different-plan",
            liveSnapshot: null);

        var row = Assert.Single(rows);
        Assert.False(row.IsLiveAcquisitionRow);
        Assert.Equal(200, row.ItemId);
        Assert.Equal("Snapshot Ore", row.ItemName);
        Assert.Equal("Market", row.SourceLabel);
        Assert.Equal("Priced", row.EvidenceStatus);
    }

    [Fact]
    public void BuildRows_WhenLiveSnapshotDoesNotMatchLinkedPlan_UsesSavedOrderEvidenceSnapshot()
    {
        var order = new TradeOrder
        {
            CraftPlanId = "linked-plan",
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                Materials =
                [
                    new TradeOrderMaterialSnapshot(
                        200,
                        "Snapshot Ore",
                        4,
                        RequiresHq: false,
                        UnitCost: 50m,
                        TotalCost: 200m,
                        EvidenceSource: "Market recommendation")
                ]
            }
        };

        var rows = TradeProcurementRowBuilder.BuildRows(
            order,
            draft: null,
            activePlanId: "different-plan",
            liveSnapshot: BuildSnapshot(CreatePlanWithSuppressedVendorChild()));

        var row = Assert.Single(rows);
        Assert.False(row.IsLiveAcquisitionRow);
        Assert.Equal("Snapshot Ore", row.ItemName);
    }

    [Fact]
    public void LiveProjectionRows_DoNotChangeSnapshotBasedPaymentTotals()
    {
        var order = new TradeOrder
        {
            CraftPlanId = "linked-plan",
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                Materials =
                [
                    new TradeOrderMaterialSnapshot(
                        200,
                        "Snapshot Ore",
                        4,
                        RequiresHq: false,
                        UnitCost: 50m,
                        TotalCost: 200m)
                ]
            }
        };
        var rows = TradeProcurementRowBuilder.BuildRows(
            order,
            draft: null,
            activePlanId: "linked-plan",
            liveSnapshot: BuildSnapshot(CreatePlanWithSuppressedVendorChild()));

        Assert.Contains(rows, row => row.ItemId == 300 && row.IsFullySuppressed && row.TotalCost > 0);
        var payment = TradeCommissionPaymentSummary.FromOrder(order, draft: null);
        Assert.Equal(200m, payment.EstimatedProcurementTotal);
        Assert.DoesNotContain(payment.Materials, material => material.ItemId == 300);
    }

    private static CraftingPlan CreatePlanWithSuppressedVendorChild()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            NodeId = "root",
            Name = "Finished Commission",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanCraft = true,
            Yield = 1
        };
        var intermediate = new PlanNode
        {
            ItemId = 200,
            NodeId = "intermediate",
            Name = "Selected Intermediate",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanBuyFromMarket = true,
            MarketPrice = 300m,
            Parent = root,
            Yield = 1
        };
        var suppressed = new PlanNode
        {
            ItemId = 300,
            NodeId = "suppressed",
            Name = "Suppressed Vendor Child",
            Quantity = 8,
            Source = AcquisitionSource.VendorBuy,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            MarketPrice = 10m,
            VendorPrice = 5m,
            Parent = intermediate,
            Yield = 1
        };

        intermediate.Children.Add(suppressed);
        root.Children.Add(intermediate);
        return new CraftingPlan { RootItems = [root] };
    }

    private static AcquisitionEvaluationSnapshot BuildSnapshot(
        CraftingPlan plan,
        IReadOnlyList<CoreMarketDataUnavailableItem>? unavailableMarketItems = null)
    {
        var demand = new RecipeDemandProjectionService().Build(plan, snapshot: null);
        return AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans: [],
            unavailableMarketItems ?? [],
            AcquisitionFilter.All,
            demand);
    }
}
