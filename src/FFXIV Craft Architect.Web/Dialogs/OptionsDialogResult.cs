namespace FFXIV_Craft_Architect.Web.Dialogs;

public sealed record OptionsDialogResult(
    bool OpenCompanyMigration = false,
    string? HostUrl = null,
    string? AccessKey = null);
