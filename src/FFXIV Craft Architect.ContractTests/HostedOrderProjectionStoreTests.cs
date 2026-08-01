using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class HostedOrderProjectionStoreTests
{
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
