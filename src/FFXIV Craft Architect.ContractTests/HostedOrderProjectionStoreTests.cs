using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class HostedOrderProjectionStoreTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ProjectionStoreScenario.CanonicalRevisionAndTombstone)]
    [InlineData(ProjectionStoreScenario.CompanyProfileIsImmutable)]
    [InlineData(ProjectionStoreScenario.ProfileResetClearsRevisionHistory)]
    [InlineData(ProjectionStoreScenario.OwnerUpgradeAtSameRevision)]
    [InlineData(ProjectionStoreScenario.SameProfileReconnect)]
    [InlineData(ProjectionStoreScenario.ScopeChange)]
    [InlineData(ProjectionStoreScenario.RestoreRevisionCannotRollBack)]
    [InlineData(ProjectionStoreScenario.CompanySnapshotComposition)]
    public void ProjectionStorePreservesCanonicalIdentityAndRestoreTruth(
        ProjectionStoreScenario scenario)
    {
        switch (scenario)
        {
            case ProjectionStoreScenario.CanonicalRevisionAndTombstone:
                NewerCanonicalOrderWinsAndTombstoneCannotRollBack();
                break;
            case ProjectionStoreScenario.CompanyProfileIsImmutable:
                SameOrderCannotMoveBetweenCompanyProfiles();
                break;
            case ProjectionStoreScenario.ProfileResetClearsRevisionHistory:
                TombstoneWinsSameRevisionAndProfileResetClearsHistory();
                break;
            case ProjectionStoreScenario.OwnerUpgradeAtSameRevision:
                SameObjectRevisionOwnerUpgradeIsAcceptedAndNotified();
                break;
            case ProjectionStoreScenario.SameProfileReconnect:
                SameProfileReconnectRetainsReadyProjection();
                break;
            case ProjectionStoreScenario.ScopeChange:
                ScopeChangeClearsProjectionAndDoesNotCarryRevisionFloor();
                break;
            case ProjectionStoreScenario.RestoreRevisionCannotRollBack:
                RestoreStateCannotRollBackAppliedRevision();
                break;
            case ProjectionStoreScenario.CompanySnapshotComposition:
                SharedSnapshotCanBeComposedWithoutAPageCache();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    [Fact]
    public void SameProfileConnectionReplacementInvalidatesCapturedAuthorityAndRevisionFloor()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Old authority");
        var resets = 0;
        store.Reset += () => resets++;
        store.BeginProfileRestore(
            profileId,
            false,
            20,
            Now,
            $"https://first.example/|{profileId}");
        Assert.True(store.TryPublishRemoteOrder(order, 20));
        var oldAuthority = store.CaptureAuthorityScope();

        store.BeginProfileRestore(
            profileId,
            false,
            2,
            Now.AddSeconds(1),
            $"https://replacement.example/|{profileId}");

        Assert.Null(store.Get(order.Id));
        Assert.Equal(1, resets);
        Assert.Equal(2, store.RestoreState.LastAppliedRevision);
        Assert.Equal(HostedOrderRestoreStage.ScopeChanging, store.RestoreState.Stage);
        Assert.Equal(
            HostedOrderCommittedProjectionResult.ScopeChanged,
            store.TryAdoptCommittedOrder(oldAuthority, order, 21));

        var replacement = CreateOrder(order.Id, order.CompanyProfileId, "New authority");
        Assert.True(store.TryPublishRemoteOrder(replacement, 3));
        store.BeginProfileRestore(
            profileId,
            false,
            1,
            Now.AddSeconds(2),
            $"https://replacement.example/|{profileId}");

        Assert.Same(replacement, store.Get(order.Id)?.Order);
        Assert.Equal(3, store.RestoreState.LastAppliedRevision);
        Assert.Equal(1, resets);
    }

    [Fact]
    public async Task PersistenceReconcilesSameRevisionOwnerUpgrade()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Canonical");
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            Now,
            $"https://profiles.example/|{profileId}");
        Assert.True(store.TryPublishRemoteOrder(order, 4));
        var authority = store.CaptureAuthorityScope();
        var firstWriteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new List<HostedOrderProjectionSnapshot>();

        var persistence = store.AdoptAndPersistCommittedOrderAsync(
            authority,
            order,
            4,
            async candidate =>
            {
                persisted.Add(candidate);
                if (persisted.Count == 1)
                {
                    firstWriteEntered.SetResult();
                    await releaseFirstWrite.Task;
                }
            });
        await firstWriteEntered.Task;
        var owner = new CompanyCommissionOwnerProjection
        {
            Order = order,
            ObjectRevision = new CompanyRecordRevision(4),
            CompanyRevision = new CompanyRecordRevision(9)
        };
        Assert.True(store.TryPublishOwner(owner));
        releaseFirstWrite.SetResult();

        Assert.Equal(HostedOrderCommittedProjectionResult.AlreadyCurrent, await persistence);
        Assert.Equal(2, persisted.Count);
        Assert.Null(persisted[0].OwnerProjection);
        Assert.Same(owner, persisted[1].OwnerProjection);
        Assert.Equal(4, store.RestoreState.LastAppliedRevision);
    }

    [Fact]
    public async Task PersistenceReconcilesNewerTombstoneAfterBlockedLiveWrite()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Live revision");
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            Now,
            $"https://profiles.example/|{profileId}");
        var authority = store.CaptureAuthorityScope();
        var firstWriteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new List<HostedOrderProjectionSnapshot>();

        var persistence = store.AdoptAndPersistCommittedOrderAsync(
            authority,
            order,
            5,
            async candidate =>
            {
                persisted.Add(candidate);
                if (persisted.Count == 1)
                {
                    firstWriteEntered.SetResult();
                    await releaseFirstWrite.Task;
                }
            });
        await firstWriteEntered.Task;
        Assert.True(store.TryPublishTombstone(order.Id, 6, order.CompanyProfileId));
        releaseFirstWrite.SetResult();

        Assert.Equal(HostedOrderCommittedProjectionResult.Adopted, await persistence);
        Assert.Equal([false, true], persisted.Select(candidate => candidate.Deleted));
        Assert.True(store.Get(order.Id)?.Deleted);
        Assert.Equal(6, store.RestoreState.LastAppliedRevision);
    }

    private static void NewerCanonicalOrderWinsAndTombstoneCannotRollBack()
    {
        var store = new HostedOrderProjectionStore();
        var orderId = Guid.NewGuid();
        var companyProfileId = Guid.NewGuid();
        var revisions = new List<long>();
        store.Changed += projection => revisions.Add(projection.ObjectRevision);

        Assert.True(store.TryPublishRemoteOrder(
            CreateOrder(orderId, companyProfileId, "Revision two"),
            objectRevision: 2));
        Assert.False(store.TryPublishRemoteOrder(
            CreateOrder(orderId, companyProfileId, "Stale revision one"),
            objectRevision: 1));
        Assert.True(store.TryPublishTombstone(orderId, objectRevision: 3));
        Assert.False(store.TryPublishRemoteOrder(
            CreateOrder(orderId, companyProfileId, "Stale after delete"),
            objectRevision: 2));

        var current = Assert.IsType<HostedOrderProjectionSnapshot>(store.Get(orderId));
        Assert.True(current.Deleted);
        Assert.Equal(3, current.ObjectRevision);
        Assert.Equal([2L, 3L], revisions);
    }

    private static void SameOrderCannotMoveBetweenCompanyProfiles()
    {
        var store = new HostedOrderProjectionStore();
        var orderId = Guid.NewGuid();
        Assert.True(store.TryPublishRemoteOrder(
            CreateOrder(orderId, Guid.NewGuid(), "Original company"),
            objectRevision: 1));

        Assert.Throws<InvalidOperationException>(() =>
            store.TryPublishRemoteOrder(
                CreateOrder(orderId, Guid.NewGuid(), "Different company"),
                objectRevision: 2));
    }

    private static void TombstoneWinsSameRevisionAndProfileResetClearsHistory()
    {
        var store = new HostedOrderProjectionStore();
        var orderId = Guid.NewGuid();
        var companyProfileId = Guid.NewGuid();
        store.ResetForProfile("profile-one");

        Assert.True(store.TryPublishRemoteOrder(
            CreateOrder(orderId, companyProfileId, "Live"),
            objectRevision: 3));
        Assert.True(store.TryPublishTombstone(orderId, objectRevision: 3));
        Assert.False(store.TryPublishRemoteOrder(
            CreateOrder(orderId, companyProfileId, "Resurrected"),
            objectRevision: 3));

        store.ResetForProfile("profile-two");
        Assert.Null(store.Get(orderId));
        Assert.True(store.TryPublishRemoteOrder(
            CreateOrder(orderId, Guid.NewGuid(), "New profile"),
            objectRevision: 1));
    }

    private static void SameObjectRevisionOwnerUpgradeIsAcceptedAndNotified()
    {
        var store = new HostedOrderProjectionStore();
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Canonical");
        var notifications = 0;
        store.Changed += _ => notifications++;

        Assert.True(store.TryPublishRemoteOrder(order, objectRevision: 4));
        Assert.True(store.TryPublishOwner(new CompanyCommissionOwnerProjection
        {
            Order = order,
            ObjectRevision = new CompanyRecordRevision(4),
            CompanyRevision = new CompanyRecordRevision(9)
        }));

        Assert.NotNull(store.GetOwnerProjection(order.Id));
        Assert.Equal(2, notifications);
    }

    private static void SameProfileReconnectRetainsReadyProjection()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        var connectionScopeId = $"https://profiles.example/|{profileId}";
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Trusted order");
        store.BeginProfileRestore(profileId, false, 0, Now, connectionScopeId);
        Assert.True(store.TryPublishRemoteOrder(order, objectRevision: 4));
        Assert.True(store.TryPublishRestoreState(store.RestoreState.Apply(
            ReadyStatus(profileId, revision: 4),
            Now.AddSeconds(1))));

        store.BeginProfileRestore(profileId, false, 4, Now.AddSeconds(2), connectionScopeId);

        Assert.Same(order, store.Get(order.Id)?.Order);
        Assert.Equal(HostedOrderRestoreStage.Reconnecting, store.RestoreState.Stage);
        Assert.True(store.RestoreState.ShowsCompleteProjection);
    }

    private static void ScopeChangeClearsProjectionAndDoesNotCarryRevisionFloor()
    {
        var store = new HostedOrderProjectionStore();
        var firstProfile = Guid.NewGuid().ToString("D");
        var secondProfile = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Old scope");
        store.BeginProfileRestore(
            firstProfile,
            false,
            20,
            Now,
            $"https://profiles.example/|{firstProfile}");
        Assert.True(store.TryPublishRemoteOrder(order, objectRevision: 20));

        store.BeginProfileRestore(
            secondProfile,
            false,
            2,
            Now.AddSeconds(1),
            $"https://profiles.example/|{secondProfile}");

        Assert.Null(store.Get(order.Id));
        Assert.Equal(HostedOrderRestoreStage.ScopeChanging, store.RestoreState.Stage);
        Assert.Equal(2, store.RestoreState.LastAppliedRevision);
        Assert.False(store.RestoreState.CanShowAuthoritativeEmpty);
    }

    private static void RestoreStateCannotRollBackAppliedRevision()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        store.BeginProfileRestore(
            profileId,
            false,
            7,
            Now,
            $"https://profiles.example/|{profileId}");

        Assert.False(store.TryPublishRestoreState(store.RestoreState with
        {
            LastAppliedRevision = 6,
            Stage = HostedOrderRestoreStage.Ready
        }));
        Assert.Equal(7, store.RestoreState.LastAppliedRevision);
        Assert.False(store.RestoreState.IsAuthoritative);
    }

    private static void SharedSnapshotCanBeComposedWithoutAPageCache()
    {
        var store = new HostedOrderProjectionStore();
        var companyProfileId = Guid.NewGuid();
        var included = CreateOrder(Guid.NewGuid(), companyProfileId, "Included");
        var other = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Other company");
        Assert.True(store.TryPublishRemoteOrder(included, 2));
        Assert.True(store.TryPublishRemoteOrder(other, 3));

        var snapshot = Assert.Single(store.GetAll(companyProfileId));

        Assert.Same(included, snapshot.Order);
        Assert.Equal(2, store.GetAll().Count);
    }

    private static ProfileSyncStatus ReadyStatus(string profileId, long revision) =>
        new(
            IsConnected: true,
            HostReachable: true,
            LastSyncRevision: revision,
            PendingCount: 0,
            ConflictCount: 0,
            LastSyncedAtUtc: Now,
            Message: "Synced")
        {
            ProfileId = profileId,
            Stage = ProfileSyncStage.Ready
        };

    private static TradeOrder CreateOrder(
        Guid orderId,
        Guid companyProfileId,
        string title) =>
        new()
        {
            Id = orderId,
            CompanyProfileId = companyProfileId,
            Title = title
        };

    public enum ProjectionStoreScenario
    {
        CanonicalRevisionAndTombstone,
        CompanyProfileIsImmutable,
        ProfileResetClearsRevisionHistory,
        OwnerUpgradeAtSameRevision,
        SameProfileReconnect,
        ScopeChange,
        RestoreRevisionCannotRollBack,
        CompanySnapshotComposition
    }
}
