using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Tests;

[Trait(TestTraits.Surface, TestTraits.DeployLodestone)]
public sealed class SqliteProfileHostStoreTests
{
    [Fact]
    public async Task PutObjectAsync_WithMatchingRevision_IncrementsRevision()
    {
        using var temp = new TemporaryProfileHostDatabase();
        var store = temp.CreateStore();
        var profile = await store.CreateProfileAsync("Sapphire Avenue", CancellationToken.None);

        var first = await store.PutObjectAsync(profile.ProfileId, ProfileSyncCollections.TradeOrders, "order-1", "{\"id\":\"order-1\"}", 0, CancellationToken.None);
        var second = await store.PutObjectAsync(profile.ProfileId, ProfileSyncCollections.TradeOrders, "order-1", "{\"id\":\"order-1\",\"title\":\"Updated\"}", first.Object!.Revision, CancellationToken.None);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.Object.Revision + 1, second.Object!.Revision);
    }

    [Fact]
    public async Task PutObjectAsync_WithStaleRevision_ReturnsConflict()
    {
        using var temp = new TemporaryProfileHostDatabase();
        var store = temp.CreateStore();
        var profile = await store.CreateProfileAsync("Sapphire Avenue", CancellationToken.None);

        var first = await store.PutObjectAsync(profile.ProfileId, ProfileSyncCollections.Plans, "plan-1", "{\"id\":\"plan-1\"}", 0, CancellationToken.None);
        var conflict = await store.PutObjectAsync(profile.ProfileId, ProfileSyncCollections.Plans, "plan-1", "{\"id\":\"plan-1\",\"name\":\"stale\"}", 0, CancellationToken.None);

        Assert.True(first.Success);
        Assert.True(conflict.Conflict);
        Assert.NotNull(conflict.RemoteObject);
        Assert.Equal(first.Object!.Revision, conflict.RemoteObject!.Revision);
    }

    [Fact]
    public async Task DeleteObjectAsync_CreatesTombstone()
    {
        using var temp = new TemporaryProfileHostDatabase();
        var store = temp.CreateStore();
        var profile = await store.CreateProfileAsync("Sapphire Avenue", CancellationToken.None);

        var first = await store.PutObjectAsync(profile.ProfileId, ProfileSyncCollections.TradeOrders, "order-1", "{\"id\":\"order-1\"}", 0, CancellationToken.None);
        var deleted = await store.DeleteObjectAsync(profile.ProfileId, ProfileSyncCollections.TradeOrders, "order-1", first.Object!.Revision, CancellationToken.None);

        Assert.True(deleted.Success);
        Assert.True(deleted.Object!.Deleted);
        Assert.NotNull(deleted.Object.DeletedAtUtc);
    }

    private sealed class TemporaryProfileHostDatabase : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

        public SqliteProfileHostStore CreateStore()
        {
            return new SqliteProfileHostStore(new ProfileHostOptions { DatabasePath = _path });
        }

        public void Dispose()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }
}
