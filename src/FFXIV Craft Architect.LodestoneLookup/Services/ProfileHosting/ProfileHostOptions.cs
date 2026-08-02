namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed class ProfileHostOptions
{
    public bool Enabled { get; set; }
    public string DatabasePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "profile-host.db");
    public TimeSpan ChangeStreamLease { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ChangeStreamHeartbeat { get; set; } = TimeSpan.FromSeconds(15);
}
