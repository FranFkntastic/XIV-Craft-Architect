using System.Security.Cryptography;
using System.Text;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.AspNetCore.WebUtilities;

namespace FFXIV_Craft_Architect.ContractTests;

internal static class DiscordIdentityContractScenarios
{
    private const string DiscordUser = "111111111111111111";
    private const string OtherDiscordUser = "222222222222222222";
    private const string ParticipantCredential =
        "participant_credential_contract_aaaaaaaaaaaaaaaa";
    private const string OtherParticipantCredential =
        "participant_credential_contract_bbbbbbbbbbbbbbbb";
    public static async Task RunAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"craft-architect-discord-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await RunAsync(root);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
    private static async Task RunAsync(string root)
    {
        var now = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-02T12:00:00Z"));
        var options = CreateOptions(root);
        options.Validate();
        var linkStore = new SqliteDiscordIdentityStore(options);
        var profiles = new SqliteProfileHostStore(new ProfileHostOptions
        {
            Enabled = true,
            DatabasePath = Path.Combine(root, "profiles.db")
        });
        var hasher = new ProfileAccessKeyHasher();
        var ownerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var owner = (await profiles.EnsureProfileAsync(
            ownerId.ToString("D"),
            "Owner",
            "cap_discord-owner-contract-key",
            hasher,
            CancellationToken.None)).Profile;
        var other = (await profiles.EnsureProfileAsync(
            otherId.ToString("D"),
            "Other",
            "cap_discord-other-contract-key",
            hasher,
            CancellationToken.None)).Profile;
        var oauth = new StubDiscordOAuthClient();
        var linking = new DiscordIdentityLinkService(
            options,
            linkStore,
            profiles,
            oauth,
            now);
        var start = await linking.StartAsync(owner);
        var authorize = new Uri(start.AuthorizationUrl);
        var query = QueryHelpers.ParseQuery(authorize.Query);
        Assert.Equal(options.AuthorizationEndpoint, authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal(options.ClientId, query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(options.CallbackUri, query["redirect_uri"]);
        Assert.Equal("identify", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["state"]));
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));

        oauth.Identity = new DiscordOAuthIdentity(DiscordUser, "owner-context-only");
        var linked = await linking.CompleteAsync("oauth-code", query["state"]);
        Assert.Equal(DiscordLinkCompletionStatus.Linked, linked.Status);
        Assert.Equal(
            query["code_challenge"],
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(oauth.LastVerifier!))));
        Assert.Equal(
            DiscordLinkCompletionStatus.ReplayedState,
            (await linking.CompleteAsync("oauth-code", query["state"])).Status);
        var conflictingDiscord = await linking.StartAsync(other);
        oauth.Identity = new DiscordOAuthIdentity(DiscordUser, "conflicting-display");
        Assert.Equal(
            DiscordLinkCompletionStatus.Conflict,
            (await linking.CompleteAsync(
                "oauth-code",
                StateFrom(conflictingDiscord))).Status);
        var conflictingProfile = await linking.StartAsync(owner);
        oauth.Identity = new DiscordOAuthIdentity(OtherDiscordUser, "other-display");
        Assert.Equal(
            DiscordLinkCompletionStatus.Conflict,
            (await linking.CompleteAsync(
                "oauth-code",
                StateFrom(conflictingProfile))).Status);
        Assert.Equal(
            DiscordUser,
            (await linkStore.LoadByProfileAsync(ownerId))!.DiscordUserId);

        var stale = await linking.StartAsync(other);
        now.Advance(options.StateLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(
            DiscordLinkCompletionStatus.ExpiredState,
            (await linking.CompleteAsync("oauth-code", StateFrom(stale))).Status);
        var auditKinds = (await linkStore.LoadAuditAsync(ownerId))
            .Select(item => item.EventKind)
            .ToArray();
        Assert.Contains("linked", auditKinds);
        Assert.Contains("profile_link_conflict", auditKinds);
        Assert.Contains("oauth_consumed", auditKinds);
        var companyId = new CompanyId(Guid.NewGuid());
        var commissionId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        const string publicId = "discord-contract-public-id";
        var authority = new StubCanonicalAuthority
        {
            ProfileId = ownerId,
            CompanyId = companyId,
            CommissionId = commissionId,
            PublicBriefId = publicId,
            GrantId = grantId,
            CapabilityRevision = 7,
            IsCompanyOperator = true,
            IsActiveParticipant = true
        };
        var capabilities = new SqliteCompanyCommissionCapabilityStore(
            new CommissionBriefOptions
            {
                DatabasePath = Path.Combine(root, "capabilities.db")
            });
        var resolver = new DiscordInteractionAccessResolver(
            options,
            linkStore,
            authority,
            capabilities,
            now);
        var target = new DiscordInteractionTarget(
            "333333333333333333",
            DiscordUser,
            companyId,
            commissionId,
            publicId);

        var allowed = await resolver.ResolveAsync(target);
        Assert.True(allowed.Authorized);
        Assert.True(allowed.IsCompanyOperator);
        var ownerAction = Assert.Single(allowed.Actions);
        Assert.Equal(DiscordInteractionActionKind.OpenOwnerOrder, ownerAction.Kind);
        Assert.Equal(DiscordInteractionActionDelivery.EphemeralOnly, ownerAction.Delivery);
        Assert.Equal("https", ownerAction.Uri.Scheme);
        Assert.Equal(
            DiscordInteractionAccessStatus.Forbidden,
            (await resolver.ResolveAsync(target with
            {
                DiscordUserId = OtherDiscordUser
            })).Status);
        authority.Available = false;
        Assert.Equal(
            DiscordInteractionAccessStatus.Forbidden,
            (await resolver.ResolveAsync(target)).Status);
        authority.Available = true;

        var staleEntry = await resolver.IssueParticipantEntryAsync(target);
        var staleToken = BootstrapFrom(staleEntry);
        Assert.DoesNotContain(ParticipantCredential, staleEntry.Actions[1].Uri.AbsoluteUri);
        authority.IsActiveParticipant = false;
        Assert.Null(await resolver.ExchangeAsync(
            new DiscordParticipantExchangeRequest(
                staleToken,
                ParticipantCredential)));

        authority.IsActiveParticipant = true;
        authority.CapabilityRevision++;
        var currentTarget = target with { InteractionId = "444444444444444444" };
        var currentEntry = await resolver.IssueParticipantEntryAsync(currentTarget);
        var currentAction = Assert.Single(
            currentEntry.Actions,
            item => item.Kind == DiscordInteractionActionKind.OpenParticipantCommission);
        Assert.Equal(DiscordInteractionActionDelivery.EphemeralOnly, currentAction.Delivery);
        Assert.StartsWith("bootstrap=", currentAction.Uri.Fragment.TrimStart('#'));
        var currentToken = BootstrapFrom(currentEntry);
        Assert.Equal(
            publicId,
            (await resolver.ExchangeAsync(
                new DiscordParticipantExchangeRequest(
                    currentToken,
                    ParticipantCredential)))!.PublicBriefId);
        Assert.NotNull(await capabilities.ResolveAsync(
            publicId,
            CompanyCommissionCapabilityKind.Participant,
            ParticipantCredential));
        Assert.NotNull(await resolver.ExchangeAsync(
            new DiscordParticipantExchangeRequest(
                currentToken,
                ParticipantCredential)));
        Assert.Null(await resolver.ExchangeAsync(
            new DiscordParticipantExchangeRequest(
                currentToken,
                OtherParticipantCredential)));
        Assert.Null(await capabilities.ResolveAsync(
            publicId,
            CompanyCommissionCapabilityKind.Participant,
            OtherParticipantCredential));

        Assert.True(await linking.UnlinkAsync(owner));
        Assert.Equal(
            DiscordInteractionAccessStatus.Forbidden,
            (await resolver.ResolveAsync(target)).Status);
        Assert.Contains(
            await linkStore.LoadAuditAsync(ownerId),
            item => item.EventKind == "unlinked");
    }

    private static DiscordIdentityOptions CreateOptions(string root) => new()
    {
        Enabled = true,
        ClientId = "123456789012345678",
        ClientSecret = "client_secret_contract_aaaaaaaaaaaaaaaa",
        BootstrapSecret = "bootstrap_secret_contract_bbbbbbbbbbbbbbbb",
        CallbackUri = "https://identity.test/api/identity/v1/discord/callback",
        ApplicationBaseUri = "https://app.test/",
        DatabasePath = Path.Combine(root, "discord-identity.db")
    };

    private static string StateFrom(DiscordLinkStartResponse response) =>
        QueryHelpers.ParseQuery(new Uri(response.AuthorizationUrl).Query)["state"]!;

    private static string BootstrapFrom(DiscordInteractionAccessResolution resolution) =>
        Assert.Single(
            resolution.Actions,
            item => item.Kind == DiscordInteractionActionKind.OpenParticipantCommission)
        .Uri.Fragment["#bootstrap=".Length..];

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class StubDiscordOAuthClient : IDiscordOAuthClient
    {
        public DiscordOAuthIdentity? Identity { get; set; }
        public string? LastVerifier { get; private set; }

        public Task<DiscordOAuthIdentity?> ResolveIdentityAsync(
            string code,
            string pkceVerifier,
            CancellationToken cancellationToken = default)
        {
            LastVerifier = pkceVerifier;
            return Task.FromResult(Identity);
        }
    }

    private sealed class StubCanonicalAuthority : IDiscordCanonicalInteractionAuthority
    {
        public bool Available { get; set; } = true;
        public Guid ProfileId { get; init; }
        public CompanyId CompanyId { get; init; }
        public Guid CommissionId { get; init; }
        public string PublicBriefId { get; init; } = string.Empty;
        public Guid GrantId { get; init; }
        public long CapabilityRevision { get; set; }
        public bool IsCompanyOperator { get; init; }
        public bool IsActiveParticipant { get; set; }

        public Task<DiscordParticipantAuthority?> ResolveAsync(
            DiscordIdentityLink link,
            DiscordInteractionTarget target,
            CancellationToken cancellationToken = default)
        {
            DiscordParticipantAuthority? result = Available &&
                link.ProfileId == ProfileId &&
                link.DiscordUserId == target.DiscordUserId &&
                target.CompanyId == CompanyId &&
                target.CommissionId == CommissionId &&
                target.PublicBriefId == PublicBriefId
                    ? new DiscordParticipantAuthority(
                        ProfileId,
                        link.DiscordUserId,
                        CompanyId,
                        CommissionId,
                        PublicBriefId,
                        GrantId,
                        CapabilityRevision,
                        new Uri($"https://brief.test/commission.html?id={PublicBriefId}"),
                        IsCompanyOperator,
                        IsActiveParticipant)
                    : null;
            return Task.FromResult(result);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
