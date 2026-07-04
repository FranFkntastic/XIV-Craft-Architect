namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public enum ProfileHostProvisioningAction
{
    CreateProfile,
    RotateKey,
    DisableProfile,
    ExportProfile
}

public sealed record ProfileHostProvisioningCommand(
    ProfileHostProvisioningAction Action,
    string? ProfileId,
    string? DisplayName)
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
