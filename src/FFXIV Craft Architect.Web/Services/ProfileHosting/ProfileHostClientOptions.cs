namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed record ProfileHostClientOptions(string DefaultHostUrl);

public enum ProfileHostConnectionFailure
{
    InvalidAddress,
    HostUnavailable,
    ProfileHostingDisabled,
    IncompatibleHost,
    AccessKeyRejected,
    PairingCodeRejected
}

public sealed class ProfileHostConnectionException : InvalidOperationException
{
    public ProfileHostConnectionException(
        ProfileHostConnectionFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public ProfileHostConnectionFailure Failure { get; }
}
