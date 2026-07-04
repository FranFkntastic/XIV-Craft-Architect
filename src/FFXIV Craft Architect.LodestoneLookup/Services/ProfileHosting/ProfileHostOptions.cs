namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed class ProfileHostOptions
{
    public string DatabasePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "profile-host.db");
}
