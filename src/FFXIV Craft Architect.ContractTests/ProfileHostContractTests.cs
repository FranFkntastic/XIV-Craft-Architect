using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

[CollectionDefinition("Profile host integration", DisableParallelization = true)]
public sealed class ProfileHostIntegrationCollection
{
}

[Collection("Profile host integration")]
public sealed class ProfileHostContractTests
{
    [Fact]
    public void Pbkdf2Verifier_MatchesFixedSha256Vector()
    {
        var hasher = new ProfileAccessKeyHasher();
        const string storedHash =
            "pbkdf2-sha256:210000:AAECAwQFBgcICQoLDA0ODw==:dzo2npct2bWeVeoHpwQ4+jkONUExvi4ebpQ8zmEun8Y=";

        Assert.True(hasher.Verify("contract-password", storedHash));
        Assert.False(hasher.Verify("Contract-password", storedHash));
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
            if (line.Length > 0 && !line.StartsWith(':'))
            {
                lines.Add(line);
            }
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"id: {put.ServerRevision}", lines[0]);
        Assert.Equal("event: profile-revision", lines[1]);
        Assert.Equal($"data: {{\"serverRevision\":{put.ServerRevision}}}", lines[2]);
        await reader.ReadToEndAsync(timeout.Token);

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
    public async Task TradeOrder_NewGeneratedPointerRequiresExactSealedSnapshot()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"ca-order-plan-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteProfileHostStore(
                new ProfileHostOptions { DatabasePath = databasePath });
            var profile = await store.CreateProfileAsync("Order Plan Profile", CancellationToken.None);
            var orderId = Guid.NewGuid();
            var savedAt = new DateTime(2026, 8, 2, 6, 30, 0, DateTimeKind.Utc);
            var planId = Guid.NewGuid().ToString("D");
            var order = new TradeOrder
            {
                Id = orderId,
                CompanyProfileId = Guid.NewGuid(),
                Title = "Cobalt Joint Plate Commission",
                CraftPlanId = planId,
                CraftPlanSavedAtUtc = savedAt,
                CraftPlanLinkKind = TradeOrderCraftPlanLinkKind.OrderGenerated
            };
            var orderPayload = JsonSerializer.Serialize(order, ProfileSyncJson.CreateOptions());
            Task<ProfileSyncPutResponse> Put(string collection, string id, string payload, long revision = 0) =>
                store.PutObjectAsync(profile.ProfileId, collection, id, payload, revision, CancellationToken.None);
            Task<ProfileSyncPutResponse> Delete(string collection, string id, long revision) =>
                store.DeleteObjectAsync(profile.ProfileId, collection, id, revision, CancellationToken.None);

            var missingPlan = await Put(ProfileSyncCollections.TradeOrders, orderId.ToString("D"), orderPayload);
            var plan = new ProfileSyncPlanSnapshot
            {
                Id = planId,
                SavedAt = savedAt,
                LinkedOrderId = orderId,
                PlanJson = "{\"recipe\":true}"
            };
            var planPayload = ProfileSyncPlanPayloadCodec.Serialize(plan);
            var sealedPlan = await Put(ProfileSyncCollections.Plans, planId, planPayload);
            var identicalPlan = await Put(ProfileSyncCollections.Plans, planId, planPayload);
            plan.DataCenter = "Primal";
            var overwrittenPlan = await Put(
                ProfileSyncCollections.Plans, planId, ProfileSyncPlanPayloadCodec.Serialize(plan), sealedPlan.Object!.Revision);
            var nonCanonicalOrderId = await Put(
                ProfileSyncCollections.TradeOrders, orderId.ToString("D").ToUpperInvariant(), orderPayload);
            var accepted = await Put(ProfileSyncCollections.TradeOrders, orderId.ToString("D"), orderPayload);
            var protectedDelete = await Delete(ProfileSyncCollections.Plans, planId, sealedPlan.Object.Revision);
            var deletedOrder = await Delete(ProfileSyncCollections.TradeOrders, orderId.ToString("D"), accepted.Object!.Revision);
            var deletedPlan = await Delete(ProfileSyncCollections.Plans, planId, sealedPlan.Object.Revision);

            Assert.True(missingPlan.Conflict);
            Assert.Equal("linked_plan_invalid", missingPlan.ErrorCode);
            Assert.True(sealedPlan.Success);
            Assert.Equal(sealedPlan.Object.Revision, identicalPlan.Object!.Revision);
            Assert.Equal("immutable_plan_snapshot", overwrittenPlan.ErrorCode);
            Assert.True(nonCanonicalOrderId.Conflict);
            Assert.Equal("linked_plan_invalid", nonCanonicalOrderId.ErrorCode);
            Assert.True(accepted.Success);
            Assert.True(protectedDelete.Conflict);
            Assert.True(deletedOrder.Success);
            Assert.True(deletedPlan.Success);
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
    public async Task AccessKeyList_ReturnsOnlyActiveMetadataAndMarksPresentedKey()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var otherKey = new ProfileAccessKeyHasher().CreateAccessKey();
        await fixture.Store.AddAccessKeyAsync(
            fixture.ProfileId,
            otherKey.StoredHash,
            CancellationToken.None);
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync("/profile-host/keys");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        response.EnsureSuccessStatusCode();
        var keys = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, keys.Length);
        Assert.Single(keys, key => key.GetProperty("isCurrent").GetBoolean());
        Assert.All(keys, key => Assert.Equal(
            ["id", "createdAtUtc", "lastUsedAtUtc", "isCurrent"],
            key.EnumerateObject().Select(property => property.Name).ToArray()));
        Assert.DoesNotContain("hash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.AccessKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(otherKey.PlaintextKey, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokeCurrentKey_SignsOutPresentedBrowser()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient();
        var keys = await client.GetFromJsonAsync<IReadOnlyList<ProfileHostAccessKeyMetadata>>("/profile-host/keys");
        var currentKeyId = Assert.Single(keys!).Id;

        using var wrongRoute = await client.DeleteAsync($"/profile-host/keys/{currentKeyId}");
        using var revoked = await client.DeleteAsync("/profile-host/keys/current");
        using var afterRevocation = await client.GetAsync("/profile-host/profile");

        Assert.Equal(HttpStatusCode.BadRequest, wrongRoute.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevocation.StatusCode);
    }

    [Fact]
    public async Task RevokeOtherKey_LeavesPresentedKeyAuthorized()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var otherKey = new ProfileAccessKeyHasher().CreateAccessKey();
        await fixture.Store.AddAccessKeyAsync(
            fixture.ProfileId,
            otherKey.StoredHash,
            CancellationToken.None);
        using var currentClient = fixture.CreateClient();
        var keys = await currentClient.GetFromJsonAsync<IReadOnlyList<ProfileHostAccessKeyMetadata>>(
            "/profile-host/keys");
        var otherKeyId = Assert.Single(keys!, key => !key.IsCurrent).Id;

        using var revoked = await currentClient.DeleteAsync($"/profile-host/keys/{otherKeyId}");
        using var currentStillAuthorized = await currentClient.GetAsync("/profile-host/profile");
        using var otherClient = fixture.CreateClient(accessKey: otherKey.PlaintextKey);
        using var otherRejected = await otherClient.GetAsync("/profile-host/profile");

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, currentStillAuthorized.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherRejected.StatusCode);
    }

    [Fact]
    public async Task KeyManagement_RefusesCrossProfileAndDisabledHost()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var secondaryProfile = await fixture.Store.CreateProfileAsync("Other Account", CancellationToken.None);
        var secondaryKey = new ProfileAccessKeyHasher().CreateAccessKey();
        await fixture.Store.AddAccessKeyAsync(
            secondaryProfile.ProfileId,
            secondaryKey.StoredHash,
            CancellationToken.None);
        using var secondaryClient = fixture.CreateClient(accessKey: secondaryKey.PlaintextKey);
        var secondaryKeys = await secondaryClient.GetFromJsonAsync<IReadOnlyList<ProfileHostAccessKeyMetadata>>(
            "/profile-host/keys");
        var secondaryKeyId = Assert.Single(secondaryKeys!).Id;
        using var primaryClient = fixture.CreateClient();

        using var crossProfile = await primaryClient.DeleteAsync($"/profile-host/keys/{secondaryKeyId}");
        using var secondaryStillAuthorized = await secondaryClient.GetAsync("/profile-host/profile");
        using var disabledApplication = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ProfileHost:Enabled"] = "false"
                })));
        using var disabledClient = disabledApplication.CreateClient();
        using var disabledList = await disabledClient.GetAsync("/profile-host/keys");
        using var disabledCurrent = await disabledClient.DeleteAsync("/profile-host/keys/current");
        using var disabledOther = await disabledClient.DeleteAsync($"/profile-host/keys/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.NotFound, crossProfile.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondaryStillAuthorized.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, disabledList.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, disabledCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, disabledOther.StatusCode);
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

    [Fact]
    public async Task ChangesFeed_SummarizesArchivedOrdersAndScopesCollections()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient();
        var active = CreateOrder(TradeOrderStatus.InProgress);
        var archived = CreateOrder(TradeOrderStatus.Completed);
        await PutOrderAsync(client, active, 0);
        var archivedPut = await PutOrderAsync(client, archived, 0);
        using var planResponse = await client.PutAsJsonAsync(
            "/profile-host/objects/plans/plan-filter-check",
            new ProfileSyncPutRequest { PayloadJson = "{\"id\":\"plan-filter-check\",\"name\":\"filter\"}", ExpectedRevision = 0 });
        planResponse.EnsureSuccessStatusCode();

        var changes = await client.GetFromJsonAsync<ProfileSyncChangesResponse>(
            "/profile-host/changes?sinceRevision=0");
        var activeEnvelope = Assert.Single(
            changes!.Objects,
            item => item.ObjectId == active.Id.ToString("D"));
        Assert.Null(activeEnvelope.SummaryJson);
        Assert.NotEmpty(activeEnvelope.PayloadJson);
        var archivedEnvelope = Assert.Single(
            changes.Objects,
            item => item.ObjectId == archived.Id.ToString("D"));
        Assert.Equal(archivedPut.Object!.Revision, archivedEnvelope.Revision);
        Assert.Empty(archivedEnvelope.PayloadJson);
        var summary = TradeOrderArchiveSummaryCodec.Deserialize(
            Assert.IsType<string>(archivedEnvelope.SummaryJson),
            archivedEnvelope.ObjectId);
        Assert.Equal(archived.Title, summary.Title);
        Assert.Equal(TradeOrderStatus.Completed, summary.Status);
        Assert.Equal(archived.CompanyProfileId, summary.CompanyProfileId);
        Assert.Equal(
            archived.SourceSnapshot.RootItems.Select(item => item.Name),
            summary.Outputs.Select(item => item.Name));

        var ordersOnly = await client.GetFromJsonAsync<ProfileSyncChangesResponse>(
            $"/profile-host/changes?sinceRevision=0&collections={ProfileSyncCollections.TradeOrders}");
        using var invalidResponse = await client.GetAsync(
            "/profile-host/changes?sinceRevision=0&collections=privateSecrets");
        Assert.Equal(2, ordersOnly!.Objects.Count);
        Assert.All(
            ordersOnly.Objects,
            item => Assert.Equal(ProfileSyncCollections.TradeOrders, item.Collection));
        Assert.False(ordersOnly.HasMore);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
    }

    [Fact]
    public async Task SingleObjectGet_ReturnsFullArchivedPayloadAndGuardsIdentity()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient();
        var archived = CreateOrder(TradeOrderStatus.Canceled);
        var put = await PutOrderAsync(client, archived, 0);

        var fetched = await client.GetFromJsonAsync<ProfileSyncObjectEnvelope>(
            $"/profile-host/objects/{ProfileSyncCollections.TradeOrders}/{archived.Id:D}");
        using var missingResponse = await client.GetAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeOrders}/{Guid.NewGuid():D}");
        using var unauthorizedResponse = await fixture
            .CreateClient(withAccessKey: false)
            .GetAsync($"/profile-host/objects/{ProfileSyncCollections.TradeOrders}/{archived.Id:D}");

        Assert.Equal(put.Object!.Revision, fetched!.Revision);
        Assert.Null(fetched.SummaryJson);
        var order = JsonSerializer.Deserialize<TradeOrder>(fetched.PayloadJson, ProfileSyncJson.CreateOptions());
        Assert.Equal(archived.Title, order!.Title);
        Assert.Equal(
            archived.SourceSnapshot.Materials.Count,
            order.SourceSnapshot.Materials.Count);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedResponse.StatusCode);
    }

    [Fact]
    public async Task RetentionSweep_PurgesBacksUpAndRestoresThroughOrdinaryPaths()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient();
        var oldArchived = CreateOrder(TradeOrderStatus.Completed);
        var recentArchived = CreateOrder(TradeOrderStatus.Canceled);
        var active = CreateOrder(TradeOrderStatus.AwaitingDelivery);
        await PutOrderAsync(client, oldArchived, 0);
        await PutOrderAsync(client, recentArchived, 0);
        await PutOrderAsync(client, active, 0);
        var oldPayload = (await fixture.Store.LoadHostedObjectAsync(
            fixture.ProfileId,
            ProfileSyncCollections.TradeOrders,
            oldArchived.Id.ToString("D"),
            CancellationToken.None))!.PayloadJson;
        fixture.BackdateOrder(
            oldArchived.Id,
            DateTime.UtcNow.AddDays(-(fixture.Options.ArchiveRetentionDays + 30)));

        var result = await fixture.CreateRetentionService().RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Purged);
        Assert.Equal(0, result.BackupFailures);
        var purged = await fixture.Store.LoadHostedObjectAsync(
            fixture.ProfileId,
            ProfileSyncCollections.TradeOrders,
            oldArchived.Id.ToString("D"),
            CancellationToken.None);
        Assert.True(purged!.Deleted);
        var backupLine = Assert.Single(fixture.ReadBackupLines());
        using var backupDocument = JsonDocument.Parse(backupLine);
        var backupRootElement = backupDocument.RootElement;
        Assert.Equal(
            oldArchived.Id.ToString("D"),
            backupRootElement.GetProperty("objectId").GetString());
        Assert.Equal(
            fixture.ProfileId,
            backupRootElement.GetProperty("profileId").GetString());
        Assert.Equal(oldPayload, backupRootElement.GetProperty("payloadJson").GetString());
        Assert.False((await fixture.Store.LoadHostedObjectAsync(
            fixture.ProfileId,
            ProfileSyncCollections.TradeOrders,
            recentArchived.Id.ToString("D"),
            CancellationToken.None))!.Deleted);
        Assert.False((await fixture.Store.LoadHostedObjectAsync(
            fixture.ProfileId,
            ProfileSyncCollections.TradeOrders,
            active.Id.ToString("D"),
            CancellationToken.None))!.Deleted);

        using var restoreResponse = await client.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeOrders}/{oldArchived.Id:D}",
            new ProfileSyncPutRequest
            {
                PayloadJson = oldPayload,
                ExpectedRevision = purged.Revision
            });
        var restored = await fixture.Store.LoadHostedObjectAsync(
            fixture.ProfileId,
            ProfileSyncCollections.TradeOrders,
            oldArchived.Id.ToString("D"),
            CancellationToken.None);
        restoreResponse.EnsureSuccessStatusCode();
        Assert.False(restored!.Deleted);
        Assert.Equal(oldPayload, restored.PayloadJson);
    }

    [Fact]
    public async Task RetentionSweep_SkipsDeletionWhenBackupWriteFails()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient();
        var oldArchived = CreateOrder(TradeOrderStatus.Completed);
        await PutOrderAsync(client, oldArchived, 0);
        fixture.BackdateOrder(
            oldArchived.Id,
            DateTime.UtcNow.AddDays(-(fixture.Options.ArchiveRetentionDays + 30)));
        Directory.CreateDirectory(fixture.BackupRoot);
        var blockerFile = Path.Combine(fixture.BackupRoot, "blocker");
        await File.WriteAllTextAsync(blockerFile, "not a directory");
        fixture.Options.ArchiveBackupDirectory = blockerFile;

        var result = await fixture.CreateRetentionService().RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(0, result.Purged);
        Assert.Equal(1, result.BackupFailures);
        var retained = await fixture.Store.LoadHostedObjectAsync(
            fixture.ProfileId,
            ProfileSyncCollections.TradeOrders,
            oldArchived.Id.ToString("D"),
            CancellationToken.None);
        Assert.False(retained!.Deleted);
    }

    private static TradeOrder CreateOrder(TradeOrderStatus status) =>
        new()
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = Guid.NewGuid(),
            Title = $"Contract order {status}",
            Status = status,
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                RootItems = [new TradeOrderRootItemSnapshot(1, "Grade 2 Gemdraught", 2, false, 0)],
                Materials = [new TradeOrderMaterialSnapshot(2, "Aetherial Reduction", 4, false, 10, 40)]
            }
        };

    private static async Task<ProfileSyncPutResponse> PutOrderAsync(
        HttpClient client,
        TradeOrder order,
        long expectedRevision)
    {
        using var response = await client.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeOrders}/{order.Id:D}",
            new ProfileSyncPutRequest
            {
                PayloadJson = JsonSerializer.Serialize(order, ProfileSyncJson.CreateOptions()),
                ExpectedRevision = expectedRevision
            });
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
            ProfileHostOptions options,
            string backupRoot,
            WebApplicationFactory<Program> application)
        {
            this.databasePath = databasePath;
            ProfileId = profileId;
            AccessKey = accessKey;
            Store = store;
            Options = options;
            BackupRoot = backupRoot;
            this.application = application;
        }

        public string ProfileId { get; }
        public string AccessKey { get; }
        public SqliteProfileHostStore Store { get; }
        public ProfileHostOptions Options { get; }
        public string BackupRoot { get; }

        public static async Task<ProfileFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"ca-contract-{Guid.NewGuid():N}.db");
            var backupRoot = Path.Combine(Path.GetTempPath(), $"ca-contract-backups-{Guid.NewGuid():N}");
            var options = new ProfileHostOptions
            {
                DatabasePath = databasePath,
                ArchiveBackupDirectory = backupRoot
            };
            var store = new SqliteProfileHostStore(options);
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
                            ["ProfileHost:ChangeStreamLease"] = "00:00:00.250",
                            ["ProfileHost:ChangeStreamHeartbeat"] = "00:00:00.050",
                        });
                    });
                });
            return new ProfileFixture(
                databasePath,
                profile.ProfileId,
                accessKey.PlaintextKey,
                store,
                options,
                backupRoot,
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

        public ProfileHostRetentionService CreateRetentionService() =>
            new(
                Options,
                Store,
                new ProfileArchiveBackupStore(
                    Options,
                    NullLogger<ProfileArchiveBackupStore>.Instance),
                NullLogger<ProfileHostRetentionService>.Instance);

        public void BackdateOrder(Guid orderId, DateTime updatedAtUtc)
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWrite
                }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                update sync_objects
                set updated_at_utc = $updatedAtUtc
                where profile_id = $profileId
                  and collection = $collection
                  and object_id = $objectId;
                """;
            command.Parameters.AddWithValue("$updatedAtUtc", updatedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$profileId", ProfileId);
            command.Parameters.AddWithValue("$collection", ProfileSyncCollections.TradeOrders);
            command.Parameters.AddWithValue("$objectId", orderId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public IReadOnlyList<string> ReadBackupLines()
        {
            var directory = Path.Combine(BackupRoot, Uri.EscapeDataString(ProfileId));
            if (!Directory.Exists(directory))
            {
                return [];
            }

            return Directory
                .GetFiles(directory, "archived-orders-*.jsonl")
                .SelectMany(File.ReadAllLines)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            application.Services
                .GetRequiredService<IHostApplicationLifetime>()
                .StopApplication();
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
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

            if (Directory.Exists(BackupRoot))
            {
                try
                {
                    Directory.Delete(BackupRoot, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
