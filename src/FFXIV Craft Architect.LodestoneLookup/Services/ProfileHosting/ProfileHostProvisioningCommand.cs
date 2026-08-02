namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public enum ProfileHostProvisioningAction
{
    CreateProfile,
    EnsureProfile,
    ImportActiveCredentials,
    RotateKey,
    DisableProfile,
    ExportProfile
}

public sealed record ProfileHostProvisioningCommand(
    ProfileHostProvisioningAction Action,
    string? ProfileId,
    string? DisplayName,
    string? SourceDatabasePath = null)
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
            _ => null
        };
    }
}
