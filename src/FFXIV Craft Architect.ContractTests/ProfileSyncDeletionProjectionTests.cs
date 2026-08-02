using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class ProfileSyncDeletionProjectionTests
{
    private const string Host = "https://profiles.example/api/";

    [Fact]
    public async Task ConfirmedDeletionColdStartsScopedTombstoneAndDeletesLocalOrder()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Retiring order");
        var fixture = CreateDeletionFixture(profileId, order, responseRevision: 5);

        await fixture.Service.DeleteObjectAsync(
            ProfileSyncCollections.TradeOrders,
            order.Id.ToString("D"));

        var tombstone = Assert.Single(fixture.Store.GetAll(order.CompanyProfileId));
        Assert.Equal(order.Id, tombstone.OrderId);
        Assert.Equal(5, tombstone.ObjectRevision);
        Assert.True(tombstone.Deleted);
        Assert.Equal(1, fixture.Adapter.DeleteCount);
        Assert.DoesNotContain(
            TradeOrderWorkspaceCompositionPolicy.GetDeviceOnlyOrders(
                [order],
                fixture.Store.GetAll(order.CompanyProfileId)),
            candidate => candidate.Id == order.Id);
    }

    [Fact]
    public async Task DelayedStaleDeletionCannotOverwriteOrDeleteNewerProjection()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Revision four");
        TradeOrder? newer = null;
        DeletionFixture? fixture = null;
        fixture = CreateDeletionFixture(
            profileId,
            order,
            responseRevision: 5,
            beforeDeleteResponse: () =>
            {
                newer = CreateOrder(order.Id, order.CompanyProfileId, "Revision six");
                Assert.True(fixture!.Store.TryPublishRemoteOrder(newer, 6));
            });
        fixture.Store.TryPublishRemoteOrder(order, 4);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DeleteObjectAsync(
                ProfileSyncCollections.TradeOrders,
                order.Id.ToString("D")));

        Assert.Same(newer, fixture.Store.Get(order.Id)?.Order);
        Assert.Equal(6, fixture.Store.Get(order.Id)?.ObjectRevision);
        Assert.Equal(0, fixture.Adapter.DeleteCount);
    }

    [Fact]
    public async Task ConfirmedDeletionCannotCrossCompanyIdentity()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var remoteOrder = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Remote company");
        var otherCompanyOrder = CreateOrder(
            remoteOrder.Id,
            Guid.NewGuid(),
            "Other company");
        var fixture = CreateDeletionFixture(profileId, remoteOrder, responseRevision: 5);
        fixture.Store.TryPublishRemoteOrder(otherCompanyOrder, 4);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DeleteObjectAsync(
                ProfileSyncCollections.TradeOrders,
                remoteOrder.Id.ToString("D")));

        Assert.Same(otherCompanyOrder, fixture.Store.Get(remoteOrder.Id)?.Order);
        Assert.Empty(fixture.Store.GetAll(remoteOrder.CompanyProfileId));
        Assert.Equal(0, fixture.Adapter.DeleteCount);
    }

    [Fact]
    public async Task CollaborationResponseAfterProfileSwitchCannotPublishOrPersist()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var nextProfileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Publish me");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(profileId, false, 4, DateTime.UtcNow);
        Assert.True(store.TryPublishRemoteOrder(order, 4));
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(
            indexedDb,
            new ProfileHostClientOptions(Host));
        await localState.LoadConnectionSettingsAsync();
        await localState.SaveObjectRevisionAsync(
            profileId,
            ProfileSyncCollections.TradeOrders,
            order.Id.ToString("D"),
            4);
        var profileSync = CreateProfileSync(localState, indexedDb, store);
        SetReadyStatus(profileSync, profileId);
        var committed = CreatePublishedOrder(order, revision: 4);
        var collaborationClient = new TradeCompanyCollaborationClient(
            new HttpClient(new PortablePublicationHandler(
                committed,
                revision: 5,
                () => store.BeginProfileRestore(
                    nextProfileId,
                    false,
                    0,
                    DateTime.UtcNow)))
            {
                BaseAddress = new Uri(Host)
            },
            localState);
        var persistence = new TradeOperationsPersistenceService(
            indexedDb,
            new TradeCompanyProfilePackageService());
        var collaboration = new TradeCompanyCollaborationService(
            collaborationClient,
            persistence,
            localState,
            profileSync,
            store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collaboration.PublishPortableLinkAsync(order, new CommissionBriefDocument()));

        Assert.Equal(0, runtime.SaveTradeOrderCount);
        Assert.Null(store.Get(order.Id));
        Assert.Equal(nextProfileId, store.CaptureAuthorityScope().ProfileId);
    }

    [Fact]
    public async Task DelayedCollaborationResponseCannotPersistOverNewerProjection()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Revision four");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(profileId, false, 4, DateTime.UtcNow);
        Assert.True(store.TryPublishRemoteOrder(order, 4));
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(
            indexedDb,
            new ProfileHostClientOptions(Host));
        await localState.LoadConnectionSettingsAsync();
        await localState.SaveObjectRevisionAsync(
            profileId,
            ProfileSyncCollections.TradeOrders,
            order.Id.ToString("D"),
            4);
        var profileSync = CreateProfileSync(localState, indexedDb, store);
        SetReadyStatus(profileSync, profileId);
        var committed = CreatePublishedOrder(order, revision: 4);
        var newer = CreateOrder(order.Id, order.CompanyProfileId, "Revision six");
        var collaboration = new TradeCompanyCollaborationService(
            new TradeCompanyCollaborationClient(
                new HttpClient(new PortablePublicationHandler(
                    committed,
                    revision: 5,
                    () => Assert.True(store.TryPublishRemoteOrder(newer, 6))))
                {
                    BaseAddress = new Uri(Host)
                },
                localState),
            new TradeOperationsPersistenceService(
                indexedDb,
                new TradeCompanyProfilePackageService()),
            localState,
            profileSync,
            store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collaboration.PublishPortableLinkAsync(order, new CommissionBriefDocument()));

        Assert.Equal(0, runtime.SaveTradeOrderCount);
        Assert.Same(newer, store.Get(order.Id)?.Order);
        Assert.Equal(6, store.Get(order.Id)?.ObjectRevision);
    }

    private static DeletionFixture CreateDeletionFixture(
        string profileId,
        TradeOrder order,
        long responseRevision,
        Action? beforeDeleteResponse = null)
    {
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(profileId, false, 0, DateTime.UtcNow);
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(
            indexedDb,
            new ProfileHostClientOptions(Host));
        var envelope = Envelope(order, revision: responseRevision - 1);
        var handler = new ProfileDeletionHandler(
            envelope,
            responseRevision,
            beforeDeleteResponse);
        var adapter = new RecordingOrderAdapter(envelope);
        var service = new ProfileSyncService(
            new ProfileHostClient(
                new HttpClient(handler) { BaseAddress = new Uri(Host) },
                new ProfileHostClientOptions(Host)),
            localState,
            new WebSettingsService(indexedDb),
            store,
            [adapter]);
        return new DeletionFixture(service, store, adapter);
    }

    private static ProfileSyncService CreateProfileSync(
        ProfileSyncLocalStateService localState,
        IndexedDbService indexedDb,
        HostedOrderProjectionStore store) =>
        new(
            new ProfileHostClient(
                new HttpClient(new UnusedHandler()) { BaseAddress = new Uri(Host) },
                new ProfileHostClientOptions(Host)),
            localState,
            new WebSettingsService(indexedDb),
            store,
            []);

    private static void SetReadyStatus(ProfileSyncService service, string profileId)
    {
        var property = typeof(ProfileSyncService).GetProperty(
            nameof(ProfileSyncService.CurrentStatus),
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(nameof(ProfileSyncService.CurrentStatus));
        property.SetValue(service, new ProfileSyncStatus(
            true,
            true,
            4,
            0,
            0,
            DateTime.UtcNow,
            "Synced")
        {
            ProfileId = profileId,
            Stage = ProfileSyncStage.Ready
        });
    }

    private static Dictionary<string, string> ConnectionSettings(string profileId) =>
        new(StringComparer.Ordinal)
        {
            [ProfileSyncSettingsKeys.HostUrl] = JsonSerializer.Serialize(Host),
            [ProfileSyncSettingsKeys.AccessKey] = JsonSerializer.Serialize("access-key"),
            [ProfileSyncSettingsKeys.RememberAccessKey] = JsonSerializer.Serialize(true),
            [ProfileSyncSettingsKeys.ConnectedProfileId] = JsonSerializer.Serialize(profileId),
            ["profileHost.connectedProfileName"] = JsonSerializer.Serialize("Test profile")
        };

    private static TradeOrder CreateOrder(Guid orderId, Guid companyProfileId, string title) =>
        new()
        {
            Id = orderId,
            CompanyProfileId = companyProfileId,
            Title = title
        };

    private static TradeOrder CreatePublishedOrder(TradeOrder source, long revision)
    {
        var published = TradeOrderWorkflow.CopyOrder(source);
        published.CommissionPublication = new TradeCommissionPublication
        {
            PublicId = "public-id",
            PublicUrl = "https://profiles.example/brief/public-id",
            Version = 1,
            PublishedAtUtc = DateTime.UtcNow,
            Ownership = new TradeCompanyPublicationOwnership(
                new CompanyId(source.CompanyProfileId),
                source.Id,
                new CompanyRecordRevision(revision))
        };
        return published;
    }

    private static ProfileSyncObjectEnvelope Envelope(TradeOrder order, long revision) =>
        new()
        {
            Collection = ProfileSyncCollections.TradeOrders,
            ObjectId = order.Id.ToString("D"),
            PayloadJson = JsonSerializer.Serialize(order, ProfileSyncJson.CreateOptions()),
            Revision = revision,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private sealed record DeletionFixture(
        ProfileSyncService Service,
        HostedOrderProjectionStore Store,
        RecordingOrderAdapter Adapter);

    private sealed class RecordingOrderAdapter(ProfileSyncObjectEnvelope local)
        : IProfileSyncCollectionAdapter
    {
        public string Collection => ProfileSyncCollections.TradeOrders;
        public int DeleteCount { get; private set; }
        public Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProfileSyncObjectEnvelope>>([local]);
        public Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
        {
            DeleteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ProfileDeletionHandler(
        ProfileSyncObjectEnvelope remote,
        long responseRevision,
        Action? beforeDeleteResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.EndsWith("/profile-host/bootstrap/export"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ProfileHostBootstrapPayload
                    {
                        Objects = [remote]
                    })
                });
            }
            if (request.Method == HttpMethod.Delete)
            {
                beforeDeleteResponse?.Invoke();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ProfileSyncPutResponse
                    {
                        Success = true,
                        ServerRevision = responseRevision,
                        Object = new ProfileSyncObjectEnvelope
                        {
                            Collection = remote.Collection,
                            ObjectId = remote.ObjectId,
                            Revision = responseRevision,
                            Deleted = true,
                            UpdatedAtUtc = DateTime.UtcNow
                        }
                    })
                });
            }
            throw new NotSupportedException(request.RequestUri?.ToString());
        }
    }

    private sealed class PortablePublicationHandler(
        TradeOrder committed,
        long revision,
        Action beforeResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            beforeResponse();
            var response = new CommissionBriefCreateResponse
            {
                PublicId = committed.CommissionPublication!.PublicId,
                PublicUrl = committed.CommissionPublication.PublicUrl!,
                EditorToken = "editor-token",
                Version = committed.CommissionPublication.Version,
                PublishedAtUtc = committed.CommissionPublication.PublishedAtUtc,
                OrderRecord = new TradeCompanyRecordEnvelope(
                    new CompanyId(committed.CompanyProfileId),
                    TradeCompanyRecordKinds.Order,
                    committed.Id.ToString("D"),
                    JsonSerializer.Serialize(committed, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    new CompanyRecordRevision(revision),
                    DateTime.UtcNow)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(response)
            });
        }
    }

    private sealed class UnusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException(request.RequestUri?.ToString());
    }

    private sealed class StorageRuntime(Dictionary<string, string> settings) : IJSRuntime
    {
        public int SaveTradeOrderCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            object? result = identifier switch
            {
                "IndexedDB.loadAllSettings" => new Dictionary<string, string>(settings, StringComparer.Ordinal),
                "IndexedDB.loadSetting" => settings.GetValueOrDefault((string)args![0]!),
                "IndexedDB.saveSettingsBatch" => SaveBatch((Dictionary<string, string>)args![0]!),
                "IndexedDB.saveSetting" => SaveSetting((string)args![0]!, (string)args[1]!),
                "IndexedDB.saveTradeOrder" => SaveTradeOrder(),
                _ => throw new NotSupportedException(identifier)
            };
            return ValueTask.FromResult((TValue)result!);
        }

        private bool SaveBatch(Dictionary<string, string> values)
        {
            foreach (var (key, value) in values)
            {
                settings[key] = value;
            }
            return true;
        }

        private bool SaveSetting(string key, string value)
        {
            settings[key] = value;
            return true;
        }

        private bool SaveTradeOrder()
        {
            SaveTradeOrderCount++;
            return true;
        }
    }
}
