namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public enum ProfileHostProvisioningAction
{
    CreateProfile,
    ProvisionProfile,
    EnsureProfile,
    ImportActiveCredentials,
    RotateKey,
    DisableProfile,
    ExportProfile,
    ConsolidateCrafters,
    RetireCompanies
}

public sealed record ProfileHostProvisioningCommand(
    ProfileHostProvisioningAction Action,
    string? ProfileId,
    string? DisplayName,
    string? SourceDatabasePath = null,
    bool DryRun = false,
    IReadOnlyList<string>? CompanyIds = null)
{
    public static ProfileHostProvisioningCommand? TryParse(string[] args)
    {
        if (args.Length < 2 || args[0] != "profile-host")
        {
            return null;
        }

        return args[1] switch
        {
            "create-profile" when args.Length >= 3 =>
                new ProfileHostProvisioningCommand(
                    ProfileHostProvisioningAction.CreateProfile,
                    null,
                    string.Join(' ', args.Skip(2))),
            "ensure-profile" when args.Length >= 4 =>
                new ProfileHostProvisioningCommand(
                    ProfileHostProvisioningAction.EnsureProfile,
                    args[2],
                    string.Join(' ', args.Skip(3))),
            "ensure-profile" => throw new InvalidOperationException(
                "Usage: profile-host ensure-profile <profile-id> <display-name>"),
            "provision-profile" when args.Length >= 4 =>
                new ProfileHostProvisioningCommand(
                    ProfileHostProvisioningAction.ProvisionProfile,
                    args[2],
                    string.Join(' ', args.Skip(3))),
            "provision-profile" => throw new InvalidOperationException(
                "Usage: profile-host provision-profile <profile-id> <display-name>"),
            "import-active-credentials" when args.Length >= 5 =>
                new ProfileHostProvisioningCommand(
                    ProfileHostProvisioningAction.ImportActiveCredentials,
                    args[3],
                    string.Join(' ', args.Skip(4)),
                    args[2]),
            "import-active-credentials" => throw new InvalidOperationException(
                "Usage: profile-host import-active-credentials <source-db> <profile-id> <expected-display-name>"),
            "rotate-key" when args.Length == 3 =>
                new ProfileHostProvisioningCommand(ProfileHostProvisioningAction.RotateKey, args[2], null),
            "disable-profile" when args.Length == 3 =>
                new ProfileHostProvisioningCommand(ProfileHostProvisioningAction.DisableProfile, args[2], null),
            "export-profile" when args.Length == 3 =>
                new ProfileHostProvisioningCommand(ProfileHostProvisioningAction.ExportProfile, args[2], null),
            "consolidate-crafters" when args.Length == 2 ||
                                        args.Length == 3 && args[2] == "--dry-run" =>
                new ProfileHostProvisioningCommand(
                    ProfileHostProvisioningAction.ConsolidateCrafters,
                    null,
                    null,
                    DryRun: args.Length == 3),
            "retire-company" when args.Skip(2).Any(arg => arg != "--dry-run") =>
                new ProfileHostProvisioningCommand(
                    ProfileHostProvisioningAction.RetireCompanies,
                    null,
                    null,
                    DryRun: args.Skip(2).Contains("--dry-run"),
                    CompanyIds: args.Skip(2).Where(arg => arg != "--dry-run").ToArray()),
            "retire-company" => throw new InvalidOperationException(
                "Usage: profile-host retire-company <company-id> [<company-id>...] [--dry-run]"),
            "consolidate-crafters" => throw new InvalidOperationException(
                "Usage: profile-host consolidate-crafters [--dry-run]"),
            _ => null
        };
    }
}
