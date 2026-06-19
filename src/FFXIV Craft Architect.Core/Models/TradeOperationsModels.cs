namespace FFXIV_Craft_Architect.Core.Models;

public enum TradeSyncState
{
    LocalOnly,
    Synced,
    PendingSync,
    Conflict
}

public sealed class TradeCompanyProfile
{
    public const int CurrentSchemaVersion = 1;

    public Guid Id { get; set; } = Guid.NewGuid();
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? RemoteId { get; set; }
    public TradeSyncState SyncState { get; set; } = TradeSyncState.LocalOnly;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public static TradeCompanyProfile CreateLocal(string name, DateTime createdAtUtc)
    {
        return new TradeCompanyProfile
        {
            Id = Guid.NewGuid(),
            SchemaVersion = CurrentSchemaVersion,
            Name = name,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
            SyncState = TradeSyncState.LocalOnly
        };
    }
}

public enum TradeCraftingJob
{
    Carpenter,
    Blacksmith,
    Armorer,
    Goldsmith,
    Leatherworker,
    Weaver,
    Alchemist,
    Culinarian
}

public sealed record TradeCraftingJobLevel(TradeCraftingJob Job, int Level);

public sealed class TradeCrafterProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyProfileId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ContactHandle { get; set; }
    public string? WorldName { get; set; }
    public string? DataCenter { get; set; }
    public string? AvailabilityNotes { get; set; }
    public string? PaymentNotes { get; set; }
    public string? OperatorNotes { get; set; }
    public IReadOnlyList<TradeCraftingJobLevel> JobLevels { get; set; } = Array.Empty<TradeCraftingJobLevel>();
    public string? RemoteId { get; set; }
    public TradeSyncState SyncState { get; set; } = TradeSyncState.LocalOnly;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public enum TradeOrderStatus
{
    Draft,
    ReadyToAssign,
    Assigned,
    InProgress,
    AwaitingDelivery,
    Completed,
    Canceled
}

public static class TradeOrderStatusWorkflow
{
    public static IReadOnlyList<TradeOrderStatus> ActiveStatuses { get; } =
    [
        TradeOrderStatus.Draft,
        TradeOrderStatus.ReadyToAssign,
        TradeOrderStatus.Assigned,
        TradeOrderStatus.InProgress,
        TradeOrderStatus.AwaitingDelivery
    ];

    public static IReadOnlyList<TradeOrderStatus> ArchiveStatuses { get; } =
    [
        TradeOrderStatus.Completed,
        TradeOrderStatus.Canceled
    ];

    public static bool IsArchived(TradeOrderStatus status)
    {
        return ArchiveStatuses.Contains(status);
    }
}

public enum TradeOrderSourceKind
{
    ActiveCraftPlan,
    TradeRequestedOutputs,
    ImportedExternal
}

public sealed class TradeOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyProfileId { get; set; }
    public string Title { get; set; } = string.Empty;
    public TradeOrderStatus Status { get; set; } = TradeOrderStatus.ReadyToAssign;
    public Guid? AssignedCrafterId { get; set; }
    public DateTime CommissionedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Notes { get; set; }
    public TradeOrderSourceSnapshot SourceSnapshot { get; set; } = new();
    public IReadOnlyList<TradeOrderHistoryEvent> History { get; set; } = Array.Empty<TradeOrderHistoryEvent>();
    public string? PayrollDraftId { get; set; }
    public string? RemoteId { get; set; }
    public TradeSyncState SyncState { get; set; } = TradeSyncState.LocalOnly;
}

public sealed class TradeOrderSourceSnapshot
{
    public TradeOrderSourceKind SourceKind { get; set; } = TradeOrderSourceKind.ActiveCraftPlan;
    public string? SourcePlanId { get; set; }
    public string SourcePlanName { get; set; } = "Active craft plan";
    public string? DataCenter { get; set; }
    public string? World { get; set; }
    public long PlanSessionVersion { get; set; }
    public long MarketAnalysisVersion { get; set; }
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    public IReadOnlyList<TradeOrderRootItemSnapshot> RootItems { get; set; } = Array.Empty<TradeOrderRootItemSnapshot>();
    public IReadOnlyList<TradeOrderMaterialSnapshot> Materials { get; set; } = Array.Empty<TradeOrderMaterialSnapshot>();
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}

public sealed record TradeOrderRootItemSnapshot(
    int ItemId,
    string Name,
    int Quantity,
    bool MustBeHq,
    decimal EstimatedSaleValue);

public sealed record TradeOrderMaterialSnapshot(
    int ItemId,
    string Name,
    int Quantity,
    bool RequiresHq,
    decimal UnitCost,
    decimal TotalCost,
    string EvidenceSource = "",
    string UnitCostExplanation = "",
    DateTime? EvidenceTimestampUtc = null,
    IReadOnlyList<string>? Warnings = null);

public enum TradeOrderHistoryEventKind
{
    Created,
    Assigned,
    StatusChanged,
    ManualNote,
    Closed,
    Reopened,
    PayrollLinked
}

public sealed class TradeOrderHistoryEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyProfileId { get; set; }
    public Guid OrderId { get; set; }
    public TradeOrderHistoryEventKind Kind { get; set; }
    public string Note { get; set; } = string.Empty;
    public TradeOrderStatus? FromStatus { get; set; }
    public TradeOrderStatus? ToStatus { get; set; }
    public Guid? CrafterId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public static TradeOrderHistoryEvent CreateManualNote(
        Guid companyProfileId,
        Guid orderId,
        string note,
        DateTime createdAtUtc)
    {
        return new TradeOrderHistoryEvent
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = companyProfileId,
            OrderId = orderId,
            Kind = TradeOrderHistoryEventKind.ManualNote,
            Note = note,
            CreatedAtUtc = createdAtUtc
        };
    }
}
