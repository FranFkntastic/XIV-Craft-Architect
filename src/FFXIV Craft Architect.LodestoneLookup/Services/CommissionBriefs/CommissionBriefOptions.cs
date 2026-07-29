namespace FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;

public sealed class CommissionBriefOptions
{
    public bool Enabled { get; set; } = true;
    public string DatabasePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "commission-briefs.db");
    public IReadOnlySet<string> AllowedHosts { get; set; } = new HashSet<string>(
        ["dev.xivcraftarchitect.com", "localhost", "127.0.0.1"],
        StringComparer.OrdinalIgnoreCase);
}
