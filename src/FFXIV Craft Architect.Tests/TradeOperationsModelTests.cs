using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradeOperationsModelTests
{
    [Fact]
    public void CreateLocalCompanyProfile_UsesStableIdentityAndSyncFields()
    {
        var createdAt = new DateTime(2026, 6, 17, 12, 30, 0, DateTimeKind.Utc);

        var profile = TradeCompanyProfile.CreateLocal("Night Market Co.", createdAt);

        Assert.NotEqual(Guid.Empty, profile.Id);
        Assert.Equal(TradeCompanyProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal("Night Market Co.", profile.Name);
        Assert.Equal(createdAt, profile.CreatedAtUtc);
        Assert.Equal(createdAt, profile.UpdatedAtUtc);
        Assert.Null(profile.RemoteId);
        Assert.Equal(TradeSyncState.LocalOnly, profile.SyncState);
        Assert.Equal(TradePaymentPolicy.LegacyDefault, profile.PaymentPolicy);
    }


    [Fact]
    public void TradeCrafterProfile_CanStoreLodestoneProvenance()
    {
        var syncedAt = new DateTime(2026, 6, 20, 4, 0, 0, DateTimeKind.Utc);

        var crafter = new TradeCrafterProfile
        {
            DisplayName = "Level Checker",
            LodestoneCharacterId = "16331040",
            LodestoneProfileUrl = "https://na.finalfantasyxiv.com/lodestone/character/16331040/",
            LodestoneAvatarUrl = "https://img2.finalfantasyxiv.com/example.jpg",
            LodestonePortraitUrl = "https://img2.finalfantasyxiv.com/portrait.jpg",
            LodestoneFreeCompanyName = "Terms of Service",
            LodestoneRace = "Viera",
            LodestoneClan = "Veena",
            LodestoneGender = "Female",
            LodestoneLastSyncedAtUtc = syncedAt
        };

        Assert.Equal("16331040", crafter.LodestoneCharacterId);
        Assert.Equal("https://na.finalfantasyxiv.com/lodestone/character/16331040/", crafter.LodestoneProfileUrl);
        Assert.Equal("https://img2.finalfantasyxiv.com/example.jpg", crafter.LodestoneAvatarUrl);
        Assert.Equal("https://img2.finalfantasyxiv.com/portrait.jpg", crafter.LodestonePortraitUrl);
        Assert.Equal("Terms of Service", crafter.LodestoneFreeCompanyName);
        Assert.Equal("Viera", crafter.LodestoneRace);
        Assert.Equal("Veena", crafter.LodestoneClan);
        Assert.Equal("Female", crafter.LodestoneGender);
        Assert.Equal(syncedAt, crafter.LodestoneLastSyncedAtUtc);
    }
    [Fact]
    public void OrderStatus_WorkflowOrderKeepsActiveStatusesBeforeArchiveStatuses()
    {
        var ordered = TradeOrderStatusWorkflow.ActiveStatuses.Concat(TradeOrderStatusWorkflow.ArchiveStatuses).ToArray();

        Assert.Equal(
            [
                TradeOrderStatus.Draft,
                TradeOrderStatus.ReadyToAssign,
                TradeOrderStatus.Assigned,
                TradeOrderStatus.InProgress,
                TradeOrderStatus.AwaitingDelivery,
                TradeOrderStatus.Completed,
                TradeOrderStatus.Canceled
            ],
            ordered);
    }
    [Theory]
    [InlineData(TradeOrderStatus.ReadyToAssign, false, true)]
    [InlineData(TradeOrderStatus.ReadyToAssign, true, true)]
    [InlineData(TradeOrderStatus.Draft, false, true)]
    [InlineData(TradeOrderStatus.Assigned, true, true)]
    [InlineData(TradeOrderStatus.Assigned, false, true)]
    [InlineData(TradeOrderStatus.InProgress, true, false)]
    [InlineData(TradeOrderStatus.AwaitingDelivery, true, false)]
    [InlineData(TradeOrderStatus.Completed, false, false)]
    [InlineData(TradeOrderStatus.Canceled, false, false)]
    public void TradeOrderWorkflow_CanEditRequestedOutputsOnlyBeforeWorkStarts(
          TradeOrderStatus status,
          bool hasCrafter,
          bool expected)
    {
        var order = new TradeOrder
        {
            Status = status,
            AssignedCrafterId = hasCrafter ? Guid.NewGuid() : null
        };

        Assert.Equal(expected, TradeOrderWorkflow.CanEditRequestedOutputs(order));
    }

    [Fact]
    public void TradeOrderWorkflow_CopyOrderCreatesDetachedMutableSnapshot()
    {
        var order = new TradeOrder
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = Guid.NewGuid(),
            Title = "Original",
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                RootItems =
                [
                    new TradeOrderRootItemSnapshot(100, "Root", 1, MustBeHq: false, EstimatedSaleValue: 1000m)
                ],
                Materials =
                [
                    new TradeOrderMaterialSnapshot(200, "Ore", 2, RequiresHq: false, UnitCost: 50m, TotalCost: 100m)
                ],
                CraftLabor =
                [
                    new TradeOrderCraftLaborSnapshot("root", 300, "Root", 1, CraftCount: 4)
                ],
                Warnings = ["old evidence"]
            },
            History =
            [
                TradeOrderHistoryEvent.CreateManualNote(Guid.NewGuid(), Guid.NewGuid(), "note", DateTime.UtcNow)
            ]
        };

        var copy = TradeOrderWorkflow.CopyOrder(order);

        Assert.NotSame(order, copy);
        Assert.NotSame(order.SourceSnapshot, copy.SourceSnapshot);
        Assert.NotSame(order.SourceSnapshot.Materials, copy.SourceSnapshot.Materials);
        Assert.NotSame(order.SourceSnapshot.CraftLabor, copy.SourceSnapshot.CraftLabor);
        Assert.NotSame(order.History, copy.History);
        copy.Title = "Changed";
        copy.SourceSnapshot.Materials = [];
        copy.SourceSnapshot.CraftLabor = [];
        Assert.Equal("Original", order.Title);
        Assert.NotEmpty(order.SourceSnapshot.Materials);
        Assert.NotEmpty(order.SourceSnapshot.CraftLabor);
    }


    [Fact]
    public void TradeOrderWorkflow_IsPaymentReadyUsesEffectiveLaborPolicyWithoutDraft()
    {
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                CraftLabor =
                [
                    new TradeOrderCraftLaborSnapshot("root", 200, "Finished Item", 1, 3)
                ]
            }
        };

        Assert.False(TradeOrderWorkflow.IsPaymentReady(order, draft: null));
        Assert.True(TradeOrderWorkflow.IsPaymentReady(order, draft: null, effectivePolicy: CreateLaborPolicy()));
    }

    [Fact]
    public void TradeOrderWorkflow_WithMaterialResponsibilityDoesNotMutateLoadedDraft()
    {
        var laborStandard = new TradeLaborStandard(
            "Cobalt Rivets benchmark",
            5094,
            "Cobalt Rivets",
            999,
            true,
            120_000m,
            200,
            DateTime.UtcNow);
        var draft = new TradePayrollWorkflowDraft
        {
            ActivePaymentContract = TradePaymentContractMode.LaborStandard,
            LaborStandard = laborStandard,
            Responsibilities =
            [
                new TradePayrollResponsibilityLine(100, RequiresHq: false, CommissionMaterialResponsibility.Crafter),
                new TradePayrollResponsibilityLine(200, RequiresHq: true, CommissionMaterialResponsibility.Provided)
            ]
        };

        var changed = TradeOrderWorkflow.WithMaterialResponsibility(
            draft,
            100,
            requiresHq: false,
            CommissionMaterialResponsibility.Provided);

        Assert.Equal(CommissionMaterialResponsibility.Crafter, draft.Responsibilities.First(line => line.ItemId == 100).Responsibility);
        Assert.Equal(TradePaymentContractMode.LaborStandard, changed.ActivePaymentContract);
        Assert.Equal(laborStandard, changed.LaborStandard);
        Assert.Equal(CommissionMaterialResponsibility.Provided, changed.Responsibilities.First(line => line.ItemId == 100).Responsibility);
        Assert.Contains(changed.Responsibilities, line => line.ItemId == 200 && line.RequiresHq && line.Responsibility == CommissionMaterialResponsibility.Provided);
        Assert.Equal(2, changed.Responsibilities.Count);
    }

    [Fact]
    public void TradeOrderWorkflow_AppendsLifecycleHistoryWithStableKinds()
    {
        var order = new TradeOrder
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = Guid.NewGuid()
        };
        var timestamp = new DateTime(2026, 6, 20, 17, 30, 0, DateTimeKind.Utc);

        var assigned = TradeOrderWorkflow.AppendAssignmentHistory(
            order,
            previousCrafterId: null,
            newCrafterId: Guid.NewGuid(),
            newCrafterDisplayName: "Zeltrech Alba",
            timestamp);
        var closed = TradeOrderWorkflow.AppendStatusHistory(
            order,
            TradeOrderStatus.Assigned,
            TradeOrderStatus.Completed,
            "Done",
            timestamp);
        var reopened = TradeOrderWorkflow.AppendReopenedHistory(
            order,
            TradeOrderStatus.Completed,
            TradeOrderStatus.Assigned,
            timestamp);

        Assert.True(assigned);
        Assert.True(closed);
        Assert.True(reopened);
        Assert.Contains(order.History, history => history.Kind == TradeOrderHistoryEventKind.Assigned && history.Note == "Assigned to Zeltrech Alba.");
        Assert.Contains(order.History, history => history.Kind == TradeOrderHistoryEventKind.Closed && history.Note == "Done");
        Assert.Contains(order.History, history => history.Kind == TradeOrderHistoryEventKind.Reopened && history.Note == "Reopened order.");
    }

    [Fact]
    public void TradeOrderWorkflow_AssignedCrafterPromotesReadyToAssignStatus()
    {
        var crafterId = Guid.NewGuid();

        var status = TradeOrderWorkflow.ResolveStatusForAssignment(
            TradeOrderStatus.ReadyToAssign,
            crafterId);

        Assert.Equal(TradeOrderStatus.Assigned, status);
    }
    [Fact]
    public void TradeOrderWorkflow_ProcurementEvidenceStateSummarizesPricedMaterialLines()
    {
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                Materials =
                [
                    new TradeOrderMaterialSnapshot(100, "Priced", 1, RequiresHq: false, UnitCost: 100m, TotalCost: 100m),
                    new TradeOrderMaterialSnapshot(101, "Missing", 1, RequiresHq: false, UnitCost: 0m, TotalCost: 0m)
                ]
            }
        };

        var state = TradeOrderWorkflow.GetProcurementEvidenceState(order);

        Assert.True(state.HasMaterials);
        Assert.False(state.IsFullyPriced);
        Assert.Equal(2, state.MaterialCount);
        Assert.Equal(1, state.PricedMaterialCount);
    }


    [Fact]
    public void TradeOrderWorkflow_WithRequestedOutputsReplacesRootItemsAndClearsStaleEvidence()
    {
        var updatedAt = new DateTime(2026, 7, 2, 20, 0, 0, DateTimeKind.Utc);
        var order = new TradeOrder
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = Guid.NewGuid(),
            Status = TradeOrderStatus.ReadyToAssign,
            CraftPlanId = "old-plan",
            CraftPlanName = "Order - Old",
            CraftPlanSavedAtUtc = updatedAt.AddDays(-1),
            CraftPlanLinkKind = TradeOrderCraftPlanLinkKind.OrderGenerated,
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                SourceKind = TradeOrderSourceKind.ActiveCraftPlan,
                SourcePlanId = "old-source-plan",
                SourcePlanName = "Old source plan",
                RootItems =
                [
                    new TradeOrderRootItemSnapshot(100, "Old Plate", 999, false, 10_000m)
                ],
                Materials =
                [
                    new TradeOrderMaterialSnapshot(200, "Old Ore", 3, false, 50m, 150m)
                ],
                CraftLabor =
                [
                    new TradeOrderCraftLaborSnapshot("old", 100, "Old Plate", 999, 999)
                ],
                Warnings = ["old warning"]
            }
        };

        var changed = TradeOrderWorkflow.WithRequestedOutputs(
            order,
            [
                new TradeRequestedOrderOutput(300, "New Plate", 1_998, false, 25_000m)
            ],
            updatedAt);

        Assert.NotSame(order, changed);
        var root = Assert.Single(changed.SourceSnapshot.RootItems);
        Assert.Equal(300, root.ItemId);
        Assert.Equal("New Plate", root.Name);
        Assert.Equal(1_998, root.Quantity);
        Assert.Null(changed.CraftPlanId);
        Assert.Null(changed.CraftPlanName);
        Assert.Null(changed.CraftPlanSavedAtUtc);
        Assert.Equal(TradeOrderCraftPlanLinkKind.Unknown, changed.CraftPlanLinkKind);
        Assert.Equal(TradeOrderSourceKind.TradeRequestedOutputs, changed.SourceSnapshot.SourceKind);
        Assert.Null(changed.SourceSnapshot.SourcePlanId);
        Assert.Equal("Trade requested outputs", changed.SourceSnapshot.SourcePlanName);
        Assert.Empty(changed.SourceSnapshot.Materials);
        Assert.Empty(changed.SourceSnapshot.CraftLabor);
        Assert.Contains(changed.SourceSnapshot.Warnings, warning => warning.Contains("Requested outputs changed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(changed.History, history => history.Kind == TradeOrderHistoryEventKind.RequestUpdated);
        Assert.Equal(updatedAt, changed.UpdatedAtUtc);
        Assert.NotNull(order.CraftPlanId);
        Assert.NotEmpty(order.SourceSnapshot.Materials);
    }






    [Fact]
    public void TradeOrderWorkflow_ApplyGeneratedCraftPlanLinkRebuildsMaterialSnapshot()
    {
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                Materials =
                [
                    new TradeOrderMaterialSnapshot(999, "Old", 1, RequiresHq: false, UnitCost: 1m, TotalCost: 1m)
                ]
            }
        };
        var savedAt = new DateTime(2026, 6, 20, 18, 0, 0, DateTimeKind.Utc);
        var activeProcurementItems = new[]
        {
            new MaterialAggregate
            {
                ItemId = 200,
                Name = "Cobalt Ore",
                TotalQuantity = 3,
                UnitPrice = 100m
            }
        };
        var outputs = new[]
        {
            new TradeRequestedOrderOutput(100, "Cobalt Ingot", 1, MustBeHq: false, EstimatedSaleValue: 1000m)
        };

        TradeOrderWorkflow.ApplyGeneratedCraftPlanLink(
            order,
            "generated-plan",
            "Order - Cobalt Ingot",
            activeProcurementItems,
            outputs,
            savedAt);

        Assert.Equal("generated-plan", order.CraftPlanId);
        Assert.Equal("Order - Cobalt Ingot", order.CraftPlanName);
        Assert.Equal(savedAt, order.CraftPlanSavedAtUtc);
        Assert.Equal(TradeOrderCraftPlanLinkKind.OrderGenerated, order.CraftPlanLinkKind);
        Assert.Equal(savedAt, order.SourceSnapshot.ImportedAtUtc);
        Assert.Equal(savedAt, order.UpdatedAtUtc);
        var material = Assert.Single(order.SourceSnapshot.Materials);
        Assert.Equal(200, material.ItemId);
        Assert.Equal("Cobalt Ore", material.Name);
        Assert.Equal(3, material.Quantity);
        Assert.Equal(100m, material.UnitCost);
        Assert.Equal(300m, material.TotalCost);
    }

    private static TradePaymentPolicy CreateLaborPolicy()
    {
        return new TradePaymentPolicy(
            TradePaymentContractMode.LaborStandard,
            18m,
            new TradeLaborStandard(
                "Cobalt Rivets benchmark",
                5094,
                "Cobalt Rivets",
                999,
                true,
                150_000m,
                200,
                new DateTime(2026, 6, 25, 18, 0, 0, 0, DateTimeKind.Utc)));
    }
}
