using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Tests;

public sealed class ProfileSyncServiceTests
{
    [Fact]
    public void ProfileSyncStatus_DefaultsToLocalOnly()
    {
        var status = ProfileSyncStatus.LocalOnly();

        Assert.False(status.IsConnected);
        Assert.Equal(0, status.PendingCount);
        Assert.Equal(0, status.ConflictCount);
    }
}
