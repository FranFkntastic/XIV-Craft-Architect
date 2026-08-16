namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed class ProfileHostOptions
{
    public bool Enabled { get; set; }
    public string DatabasePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "profile-host.db");
    public TimeSpan ChangeStreamLease { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ChangeStreamHeartbeat { get; set; } = TimeSpan.FromSeconds(15);
    public bool DeepArchiveEnabled { get; set; } = true;
    public int DeepArchiveAfterDays { get; set; } = 180;
    public TimeSpan DeepArchiveSweepInterval { get; set; } = TimeSpan.FromHours(24);
}
