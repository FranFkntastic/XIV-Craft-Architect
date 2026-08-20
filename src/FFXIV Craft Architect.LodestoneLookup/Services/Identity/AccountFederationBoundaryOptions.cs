namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;

public sealed class AccountFederationBoundaryOptions
{
    internal const string ReviewedActivationPermit = "account-federation-v1-reviewed";

    public string ServiceEnvironment { get; init; } = "local";
    public string Mode { get; init; } = "disabled";
    public string CanonicalAccountAuthority { get; init; } = string.Empty;
    public string TokenIssuer { get; init; } = string.Empty;
    public string TokenAudience { get; init; } = string.Empty;
    public string ActivationPermit { get; init; } = string.Empty;

    public void Validate()
    {
        if (ServiceEnvironment is not ("local" or "canonical" or "staging"))
        {
            throw new InvalidOperationException(
                "AuthorityBoundary:Environment must be local, canonical, or staging.");
        }

        if (Mode == "disabled")
        {
            return;
        }

        if (Mode is not ("issuer" or "consumer"))
        {
            throw new InvalidOperationException(
                "AuthorityBoundary:AccountFederationMode must be disabled, issuer, or consumer.");
        }

        if (!string.Equals(ActivationPermit, ReviewedActivationPermit, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Account federation activation requires the reviewed v1 activation permit.");
        }

        if (Mode == "issuer" && ServiceEnvironment != "canonical")
        {
            throw new InvalidOperationException(
                "Only the canonical service environment may issue global account assertions.");
        }

        if (Mode == "consumer" && ServiceEnvironment == "canonical")
        {
            throw new InvalidOperationException(
                "The canonical service environment cannot consume its own account assertions.");
        }

        RequireSecureAbsoluteUri(TokenIssuer, nameof(TokenIssuer));
        if (Mode == "consumer")
        {
            RequireSecureAbsoluteUri(
                CanonicalAccountAuthority,
                nameof(CanonicalAccountAuthority));
        }

        if (string.IsNullOrWhiteSpace(TokenAudience) || TokenAudience.Length > 128)
        {
            throw new InvalidOperationException(
                "Account federation TokenAudience must be a bounded, non-empty service audience.");
        }

        throw new InvalidOperationException(
            "Account federation runtime is not implemented; activation remains locked.");
    }

    private void RequireSecureAbsoluteUri(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps &&
            !(ServiceEnvironment == "local" && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"Account federation {name} must be an absolute HTTPS URI without credentials, query, or fragment data (HTTP is allowed only for local loopback development).");
        }
    }
}
