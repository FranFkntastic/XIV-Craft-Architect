using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FFXIV_Craft_Architect.ContractTests;

[CollectionDefinition("Profile host integration", DisableParallelization = true)]
public sealed class ProfileHostIntegrationCollection
{
}

[Collection("Profile host integration")]
public sealed class ProfileHostContractTests
{
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
    public async Task ProvisionProfile_PreservesExistingIdentityAndRotatedKeys()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient();
        using var rename = await client.PutAsJsonAsync(
            "/profile-host/profile",
            new ProfileHostDisplayNameUpdateRequest
            {
                ExpectedMetadataRevision = 0,
                DisplayName = "Canonical operator"
            });
        rename.EnsureSuccessStatusCode();
        var staleBootstrapKey = new ProfileAccessKeyHasher().CreateAccessKey();

        var provisioned = await fixture.Store.ProvisionProfileIfMissingAsync(
            fixture.ProfileId,
            "Deployment seed name",
            staleBootstrapKey.PlaintextKey,
            new ProfileAccessKeyHasher(),
            CancellationToken.None);

        Assert.False(provisioned.Created);
        Assert.Equal("Canonical operator", provisioned.Profile.DisplayName);
        Assert.Equal(1, provisioned.Profile.MetadataRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.EnsureProfileAsync(
                fixture.ProfileId,
                "Deployment seed name",
                staleBootstrapKey.PlaintextKey,
                new ProfileAccessKeyHasher(),
                CancellationToken.None));
    }

    [Fact]
    public async Task ExistingProfileSchema_AddsMetadataRevisionWithoutChangingIdentity()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"ca-profile-metadata-{Guid.NewGuid():N}.db");
        var profileId = Guid.NewGuid().ToString("D");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    create table hosted_profiles (
                        id text primary key,
                        display_name text not null,
                        created_at_utc text not null,
                        updated_at_utc text not null,
                        disabled_at_utc text null
                    );
                    insert into hosted_profiles (id, display_name, created_at_utc, updated_at_utc)
                    values ($id, 'Sapphire Avenue', $now, $now);
                    """;
                command.Parameters.AddWithValue("$id", profileId);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            var store = new SqliteProfileHostStore(new ProfileHostOptions { DatabasePath = databasePath });
            var profile = await store.LoadProfileAsync(profileId, CancellationToken.None);

            Assert.NotNull(profile);
            Assert.Equal(profileId, profile.ProfileId);
            Assert.Equal("Sapphire Avenue", profile.DisplayName);
            Assert.Equal(0, profile.MetadataRevision);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
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
    public async Task SuccessfulAccessKeyAuthentication_IsCachedButStillHonorsRevocation()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var hasher = new RepeatedVerificationRejectingHasher();

        var first = await fixture.Store.AuthenticateAccessKeyAsync(
            fixture.AccessKey,
            hasher,
            CancellationToken.None);
        var verificationCount = hasher.VerificationCount;
        hasher.RejectFurtherVerification = true;
        var cached = await fixture.Store.AuthenticateAccessKeyAsync(
            fixture.AccessKey,
            hasher,
            CancellationToken.None);
        await fixture.Store.RevokeAccessKeysAsync(
            fixture.ProfileId,
            CancellationToken.None);
        var revoked = await fixture.Store.AuthenticateAccessKeyAsync(
            fixture.AccessKey,
            hasher,
            CancellationToken.None);

        Assert.Equal(fixture.ProfileId, first?.Profile.ProfileId);
        Assert.Equal(fixture.ProfileId, cached?.Profile.ProfileId);
        Assert.True(verificationCount > 0);
        Assert.Equal(verificationCount, hasher.VerificationCount);
        Assert.Null(revoked);
    }

    private sealed class RepeatedVerificationRejectingHasher : ProfileAccessKeyHasher
    {
        public int VerificationCount { get; private set; }
        public bool RejectFurtherVerification { get; set; }

        public override bool Verify(string plaintextKey, string storedHash)
        {
            if (RejectFurtherVerification)
            {
                throw new InvalidOperationException("The cached access key was rehashed.");
            }

            VerificationCount++;
            return base.Verify(plaintextKey, storedHash);
        }
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
    public async Task DeepArchiveMove_StaleCandidateLeavesLiveOrderIntact()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        using var client = fixture.CreateClient();
        var oldArchived = CreateOrder(TradeOrderStatus.Completed);
        await PutOrderAsync(client, oldArchived, 0);
        var stale = await fixture.Store.LoadHostedObjectAsync(
            fixture.ProfileId,
            ProfileSyncCollections.TradeOrders,
            oldArchived.Id.ToString("D"),
            CancellationToken.None);
        oldArchived.Title = "Changed after scan";
        await PutOrderAsync(client, oldArchived, stale!.Revision);

        var archived = await fixture.Store.MoveOrderToDeepArchiveAsync(
            fixture.ProfileId,
            stale,
            CancellationToken.None);

        Assert.False(archived);
        var retained = await fixture.Store.LoadHostedObjectAsync(
            fixture.ProfileId,
            ProfileSyncCollections.TradeOrders,
            oldArchived.Id.ToString("D"),
            CancellationToken.None);
        Assert.False(retained!.Deleted);
        Assert.Contains("Changed after scan", retained.PayloadJson, StringComparison.Ordinal);
        Assert.Empty((await fixture.Store.SearchDeepArchivedOrdersAsync(
            fixture.ProfileId,
            "contract order",
            0,
            20,
            CancellationToken.None)).Orders);
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
            string storedAccessKeyHash,
            SqliteProfileHostStore store,
            ProfileHostOptions options,
            string backupRoot,
            WebApplicationFactory<Program> application)
        {
            this.databasePath = databasePath;
            ProfileId = profileId;
            AccessKey = accessKey;
            StoredAccessKeyHash = storedAccessKeyHash;
            Store = store;
            Options = options;
            BackupRoot = backupRoot;
            this.application = application;
        }

        public string ProfileId { get; }
        public string AccessKey { get; }
        public string StoredAccessKeyHash { get; }
        public string DatabasePath => databasePath;
        public SqliteProfileHostStore Store { get; }
        public ProfileHostOptions Options { get; }
        public string BackupRoot { get; }

        public static async Task<ProfileFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"ca-contract-{Guid.NewGuid():N}.db");
            var backupRoot = Path.Combine(Path.GetTempPath(), $"ca-contract-backups-{Guid.NewGuid():N}");
            var options = new ProfileHostOptions { DatabasePath = databasePath };
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
                accessKey.StoredHash,
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
