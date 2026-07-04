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
    public long Revision { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool Deleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
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
    public ProfileSyncObjectEnvelope? Object { get; set; }
    public ProfileSyncObjectEnvelope? RemoteObject { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class ProfileSyncChangesResponse
{
    public long ServerRevision { get; set; }
    public IReadOnlyList<ProfileSyncObjectEnvelope> Objects { get; set; } = Array.Empty<ProfileSyncObjectEnvelope>();
}

public sealed class ProfileHostProfileResponse
{
    public string ProfileId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long ServerRevision { get; set; }
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
}
