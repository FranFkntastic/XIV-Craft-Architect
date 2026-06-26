using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class TradeOrderWorkflow
{
    public static TradeOrder CopyOrder(TradeOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new TradeOrder
        {
            Id = order.Id,
            CompanyProfileId = order.CompanyProfileId,
            Title = order.Title,
            Status = order.Status,
            AssignedCrafterId = order.AssignedCrafterId,
            CommissionedAtUtc = order.CommissionedAtUtc,
            CreatedAtUtc = order.CreatedAtUtc,
            UpdatedAtUtc = order.UpdatedAtUtc,
            Notes = order.Notes,
            SourceSnapshot = CopySourceSnapshot(order.SourceSnapshot),
            History = (order.History ?? Array.Empty<TradeOrderHistoryEvent>()).ToArray(),
            PayrollDraftId = order.PayrollDraftId,
            CraftPlanId = order.CraftPlanId,
            CraftPlanName = order.CraftPlanName,
            CraftPlanSavedAtUtc = order.CraftPlanSavedAtUtc,
            CraftPlanLinkKind = order.CraftPlanLinkKind,
            RemoteId = order.RemoteId,
            SyncState = order.SyncState
        };
    }

    public static TradeOrderSourceSnapshot CopySourceSnapshot(TradeOrderSourceSnapshot? source)
    {
        source ??= new TradeOrderSourceSnapshot();

        return new TradeOrderSourceSnapshot
        {
            SourceKind = source.SourceKind,
            SourcePlanId = source.SourcePlanId,
            SourcePlanName = source.SourcePlanName,
            DataCenter = source.DataCenter,
            World = source.World,
            PlanSessionVersion = source.PlanSessionVersion,
            MarketAnalysisVersion = source.MarketAnalysisVersion,
            ImportedAtUtc = source.ImportedAtUtc,
            RootItems = (source.RootItems ?? Array.Empty<TradeOrderRootItemSnapshot>()).ToArray(),
            Materials = (source.Materials ?? Array.Empty<TradeOrderMaterialSnapshot>()).ToArray(),
            CraftLabor = (source.CraftLabor ?? Array.Empty<TradeOrderCraftLaborSnapshot>()).ToArray(),
            Warnings = (source.Warnings ?? Array.Empty<string>()).ToArray()
        };
    }

    public static TradePayrollWorkflowDraft CopyPayrollDraft(TradePayrollWorkflowDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return new TradePayrollWorkflowDraft
        {
            Id = draft.Id,
            CompanyProfileId = draft.CompanyProfileId,
            OrderId = draft.OrderId,
            PlanSessionVersion = draft.PlanSessionVersion,
            MarketAnalysisVersion = draft.MarketAnalysisVersion,
            SourcePlanName = draft.SourcePlanName,
            AssignedCrafterId = draft.AssignedCrafterId,
            AssignedCrafterDisplayName = draft.AssignedCrafterDisplayName,
            CommissionPercent = draft.CommissionPercent,
            ActivePaymentContract = draft.ActivePaymentContract,
            LaborStandard = draft.LaborStandard,
            Responsibilities = (draft.Responsibilities ?? Array.Empty<TradePayrollResponsibilityLine>()).ToArray(),
            RemoteId = draft.RemoteId,
            SyncState = draft.SyncState,
            CreatedAtUtc = draft.CreatedAtUtc,
            UpdatedAtUtc = draft.UpdatedAtUtc
        };
    }

    public static TradePayrollWorkflowDraft WithMaterialResponsibility(
        TradePayrollWorkflowDraft draft,
        int itemId,
        bool requiresHq,
        CommissionMaterialResponsibility responsibility)
    {
        var copy = CopyPayrollDraft(draft);
        copy.Responsibilities = (copy.Responsibilities ?? Array.Empty<TradePayrollResponsibilityLine>())
            .Where(line => line.ItemId != itemId || line.RequiresHq != requiresHq)
            .Append(new TradePayrollResponsibilityLine(itemId, requiresHq, responsibility))
            .ToArray();
        return copy;
    }

    public static bool AppendStatusHistory(
        TradeOrder order,
        TradeOrderStatus previousStatus,
        TradeOrderStatus newStatus,
        string note,
        DateTime createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (previousStatus == newStatus)
        {
            return false;
        }

        AppendHistory(order, new TradeOrderHistoryEvent
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = order.CompanyProfileId,
            OrderId = order.Id,
            Kind = TradeOrderStatusWorkflow.IsArchived(newStatus)
                ? TradeOrderHistoryEventKind.Closed
                : TradeOrderHistoryEventKind.StatusChanged,
            Note = note,
            FromStatus = previousStatus,
            ToStatus = newStatus,
            CreatedAtUtc = createdAtUtc
        });
        return true;
    }

    public static bool AppendReopenedHistory(
        TradeOrder order,
        TradeOrderStatus previousStatus,
        TradeOrderStatus newStatus,
        DateTime createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (previousStatus == newStatus)
        {
            return false;
        }

        AppendHistory(order, new TradeOrderHistoryEvent
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = order.CompanyProfileId,
            OrderId = order.Id,
            Kind = TradeOrderHistoryEventKind.Reopened,
            Note = "Reopened order.",
            FromStatus = previousStatus,
            ToStatus = newStatus,
            CreatedAtUtc = createdAtUtc
        });
        return true;
    }

    public static bool AppendAssignmentHistory(
        TradeOrder order,
        Guid? previousCrafterId,
        Guid? newCrafterId,
        string? newCrafterDisplayName,
        DateTime createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (previousCrafterId == newCrafterId)
        {
            return false;
        }

        var crafterName = newCrafterId.HasValue
            ? string.IsNullOrWhiteSpace(newCrafterDisplayName) ? "unknown crafter" : newCrafterDisplayName.Trim()
            : "unassigned";
        AppendHistory(order, new TradeOrderHistoryEvent
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = order.CompanyProfileId,
            OrderId = order.Id,
            Kind = TradeOrderHistoryEventKind.Assigned,
            Note = newCrafterId.HasValue ? $"Assigned to {crafterName}." : "Assignment cleared.",
            CrafterId = newCrafterId,
            CreatedAtUtc = createdAtUtc
        });
        return true;
    }

    public static TradeOrderStatus ResolveStatusForAssignment(
        TradeOrderStatus requestedStatus,
        Guid? assignedCrafterId)
    {
        return assignedCrafterId.HasValue && requestedStatus == TradeOrderStatus.ReadyToAssign
            ? TradeOrderStatus.Assigned
            : requestedStatus;
    }

    public static void AppendPricingEvidenceHistory(
        TradeOrder order,
        int materialCount,
        int refreshedEvidenceCount,
        DateTime refreshedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(order);

        var refreshedEvidenceText = refreshedEvidenceCount > 0
            ? $" Refreshed {refreshedEvidenceCount:N0} market evidence entries."
            : string.Empty;
        AppendHistory(order, new TradeOrderHistoryEvent
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = order.CompanyProfileId,
            OrderId = order.Id,
            Kind = TradeOrderHistoryEventKind.PricingRefreshed,
            Note = $"Pricing evidence refreshed for {materialCount:N0} material lines.{refreshedEvidenceText}",
            CreatedAtUtc = refreshedAtUtc
        });
    }

    public static bool AppendPayrollLinkedHistory(TradeOrder order, DateTime createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(order);

        if ((order.History ?? Array.Empty<TradeOrderHistoryEvent>()).Any(history => history.Kind == TradeOrderHistoryEventKind.PayrollLinked))
        {
            return false;
        }

        AppendHistory(order, new TradeOrderHistoryEvent
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = order.CompanyProfileId,
            OrderId = order.Id,
            Kind = TradeOrderHistoryEventKind.PayrollLinked,
            Note = "Payment workflow draft linked.",
            CreatedAtUtc = createdAtUtc
        });
        return true;
    }

    public static TradeOrderProcurementEvidenceState GetProcurementEvidenceState(TradeOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var materials = order.SourceSnapshot?.Materials ?? Array.Empty<TradeOrderMaterialSnapshot>();
        var materialCount = materials.Count;
        var pricedCount = materials.Count(material => material.UnitCost > 0 && material.TotalCost > 0);
        return new TradeOrderProcurementEvidenceState(materialCount, pricedCount);
    }

    public static bool IsPaymentReady(TradeOrder order, TradePayrollWorkflowDraft? draft)
    {
        ArgumentNullException.ThrowIfNull(order);

        return TradeCommissionPaymentSummary.FromOrder(order, draft).TotalPayment > 0;
    }

    public static IReadOnlyList<TradeOrderProcurementRow> BuildProcurementRows(
        TradeOrder order,
        TradePayrollWorkflowDraft? draft)
    {
        ArgumentNullException.ThrowIfNull(order);

        return TradeCommissionPaymentSummary.FromOrder(order, draft)
            .Materials
            .Select(material =>
            {
                var evidenceStatus = GetEvidenceStatus(material);
                return new TradeOrderProcurementRow(
                    $"{material.ItemId}:{material.RequiresHq}",
                    material.ItemId,
                    material.Name,
                    material.Quantity,
                    material.RequiresHq,
                    GetSourceLabel(material),
                    material.UnitCost,
                    material.TotalCost,
                    material.Responsibility,
                    string.IsNullOrWhiteSpace(material.EvidenceSource) ? evidenceStatus : material.EvidenceSource,
                    evidenceStatus,
                    material.UnitCostExplanation,
                    material.Warnings.Count > 0 ? material.Warnings[0] : string.Empty,
                    material.Warnings);
            })
            .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static TradeOrderCraftPlanLinkDraft CreateGeneratedCraftPlanLinkDraft(
        TradeOrder order,
        bool replaceExistingPlan)
    {
        ArgumentNullException.ThrowIfNull(order);

        var previousPlanId = string.IsNullOrWhiteSpace(order.CraftPlanId)
            ? null
            : order.CraftPlanId;
        var canReuseExistingPlan =
            replaceExistingPlan &&
            order.CraftPlanLinkKind == TradeOrderCraftPlanLinkKind.OrderGenerated &&
            !string.IsNullOrWhiteSpace(previousPlanId);
        return new TradeOrderCraftPlanLinkDraft(
            canReuseExistingPlan ? previousPlanId! : Guid.NewGuid().ToString("D"),
            CreateGeneratedCraftPlanName(order),
            canReuseExistingPlan,
            PreviousPlanId: replaceExistingPlan && !canReuseExistingPlan ? previousPlanId : null);
    }

    public static TradeOrderCraftPlanReplacementAssessment AssessGeneratedCraftPlanReplacement(TradeOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var evidence = GetProcurementEvidenceState(order);
        var rootItems = order.SourceSnapshot?.RootItems ?? Array.Empty<TradeOrderRootItemSnapshot>();
        var hasLinkedPlan = !string.IsNullOrWhiteSpace(order.CraftPlanId);
        var mode = hasLinkedPlan
            ? TradeOrderCraftPlanReplacementMode.Rebuild
            : TradeOrderCraftPlanReplacementMode.Create;
        if (hasLinkedPlan && order.CraftPlanLinkKind == TradeOrderCraftPlanLinkKind.Unknown)
        {
            mode = TradeOrderCraftPlanReplacementMode.ReplaceLegacyLink;
        }

        return new TradeOrderCraftPlanReplacementAssessment(
            mode,
            hasLinkedPlan,
            order.CraftPlanLinkKind,
            string.IsNullOrWhiteSpace(order.CraftPlanName) ? "Linked craft plan" : order.CraftPlanName,
            order.CraftPlanSavedAtUtc,
            rootItems.Count,
            rootItems.Sum(item => item.Quantity),
            evidence.MaterialCount,
            evidence.PricedMaterialCount,
            RequiresConfirmation: hasLinkedPlan || evidence.HasMaterials);
    }

    public static string CreateGeneratedCraftPlanName(TradeOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return string.IsNullOrWhiteSpace(order.Title)
            ? $"Trade Order {order.Id:N}"
            : $"Order - {order.Title.Trim()}";
    }

    public static void ApplyGeneratedCraftPlanLink(
        TradeOrder order,
        string planId,
        string planName,
        IReadOnlyList<MaterialAggregate> activeProcurementItems,
        IReadOnlyList<TradeRequestedOrderOutput> outputs,
        DateTime savedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planName);
        ArgumentNullException.ThrowIfNull(activeProcurementItems);
        ArgumentNullException.ThrowIfNull(outputs);

        order.CraftPlanId = planId;
        order.CraftPlanName = planName;
        order.CraftPlanSavedAtUtc = savedAtUtc;
        order.CraftPlanLinkKind = TradeOrderCraftPlanLinkKind.OrderGenerated;
        order.SourceSnapshot ??= new TradeOrderSourceSnapshot();
        order.SourceSnapshot.Materials = TradeRequestedOrderWorkflow.BuildMaterialSnapshots(
            activeProcurementItems,
            outputs);
        order.SourceSnapshot.ImportedAtUtc = savedAtUtc;
        order.UpdatedAtUtc = savedAtUtc;
    }

    public static bool AppendCraftPlanLinkedHistory(
        TradeOrder order,
        TradeOrderCraftPlanLinkDraft linkDraft,
        DateTime createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(linkDraft);

        var note = string.IsNullOrWhiteSpace(linkDraft.PreviousPlanId)
            ? "Linked craft plan created."
            : "Linked craft plan rebuilt from order request.";
        AppendHistory(order, new TradeOrderHistoryEvent
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = order.CompanyProfileId,
            OrderId = order.Id,
            Kind = TradeOrderHistoryEventKind.CraftPlanLinked,
            Note = note,
            CreatedAtUtc = createdAtUtc
        });
        return true;
    }

    private static void AppendHistory(TradeOrder order, TradeOrderHistoryEvent historyEvent)
    {
        order.History = (order.History ?? Array.Empty<TradeOrderHistoryEvent>())
            .Append(historyEvent)
            .ToArray();
    }

    private static string GetEvidenceStatus(TradeCommissionPaymentMaterial material)
    {
        return material.UnitCost > 0 && material.TotalCost > 0
            ? "Priced"
            : "Missing evidence";
    }

    private static string GetSourceLabel(TradeCommissionPaymentMaterial material)
    {
        if (material.UnitCost <= 0 || material.TotalCost <= 0)
        {
            return "Unpriced";
        }

        if (material.EvidenceSource.Contains("vendor", StringComparison.OrdinalIgnoreCase))
        {
            return "Vendor";
        }

        if (material.EvidenceSource.Contains("market", StringComparison.OrdinalIgnoreCase) ||
            material.EvidenceSource.Contains("world", StringComparison.OrdinalIgnoreCase) ||
            material.EvidenceSource.Contains("split", StringComparison.OrdinalIgnoreCase))
        {
            return "Market";
        }

        return string.IsNullOrWhiteSpace(material.EvidenceSource)
            ? "Captured"
            : material.EvidenceSource;
    }
}

public sealed record TradeOrderProcurementEvidenceState(int MaterialCount, int PricedMaterialCount)
{
    public bool HasMaterials => MaterialCount > 0;

    public bool IsFullyPriced => HasMaterials && PricedMaterialCount == MaterialCount;
}

public sealed record TradeOrderCraftPlanLinkDraft(
    string PlanId,
    string PlanName,
    bool ReusesExistingPlan,
    string? PreviousPlanId);

public sealed record TradeOrderProcurementRow(
    string RowKey,
    int ItemId,
    string ItemName,
    int Quantity,
    bool RequiresHq,
    string SourceLabel,
    decimal UnitCost,
    decimal TotalCost,
    CommissionMaterialResponsibility Responsibility,
    string EvidenceSource,
    string EvidenceStatus,
    string UnitCostExplanation,
    string WarningSummary,
    IReadOnlyList<string> Warnings,
    bool IsLiveAcquisitionRow = false,
    bool IsActiveProcurement = true,
    bool HasSuppressedOccurrences = false,
    bool IsFullySuppressed = false,
    IReadOnlyList<string>? SuppressedBy = null,
    int ActiveQuantity = 0,
    string UsedIn = "",
    bool HasEditableOccurrences = true,
    AcquisitionSource Source = AcquisitionSource.UnknownSource)
{
    public IReadOnlyList<string> SuppressedBy { get; init; } = SuppressedBy ?? Array.Empty<string>();
}

public enum TradeOrderCraftPlanReplacementMode
{
    Create,
    Rebuild,
    ReplaceLegacyLink
}

public sealed record TradeOrderCraftPlanReplacementAssessment(
    TradeOrderCraftPlanReplacementMode Mode,
    bool HasLinkedPlan,
    TradeOrderCraftPlanLinkKind LinkKind,
    string ExistingPlanName,
    DateTime? ExistingPlanSavedAtUtc,
    int OutputLineCount,
    int OutputQuantity,
    int MaterialLineCount,
    int PricedMaterialLineCount,
    bool RequiresConfirmation);
