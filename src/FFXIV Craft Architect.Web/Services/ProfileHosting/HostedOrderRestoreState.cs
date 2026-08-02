namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public enum HostedOrderRestoreStage
{
    Inactive,
    ColdRestoring,
    Reconnecting,
    ScopeChanging,
    Ready,
    IdentityOnly,
    Failed
}

public enum HostedOrderRestoreFailure
{
    None,
    Offline,
    Authentication,
    Incompatible,
    Unverifiable
}

public sealed record HostedOrderRestoreState
{
    public string? ProfileId { get; init; }
    public HostedOrderRestoreStage Stage { get; init; } = HostedOrderRestoreStage.Inactive;
    public HostedOrderRestoreFailure Failure { get; init; }
    public bool HasTrustedProjection { get; init; }
    public long LastAppliedRevision { get; init; }
    public long? TargetRevision { get; init; }
    public int AppliedObjectCount { get; init; }
    public string? ProgressStage { get; init; }
    public string? Message { get; init; }
    public DateTime? LastTrustedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

    public bool IsAuthoritative => Stage == HostedOrderRestoreStage.Ready;

    public bool CanShowAuthoritativeEmpty => IsAuthoritative;

    public bool ShowsCompleteProjection =>
        HasTrustedProjection &&
        Stage is HostedOrderRestoreStage.Ready or HostedOrderRestoreStage.Reconnecting;

    public bool RequiresIdentityOnly =>
        Stage is HostedOrderRestoreStage.IdentityOnly or HostedOrderRestoreStage.ScopeChanging;

    public bool CanMutate => IsAuthoritative && Failure == HostedOrderRestoreFailure.None;

    public static HostedOrderRestoreState Inactive(DateTime now) => new()
    {
        UpdatedAtUtc = now
    };

    public static HostedOrderRestoreState BeginProfile(
        string profileId,
        bool hasTrustedProjection,
        long lastAppliedRevision,
        bool scopeChanged,
        DateTime now) => new()
        {
            ProfileId = profileId,
            Stage = scopeChanged
                ? HostedOrderRestoreStage.ScopeChanging
                : hasTrustedProjection
                    ? HostedOrderRestoreStage.Reconnecting
                    : HostedOrderRestoreStage.ColdRestoring,
            HasTrustedProjection = !scopeChanged && hasTrustedProjection,
            LastAppliedRevision = lastAppliedRevision,
            ProgressStage = scopeChanged ? "Changing profile" : "Preparing order restoration",
            UpdatedAtUtc = now
        };

    public HostedOrderRestoreState Apply(ProfileSyncStatus status, DateTime now)
    {
        if (status.ProfileId == null ||
            !string.Equals(ProfileId, status.ProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return this;
        }

        var reportedFailure = MapFailure(status.Failure);
        var holdIdentityOnly = Stage == HostedOrderRestoreStage.IdentityOnly &&
                               status.Stage != ProfileSyncStage.Ready &&
                               reportedFailure == HostedOrderRestoreFailure.None;
        var holdScopeChange = Stage == HostedOrderRestoreStage.ScopeChanging &&
                              status.Stage != ProfileSyncStage.Ready &&
                              reportedFailure is HostedOrderRestoreFailure.None or
                                  HostedOrderRestoreFailure.Offline;
        var failure = holdIdentityOnly ? Failure : reportedFailure;
        var trusted = (HasTrustedProjection || status.Stage == ProfileSyncStage.Ready) &&
                      failure is not HostedOrderRestoreFailure.Authentication and
                          not HostedOrderRestoreFailure.Incompatible and
                          not HostedOrderRestoreFailure.Unverifiable;
        var stage = status.Stage switch
        {
            ProfileSyncStage.Ready => HostedOrderRestoreStage.Ready,
            _ when holdIdentityOnly => HostedOrderRestoreStage.IdentityOnly,
            _ when holdScopeChange => HostedOrderRestoreStage.ScopeChanging,
            ProfileSyncStage.Failed when failure is HostedOrderRestoreFailure.Authentication or
                HostedOrderRestoreFailure.Incompatible or
                HostedOrderRestoreFailure.Unverifiable => HostedOrderRestoreStage.IdentityOnly,
            ProfileSyncStage.Failed when trusted => HostedOrderRestoreStage.Reconnecting,
            ProfileSyncStage.Failed => HostedOrderRestoreStage.Failed,
            ProfileSyncStage.Inactive => HostedOrderRestoreStage.Inactive,
            _ when trusted => HostedOrderRestoreStage.Reconnecting,
            _ => HostedOrderRestoreStage.ColdRestoring
        };

        return this with
        {
            Stage = stage,
            Failure = failure,
            HasTrustedProjection = trusted,
            LastAppliedRevision = Math.Max(LastAppliedRevision, status.LastSyncRevision),
            TargetRevision = status.TargetRevision,
            AppliedObjectCount = status.AppliedObjectCount,
            ProgressStage = FormatStage(status.Stage),
            Message = status.Message,
            LastTrustedAtUtc = status.LastSyncedAtUtc ?? LastTrustedAtUtc,
            UpdatedAtUtc = now
        };
    }

    private static HostedOrderRestoreFailure MapFailure(ProfileSyncFailure failure) =>
        failure switch
        {
            ProfileSyncFailure.None => HostedOrderRestoreFailure.None,
            ProfileSyncFailure.Offline => HostedOrderRestoreFailure.Offline,
            ProfileSyncFailure.Authentication => HostedOrderRestoreFailure.Authentication,
            ProfileSyncFailure.Incompatible => HostedOrderRestoreFailure.Incompatible,
            _ => HostedOrderRestoreFailure.Unverifiable
        };

    private static string FormatStage(ProfileSyncStage stage) =>
        stage switch
        {
            ProfileSyncStage.ReadingLocalState => "Reading saved profile state",
            ProfileSyncStage.DownloadingChanges => "Checking hosted revisions",
            ProfileSyncStage.ApplyingChanges => "Applying hosted changes",
            ProfileSyncStage.PublishingLocalChanges => "Publishing local changes",
            ProfileSyncStage.Ready => "Orders restored",
            ProfileSyncStage.Failed => "Order restoration needs attention",
            _ => "Preparing order restoration"
        };
}
