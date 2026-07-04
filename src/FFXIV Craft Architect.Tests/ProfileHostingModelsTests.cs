using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Tests;

public sealed class ProfileHostingModelsTests
{
    [Fact]
    public void CollectionNames_MatchIndexedDbStores()
    {
        Assert.Equal("settings", ProfileSyncCollections.Settings);
        Assert.Equal("plans", ProfileSyncCollections.Plans);
        Assert.Equal("tradeCompanyProfiles", ProfileSyncCollections.TradeCompanyProfiles);
        Assert.Equal("tradeCrafters", ProfileSyncCollections.TradeCrafters);
        Assert.Equal("tradeOrders", ProfileSyncCollections.TradeOrders);
        Assert.Equal("tradePayrollDrafts", ProfileSyncCollections.TradePayrollDrafts);
    }

    [Fact]
    public void SyncObjectEnvelope_SerializesStableJson()
    {
        var envelope = new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.TradeOrders,
            ObjectId = "order-1",
            PayloadJson = "{\"id\":\"order-1\"}",
            Revision = 7,
            UpdatedAtUtc = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc),
            Deleted = false
        };

        var json = JsonSerializer.Serialize(envelope);
        var roundTrip = JsonSerializer.Deserialize<ProfileSyncObjectEnvelope>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal(ProfileSyncCollections.TradeOrders, roundTrip.Collection);
        Assert.Equal("order-1", roundTrip.ObjectId);
        Assert.Equal(7, roundTrip.Revision);
        Assert.False(roundTrip.Deleted);
    }

    [Fact]
    public void ConnectionSettingKeys_AreExcludedFromSyncedSettings()
    {
        Assert.Contains(ProfileSyncSettingsKeys.HostUrl, ProfileSyncSettingsKeys.ConnectionSettingKeys);
        Assert.Contains(ProfileSyncSettingsKeys.AccessKey, ProfileSyncSettingsKeys.ConnectionSettingKeys);
        Assert.DoesNotContain("market.default_datacenter", ProfileSyncSettingsKeys.ConnectionSettingKeys);
    }
}
