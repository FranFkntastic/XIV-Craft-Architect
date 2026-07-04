using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Tests;

public sealed class ProfileSyncAdapterTests
{
    [Fact]
    public void SettingsAdapter_SkipsConnectionSettings()
    {
        var settings = new Dictionary<string, string>
        {
            [ProfileSyncSettingsKeys.HostUrl] = "\"https://host.test\"",
            ["market.default_datacenter"] = "\"Aether\""
        };

        var objects = SettingsProfileSyncAdapter.ToSyncObjects(settings, DateTime.UtcNow);

        Assert.DoesNotContain(objects, item => item.ObjectId == ProfileSyncSettingsKeys.HostUrl);
        Assert.Contains(objects, item => item.ObjectId == "market.default_datacenter");
    }

    [Fact]
    public void PlansAdapter_UsesPlansCollection()
    {
        var plan = new StoredPlan
        {
            Id = "plan-1",
            Name = "Plan",
            MarketIntelligenceJson = "{\"analysis\":true}"
        };

        var envelope = PlansProfileSyncAdapter.ToSyncObject(plan, DateTime.UtcNow);

        Assert.Equal(ProfileSyncCollections.Plans, envelope.Collection);
        Assert.Equal("plan-1", envelope.ObjectId);
        Assert.Contains("MarketIntelligenceJson", envelope.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }
}
