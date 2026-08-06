namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed class ProfileHostOptions
{
    public bool Enabled { get; set; }
    public string DatabasePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "profile-host.db");
    public TimeSpan ChangeStreamLease { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ChangeStreamHeartbeat { get; set; } = TimeSpan.FromSeconds(15);
    public bool ArchiveRetentionEnabled { get; set; } = true;
    public int ArchiveRetentionDays { get; set; } = 180;
    public string ArchiveBackupDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "archive-backups");
    public TimeSpan RetentionSweepInterval { get; set; } = TimeSpan.FromHours(24);
}
