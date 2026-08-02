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
    internal static async Task AssertAllAsync() {
        Func<Task>[] scenarios = [
            ConfirmedDeletionColdStartsScopedTombstoneAndDeletesLocalOrder, RevisionZeroDeletionWithoutLocalIdentityAdvancesWithoutInventingTenant,
            DelayedStaleDeletionCannotOverwriteOrDeleteNewerProjection, ConfirmedDeletionCannotCrossCompanyIdentity,
            CollaborationResponseAfterProfileSwitchCannotPublishOrPersist, DelayedCollaborationResponseCannotPersistOverNewerProjection,
            CollaborationResponseFromReplacedHostScopeCannotPersist, CollaborationCannotSendAcrossCaseDistinctConnectionPath,
            AdapterRejectsReplacementHostBeforeProjectionOrPersistence, AdapterHostReplacementDuringPersistenceCannotWriteReplacementRevisionNamespace,
            AdapterReconcilesDurableWinnerAfterOlderWriteFinishesLast, AdapterAlreadyCurrentReplayRepairsMissingDurableOrder,
            AdapterTombstoneReconcilesNewerLiveOrderAfterBlockedDelete, CollaborationReconcilesDurableWinnerAfterOlderWriteFinishesLast,
            ConnectionScopeChangeDuringPersistenceCannotWriteReplacementRevisionNamespace];
        foreach (var scenario in scenarios) await scenario();
        OwnerProjectionIsPreservedThenClearedAndRehydratedByRevision();
    }
    private static async Task ConfirmedDeletionColdStartsScopedTombstoneAndDeletesLocalOrder() {
        var profileId = NewId();
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Retiring order");
        var fixture = CreateDeletionFixture(profileId, order, 5);
        await fixture.Service.DeleteObjectAsync(ProfileSyncCollections.TradeOrders, Key(order));
        var tombstone = Assert.Single(fixture.Store.GetAll(order.CompanyProfileId));
        Assert.Equal(order.Id, tombstone.OrderId);
        Assert.Equal(5, tombstone.ObjectRevision);
        Assert.True(tombstone.Deleted);
        Assert.Equal(1, fixture.Adapter.DeleteCount);
        Assert.DoesNotContain(
            TradeOrderWorkspaceCompositionPolicy.GetDeviceOnlyOrders([order], fixture.Store.GetAll(order.CompanyProfileId)),
            candidate => candidate.Id == order.Id);
    }
    private static async Task RevisionZeroDeletionWithoutLocalIdentityAdvancesWithoutInventingTenant() {
        var profileId = NewId();
        var orderId = Guid.NewGuid();
        var store = new HostedOrderProjectionStore();
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var indexedDb = new IndexedDbService(runtime);
        var localState = CreateLocalState(indexedDb);
        var adapter = new RecordingOrderAdapter(null);
        var service = new ProfileSyncService(
            CreateHostClient(new RevisionZeroDeletionHandler(orderId)), localState,
            new WebSettingsService(indexedDb), store, [adapter]);
        await service.InitializeAsync();
        Assert.Equal(1, service.CurrentStatus.LastSyncRevision);
        Assert.Equal(0, adapter.DeleteCount);
        Assert.Empty(store.GetAll());
    }
    private static async Task DelayedStaleDeletionCannotOverwriteOrDeleteNewerProjection() {
        var profileId = NewId();
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Revision four");
        TradeOrder? newer = null;
        DeletionFixture? fixture = null;
        fixture = CreateDeletionFixture(profileId, order, 5, () =>
        {
            newer = CreateOrder(order.Id, order.CompanyProfileId, "Revision six");
            Assert.True(fixture!.Store.TryPublishRemoteOrder(newer, 6));
        });
        fixture.Store.TryPublishRemoteOrder(order, 4);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DeleteObjectAsync(ProfileSyncCollections.TradeOrders, Key(order)));
        Assert.Same(newer, fixture.Store.Get(order.Id)?.Order);
        Assert.Equal(6, fixture.Store.Get(order.Id)?.ObjectRevision);
        Assert.Equal(0, fixture.Adapter.DeleteCount);
    }
    private static async Task ConfirmedDeletionCannotCrossCompanyIdentity() {
        var remoteOrder = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Remote company");
        var otherCompanyOrder = CreateOrder(remoteOrder.Id, Guid.NewGuid(), "Other company");
        var fixture = CreateDeletionFixture(NewId(), remoteOrder, 5);
        fixture.Store.TryPublishRemoteOrder(otherCompanyOrder, 4);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DeleteObjectAsync(ProfileSyncCollections.TradeOrders, Key(remoteOrder)));
        Assert.Same(otherCompanyOrder, fixture.Store.Get(remoteOrder.Id)?.Order);
        Assert.Empty(fixture.Store.GetAll(remoteOrder.CompanyProfileId));
        Assert.Equal(0, fixture.Adapter.DeleteCount);
    }
    private static async Task CollaborationResponseAfterProfileSwitchCannotPublishOrPersist() {
        var fixture = new ProjectionFixture("Publish me");
        var nextProfileId = NewId();
        fixture.Runtime.AddCompany(fixture.Order.CompanyProfileId);
        await fixture.PrepareCollaborationAsync();
        var collaboration = fixture.CreateCollaboration(5, () => fixture.Store.BeginProfileRestore(
            nextProfileId, false, 0, DateTime.UtcNow, ConnectionScope(nextProfileId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collaboration.PublishPortableLinkAsync(fixture.Order, new CommissionBriefDocument()));
        Assert.Equal(0, fixture.Runtime.SaveTradeOrderCount);
        Assert.Null(fixture.Store.Get(fixture.Order.Id));
        Assert.Equal(nextProfileId, fixture.Store.CaptureAuthorityScope().ProfileId);
    }
    private static async Task DelayedCollaborationResponseCannotPersistOverNewerProjection() {
        var fixture = new ProjectionFixture("Revision four");
        await fixture.PrepareCollaborationAsync();
        var newer = fixture.OrderAt("Revision six");
        var collaboration = fixture.CreateCollaboration(5,
            () => Assert.True(fixture.Store.TryPublishRemoteOrder(newer, 6)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collaboration.PublishPortableLinkAsync(fixture.Order, new CommissionBriefDocument()));
        Assert.Equal(0, fixture.Runtime.SaveTradeOrderCount);
        Assert.Same(newer, fixture.Store.Get(fixture.Order.Id)?.Order);
        Assert.Equal(6, fixture.Store.Get(fixture.Order.Id)?.ObjectRevision);
    }
    private static async Task CollaborationResponseFromReplacedHostScopeCannotPersist() {
        var fixture = new ProjectionFixture("Original host");
        await fixture.PrepareCollaborationAsync();
        var collaboration = fixture.CreateCollaboration(5, fixture.ReplaceHost);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collaboration.PublishPortableLinkAsync(fixture.Order, new CommissionBriefDocument()));
        Assert.Equal(0, fixture.Runtime.SaveTradeOrderCount);
        Assert.Same(fixture.Order, fixture.Store.Get(fixture.Order.Id)?.Order);
        Assert.Equal(4, fixture.Store.Get(fixture.Order.Id)?.ObjectRevision);
        Assert.Equal(0, await fixture.LoadRevisionAsync());
    }
    private static async Task CollaborationCannotSendAcrossCaseDistinctConnectionPath() {
        var profileId = NewId();
        var fixture = new ProjectionFixture(
            "Case-sensitive host path", profileId: profileId,
            connectionScope: $"https://profiles.example/API/|{profileId}");
        SetReadyStatus(fixture.ProfileSync, profileId);
        var collaboration = fixture.CreateUnusedCollaboration();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collaboration.PublishPortableLinkAsync(fixture.Order, new CommissionBriefDocument()));
        Assert.Contains("authority", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Runtime.SaveTradeOrderCount);
    }
    private static async Task AdapterRejectsReplacementHostBeforeProjectionOrPersistence() {
        var fixture = new ProjectionFixture("Original host", addCompany: true);
        var replacement = fixture.OrderAt("Replacement host");
        await fixture.LocalState.LoadConnectionSettingsAsync();
        fixture.ReplaceHost();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Adapter.ApplyRemoteObjectAsync(Envelope(replacement, 5), default));
        Assert.Contains("scope", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Runtime.SaveTradeOrderCount);
        Assert.Same(fixture.Order, fixture.Store.Get(fixture.Order.Id)?.Order);
        Assert.Equal(0, await fixture.LoadRevisionAsync());
    }
    private static async Task AdapterHostReplacementDuringPersistenceCannotWriteReplacementRevisionNamespace() {
        var fixture = new ProjectionFixture("Revision four", addCompany: true);
        var replacement = fixture.OrderAt("Revision five");
        fixture.Runtime.BeforeSaveTradeOrderAsync = _ =>
        {
            fixture.ReplaceHost();
            return Task.CompletedTask;
        };
        await fixture.LocalState.LoadConnectionSettingsAsync();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Adapter.ApplyRemoteObjectAsync(Envelope(replacement, 5), default));
        Assert.Contains("authority", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Revision five", fixture.Runtime.DurableOrder?.Title);
        Assert.Equal(5, fixture.Store.Get(fixture.Order.Id)?.ObjectRevision);
        Assert.Equal(0, await fixture.LoadRevisionAsync());
    }
    private static async Task AdapterReconcilesDurableWinnerAfterOlderWriteFinishesLast() {
        var fixture = new ProjectionFixture("Revision four", addCompany: true);
        var revisionFive = fixture.OrderAt("Revision five");
        var revisionSix = fixture.OrderAt("Revision six");
        var gate = fixture.BlockFirstSave("Revision five");
        await fixture.LocalState.LoadConnectionSettingsAsync();
        var older = fixture.Adapter.ApplyRemoteObjectAsync(Envelope(revisionFive, 5), default);
        await gate.Entered.Task;
        await fixture.Adapter.ApplyRemoteObjectAsync(Envelope(revisionSix, 6), default);
        gate.Release.SetResult();
        await older;
        Assert.Equal("Revision six", fixture.Runtime.DurableOrder?.Title);
        Assert.Equal(6, await fixture.LoadRevisionAsync());
    }
    private static async Task AdapterAlreadyCurrentReplayRepairsMissingDurableOrder() {
        var fixture = new ProjectionFixture("Revision five", revision: 5, addCompany: true);
        await fixture.LocalState.LoadConnectionSettingsAsync();
        await fixture.Adapter.ApplyRemoteObjectAsync(Envelope(fixture.Order, 5), default);
        Assert.Same(fixture.Order, fixture.Runtime.DurableOrder);
        Assert.Equal(1, fixture.Runtime.SaveTradeOrderCount);
    }
    private static async Task AdapterTombstoneReconcilesNewerLiveOrderAfterBlockedDelete() {
        var fixture = new ProjectionFixture("Revision four", addCompany: true);
        var revisionSix = fixture.OrderAt("Revision six");
        await fixture.LocalState.LoadConnectionSettingsAsync();
        await fixture.Adapter.ApplyRemoteObjectAsync(Envelope(fixture.Order, 4), default);
        var gate = fixture.BlockDelete();
        var deletion = fixture.Adapter.ApplyRemoteDeletionAsync(
            fixture.Order.Id, fixture.Order.CompanyProfileId, 5, default);
        await gate.Entered.Task;
        Assert.True(fixture.Store.TryPublishRemoteOrder(revisionSix, 6));
        gate.Release.SetResult();
        await deletion;
        Assert.Equal("Revision six", fixture.Runtime.DurableOrder?.Title);
        Assert.Equal(6, fixture.Store.Get(fixture.Order.Id)?.ObjectRevision);
        Assert.Equal(6, await fixture.LoadRevisionAsync());
    }
    private static async Task CollaborationReconcilesDurableWinnerAfterOlderWriteFinishesLast() {
        var fixture = new ProjectionFixture("Revision four");
        var gate = fixture.BlockFirstSave("Revision five");
        await fixture.PrepareCollaborationAsync();
        var revisionFive = fixture.PublishedOrder("Revision five");
        var revisionSix = fixture.PublishedOrder("Revision six");
        var olderService = fixture.CreateCollaboration(revisionFive, 5);
        var newerService = fixture.CreateCollaboration(revisionSix, 6);
        var older = olderService.PublishPortableLinkAsync(fixture.Order, new CommissionBriefDocument());
        await gate.Entered.Task;
        await newerService.PublishPortableLinkAsync(fixture.Order, new CommissionBriefDocument());
        gate.Release.SetResult();
        await older;
        Assert.Equal("Revision six", fixture.Runtime.DurableOrder?.Title);
        Assert.Equal(6, await fixture.LoadRevisionAsync());
    }
    private static async Task ConnectionScopeChangeDuringPersistenceCannotWriteReplacementRevisionNamespace() {
        var fixture = new ProjectionFixture("Revision four");
        var switched = false;
        fixture.Runtime.BeforeSaveTradeOrderAsync = candidate =>
        {
            if (!switched && candidate.Title == "Revision five") {
                switched = true;
                fixture.ReplaceHost();
            }
            return Task.CompletedTask;
        };
        await fixture.PrepareCollaborationAsync();
        var committed = fixture.PublishedOrder("Revision five");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreateCollaboration(committed, 5)
                .PublishPortableLinkAsync(fixture.Order, new CommissionBriefDocument()));
        Assert.Contains("authority", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Revision five", fixture.Runtime.DurableOrder?.Title);
        Assert.Equal(5, fixture.Store.Get(fixture.Order.Id)?.ObjectRevision);
        Assert.Equal(0, await fixture.LoadRevisionAsync());
    }
    private static void OwnerProjectionIsPreservedThenClearedAndRehydratedByRevision() {
        var fixture = new ProjectionFixture("Owner revision four");
        var ownerFour = Owner(fixture.Order, 4, 8);
        Assert.True(fixture.Store.TryPublishOwner(ownerFour));
        var authority = fixture.Store.CaptureAuthorityScope();
        Assert.Equal(HostedOrderCommittedProjectionResult.AlreadyCurrent,
            fixture.Store.TryAdoptCommittedOrder(authority, fixture.Order, 4));
        Assert.Same(ownerFour, fixture.Store.GetOwnerProjection(fixture.Order.Id));
        var revisionFive = fixture.OrderAt("Owner pending");
        Assert.Equal(HostedOrderCommittedProjectionResult.Adopted,
            fixture.Store.TryAdoptCommittedOrder(authority, revisionFive, 5));
        Assert.Null(fixture.Store.GetOwnerProjection(fixture.Order.Id));
        var ownerFive = Owner(revisionFive, 5, 9);
        Assert.True(fixture.Store.TryPublishOwner(ownerFive));
        Assert.Same(ownerFive, fixture.Store.GetOwnerProjection(fixture.Order.Id));
    }
    private static DeletionFixture CreateDeletionFixture(
        string profileId, TradeOrder order, long responseRevision, Action? beforeDeleteResponse = null) {
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(profileId, false, 0, DateTime.UtcNow, ConnectionScope(profileId));
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var indexedDb = new IndexedDbService(runtime);
        var localState = CreateLocalState(indexedDb);
        var envelope = Envelope(order, responseRevision - 1);
        var adapter = new RecordingOrderAdapter(envelope);
        var service = new ProfileSyncService(
            CreateHostClient(new ProfileDeletionHandler(envelope, responseRevision, beforeDeleteResponse)),
            localState, new WebSettingsService(indexedDb), store, [adapter]);
        return new(service, store, adapter);
    }
    private static ProfileSyncLocalStateService CreateLocalState(IndexedDbService indexedDb) =>
        new(indexedDb, new ProfileHostClientOptions(Host));
    private static ProfileHostClient CreateHostClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(Host) }, new ProfileHostClientOptions(Host));
    private static ProfileSyncService CreateProfileSync(
        ProfileSyncLocalStateService localState, IndexedDbService indexedDb, HostedOrderProjectionStore store) =>
        new(CreateHostClient(new UnusedHandler()), localState, new WebSettingsService(indexedDb), store, []);
    private static TradeCompanyCollaborationService CreateCollaboration(
        TradeOrder committed, long revision, ProfileSyncLocalStateService localState,
        ProfileSyncService profileSync, TradeOperationsPersistenceService persistence,
        HostedOrderProjectionStore store, Action? beforeResponse = null) =>
        new(new TradeCompanyCollaborationClient(
                new HttpClient(new PortablePublicationHandler(committed, revision, beforeResponse ?? (() => { })))
                    { BaseAddress = new Uri(Host) }, localState),
            persistence, localState, profileSync, store);
    private static void SetReadyStatus(ProfileSyncService service, string profileId) {
        var property = typeof(ProfileSyncService).GetProperty(
            nameof(ProfileSyncService.CurrentStatus), BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(nameof(ProfileSyncService.CurrentStatus));
        property.SetValue(service, new ProfileSyncStatus(true, true, 4, 0, 0, DateTime.UtcNow, "Synced") {
            ProfileId = profileId,
            Stage = ProfileSyncStage.Ready
        });
    }
    private static Dictionary<string, string> ConnectionSettings(string profileId) =>
        new(StringComparer.Ordinal) {
            [ProfileSyncSettingsKeys.HostUrl] = JsonSerializer.Serialize(Host),
            [ProfileSyncSettingsKeys.AccessKey] = JsonSerializer.Serialize("access-key"),
            [ProfileSyncSettingsKeys.RememberAccessKey] = JsonSerializer.Serialize(true),
            [ProfileSyncSettingsKeys.ConnectedProfileId] = JsonSerializer.Serialize(profileId),
            ["profileHost.connectedProfileName"] = JsonSerializer.Serialize("Test profile")
        };
    private static string NewId() => Guid.NewGuid().ToString("D");
    private static string Key(TradeOrder order) => order.Id.ToString("D");
    private static string ConnectionScope(string profileId) =>
        $"{ProfileHostClient.NormalizeHostUrl(Host)}|{profileId}";
    private static TradeOrder CreateOrder(Guid orderId, Guid companyProfileId, string title) =>
        new() { Id = orderId, CompanyProfileId = companyProfileId, Title = title };
    private static CompanyCommissionOwnerProjection Owner(
        TradeOrder order, long objectRevision, long companyRevision) =>
        new() {
            Order = order,
            ObjectRevision = new(objectRevision),
            CompanyRevision = new(companyRevision)
        };
    private static TradeOrder CreatePublishedOrder(TradeOrder source, long revision) {
        var published = TradeOrderWorkflow.CopyOrder(source);
        published.CommissionPublication = new() {
            PublicId = "public-id",
            PublicUrl = "https://profiles.example/brief?id=public-id",
            Version = 1,
            PublishedAtUtc = DateTime.UtcNow,
            Ownership = new(new(source.CompanyProfileId), source.Id, new(revision))
        };
        return published;
    }
    private static ProfileSyncObjectEnvelope Envelope(TradeOrder order, long revision) =>
        new() {
            Collection = ProfileSyncCollections.TradeOrders,
            ObjectId = Key(order),
            PayloadJson = JsonSerializer.Serialize(order, ProfileSyncJson.CreateOptions()),
            Revision = revision,
            UpdatedAtUtc = DateTime.UtcNow
        };
    private sealed class ProjectionFixture
    {
        public ProjectionFixture(
            string title, long revision = 4, string? profileId = null,
            string? connectionScope = null, bool addCompany = false) {
            ProfileId = profileId ?? NewId();
            Order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), title);
            Store.BeginProfileRestore(ProfileId, false, revision, DateTime.UtcNow,
                connectionScope ?? ConnectionScope(ProfileId));
            Assert.True(Store.TryPublishRemoteOrder(Order, revision));
            Runtime = new(ConnectionSettings(ProfileId));
            if (addCompany) Runtime.AddCompany(Order.CompanyProfileId);
            IndexedDb = new(Runtime);
            LocalState = CreateLocalState(IndexedDb);
            ProfileSync = CreateProfileSync(LocalState, IndexedDb, Store);
            Adapter = new(new TradeOperationsPersistenceService(
                IndexedDb, new TradeCompanyProfilePackageService()), Store, LocalState);
        }
        public string ProfileId { get; }
        public TradeOrder Order { get; }
        public HostedOrderProjectionStore Store { get; } = new();
        public StorageRuntime Runtime { get; }
        public IndexedDbService IndexedDb { get; }
        public ProfileSyncLocalStateService LocalState { get; }
        public ProfileSyncService ProfileSync { get; }
        public TradeOrderProfileSyncAdapter Adapter { get; }
        public TradeOrder OrderAt(string title) => CreateOrder(Order.Id, Order.CompanyProfileId, title);
        public TradeOrder PublishedOrder(string title) {
            var result = CreatePublishedOrder(Order, 4);
            result.Title = title;
            return result;
        }
        public async Task PrepareCollaborationAsync() {
            await LocalState.LoadConnectionSettingsAsync();
            await LocalState.SaveObjectRevisionAsync(
                ProfileId, ProfileSyncCollections.TradeOrders, Key(Order), 4);
            SetReadyStatus(ProfileSync, ProfileId);
        }
        public Task<long> LoadRevisionAsync() => LocalState.LoadObjectRevisionAsync(
            ProfileId, ProfileSyncCollections.TradeOrders, Key(Order));
        public TradeCompanyCollaborationService CreateCollaboration(long revision, Action beforeResponse) =>
            CreateCollaboration(CreatePublishedOrder(Order, 4), revision, beforeResponse);
        public TradeCompanyCollaborationService CreateCollaboration(TradeOrder committed, long revision) =>
            CreateCollaboration(committed, revision, () => { });
        public TradeCompanyCollaborationService CreateUnusedCollaboration() =>
            new(new TradeCompanyCollaborationClient(
                    new HttpClient(new UnusedHandler()) { BaseAddress = new Uri(Host) }, LocalState),
                new TradeOperationsPersistenceService(IndexedDb, new TradeCompanyProfilePackageService()),
                LocalState, ProfileSync, Store);
        private TradeCompanyCollaborationService CreateCollaboration(
            TradeOrder committed, long revision, Action beforeResponse) =>
            ProfileSyncDeletionProjectionTests.CreateCollaboration(
                committed, revision, LocalState, ProfileSync,
                new TradeOperationsPersistenceService(IndexedDb, new TradeCompanyProfilePackageService()),
                Store, beforeResponse);
        public void ReplaceHost() => Runtime.SaveRawSetting(
            ProfileSyncSettingsKeys.HostUrl,
            JsonSerializer.Serialize("https://replacement.example/api/"));
        public AsyncGate BlockFirstSave(string title) {
            var gate = new AsyncGate();
            var blocked = false;
            Runtime.BeforeSaveTradeOrderAsync = async candidate =>
            {
                if (blocked || candidate.Title != title) return;
                blocked = true;
                gate.Entered.SetResult();
                await gate.Release.Task;
            };
            return gate;
        }
        public AsyncGate BlockDelete() {
            var gate = new AsyncGate();
            Runtime.BeforeDeleteTradeOrderAsync = async _ =>
            {
                gate.Entered.SetResult();
                await gate.Release.Task;
            };
            return gate;
        }
    }
    private sealed class AsyncGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record DeletionFixture(
        ProfileSyncService Service, HostedOrderProjectionStore Store, RecordingOrderAdapter Adapter);
    private sealed class RecordingOrderAdapter(ProfileSyncObjectEnvelope? local) : IProfileSyncCollectionAdapter
    {
        public string Collection => ProfileSyncCollections.TradeOrders;
        public int DeleteCount { get; private set; }
        public Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProfileSyncObjectEnvelope>>(local == null ? [] : [local]);
        public Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteLocalObjectAsync(string objectId, CancellationToken ct) {
            DeleteCount++;
            return Task.CompletedTask;
        }
    }
    private sealed class RevisionZeroDeletionHandler(Guid orderId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/profile-host/changes"))
                throw new NotSupportedException(request.RequestUri.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = JsonContent.Create(new ProfileSyncChangesResponse
                {
                    ServerRevision = 1,
                    HasMore = false,
                    Objects = [new() {
                        Collection = ProfileSyncCollections.TradeOrders,
                        ObjectId = orderId.ToString("D"),
                        Revision = 1,
                        Deleted = true,
                        UpdatedAtUtc = DateTime.UtcNow
                    }]
                })
            });
        }
    }
    private sealed class ProfileDeletionHandler(
        ProfileSyncObjectEnvelope remote, long responseRevision, Action? beforeDeleteResponse)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.EndsWith("/profile-host/bootstrap/export"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = JsonContent.Create(new ProfileHostBootstrapPayload { Objects = [remote] })
                });
            if (request.Method == HttpMethod.Delete) {
                beforeDeleteResponse?.Invoke();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = JsonContent.Create(new ProfileSyncPutResponse
                    {
                        Success = true,
                        ServerRevision = responseRevision,
                        Object = new() {
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
        TradeOrder committed, long revision, Action beforeResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) {
            beforeResponse();
            var publication = committed.CommissionPublication!;
            var response = new CommissionBriefCreateResponse
            {
                PublicId = publication.PublicId,
                PublicUrl = publication.PublicUrl!,
                EditorToken = string.Empty,
                Version = publication.Version,
                PublishedAtUtc = publication.PublishedAtUtc,
                OrderRecord = new(
                    new(committed.CompanyProfileId), TradeCompanyRecordKinds.Order, Key(committed),
                    JsonSerializer.Serialize(committed, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    new(revision), DateTime.UtcNow)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(response) });
        }
    }
    private sealed class UnusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new NotSupportedException(request.RequestUri?.ToString());
    }
    private sealed class StorageRuntime(Dictionary<string, string> settings) : IJSRuntime
    {
        private readonly HashSet<Guid> _companyIds = [];
        public int SaveTradeOrderCount { get; private set; }
        public TradeOrder? DurableOrder { get; private set; }
        public Func<TradeOrder, Task>? BeforeSaveTradeOrderAsync { get; set; }
        public Func<Guid, Task>? BeforeDeleteTradeOrderAsync { get; set; }
        public void SaveRawSetting(string key, string value) => settings[key] = value;
        public void AddCompany(Guid companyId) => _companyIds.Add(companyId);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) {
            if (identifier == "IndexedDB.saveTradeOrder")
                return SaveTradeOrderAsync<TValue>((TradeOrder)args![0]!);
            if (identifier == "IndexedDB.deleteTradeOrder")
                return DeleteTradeOrderAsync<TValue>((Guid)args![0]!);
            object? result = identifier switch
            {
                "IndexedDB.loadAllSettings" => new Dictionary<string, string>(settings, StringComparer.Ordinal),
                "IndexedDB.loadSetting" => settings.GetValueOrDefault((string)args![0]!),
                "IndexedDB.loadTradeCompanyProfiles" => _companyIds.Select(companyId =>
                    new TradeCompanyProfile { Id = companyId, Name = "Test company" }).ToList(),
                "IndexedDB.saveSettingsBatch" => SaveBatch((Dictionary<string, string>)args![0]!),
                "IndexedDB.saveSetting" => SaveSetting((string)args![0]!, (string)args[1]!),
                _ => throw new NotSupportedException(identifier)
            };
            return ValueTask.FromResult((TValue)result!);
        }
        private bool SaveBatch(Dictionary<string, string> values) {
            foreach (var (key, value) in values) settings[key] = value;
            return true;
        }
        private bool SaveSetting(string key, string value) {
            settings[key] = value;
            return true;
        }
        private async ValueTask<TValue> SaveTradeOrderAsync<TValue>(TradeOrder order) {
            if (BeforeSaveTradeOrderAsync != null) await BeforeSaveTradeOrderAsync(order);
            SaveTradeOrderCount++;
            DurableOrder = order;
            return (TValue)(object)true;
        }
        private async ValueTask<TValue> DeleteTradeOrderAsync<TValue>(Guid orderId) {
            if (BeforeDeleteTradeOrderAsync != null) await BeforeDeleteTradeOrderAsync(orderId);
            if (DurableOrder?.Id == orderId) DurableOrder = null;
            return (TValue)(object)true;
        }
    }
}
