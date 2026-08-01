using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class HostedOrderProjectionStoreTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NewerCanonicalOrderWinsAndTombstoneCannotRollBack()
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

    [Fact]
    public void SameOrderCannotMoveBetweenCompanyProfiles()
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

    [Fact]
    public void TombstoneWinsSameRevisionAndProfileResetClearsHistory()
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

    [Fact]
    public void SameObjectRevisionOwnerUpgradeIsAcceptedAndNotified()
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

    [Fact]
    public void SameProfileReconnectRetainsReadyProjection()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Trusted order");
        store.BeginProfileRestore(profileId, false, 0, Now);
        Assert.True(store.TryPublishRemoteOrder(order, objectRevision: 4));
        Assert.True(store.TryPublishRestoreState(store.RestoreState.Apply(
            ReadyStatus(profileId, revision: 4),
            Now.AddSeconds(1))));

        store.BeginProfileRestore(profileId, false, 4, Now.AddSeconds(2));

        Assert.Same(order, store.Get(order.Id)?.Order);
        Assert.Equal(HostedOrderRestoreStage.Reconnecting, store.RestoreState.Stage);
        Assert.True(store.RestoreState.ShowsCompleteProjection);
    }

    [Fact]
    public void ScopeChangeClearsProjectionAndDoesNotCarryRevisionFloor()
    {
        var store = new HostedOrderProjectionStore();
        var firstProfile = Guid.NewGuid().ToString("D");
        var secondProfile = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Old scope");
        store.BeginProfileRestore(firstProfile, false, 20, Now);
        Assert.True(store.TryPublishRemoteOrder(order, objectRevision: 20));

        store.BeginProfileRestore(secondProfile, false, 2, Now.AddSeconds(1));

        Assert.Null(store.Get(order.Id));
        Assert.Equal(HostedOrderRestoreStage.ScopeChanging, store.RestoreState.Stage);
        Assert.Equal(2, store.RestoreState.LastAppliedRevision);
        Assert.False(store.RestoreState.CanShowAuthoritativeEmpty);
    }

    [Fact]
    public void RestoreStateCannotRollBackAppliedRevision()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        store.BeginProfileRestore(profileId, false, 7, Now);

        Assert.False(store.TryPublishRestoreState(store.RestoreState with
        {
            LastAppliedRevision = 6,
            Stage = HostedOrderRestoreStage.Ready
        }));
        Assert.Equal(7, store.RestoreState.LastAppliedRevision);
        Assert.False(store.RestoreState.IsAuthoritative);
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
}
