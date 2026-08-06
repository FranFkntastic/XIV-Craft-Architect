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
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

[Collection("Profile host integration")]
public sealed class ProfileHostArchiveSyncContractTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        ProfileSyncJson.CreateOptions();

    [Fact]
    public async Task ChangesFeed_SummarizesOnlyArchivedOrders()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        using var client = fixture.CreateClient();
        var active = CreateOrder(TradeOrderStatus.InProgress);
        var archived = CreateOrder(TradeOrderStatus.Completed);
        await PutOrderAsync(client, active, 0);
        var archivedPut = await PutOrderAsync(client, archived, 0);

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
    }

    [Fact]
    public async Task ChangesFeed_CollectionFilterScopesPagesAndRejectsUnknownCollections()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        using var client = fixture.CreateClient();
        await PutOrderAsync(client, CreateOrder(TradeOrderStatus.InProgress), 0);
        using var planResponse = await client.PutAsJsonAsync(
            "/profile-host/objects/plans/plan-filter-check",
            new ProfileSyncPutRequest { PayloadJson = "{\"id\":\"plan-filter-check\",\"name\":\"filter\"}", ExpectedRevision = 0 });
        planResponse.EnsureSuccessStatusCode();

        var ordersOnly = await client.GetFromJsonAsync<ProfileSyncChangesResponse>(
            $"/profile-host/changes?sinceRevision=0&collections={ProfileSyncCollections.TradeOrders}");
        using var invalidResponse = await client.GetAsync(
            "/profile-host/changes?sinceRevision=0&collections=privateSecrets");

        Assert.Single(ordersOnly!.Objects);
        Assert.All(
            ordersOnly.Objects,
            item => Assert.Equal(ProfileSyncCollections.TradeOrders, item.Collection));
        Assert.False(ordersOnly.HasMore);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
    }

    [Fact]
    public async Task SingleObjectGet_ReturnsFullArchivedPayloadAndGuardsIdentity()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
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
        var order = JsonSerializer.Deserialize<TradeOrder>(fetched.PayloadJson, JsonOptions);
        Assert.Equal(archived.Title, order!.Title);
        Assert.Equal(
            archived.SourceSnapshot.Materials.Count,
            order.SourceSnapshot.Materials.Count);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedResponse.StatusCode);
    }

    [Fact]
    public async Task RetentionSweep_PurgesOnlyOldUnmodifiedArchivedOrders_AfterVerifiedBackup()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
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
        var backupLines = fixture.ReadBackupLines();
        var backupLine = Assert.Single(backupLines);
        using var backupDocument = JsonDocument.Parse(backupLine);
        var backupRoot = backupDocument.RootElement;
        Assert.Equal(
            oldArchived.Id.ToString("D"),
            backupRoot.GetProperty("objectId").GetString());
        Assert.Equal(
            fixture.ProfileId,
            backupRoot.GetProperty("profileId").GetString());
        Assert.Equal(oldPayload, backupRoot.GetProperty("payloadJson").GetString());
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
    }

    [Fact]
    public async Task RetentionSweep_SkipsDeletionWhenBackupWriteFails()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
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

    [Fact]
    public async Task RetentionSweep_RestoresPurgedOrderThroughOrdinaryUpload()
    {
        await using var fixture = await ArchiveFixture.CreateAsync();
        using var client = fixture.CreateClient();
        var oldArchived = CreateOrder(TradeOrderStatus.Completed);
        await PutOrderAsync(client, oldArchived, 0);
        var original = await fixture.Store.LoadHostedObjectAsync(
            fixture.ProfileId,
            ProfileSyncCollections.TradeOrders,
            oldArchived.Id.ToString("D"),
            CancellationToken.None);
        fixture.BackdateOrder(
            oldArchived.Id,
            DateTime.UtcNow.AddDays(-(fixture.Options.ArchiveRetentionDays + 30)));
        await fixture.CreateRetentionService().RunSweepAsync(CancellationToken.None);
        var tombstoneRevision = (await fixture.Store.LoadHostedObjectAsync(
            fixture.ProfileId,
            ProfileSyncCollections.TradeOrders,
            oldArchived.Id.ToString("D"),
            CancellationToken.None))!.Revision;

        using var restoreResponse = await client.PutAsJsonAsync(
            $"/profile-host/objects/{ProfileSyncCollections.TradeOrders}/{oldArchived.Id:D}",
            new ProfileSyncPutRequest
            {
                PayloadJson = original!.PayloadJson,
                ExpectedRevision = tombstoneRevision
            });
        var restored = await fixture.Store.LoadHostedObjectAsync(
            fixture.ProfileId,
            ProfileSyncCollections.TradeOrders,
            oldArchived.Id.ToString("D"),
            CancellationToken.None);

        restoreResponse.EnsureSuccessStatusCode();
        Assert.False(restored!.Deleted);
        Assert.Equal(original.PayloadJson, restored.PayloadJson);
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
                PayloadJson = JsonSerializer.Serialize(order, JsonOptions),
                ExpectedRevision = expectedRevision
            });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileSyncPutResponse>())!;
    }

    private sealed class ArchiveFixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly WebApplicationFactory<Program> _application;

        private ArchiveFixture(
            string databasePath,
            string profileId,
            string accessKey,
            SqliteProfileHostStore store,
            ProfileHostOptions options,
            string backupRoot,
            WebApplicationFactory<Program> application)
        {
            _databasePath = databasePath;
            ProfileId = profileId;
            AccessKey = accessKey;
            Store = store;
            Options = options;
            BackupRoot = backupRoot;
            _application = application;
        }

        public string ProfileId { get; }
        public string AccessKey { get; }
        public SqliteProfileHostStore Store { get; }
        public ProfileHostOptions Options { get; }
        public string BackupRoot { get; }

        public static async Task<ArchiveFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"ca-archive-contract-{Guid.NewGuid():N}.db");
            var backupRoot = Path.Combine(Path.GetTempPath(), $"ca-archive-backups-{Guid.NewGuid():N}");
            var options = new ProfileHostOptions
            {
                DatabasePath = databasePath,
                ArchiveBackupDirectory = backupRoot
            };
            var store = new SqliteProfileHostStore(options);
            var hasher = new ProfileAccessKeyHasher();
            var accessKey = hasher.CreateAccessKey();
            var profile = await store.CreateProfileAsync("Archive Contract", CancellationToken.None);
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
            return new ArchiveFixture(
                databasePath,
                profile.ProfileId,
                accessKey.PlaintextKey,
                store,
                options,
                backupRoot,
                application);
        }

        public HttpClient CreateClient(bool withAccessKey = true)
        {
            var client = _application.CreateClient();
            if (withAccessKey)
            {
                client.DefaultRequestHeaders.Add("X-Profile-Key", AccessKey);
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
                    DataSource = _databasePath,
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
            _application.Services
                .GetRequiredService<IHostApplicationLifetime>()
                .StopApplication();
            await _application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            const int maximumDeleteAttempts = 50;
            for (var attempt = 0;
                 attempt < maximumDeleteAttempts && File.Exists(_databasePath);
                 attempt++)
            {
                try
                {
                    File.Delete(_databasePath);
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
