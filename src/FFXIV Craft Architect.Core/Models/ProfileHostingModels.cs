using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FFXIV_Craft_Architect.Core.Models;

public static class ProfileSyncCollections
{
    public const string Settings = "settings";
    public const string Plans = "plans";
    public const string TradeCompanyProfiles = "tradeCompanyProfiles";
    public const string TradeCrafters = "tradeCrafters";
    public const string TradeOrders = "tradeOrders";
    public const string TradePayrollDrafts = "tradePayrollDrafts";

    public static readonly IReadOnlyList<string> All =
    [
        Settings,
        Plans,
        TradeCompanyProfiles,
        TradeCrafters,
        TradeOrders,
        TradePayrollDrafts
    ];

    public static readonly IReadOnlyList<string> OrderAuthorityScope =
    [
        TradeCompanyProfiles,
        TradeCrafters,
        TradeOrders,
        TradePayrollDrafts
    ];

    public static readonly IReadOnlyList<string> BackgroundScope =
    [
        Settings,
        Plans
    ];
}

public static class ProfileSyncSettingsKeys
{
    public const string HostUrl = "profileHost.hostUrl";
    public const string AccessKey = "profileHost.accessKey";
    public const string RememberAccessKey = "profileHost.rememberAccessKey";
    public const string ConnectedProfileId = "profileHost.connectedProfileId";
    public const string LastSyncRevision = "profileHost.lastSyncRevision";

    public static readonly IReadOnlySet<string> ConnectionSettingKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HostUrl,
            AccessKey,
            RememberAccessKey,
            ConnectedProfileId,
            LastSyncRevision
        };
}

public sealed class ProfileSyncObjectEnvelope
{
    public string Collection { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string? SummaryJson { get; set; }
    public long Revision { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool Deleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }

    public bool IsSummary => SummaryJson != null;
}

public sealed class ProfileSyncPlanSnapshot
{
    // LinkedOrderId is an additive optional field. Keep the v1 envelope so
    // cached clients can continue reading ordinary plan updates during rollout.
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = "Saved Plan";
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public DateTime SavedAt { get; set; }
    public string DataCenter { get; set; } = "Aether";
    public List<ProfileSyncPlanProjectItem> ProjectItems { get; set; } = [];
    public string? PlanJson { get; set; }
    public string? PlanStateJson { get; set; }
    public int? ProcurementTravelTolerance { get; set; }
    public string? MarketAnalysisScopeSnapshotJson { get; set; }
    public RecommendationMode SavedRecommendationMode { get; set; } =
        RecommendationMode.MinimizeTotalCost;
    public MarketAcquisitionLens SavedMarketAnalysisLens { get; set; } =
        MarketAcquisitionLens.MinimumUpfrontCost;
    public string? SourcePlanId { get; set; }
    public string? SourcePlanName { get; set; }
    public Guid? LinkedOrderId { get; set; }
}

public sealed class ProfileSyncPlanProjectItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    public int Quantity { get; set; }
    public bool MustBeHq { get; set; }
}

public static class ProfileSyncPlanPayloadCodec
{
    private static readonly JsonSerializerOptions JsonOptions =
        ProfileSyncJson.CreateOptions();

    public static string CompactIfPlan(
        string collection,
        string objectId,
        string payloadJson)
    {
        return string.Equals(
                collection,
                ProfileSyncCollections.Plans,
                StringComparison.OrdinalIgnoreCase)
            ? Serialize(Deserialize(payloadJson, objectId))
            : payloadJson;
    }

    public static ProfileSyncPlanSnapshot Deserialize(
        string payloadJson,
        string expectedObjectId)
    {
        var snapshot = JsonSerializer.Deserialize<ProfileSyncPlanSnapshot>(
                payloadJson,
                JsonOptions)
            ?? throw new InvalidOperationException(
                $"Hosted plan payload '{expectedObjectId}' could not be deserialized.");
        if (string.IsNullOrWhiteSpace(snapshot.Id) ||
            !string.Equals(snapshot.Id, expectedObjectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Hosted plan payload '{expectedObjectId}' does not match its object identity.");
        }

        if (snapshot.SchemaVersion > ProfileSyncPlanSnapshot.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Hosted plan '{expectedObjectId}' uses unsupported compact schema version {snapshot.SchemaVersion}.");
        }

        snapshot.SchemaVersion = ProfileSyncPlanSnapshot.CurrentSchemaVersion;
        snapshot.ProjectItems ??= [];
        return snapshot;
    }

    public static string Serialize(ProfileSyncPlanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public static bool HasSameRevisionContent(
        ProfileSyncPlanSnapshot left,
        ProfileSyncPlanSnapshot right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return string.Equals(
            NormalizeUnsealed(left),
            NormalizeUnsealed(right),
            StringComparison.Ordinal);
    }

    private static string NormalizeUnsealed(ProfileSyncPlanSnapshot snapshot)
    {
        var node = JsonSerializer.SerializeToNode(snapshot, JsonOptions)?.AsObject()
            ?? throw new InvalidOperationException("Plan snapshot normalization failed.");
        node.Remove("schemaVersion");
        node.Remove("linkedOrderId");
        return node.ToJsonString(JsonOptions);
    }
}

public sealed class ProfileSyncPutRequest
{
    public string PayloadJson { get; set; } = "{}";
    public long ExpectedRevision { get; set; }
}

public sealed class ProfileSyncPutResponse
{
    public bool Success { get; set; }
    public bool Conflict { get; set; }
    public long ServerRevision { get; set; }
    public ProfileSyncObjectEnvelope? Object { get; set; }
    public ProfileSyncObjectEnvelope? RemoteObject { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class ProfileSyncChangesResponse
{
    public long ServerRevision { get; set; }
    public bool HasMore { get; set; }
    public IReadOnlyList<ProfileSyncObjectEnvelope> Objects { get; set; } = Array.Empty<ProfileSyncObjectEnvelope>();
}

public sealed class ProfileHostProfileResponse
{
    public string ProfileId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long MetadataRevision { get; set; }
    public long ServerRevision { get; set; }
}

public static class ProfileHostDisplayNamePolicy
{
    public const int MaximumLength = 120;

    public static bool TryNormalize(string? candidate, out string displayName)
    {
        displayName = candidate?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(displayName) &&
               displayName.Length <= MaximumLength &&
               !displayName.Any(char.IsControl) &&
               displayName.EnumerateRunes().All(IsPermittedProfileNameRune) &&
               displayName.EnumerateRunes().Any(IsRenderedBaseRune);
    }

    private static bool IsPermittedProfileNameRune(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is not (
            UnicodeCategory.Control or
            UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator);

    private static bool IsRenderedBaseRune(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is not (
            UnicodeCategory.Control or
            UnicodeCategory.Format or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark or
            UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator or
            UnicodeCategory.SpaceSeparator);
}

public sealed class ProfileHostDisplayNameUpdateRequest
{
    public long ExpectedMetadataRevision { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class ProfileHostDisplayNameUpdateResponse
{
    public bool Success { get; set; }
    public bool Conflict { get; set; }
    public ProfileHostProfileResponse? Profile { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class ProfileHostAccessKeyMetadata
{
    public string Id { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public bool IsCurrent { get; set; }
}

public sealed class ProfileHostBootstrapPayload
{
    public IReadOnlyList<ProfileSyncObjectEnvelope> Objects { get; set; } = Array.Empty<ProfileSyncObjectEnvelope>();
}

public sealed class ProfileHostHealthResponse
{
    public string Service { get; set; } = "FFXIV Craft Architect Private Backend";
    public string Status { get; set; } = "ready";
    public bool ProfileHostEnabled { get; set; }
    public int ProtocolVersion { get; set; } = 1;
}

public sealed class ProfileHostPairingCodeResponse
{
    public string PairingCode { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class ProfileHostPairingRedeemRequest
{
    public string PairingCode { get; set; } = string.Empty;
}

public sealed class ProfileHostPairingRedeemResponse
{
    public string AccessKey { get; set; } = string.Empty;
    public ProfileHostProfileResponse Profile { get; set; } = new();
}
