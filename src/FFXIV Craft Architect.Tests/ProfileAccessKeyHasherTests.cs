using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Tests;

[Trait(TestTraits.Surface, TestTraits.DeployLodestone)]
public sealed class ProfileAccessKeyHasherTests
{
    [Fact]
    public void CreateAccessKey_ReturnsTokenOnlyOnceAndHashVerifies()
    {
        var hasher = new ProfileAccessKeyHasher();

        var created = hasher.CreateAccessKey();

        Assert.StartsWith("cap_", created.PlaintextKey, StringComparison.Ordinal);
        Assert.NotEqual(created.PlaintextKey, created.StoredHash);
        Assert.True(hasher.Verify(created.PlaintextKey, created.StoredHash));
        Assert.False(hasher.Verify(created.PlaintextKey + "x", created.StoredHash));
    }
}
