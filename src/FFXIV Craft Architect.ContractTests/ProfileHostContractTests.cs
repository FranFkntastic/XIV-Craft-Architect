using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class ProfileHostContractTests
{
    [Fact]
    public async Task Pbkdf2Verifier_MatchesFixedSha256Vector()
    {
        var hasher = new ProfileAccessKeyHasher();
        const string storedHash =
            "pbkdf2-sha256:210000:AAECAwQFBgcICQoLDA0ODw==:dzo2npct2bWeVeoHpwQ4+jkONUExvi4ebpQ8zmEun8Y=";

        Assert.True(hasher.Verify("contract-password", storedHash));
        Assert.False(hasher.Verify("Contract-password", storedHash));
        await ProfileHostCredentialImportContractScenarios.RunAsync();
    }

    [Theory]
    [InlineData("pbkdf2-sha256:210000:not-base64!:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256:210000:AAAAAAAAAAAAAAAAAAAAAA==:not-base64!")]
    [InlineData("pbkdf2-sha256:210000:c2FsdA==:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256:210000:AAAAAAAAAAAAAAAAAAAAAA==:AA==")]
    [InlineData("pbkdf2-sha256:1:AAECAwQFBgcICQoLDA0ODw==:dzo2npct2bWeVeoHpwQ4+jkONUExvi4ebpQ8zmEun8Y=")]
    [InlineData("pbkdf2-sha256:209999:AAECAwQFBgcICQoLDA0ODw==:dzo2npct2bWeVeoHpwQ4+jkONUExvi4ebpQ8zmEun8Y=")]
    [InlineData("pbkdf2-sha256:210001:AAECAwQFBgcICQoLDA0ODw==:dzo2npct2bWeVeoHpwQ4+jkONUExvi4ebpQ8zmEun8Y=")]
    public async Task CorruptStoredBase64Hash_FailsClosedAtEndpoint(string corruptHash)
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.Store.AddAccessKeyAsync(fixture.ProfileId, corruptHash, CancellationToken.None);
        using var client = fixture.CreateClient(withAccessKey: false);
        client.DefaultRequestHeaders.Add("X-Profile-Key", "cap_unknown-key");

        using var response = await client.GetAsync("/profile-host/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProfileEndpoint_RejectsMissingAccessKey()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient(withAccessKey: false);

        using var response = await client.GetAsync("/profile-host/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProfileAuthentication_RejectsUnknownKeysAndQueuesValidBursts()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient(withAccessKey: false);
        client.DefaultRequestHeaders.Add("X-Profile-Key", "cap_unrecognized-contract-key");

        using var response = await client.GetAsync("/profile-host/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var testCancellationToken = timeout.Token;
        var burstGate = new ProfileAuthenticationGate();
        var burstResults = new List<string?>();
        for (var attempt = 0; attempt < 13; attempt++)
        {
            burstResults.Add(await burstGate.ExecuteAsync(
                "cap_valid-contract-key",
                _ => Task.FromResult<string?>("ok"),
                testCancellationToken));
        }

        Assert.All(burstResults, result => Assert.Equal("ok", result));

        var concurrencyGate = new ProfileAuthenticationGate();
        var entered = new SemaphoreSlim(0, 3);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string?> AuthenticateAsync(CancellationToken cancellationToken)
        {
            entered.Release();
            await release.Task.WaitAsync(cancellationToken);
            return "ok";
        }

        var first = concurrencyGate.ExecuteAsync(
            "cap_valid-contract-key",
            AuthenticateAsync,
            testCancellationToken);
        var second = concurrencyGate.ExecuteAsync(
            "cap_valid-contract-key",
            AuthenticateAsync,
            testCancellationToken);
        await entered.WaitAsync(testCancellationToken);
        await entered.WaitAsync(testCancellationToken);

        var third = concurrencyGate.ExecuteAsync(
            "cap_valid-contract-key",
            AuthenticateAsync,
            testCancellationToken);
        await Task.Delay(50, testCancellationToken);

        Assert.False(third.IsCompleted);
        release.SetResult();
        Assert.Equal(new[] { "ok", "ok", "ok" }, await Task.WhenAll(first, second, third));
    }

    [Fact]
    public async Task ChangeStream_RejectsMissingAccessKey()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient(withAccessKey: false);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/profile-host/changes/stream?sinceRevision=0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChangeStream_WakesAfterCommittedMutation()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var streamClient = fixture.CreateClient();
        using var mutationClient = fixture.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/profile-host/changes/stream?sinceRevision=0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var responseTask = streamClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        await Task.Delay(50, timeout.Token);
        var put = await PutAsync(
            mutationClient,
            "{\"name\":\"Stream Wake\"}",
            expectedRevision: 0);
        using var response = await responseTask.WaitAsync(timeout.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (lines.Count < 3)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"id: {put.ServerRevision}", lines[0]);
        Assert.Equal("event: profile-revision", lines[1]);
        Assert.Equal($"data: {{\"serverRevision\":{put.ServerRevision}}}", lines[2]);

        timeout.Cancel();
        streamClient.CancelPendingRequests();
        reader.Dispose();
        await stream.DisposeAsync();
        response.Dispose();
        await Task.Delay(50);
    }

    [Fact]
    public async Task StoreSignal_OnlyPublishesCommittedMutations()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"ca-signal-{Guid.NewGuid():N}.db");
        try
        {
            var signal = new ProfileHostChangeSignal();
            var store = new SqliteProfileHostStore(
                new ProfileHostOptions { DatabasePath = databasePath },
                signal);
            var profile = await store.CreateProfileAsync("Signal Profile", CancellationToken.None);
            var firstObservation = signal.Observe(profile.ProfileId);

            var committed = await store.PutObjectAsync(
                profile.ProfileId,
                ProfileSyncCollections.Settings,
                "signal-object",
                "{}",
                expectedRevision: 0,
                CancellationToken.None);
            await firstObservation.Changed.WaitAsync(TimeSpan.FromSeconds(1));
            var conflictObservation = signal.Observe(profile.ProfileId);
            var conflict = await store.PutObjectAsync(
                profile.ProfileId,
                ProfileSyncCollections.Settings,
                "signal-object",
                "{\"stale\":true}",
                expectedRevision: 0,
                CancellationToken.None);

            Assert.True(committed.Success);
            Assert.True(conflict.Conflict);
            Assert.False(conflictObservation.Changed.IsCompleted);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task RevokedKeyAndDisabledProfile_AreDeniedAtEndpoint()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var revokedClient = fixture.CreateClient();

        await fixture.Store.RevokeAccessKeysAsync(fixture.ProfileId, CancellationToken.None);
        using var revokedResponse = await revokedClient.GetAsync("/profile-host/profile");

        var replacement = new ProfileAccessKeyHasher().CreateAccessKey();
        await fixture.Store.AddAccessKeyAsync(
            fixture.ProfileId,
            replacement.StoredHash,
            CancellationToken.None);
        using var disabledClient = fixture.CreateClient(accessKey: replacement.PlaintextKey);
        using var enabledResponse = await disabledClient.GetAsync("/profile-host/profile");
        await fixture.Store.DisableProfileAsync(fixture.ProfileId, CancellationToken.None);
        using var disabledResponse = await disabledClient.GetAsync("/profile-host/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, enabledResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, disabledResponse.StatusCode);
    }

    [Fact]
    public async Task AccessKey_CannotSelectOrReadAnotherProfile()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var primaryClient = fixture.CreateClient();
        await PutAsync(primaryClient, "{\"owner\":\"primary\"}", expectedRevision: 0);
        var secondaryProfile = await fixture.Store.CreateProfileAsync("Second Profile", CancellationToken.None);
        var secondaryKey = new ProfileAccessKeyHasher().CreateAccessKey();
        await fixture.Store.AddAccessKeyAsync(
            secondaryProfile.ProfileId,
            secondaryKey.StoredHash,
            CancellationToken.None);
        using var secondaryClient = fixture.CreateClient(accessKey: secondaryKey.PlaintextKey);

        var authenticatedProfile = await secondaryClient.GetFromJsonAsync<ProfileHostProfileResponse>(
            $"/profile-host/profile?profileId={fixture.ProfileId}");
        var visibleChanges = await secondaryClient.GetFromJsonAsync<ProfileSyncChangesResponse>(
            $"/profile-host/changes?sinceRevision=0&profileId={fixture.ProfileId}");
        using var attemptedCrossProfileMutation = await secondaryClient.PutAsJsonAsync(
            $"/profile-host/objects/settings/test-setting?profileId={fixture.ProfileId}",
            new ProfileSyncPutRequest { PayloadJson = "{\"owner\":\"secondary\"}", ExpectedRevision = 0 });
        var primaryChanges = await fixture.Store.LoadChangesAsync(
            fixture.ProfileId,
            sinceRevision: 0,
            ct: CancellationToken.None);
        var secondaryChanges = await fixture.Store.LoadChangesAsync(
            secondaryProfile.ProfileId,
            sinceRevision: 0,
            ct: CancellationToken.None);

        Assert.Equal(secondaryProfile.ProfileId, authenticatedProfile?.ProfileId);
        Assert.Empty(visibleChanges!.Objects);
        Assert.Equal(HttpStatusCode.OK, attemptedCrossProfileMutation.StatusCode);
        Assert.Equal("{\"owner\":\"primary\"}", Assert.Single(primaryChanges.Objects).PayloadJson);
        Assert.Equal("{\"owner\":\"secondary\"}", Assert.Single(secondaryChanges.Objects).PayloadJson);
    }

    [Fact]
    public async Task UnauthorizedMutationAndBootstrap_AreDeniedWithoutStoreMutation()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient(withAccessKey: false);

        using var putResponse = await client.PutAsJsonAsync(
            "/profile-host/objects/plans/plan-1",
            new ProfileSyncPutRequest { PayloadJson = "{\"name\":\"intrusion\"}", ExpectedRevision = 0 });
        using var deleteResponse = await client.DeleteAsync(
            "/profile-host/objects/plans/plan-1?expectedRevision=0");
        using var uploadResponse = await client.PostAsJsonAsync(
            "/profile-host/bootstrap/upload",
            new ProfileHostBootstrapPayload
            {
                Objects =
                [
                    new ProfileSyncObjectEnvelope
                    {
                        Collection = ProfileSyncCollections.Plans,
                        ObjectId = "plan-1",
                        PayloadJson = "{\"name\":\"intrusion\"}",
                    },
                ],
            });
        using var exportResponse = await client.GetAsync("/profile-host/bootstrap/export");
        var stored = await fixture.Store.LoadChangesAsync(
            fixture.ProfileId,
            sinceRevision: 0,
            ct: CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, putResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deleteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, uploadResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, exportResponse.StatusCode);
        Assert.Empty(stored.Objects);
    }

    [Fact]
    public async Task UnsupportedCollections_AreDeniedWithoutStoreMutation()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient();

        using var objectResponse = await client.PutAsJsonAsync(
            "/profile-host/objects/privateSecrets/object-1",
            new ProfileSyncPutRequest { PayloadJson = "{\"secret\":true}", ExpectedRevision = 0 });
        using var bootstrapResponse = await client.PostAsJsonAsync(
            "/profile-host/bootstrap/upload",
            new ProfileHostBootstrapPayload
            {
                Objects =
                [
                    new ProfileSyncObjectEnvelope
                    {
                        Collection = ProfileSyncCollections.Plans,
                        ObjectId = "valid-before-invalid",
                        PayloadJson = "{\"name\":\"must not persist\"}",
                    },
                    new ProfileSyncObjectEnvelope
                    {
                        Collection = "privateSecrets",
                        ObjectId = "object-1",
                        PayloadJson = "{\"secret\":true}",
                    },
                ],
            });
        var stored = await fixture.Store.LoadChangesAsync(
            fixture.ProfileId,
            sinceRevision: 0,
            ct: CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, objectResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bootstrapResponse.StatusCode);
        Assert.Equal(0, stored.ServerRevision);
        Assert.Empty(stored.Objects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreMutationRejectsUnsupportedCollections(bool delete)
    {
        await using var fixture = await ProfileFixture.CreateAsync();

        Task mutation = delete
            ? fixture.Store.DeleteObjectAsync(
                fixture.ProfileId, "privateSecrets", "object-1", 0, CancellationToken.None)
            : fixture.Store.PutObjectAsync(
                fixture.ProfileId, "privateSecrets", "object-1", "{}", 0, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => mutation);
        var stored = await fixture.Store.LoadChangesAsync(
            fixture.ProfileId,
            sinceRevision: 0,
            ct: CancellationToken.None);
        Assert.Equal(0, stored.ServerRevision);
        Assert.Empty(stored.Objects);
    }

    [Fact]
    public async Task StaleExpectedRevision_ReturnsConflictWithoutOverwritingRemoteObject()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient();
        var first = await PutAsync(client, "{\"name\":\"Workshop Restock\"}", expectedRevision: 0);

        using var conflictResponse = await client.PutAsJsonAsync(
            "/profile-host/objects/settings/test-setting",
            new ProfileSyncPutRequest
            {
                PayloadJson = "{\"name\":\"Stale Copy\"}",
                ExpectedRevision = 0,
            });
        var conflict = Assert.IsType<ProfileSyncPutResponse>(
            await conflictResponse.Content.ReadFromJsonAsync<ProfileSyncPutResponse>());
        var changes = await client.GetFromJsonAsync<ProfileSyncChangesResponse>("/profile-host/changes?sinceRevision=0");

        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        Assert.True(conflict.Conflict);
        Assert.Equal(first.Object?.Revision, conflict.RemoteObject?.Revision);
        Assert.Equal("{\"name\":\"Workshop Restock\"}", Assert.Single(changes!.Objects).PayloadJson);
    }

    [Fact]
    public async Task DeleteEndpoint_PublishesRevisionedTombstone()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient();
        var first = await PutAsync(client, "{\"name\":\"Workshop Restock\"}", expectedRevision: 0);

        using var deleteResponse = await client.DeleteAsync(
            $"/profile-host/objects/settings/test-setting?expectedRevision={first.Object!.Revision}");
        var deleted = Assert.IsType<ProfileSyncPutResponse>(
            await deleteResponse.Content.ReadFromJsonAsync<ProfileSyncPutResponse>());
        var changes = await client.GetFromJsonAsync<ProfileSyncChangesResponse>(
            $"/profile-host/changes?sinceRevision={first.Object.Revision}");

        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        var tombstone = Assert.IsType<ProfileSyncObjectEnvelope>(deleted.Object);
        Assert.True(tombstone.Deleted);
        Assert.Equal("{}", tombstone.PayloadJson);
        Assert.NotNull(tombstone.DeletedAtUtc);
        Assert.True(Assert.Single(changes!.Objects).Deleted);
    }

    private static async Task<ProfileSyncPutResponse> PutAsync(
        HttpClient client,
        string payload,
        long expectedRevision)
    {
        using var response = await client.PutAsJsonAsync(
            "/profile-host/objects/settings/test-setting",
            new ProfileSyncPutRequest { PayloadJson = payload, ExpectedRevision = expectedRevision });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileSyncPutResponse>())!;
    }

    private sealed class ProfileFixture : IAsyncDisposable
    {
        private readonly string databasePath;
        private readonly WebApplicationFactory<Program> application;

        private ProfileFixture(
            string databasePath,
            string profileId,
            string accessKey,
            SqliteProfileHostStore store,
            WebApplicationFactory<Program> application)
        {
            this.databasePath = databasePath;
            ProfileId = profileId;
            AccessKey = accessKey;
            Store = store;
            this.application = application;
        }

        public string ProfileId { get; }
        public string AccessKey { get; }
        public SqliteProfileHostStore Store { get; }

        public static async Task<ProfileFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"ca-contract-{Guid.NewGuid():N}.db");
            var store = new SqliteProfileHostStore(new ProfileHostOptions { DatabasePath = databasePath });
            var hasher = new ProfileAccessKeyHasher();
            var accessKey = hasher.CreateAccessKey();
            var profile = await store.CreateProfileAsync("Sapphire Avenue", CancellationToken.None);
            await store.AddAccessKeyAsync(profile.ProfileId, accessKey.StoredHash, CancellationToken.None);
            var application = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ProfileHost:Enabled"] = "true",
                            ["ProfileHost:DatabasePath"] = databasePath,
                        });
                    });
                });
            return new ProfileFixture(
                databasePath,
                profile.ProfileId,
                accessKey.PlaintextKey,
                store,
                application);
        }

        public HttpClient CreateClient(bool withAccessKey = true, string? accessKey = null)
        {
            var client = application.CreateClient();
            if (withAccessKey)
            {
                client.DefaultRequestHeaders.Add("X-Profile-Key", accessKey ?? AccessKey);
            }

            return client;
        }

        public async ValueTask DisposeAsync()
        {
            application.Services
                .GetRequiredService<IHostApplicationLifetime>()
                .StopApplication();
            await application.DisposeAsync();
            const int maximumDeleteAttempts = 50;
            for (var attempt = 0;
                 attempt < maximumDeleteAttempts && File.Exists(databasePath);
                 attempt++)
            {
                try
                {
                    File.Delete(databasePath);
                }
                catch (IOException) when (attempt < maximumDeleteAttempts - 1)
                {
                    await Task.Delay(100);
                }
            }
        }
    }
}
