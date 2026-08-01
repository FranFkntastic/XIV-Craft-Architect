using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class HostedOrderRestoreStateTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string ProfileId = "8df327d9-2052-4d63-ae4b-51fcb87ec397";

    [Fact]
    public void ColdRestoreCannotClaimAuthoritativeEmpty()
    {
        var state = HostedOrderRestoreState.BeginProfile(
            ProfileId,
            hasTrustedProjection: false,
            lastAppliedRevision: 0,
            scopeChanged: false,
            Now);

        Assert.Equal(HostedOrderRestoreStage.ColdRestoring, state.Stage);
        Assert.False(state.CanShowAuthoritativeEmpty);
        Assert.False(state.ShowsCompleteProjection);
        Assert.False(state.CanMutate);
    }

    [Fact]
    public void SuccessfulRestoreMakesEvenZeroOrdersAuthoritative()
    {
        var state = HostedOrderRestoreState.BeginProfile(
                ProfileId,
                hasTrustedProjection: false,
                lastAppliedRevision: 0,
                scopeChanged: false,
                Now)
            .Apply(Status(ProfileSyncStage.Ready, revision: 12), Now.AddSeconds(2));

        Assert.Equal(HostedOrderRestoreStage.Ready, state.Stage);
        Assert.True(state.HasTrustedProjection);
        Assert.True(state.CanShowAuthoritativeEmpty);
        Assert.True(state.CanMutate);
    }

    [Fact]
    public void OrdinaryReconnectRetainsCompleteProjectionAndReportsRealProgress()
    {
        var state = HostedOrderRestoreState.BeginProfile(
            ProfileId,
            hasTrustedProjection: true,
            lastAppliedRevision: 8,
            scopeChanged: false,
            Now);
        state = state.Apply(
            Status(ProfileSyncStage.ApplyingChanges, revision: 9) with
            {
                AppliedObjectCount = 3,
                TargetRevision = 11
            },
            Now.AddSeconds(1));

        Assert.Equal(HostedOrderRestoreStage.Reconnecting, state.Stage);
        Assert.True(state.ShowsCompleteProjection);
        Assert.False(state.CanShowAuthoritativeEmpty);
        Assert.Equal(3, state.AppliedObjectCount);
        Assert.Equal(11, state.TargetRevision);
        Assert.Equal("Applying hosted changes", state.ProgressStage);
    }

    [Theory]
    [InlineData(ProfileSyncFailure.Authentication)]
    [InlineData(ProfileSyncFailure.Incompatible)]
    [InlineData(ProfileSyncFailure.Unverifiable)]
    public void UnverifiableFailuresWithholdVolatileProjection(ProfileSyncFailure failure)
    {
        var state = HostedOrderRestoreState.BeginProfile(
                ProfileId,
                hasTrustedProjection: true,
                lastAppliedRevision: 8,
                scopeChanged: false,
                Now)
            .Apply(
                Status(ProfileSyncStage.Failed, revision: 8) with { Failure = failure },
                Now.AddSeconds(1));

        Assert.Equal(HostedOrderRestoreStage.IdentityOnly, state.Stage);
        Assert.True(state.RequiresIdentityOnly);
        Assert.False(state.HasTrustedProjection);
        Assert.False(state.CanMutate);

        state = state.Apply(
            Status(ProfileSyncStage.ReadingLocalState, revision: 8),
            Now.AddSeconds(2));
        Assert.Equal(HostedOrderRestoreStage.IdentityOnly, state.Stage);
        Assert.True(state.RequiresIdentityOnly);
    }

    [Fact]
    public void OfflineReconnectKeepsLastTruthfulProjection()
    {
        var state = HostedOrderRestoreState.BeginProfile(
                ProfileId,
                hasTrustedProjection: true,
                lastAppliedRevision: 8,
                scopeChanged: false,
                Now)
            .Apply(
                Status(ProfileSyncStage.Failed, revision: 8) with
                {
                    Failure = ProfileSyncFailure.Offline
                },
                Now.AddSeconds(1));

        Assert.Equal(HostedOrderRestoreStage.Reconnecting, state.Stage);
        Assert.True(state.ShowsCompleteProjection);
        Assert.False(state.CanMutate);
    }

    [Fact]
    public void ScopeChangeRemainsExplicitUntilAuthorityIsReady()
    {
        var state = HostedOrderRestoreState.BeginProfile(
                ProfileId,
                hasTrustedProjection: false,
                lastAppliedRevision: 2,
                scopeChanged: true,
                Now)
            .Apply(
                Status(ProfileSyncStage.ApplyingChanges, revision: 3),
                Now.AddSeconds(1));

        Assert.Equal(HostedOrderRestoreStage.ScopeChanging, state.Stage);
        Assert.False(state.CanShowAuthoritativeEmpty);

        state = state.Apply(Status(ProfileSyncStage.Ready, revision: 3), Now.AddSeconds(2));
        Assert.Equal(HostedOrderRestoreStage.Ready, state.Stage);
        Assert.True(state.CanShowAuthoritativeEmpty);
    }

    private static ProfileSyncStatus Status(ProfileSyncStage stage, long revision) =>
        new(
            IsConnected: true,
            HostReachable: stage == ProfileSyncStage.Ready,
            LastSyncRevision: revision,
            PendingCount: 0,
            ConflictCount: 0,
            LastSyncedAtUtc: Now,
            Message: null)
        {
            ProfileId = ProfileId,
            Stage = stage
        };
}
