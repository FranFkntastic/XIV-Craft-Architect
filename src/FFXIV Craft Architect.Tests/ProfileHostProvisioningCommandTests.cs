using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Tests;

public sealed class ProfileHostProvisioningCommandTests
{
    [Fact]
    public void TryParse_CreateProfileCommand_ReturnsCommand()
    {
        var command = ProfileHostProvisioningCommand.TryParse([
            "profile-host",
            "create-profile",
            "Sapphire Avenue"
        ]);

        Assert.NotNull(command);
        Assert.Equal(ProfileHostProvisioningAction.CreateProfile, command.Action);
        Assert.Equal("Sapphire Avenue", command.DisplayName);
    }
}
