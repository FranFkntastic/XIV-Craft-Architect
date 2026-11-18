using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class DiscordAccountContractTests
{
    private const string DiscordUser = "111111111111111111";

    [Fact]
    public async Task SignInCallbackRejectsTamperedStoredReturnPath()
    {
        await using var fixture = await DiscordAccountFixture.CreateAsync();
        fixture.OAuth.Identity = new DiscordOAuthIdentity(DiscordUser, "Discord Crafter");
        using var client = fixture.CreateClient();
        var state = await StartSignInAsync(client, fixture.OAuth);
        await fixture.TamperReturnPathAsync("//evil.test/stolen");

        using var callback = await client.GetAsync(
            $"/identity/v1/signin/discord/callback?code=code&state={Uri.EscapeDataString(state)}");

        Assert.Equal("/", callback.Headers.Location!.AbsolutePath);
        Assert.Equal("app.test", callback.Headers.Location.Host);
    }

    [Fact]
    public async Task SignInProvisionsThenReusesHostedProfileAndIssuesOrdinaryKeys()
    {
        await using var fixture = await DiscordAccountFixture.CreateAsync();
        fixture.OAuth.Identity = new DiscordOAuthIdentity(
            DiscordUser,
            $"  Discord   Crafter {new string('x', 80)}  ");
        using var client = fixture.CreateClient();

        var firstKey = await CompleteSignInAsync(client, fixture.OAuth);
        var firstProfile = await GetProfileAsync(client, firstKey);
        Assert.Equal(64, firstProfile.DisplayName.Length);
        Assert.Equal(1, await fixture.CountRowsAsync("hosted_profiles"));
        Assert.Equal(fixture.SignInCallbackUri, fixture.OAuth.LastCallbackUri);
        Assert.Equal(
            fixture.OAuth.LastChallenge,
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(fixture.OAuth.LastVerifier!))));

        var secondKey = await CompleteSignInAsync(client, fixture.OAuth);
        var secondProfile = await GetProfileAsync(client, secondKey);
        Assert.Equal(firstProfile.ProfileId, secondProfile.ProfileId);
        Assert.NotEqual(firstKey, secondKey);
        Assert.Equal(1, await fixture.CountRowsAsync("hosted_profiles"));
        Assert.Equal(2, await fixture.CountRowsAsync("profile_access_keys"));

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "/identity/v1/discord/");
        statusRequest.Headers.Add("X-Profile-Key", firstKey);
        using var statusResponse = await client.SendAsync(statusRequest);
        statusResponse.EnsureSuccessStatusCode();
        var identityStatus = await statusResponse.Content.ReadFromJsonAsync<DiscordAccountIdentityStatus>();
        Assert.True(identityStatus!.Linked);
        Assert.Equal("Discord Crafter " + new string('x', 80), identityStatus.DisplayName);
    }

    [Fact]
    public async Task SignInRejectsUnknownExpiredAndReplayedStateWithoutExtraWrites()
    {
        await using var fixture = await DiscordAccountFixture.CreateAsync();
        fixture.OAuth.Identity = new DiscordOAuthIdentity(DiscordUser, "Discord Crafter");
        using var client = fixture.CreateClient();

        Assert.Equal(
            "invalid-state",
            await SignInErrorAsync(client, "unknown-state-value-aaaaaaaaaaaaaaaa", "code"));
        var expiredState = await StartSignInAsync(client, fixture.OAuth);
        fixture.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal("expired-state", await SignInErrorAsync(client, expiredState, "code"));
        Assert.Equal(0, await fixture.CountRowsAsync("hosted_profiles"));
        Assert.Equal(0, fixture.OAuth.Calls);

        var validState = await StartSignInAsync(client, fixture.OAuth);
        using var success = await client.GetAsync(
            $"/identity/v1/signin/discord/callback?code=code&state={Uri.EscapeDataString(validState)}");
        Assert.Equal(HttpStatusCode.Redirect, success.StatusCode);
        var profileCount = await fixture.CountRowsAsync("hosted_profiles");
        var keyCount = await fixture.CountRowsAsync("profile_access_keys");
        Assert.Equal("replayed-state", await SignInErrorAsync(client, validState, "code"));
        Assert.Equal(profileCount, await fixture.CountRowsAsync("hosted_profiles"));
        Assert.Equal(keyCount, await fixture.CountRowsAsync("profile_access_keys"));
        Assert.Equal(1, fixture.OAuth.Calls);
    }

    private static async Task<string> CompleteSignInAsync(
        HttpClient client,
        StubDiscordOAuthClient oauth)
    {
        var state = await StartSignInAsync(client, oauth);
        using var callback = await client.GetAsync(
            $"/identity/v1/signin/discord/callback?code=code&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var values = QueryHelpers.ParseQuery(callback.Headers.Location!.Fragment.TrimStart('#'));
        var key = values["signin"].ToString();
        Assert.StartsWith("cap_", key, StringComparison.Ordinal);
        Assert.DoesNotContain(key, callback.Headers.Location.PathAndQuery, StringComparison.Ordinal);
        return key;
    }

    private static async Task<string> StartSignInAsync(
        HttpClient client,
        StubDiscordOAuthClient oauth)
    {
        var start = await (await client.PostAsync("/identity/v1/signin/discord/start", null))
            .Content.ReadFromJsonAsync<DiscordLinkStartResponse>();
        var query = QueryHelpers.ParseQuery(new Uri(start!.AuthorizationUrl).Query);
        oauth.LastChallenge = query["code_challenge"];
        return query["state"]!;
    }

    private static async Task<string> SignInErrorAsync(
        HttpClient client,
        string state,
        string code)
    {
        using var response = await client.GetAsync(
            $"/identity/v1/signin/discord/callback?code={code}&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return QueryHelpers.ParseQuery(response.Headers.Location!.Fragment.TrimStart('#'))[
            "signin-error"]!;
    }

    private static async Task<FFXIV_Craft_Architect.Core.Models.ProfileHostProfileResponse> GetProfileAsync(
        HttpClient client,
        string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/profile-host/profile");
        request.Headers.Add("X-Profile-Key", key);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<
            FFXIV_Craft_Architect.Core.Models.ProfileHostProfileResponse>())!;
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class DiscordAccountFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly WebApplicationFactory<Program> application;

        private DiscordAccountFixture(
            string root,
            MutableTimeProvider time,
            StubDiscordOAuthClient oauth,
            WebApplicationFactory<Program> application)
        {
            this.root = root;
            Time = time;
            OAuth = oauth;
            this.application = application;
            Profiles = application.Services.GetRequiredService<SqliteProfileHostStore>();
            Links = application.Services.GetRequiredService<SqliteDiscordIdentityStore>();
        }

        public string SignInCallbackUri =>
            "https://localhost/identity/v1/signin/discord/callback";
        public MutableTimeProvider Time { get; }
        public StubDiscordOAuthClient OAuth { get; }
        public SqliteProfileHostStore Profiles { get; }
        public SqliteDiscordIdentityStore Links { get; }

        public static Task<DiscordAccountFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"craft-accounts-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-06T12:00:00Z"));
            var oauth = new StubDiscordOAuthClient();
            var application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ProfileHost:Enabled"] = "true",
                        ["ProfileHost:DatabasePath"] = Path.Combine(root, "profiles.db"),
                        ["DiscordIdentity:Enabled"] = "true",
                        ["DiscordIdentity:ClientId"] = "123456789012345678",
                        ["DiscordIdentity:ClientSecret"] = "client_secret_contract_aaaaaaaaaaaaaaaa",
                        ["DiscordIdentity:BootstrapSecret"] = "bootstrap_secret_contract_bbbbbbbbbbbbbbbb",
                        ["DiscordIdentity:SignInCallbackUri"] = "https://localhost/identity/v1/signin/discord/callback",
                        ["DiscordIdentity:ApplicationBaseUri"] = "https://app.test/",
                        ["DiscordIdentity:DatabasePath"] = Path.Combine(root, "identity.db"),
                        ["DiscordIdentity:AuthorizationEndpoint"] = "https://discord.test/oauth2/authorize"
                    }));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDiscordOAuthClient>();
                    services.RemoveAll<TimeProvider>();
                    services.RemoveAll<ProfileHostOptions>();
                    services.RemoveAll<DiscordIdentityOptions>();
                    services.AddSingleton(new ProfileHostOptions
                    {
                        Enabled = true,
                        DatabasePath = Path.Combine(root, "profiles.db")
                    });
                    services.AddSingleton(new DiscordIdentityOptions
                    {
                        Enabled = true,
                        ClientId = "123456789012345678",
                        ClientSecret = "client_secret_contract_aaaaaaaaaaaaaaaa",
                        BootstrapSecret = "bootstrap_secret_contract_bbbbbbbbbbbbbbbb",
                        SignInCallbackUri = "https://localhost/identity/v1/signin/discord/callback",
                        ApplicationBaseUri = "https://app.test/",
                        DatabasePath = Path.Combine(root, "identity.db"),
                        AuthorizationEndpoint = "https://discord.test/oauth2/authorize"
                    });
                    services.AddSingleton<IDiscordOAuthClient>(oauth);
                    services.AddSingleton<TimeProvider>(time);
                });
            });
            return Task.FromResult(new DiscordAccountFixture(root, time, oauth, application));
        }

        public HttpClient CreateClient() => application.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public async Task<int> CountRowsAsync(string table)
        {
            var database = table.StartsWith("discord_", StringComparison.Ordinal)
                ? Path.Combine(root, "identity.db")
                : Path.Combine(root, "profiles.db");
            await using var connection = new SqliteConnection($"Data Source={database}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async Task TamperReturnPathAsync(string returnPath)
        {
            await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "identity.db")}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE discord_oauth_states SET return_path = $returnPath WHERE consumed_at_utc IS NULL;";
            command.Parameters.AddWithValue("$returnPath", returnPath);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubDiscordOAuthClient : IDiscordOAuthClient
    {
        public DiscordOAuthIdentity? Identity { get; set; }
        public string? LastVerifier { get; private set; }
        public string? LastCallbackUri { get; private set; }
        public string? LastChallenge { get; set; }
        public int Calls { get; private set; }

        public Task<DiscordOAuthIdentity?> ResolveIdentityAsync(
            string code,
            string pkceVerifier,
            string callbackUri,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastVerifier = pkceVerifier;
            LastCallbackUri = callbackUri;
            return Task.FromResult(Identity);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
