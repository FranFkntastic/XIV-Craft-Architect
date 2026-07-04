using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Tests;

public sealed class ProfileSyncLocalStateServiceTests
{
    [Fact]
    public void IsSyncedSetting_ReturnsFalseForConnectionKeys()
    {
        Assert.False(ProfileSyncLocalStateService.IsSyncedSetting(ProfileSyncSettingsKeys.HostUrl));
        Assert.False(ProfileSyncLocalStateService.IsSyncedSetting(ProfileSyncSettingsKeys.AccessKey));
        Assert.True(ProfileSyncLocalStateService.IsSyncedSetting("market.default_datacenter"));
    }
}
