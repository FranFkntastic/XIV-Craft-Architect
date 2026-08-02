using System.Reflection;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class ProfileSyncDeletionProjectionTests
{
    [Fact]
    public void ConfirmedTradeOrderDeletionPublishesRevisionedTombstoneImmediately()
    {
        var store = new HostedOrderProjectionStore();
        var orderId = Guid.NewGuid();
        var companyProfileId = Guid.NewGuid();
        var order = new TradeOrder
        {
            Id = orderId,
            CompanyProfileId = companyProfileId,
            Title = "Retiring order"
        };
        Assert.True(store.TryPublishRemoteOrder(order, objectRevision: 4));
        HostedOrderProjectionSnapshot? notification = null;
        store.Changed += snapshot => notification = snapshot;

        var published = PublishConfirmedOrderTombstone(
            store,
            ProfileSyncCollections.TradeOrders,
            orderId.ToString("D"),
            revision: 5);

        Assert.True(published);
        Assert.NotNull(notification);
        Assert.Equal(orderId, notification.OrderId);
        Assert.Equal(companyProfileId, notification.CompanyProfileId);
        Assert.Equal(5, notification.ObjectRevision);
        Assert.True(notification.Deleted);
        Assert.Null(notification.Order);
        Assert.Null(notification.OwnerProjection);
    }

    [Theory]
    [InlineData(ProfileSyncCollections.TradePayrollDrafts, "9d96a76c-216c-440f-bf15-e97fa21a08b1")]
    [InlineData(ProfileSyncCollections.TradeOrders, "not-an-order-id")]
    public void NonOrderDeletionCannotChangeHostedOrderProjection(
        string collection,
        string objectId)
    {
        var store = new HostedOrderProjectionStore();
        var notifications = 0;
        store.Changed += _ => notifications++;

        var published = PublishConfirmedOrderTombstone(
            store,
            collection,
            objectId,
            revision: 5);

        Assert.False(published);
        Assert.Equal(0, notifications);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void CommittedCollaborationOrderReplacesVisibleProjectionImmediately()
    {
        var store = new HostedOrderProjectionStore();
        var orderId = Guid.NewGuid();
        var companyProfileId = Guid.NewGuid();
        var previous = new TradeOrder
        {
            Id = orderId,
            CompanyProfileId = companyProfileId,
            Title = "Awaiting assignment"
        };
        var committed = new TradeOrder
        {
            Id = orderId,
            CompanyProfileId = companyProfileId,
            Title = "Assigned by committed response"
        };
        Assert.True(store.TryPublishRemoteOrder(previous, objectRevision: 8));

        var published = PublishCommittedCollaborationOrder(
            store,
            committed,
            revision: 9);

        Assert.True(published);
        Assert.Same(committed, store.Get(orderId)?.Order);
        Assert.Equal(9, store.Get(orderId)?.ObjectRevision);
    }

    [Fact]
    public void TombstonedHostedOrderCannotReappearAsDeviceOnlyResidue()
    {
        var store = new HostedOrderProjectionStore();
        var order = new TradeOrder
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = Guid.NewGuid(),
            Title = "Deleted hosted residue"
        };
        Assert.True(store.TryPublishRemoteOrder(order, objectRevision: 6));
        Assert.True(store.TryPublishTombstone(order.Id, objectRevision: 7));

        var deviceOnly = TradeOrderWorkspaceCompositionPolicy.GetDeviceOnlyOrders(
            [order],
            store.GetAll(order.CompanyProfileId));

        Assert.Empty(deviceOnly);
    }

    private static bool PublishConfirmedOrderTombstone(
        HostedOrderProjectionStore store,
        string collection,
        string objectId,
        long revision)
    {
        var method = typeof(ProfileSyncService).GetMethod(
            "PublishConfirmedOrderTombstone",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(ProfileSyncService).FullName,
                "PublishConfirmedOrderTombstone");
        return (bool)method.Invoke(null, [store, collection, objectId, revision])!;
    }

    private static bool PublishCommittedCollaborationOrder(
        HostedOrderProjectionStore store,
        TradeOrder order,
        long revision)
    {
        var method = typeof(TradeCompanyCollaborationService).GetMethod(
            "PublishCommittedOrder",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(TradeCompanyCollaborationService).FullName,
                "PublishCommittedOrder");
        return (bool)method.Invoke(null, [store, order, revision])!;
    }
}
