using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFXIV_Craft_Architect.Core.Models;

public enum TradeSyncState
{
    LocalOnly,
    Synced,
    PendingSync,
    Conflict
}

public enum TradeCompanyDiscordInstallationHealth
{
    Unknown,
    Ready,
    Unavailable,
    Misconfigured
}

public sealed class TradeCompanyDiscordInstallationBinding
{
    public string ApplicationId { get; set; } = string.Empty;
    public string GuildId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public TradeCompanyDiscordInstallationHealth Health { get; set; }
    public string? HealthMessage { get; set; }
    public DateTime? HealthCheckedAtUtc { get; set; }
}

public sealed class TradeCompanyProfile
{
    public const int CurrentSchemaVersion = 4;

    public Guid Id { get; set; } = Guid.NewGuid();
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CompanyLandingTheme? Landing { get; set; }
    public IReadOnlyList<TradeCompanyUpdate> Updates { get; set; } = [];
    public string? CommissionContact { get; set; }
    public TradeCompanyDiscordInstallationBinding? DiscordInstallation { get; set; }
    public string? RemoteId { get; set; }
    public TradeSyncState SyncState { get; set; } = TradeSyncState.LocalOnly;
    public TradePaymentPolicy PaymentPolicy { get; set; } = TradePaymentPolicy.Default;
    public TradeMaterialPricingPolicy MaterialPricingPolicy { get; set; } = TradeMaterialPricingPolicy.Default;
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
            SyncState = TradeSyncState.LocalOnly,
            PaymentPolicy = TradePaymentPolicy.Default,
            MaterialPricingPolicy = TradeMaterialPricingPolicy.Default
        };
    }
}

public sealed record TradeCompanyUpdate
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string AuthorDisplayName { get; init; } = string.Empty;
    public DateTime PublishedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? EditedAtUtc { get; init; }
    public bool IsPinned { get; init; }
}

public enum CompanyLandingAccent
{
    DeepBlue,
    Crimson,
    Gold,
    Emerald,
    Violet,
    Amber,
    Teal,
    Rose,
    Slate,
    Ivory
}

public enum CompanyLandingBannerStyle
{
    None,
    Gradient,
    Pattern
}

public enum CompanyLandingEmblem
{
    Star,
    Prism,
    Crest,
    Workshop,
    Moon,
    Compass
}

public sealed record CompanyLandingTheme
{
    [JsonConverter(typeof(CompanyLandingAccentJsonConverter))]
    public CompanyLandingAccent Accent { get; init; } = CompanyLandingAccent.DeepBlue;
    [JsonConverter(typeof(CompanyLandingBannerStyleJsonConverter))]
    public CompanyLandingBannerStyle BannerStyle { get; init; } = CompanyLandingBannerStyle.Gradient;
    [JsonConverter(typeof(CompanyLandingEmblemJsonConverter))]
    public CompanyLandingEmblem Emblem { get; init; } = CompanyLandingEmblem.Star;
    public string? Tagline { get; init; }
    public string? About { get; init; }
    public bool ShowOpenCommissionCount { get; init; }
}

public sealed class CompanyLandingAccentJsonConverter : CompanyLandingEnumJsonConverter<CompanyLandingAccent>;
public sealed class CompanyLandingBannerStyleJsonConverter : CompanyLandingEnumJsonConverter<CompanyLandingBannerStyle>;
public sealed class CompanyLandingEmblemJsonConverter : CompanyLandingEnumJsonConverter<CompanyLandingEmblem>;

public abstract class CompanyLandingEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric))
        {
            return (T)Enum.ToObject(typeof(T), numeric);
        }

        reader.Skip();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public sealed class TradeCompanyProfilePackage
{
    public const int CurrentFormatVersion = 1;
    public const string PackageKindValue = "ffxiv-craft-architect.trade-company-profile";

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string PackageKind { get; set; } = PackageKindValue;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
    public TradeCompanyProfile Profile { get; set; } = new();
    public IReadOnlyList<TradeCrafterProfile> Crafters { get; set; } = Array.Empty<TradeCrafterProfile>();
}

public sealed record TradeCompanyProfileImportResult(
    TradeCompanyProfile Profile,
    IReadOnlyList<TradeCrafterProfile> Crafters);

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
    public string? Alias { get; set; }
    public string? ContactHandle { get; set; }
    public string? DiscordHandle { get; set; }
    public string? SocialProfileUrl { get; set; }
    public string? WorldName { get; set; }
    public string? DataCenter { get; set; }
    public string? LodestoneCharacterId { get; set; }
    public string? LodestoneProfileUrl { get; set; }
    public DateTime? LodestoneLastSyncedAtUtc { get; set; }
    public string? LodestoneAvatarUrl { get; set; }
    public string? LodestonePortraitUrl { get; set; }
    public string? LodestoneFreeCompanyName { get; set; }
    public string? LodestoneRace { get; set; }
    public string? LodestoneClan { get; set; }
    public string? LodestoneGender { get; set; }
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
    Canceled,
    ResolutionRequired
}

public enum TradeOrderLifecycleAction
{
    None,
    DiscardDraft,
    CancelCommission
}

public static class TradeOrderStatusWorkflow
{
    public static IReadOnlyList<TradeOrderStatus> ActiveStatuses { get; } =
    [
        TradeOrderStatus.Draft,
        TradeOrderStatus.ReadyToAssign,
        TradeOrderStatus.Assigned,
        TradeOrderStatus.InProgress,
        TradeOrderStatus.AwaitingDelivery,
        TradeOrderStatus.ResolutionRequired
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

public enum TradeOrderCraftPlanLinkKind
{
    Unknown,
    OrderGenerated
}

public sealed class TradeOrder
{
    public const int CurrentAuthoringSchemaVersion = 1;

    public Guid Id { get; set; } = Guid.NewGuid();
    public int AuthoringSchemaVersion { get; set; }
    public Guid CompanyProfileId { get; set; }
    public string Title { get; set; } = string.Empty;
    public TradeOrderStatus Status { get; set; } = TradeOrderStatus.ReadyToAssign;
    public Guid? AssignedCrafterId { get; set; }
    public DateTime CommissionedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Notes { get; set; }
    public TradeOrderSourceSnapshot SourceSnapshot { get; set; } = new();
    public TradePaymentPolicy? PaymentPolicyOverride { get; set; }
    public CompanyCommissionPaymentSchedule PaymentSchedule { get; set; } =
        CompanyCommissionPaymentSchedule.Advance;
    public string? CustomPaymentTerms { get; set; }
    public IReadOnlyList<TradeOrderHistoryEvent> History { get; set; } = Array.Empty<TradeOrderHistoryEvent>();
    public string? PayrollDraftId { get; set; }
    public string? CraftPlanId { get; set; }
    public string? CraftPlanName { get; set; }
    public DateTime? CraftPlanSavedAtUtc { get; set; }
    public TradeOrderCraftPlanLinkKind CraftPlanLinkKind { get; set; } = TradeOrderCraftPlanLinkKind.Unknown;
    public TradeCommissionPublication? CommissionPublication { get; set; }
    public TradeCompanyCommission? CompanyCommission { get; set; }
    public string? RemoteId { get; set; }
    public TradeSyncState SyncState { get; set; } = TradeSyncState.LocalOnly;
}

public sealed class TradeOrderSourceSnapshot
{
    public TradeOrderSourceKind SourceKind { get; set; } = TradeOrderSourceKind.ActiveCraftPlan;
    public string? SourcePlanId { get; set; }
    public string SourcePlanName { get; set; } = "Active craft plan";
    public CommissionCostBasis? CostBasis { get; set; }
    public MarketFetchScope? MarketFetchScope { get; set; }
    public string? Region { get; set; }
    public string? DataCenter { get; set; }
    public string? World { get; set; }
    public IReadOnlyList<string> RequestedDataCenters { get; set; } = Array.Empty<string>();
    public long PlanSessionVersion { get; set; }
    public long MarketAnalysisVersion { get; set; }
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    public IReadOnlyList<TradeOrderRootItemSnapshot> RootItems { get; set; } = Array.Empty<TradeOrderRootItemSnapshot>();
    public IReadOnlyList<TradeOrderMaterialSnapshot> Materials { get; set; } = Array.Empty<TradeOrderMaterialSnapshot>();
    public IReadOnlyList<TradeOrderCraftLaborSnapshot> CraftLabor { get; set; } = Array.Empty<TradeOrderCraftLaborSnapshot>();
    public TradeMaterialQuote? MaterialQuote { get; set; }
    public string? MaterialQuoteFailureReason { get; set; }
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}

public sealed record TradeMaterialPricingPolicy
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public decimal MaximumConsolidationPremiumPercent { get; init; } = 5m;
    public int MaximumWorldStops { get; init; } = 8;
    public int MaximumDataCenterTransfers { get; init; } = 2;
    public bool AllowSplitPurchases { get; init; } = true;
    public decimal SafetyAllowancePercent { get; init; } = 10m;
    public decimal MinimumSafetyAllowanceGil { get; init; } = 10_000m;
    public decimal MaximumSafetyAllowanceGil { get; init; } = 250_000m;
    public int QuoteLifetimeMinutes { get; init; } = 30;
    public int MaximumEvidenceAgeMinutes { get; init; } = 120;

    public static TradeMaterialPricingPolicy Default { get; } = new();
}

public sealed record TradeMaterialQuoteLine(
    int ItemId,
    string Name,
    int RequiredQuantity,
    bool RequiresHq,
    decimal CashRequired,
    IReadOnlyList<string> Worlds,
    DateTime? OldestEvidenceAtUtc);

public sealed record TradeMaterialQuote
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid CompanyProfileId { get; init; }
    public string? SourcePlanId { get; init; }
    public long PlanSessionVersion { get; init; }
    public long MarketAnalysisVersion { get; init; }
    public string RouteSelectionKey { get; init; } = string.Empty;
    public required string PolicyFingerprint { get; init; }
    public required TradeMaterialPricingPolicy AppliedPolicy { get; init; }
    public required DateTime QuotedAtUtc { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }
    public DateTime? LockedAtUtc { get; init; }
    public required decimal RouteCashRequired { get; init; }
    public required decimal SafetyAllowance { get; init; }
    public required decimal MaterialReimbursement { get; init; }
    public required int WorldStops { get; init; }
    public required int DataCenterTransfers { get; init; }
    public IReadOnlyList<TradeMaterialQuoteLine> Lines { get; init; } = [];

    [JsonIgnore]
    public bool IsLocked => LockedAtUtc.HasValue;

    public bool IsExpired(DateTime nowUtc) => !IsLocked && nowUtc >= ExpiresAtUtc;
}

public sealed record TradeOrderRootItemSnapshot(
    int ItemId,
    string Name,
    int Quantity,
    bool MustBeHq,
    decimal EstimatedSaleValue);

public sealed record TradeRequestedOrderOutput(
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

public sealed record TradeOrderCraftLaborSnapshot(
    string NodeId,
    int ItemId,
    string Name,
    int RequestedQuantity,
    int CraftCount,
    string JobName = "",
    int RecipeLevel = 0,
    IReadOnlyList<string>? Warnings = null);

public enum TradeOrderHistoryEventKind
{
    Created,
    Assigned,
    StatusChanged,
    ManualNote,
    Closed,
    Reopened,
    PayrollLinked,
    CraftPlanLinked,
    PricingRefreshed,
    RequestUpdated,
    CommissionPublished,
    CommissionRevoked
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
