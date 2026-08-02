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

    internal static async Task AssertAllAsync()
    {
        await ConfirmedDeletionColdStartsScopedTombstoneAndDeletesLocalOrder();
        await RevisionZeroDeletionWithoutLocalIdentityAdvancesWithoutInventingTenant();
        await DelayedStaleDeletionCannotOverwriteOrDeleteNewerProjection();
        await ConfirmedDeletionCannotCrossCompanyIdentity();
        await CollaborationResponseAfterProfileSwitchCannotPublishOrPersist();
        await DelayedCollaborationResponseCannotPersistOverNewerProjection();
        await CollaborationResponseFromReplacedHostScopeCannotPersist();
        await CollaborationCannotSendAcrossCaseDistinctConnectionPath();
        await AdapterRejectsReplacementHostBeforeProjectionOrPersistence();
        await AdapterHostReplacementDuringPersistenceCannotWriteReplacementRevisionNamespace();
        await AdapterReconcilesDurableWinnerAfterOlderWriteFinishesLast();
        await AdapterAlreadyCurrentReplayRepairsMissingDurableOrder();
        await AdapterTombstoneReconcilesNewerLiveOrderAfterBlockedDelete();
        await CollaborationReconcilesDurableWinnerAfterOlderWriteFinishesLast();
        await ConnectionScopeChangeDuringPersistenceCannotWriteReplacementRevisionNamespace();
        OwnerProjectionIsPreservedThenClearedAndRehydratedByRevision();
    }

    private static async Task ConfirmedDeletionColdStartsScopedTombstoneAndDeletesLocalOrder()
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

    private static async Task RevisionZeroDeletionWithoutLocalIdentityAdvancesWithoutInventingTenant()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var orderId = Guid.NewGuid();
        var store = new HostedOrderProjectionStore();
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(
            indexedDb,
            new ProfileHostClientOptions(Host));
        var adapter = new RecordingOrderAdapter(null);
        var service = new ProfileSyncService(
            new ProfileHostClient(
                new HttpClient(new RevisionZeroDeletionHandler(orderId))
                {
                    BaseAddress = new Uri(Host)
                },
                new ProfileHostClientOptions(Host)),
            localState,
            new WebSettingsService(indexedDb),
            store,
            [adapter]);

        await service.InitializeAsync();

        Assert.Equal(1, service.CurrentStatus.LastSyncRevision);
        Assert.Equal(0, adapter.DeleteCount);
        Assert.Empty(store.GetAll());
    }

    private static async Task DelayedStaleDeletionCannotOverwriteOrDeleteNewerProjection()
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

    private static async Task ConfirmedDeletionCannotCrossCompanyIdentity()
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

    private static async Task CollaborationResponseAfterProfileSwitchCannotPublishOrPersist()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var nextProfileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Publish me");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            DateTime.UtcNow,
            ConnectionScope(profileId));
        Assert.True(store.TryPublishRemoteOrder(order, 4));
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        runtime.AddCompany(order.CompanyProfileId);
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
                    DateTime.UtcNow,
                    ConnectionScope(nextProfileId))))
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

    private static async Task DelayedCollaborationResponseCannotPersistOverNewerProjection()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Revision four");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            DateTime.UtcNow,
            ConnectionScope(profileId));
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

    private static async Task CollaborationResponseFromReplacedHostScopeCannotPersist()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Original host");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            DateTime.UtcNow,
            ConnectionScope(profileId));
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
        var collaboration = new TradeCompanyCollaborationService(
            new TradeCompanyCollaborationClient(
                new HttpClient(new PortablePublicationHandler(
                    committed,
                    revision: 5,
                    () => runtime.SaveRawSetting(
                        ProfileSyncSettingsKeys.HostUrl,
                        JsonSerializer.Serialize("https://replacement.example/api/"))))
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
        Assert.Same(order, store.Get(order.Id)?.Order);
        Assert.Equal(4, store.Get(order.Id)?.ObjectRevision);
        Assert.Equal(
            0,
            await localState.LoadObjectRevisionAsync(
                profileId,
                ProfileSyncCollections.TradeOrders,
                order.Id.ToString("D")));
    }

    private static async Task CollaborationCannotSendAcrossCaseDistinctConnectionPath()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Case-sensitive host path");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            DateTime.UtcNow,
            $"https://profiles.example/API/|{profileId}");
        Assert.True(store.TryPublishRemoteOrder(order, 4));
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(
            indexedDb,
            new ProfileHostClientOptions(Host));
        var profileSync = CreateProfileSync(localState, indexedDb, store);
        SetReadyStatus(profileSync, profileId);
        var collaboration = new TradeCompanyCollaborationService(
            new TradeCompanyCollaborationClient(
                new HttpClient(new UnusedHandler()) { BaseAddress = new Uri(Host) },
                localState),
            new TradeOperationsPersistenceService(
                indexedDb,
                new TradeCompanyProfilePackageService()),
            localState,
            profileSync,
            store);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collaboration.PublishPortableLinkAsync(
                order,
                new CommissionBriefDocument()));

        Assert.Contains("authority", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runtime.SaveTradeOrderCount);
    }

    private static async Task AdapterRejectsReplacementHostBeforeProjectionOrPersistence()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Original host");
        var replacement = CreateOrder(order.Id, order.CompanyProfileId, "Replacement host");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            DateTime.UtcNow,
            ConnectionScope(profileId));
        Assert.True(store.TryPublishRemoteOrder(order, 4));
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        runtime.AddCompany(order.CompanyProfileId);
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(
            indexedDb,
            new ProfileHostClientOptions(Host));
        await localState.LoadConnectionSettingsAsync();
        runtime.SaveRawSetting(
            ProfileSyncSettingsKeys.HostUrl,
            JsonSerializer.Serialize("https://replacement.example/api/"));
        var adapter = new TradeOrderProfileSyncAdapter(
            new TradeOperationsPersistenceService(
                indexedDb,
                new TradeCompanyProfilePackageService()),
            store,
            localState);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ApplyRemoteObjectAsync(Envelope(replacement, 5), default));

        Assert.Contains("scope", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runtime.SaveTradeOrderCount);
        Assert.Same(order, store.Get(order.Id)?.Order);
        Assert.Equal(
            0,
            await localState.LoadObjectRevisionAsync(
                profileId,
                ProfileSyncCollections.TradeOrders,
                order.Id.ToString("D")));
    }

    private static async Task AdapterHostReplacementDuringPersistenceCannotWriteReplacementRevisionNamespace()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Revision four");
        var replacement = CreateOrder(order.Id, order.CompanyProfileId, "Revision five");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            DateTime.UtcNow,
            ConnectionScope(profileId));
        Assert.True(store.TryPublishRemoteOrder(order, 4));
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        runtime.AddCompany(order.CompanyProfileId);
        runtime.BeforeSaveTradeOrderAsync = _ =>
        {
            runtime.SaveRawSetting(
                ProfileSyncSettingsKeys.HostUrl,
                JsonSerializer.Serialize("https://replacement.example/api/"));
            return Task.CompletedTask;
        };
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(
            indexedDb,
            new ProfileHostClientOptions(Host));
        await localState.LoadConnectionSettingsAsync();
        var adapter = new TradeOrderProfileSyncAdapter(
            new TradeOperationsPersistenceService(
                indexedDb,
                new TradeCompanyProfilePackageService()),
            store,
            localState);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ApplyRemoteObjectAsync(Envelope(replacement, 5), default));

        Assert.Contains("authority", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Revision five", runtime.DurableOrder?.Title);
        Assert.Equal(5, store.Get(order.Id)?.ObjectRevision);
        Assert.Equal(
            0,
            await localState.LoadObjectRevisionAsync(
                profileId,
                ProfileSyncCollections.TradeOrders,
                order.Id.ToString("D")));
    }

    private static async Task AdapterReconcilesDurableWinnerAfterOlderWriteFinishesLast()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var companyId = Guid.NewGuid();
        var original = CreateOrder(Guid.NewGuid(), companyId, "Revision four");
        var revisionFive = CreateOrder(original.Id, companyId, "Revision five");
        var revisionSix = CreateOrder(original.Id, companyId, "Revision six");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            DateTime.UtcNow,
            ConnectionScope(profileId));
        Assert.True(store.TryPublishRemoteOrder(original, 4));
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        runtime.AddCompany(companyId);
        var firstWriteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = false;
        runtime.BeforeSaveTradeOrderAsync = async order =>
        {
            if (!blocked && order.Title == "Revision five")
            {
                blocked = true;
                firstWriteEntered.SetResult();
                await releaseFirstWrite.Task;
            }
        };
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(
            indexedDb,
            new ProfileHostClientOptions(Host));
        await localState.LoadConnectionSettingsAsync();
        var adapter = new TradeOrderProfileSyncAdapter(
            new TradeOperationsPersistenceService(
                indexedDb,
                new TradeCompanyProfilePackageService()),
            store,
            localState);

        var older = adapter.ApplyRemoteObjectAsync(Envelope(revisionFive, 5), default);
        await firstWriteEntered.Task;
        var newer = adapter.ApplyRemoteObjectAsync(Envelope(revisionSix, 6), default);
        await newer;
        releaseFirstWrite.SetResult();
        await older;

        Assert.Equal("Revision six", runtime.DurableOrder?.Title);
        Assert.Equal(
            6,
            await localState.LoadObjectRevisionAsync(
                profileId,
                ProfileSyncCollections.TradeOrders,
                original.Id.ToString("D")));
    }

    private static async Task AdapterAlreadyCurrentReplayRepairsMissingDurableOrder()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Revision five");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            false,
            5,
            DateTime.UtcNow,
            ConnectionScope(profileId));
        Assert.True(store.TryPublishRemoteOrder(order, 5));
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        runtime.AddCompany(order.CompanyProfileId);
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(
            indexedDb,
            new ProfileHostClientOptions(Host));
        await localState.LoadConnectionSettingsAsync();
        var adapter = new TradeOrderProfileSyncAdapter(
            new TradeOperationsPersistenceService(
                indexedDb,
                new TradeCompanyProfilePackageService()),
            store,
            localState);

        await adapter.ApplyRemoteObjectAsync(Envelope(order, 5), default);

        Assert.Same(order, runtime.DurableOrder);
        Assert.Equal(1, runtime.SaveTradeOrderCount);
    }

    private static async Task AdapterTombstoneReconcilesNewerLiveOrderAfterBlockedDelete()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var companyId = Guid.NewGuid();
        var order = CreateOrder(Guid.NewGuid(), companyId, "Revision four");
        var revisionSix = CreateOrder(order.Id, companyId, "Revision six");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            DateTime.UtcNow,
            ConnectionScope(profileId));
        Assert.True(store.TryPublishRemoteOrder(order, 4));
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        runtime.AddCompany(companyId);
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(
            indexedDb,
            new ProfileHostClientOptions(Host));
        await localState.LoadConnectionSettingsAsync();
        var adapter = new TradeOrderProfileSyncAdapter(
            new TradeOperationsPersistenceService(
                indexedDb,
                new TradeCompanyProfilePackageService()),
            store,
            localState);
        await adapter.ApplyRemoteObjectAsync(Envelope(order, 4), default);
        var deleteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.BeforeDeleteTradeOrderAsync = async _ =>
        {
            deleteEntered.SetResult();
            await releaseDelete.Task;
        };

        var deletion = adapter.ApplyRemoteDeletionAsync(
            order.Id,
            companyId,
            5,
            default);
        await deleteEntered.Task;
        Assert.True(store.TryPublishRemoteOrder(revisionSix, 6));
        releaseDelete.SetResult();
        await deletion;

        Assert.Equal("Revision six", runtime.DurableOrder?.Title);
        Assert.Equal(6, store.Get(order.Id)?.ObjectRevision);
        Assert.Equal(
            6,
            await localState.LoadObjectRevisionAsync(
                profileId,
                ProfileSyncCollections.TradeOrders,
                order.Id.ToString("D")));
    }

    private static async Task CollaborationReconcilesDurableWinnerAfterOlderWriteFinishesLast()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Revision four");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            DateTime.UtcNow,
            ConnectionScope(profileId));
        Assert.True(store.TryPublishRemoteOrder(order, 4));
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var firstWriteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = false;
        runtime.BeforeSaveTradeOrderAsync = async candidate =>
        {
            if (!blocked && candidate.Title == "Revision five")
            {
                blocked = true;
                firstWriteEntered.SetResult();
                await releaseFirstWrite.Task;
            }
        };
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
        var revisionFive = CreatePublishedOrder(order, revision: 4);
        revisionFive.Title = "Revision five";
        var revisionSix = CreatePublishedOrder(order, revision: 4);
        revisionSix.Title = "Revision six";
        var persistence = new TradeOperationsPersistenceService(
            indexedDb,
            new TradeCompanyProfilePackageService());
        var olderService = CreateCollaboration(
            revisionFive,
            5,
            localState,
            profileSync,
            persistence,
            store);
        var newerService = CreateCollaboration(
            revisionSix,
            6,
            localState,
            profileSync,
            persistence,
            store);

        var older = olderService.PublishPortableLinkAsync(order, new CommissionBriefDocument());
        await firstWriteEntered.Task;
        var newer = newerService.PublishPortableLinkAsync(order, new CommissionBriefDocument());
        await newer;
        releaseFirstWrite.SetResult();
        await older;

        Assert.Equal("Revision six", runtime.DurableOrder?.Title);
        Assert.Equal(
            6,
            await localState.LoadObjectRevisionAsync(
                profileId,
                ProfileSyncCollections.TradeOrders,
                order.Id.ToString("D")));
    }

    private static async Task ConnectionScopeChangeDuringPersistenceCannotWriteReplacementRevisionNamespace()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Revision four");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            DateTime.UtcNow,
            ConnectionScope(profileId));
        Assert.True(store.TryPublishRemoteOrder(order, 4));
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var switched = false;
        runtime.BeforeSaveTradeOrderAsync = candidate =>
        {
            if (!switched && candidate.Title == "Revision five")
            {
                switched = true;
                runtime.SaveRawSetting(
                    ProfileSyncSettingsKeys.HostUrl,
                    JsonSerializer.Serialize("https://replacement.example/api/"));
            }
            return Task.CompletedTask;
        };
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
        committed.Title = "Revision five";
        var service = CreateCollaboration(
            committed,
            5,
            localState,
            profileSync,
            new TradeOperationsPersistenceService(
                indexedDb,
                new TradeCompanyProfilePackageService()),
            store);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishPortableLinkAsync(order, new CommissionBriefDocument()));

        Assert.Contains("authority", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Revision five", runtime.DurableOrder?.Title);
        Assert.Equal(5, store.Get(order.Id)?.ObjectRevision);
        Assert.Equal(
            0,
            await localState.LoadObjectRevisionAsync(
                profileId,
                ProfileSyncCollections.TradeOrders,
                order.Id.ToString("D")));
    }

    private static void OwnerProjectionIsPreservedThenClearedAndRehydratedByRevision()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Owner revision four");
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            DateTime.UtcNow,
            ConnectionScope(profileId));
        var ownerFour = Owner(order, 4, 8);
        Assert.True(store.TryPublishOwner(ownerFour));
        var authority = store.CaptureAuthorityScope();

        Assert.Equal(
            HostedOrderCommittedProjectionResult.AlreadyCurrent,
            store.TryAdoptCommittedOrder(authority, order, 4));
        Assert.Same(ownerFour, store.GetOwnerProjection(order.Id));

        var revisionFive = CreateOrder(order.Id, order.CompanyProfileId, "Owner pending");
        Assert.Equal(
            HostedOrderCommittedProjectionResult.Adopted,
            store.TryAdoptCommittedOrder(authority, revisionFive, 5));
        Assert.Null(store.GetOwnerProjection(order.Id));

        var ownerFive = Owner(revisionFive, 5, 9);
        Assert.True(store.TryPublishOwner(ownerFive));
        Assert.Same(ownerFive, store.GetOwnerProjection(order.Id));
    }

    private static DeletionFixture CreateDeletionFixture(
        string profileId,
        TradeOrder order,
        long responseRevision,
        Action? beforeDeleteResponse = null)
    {
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            false,
            0,
            DateTime.UtcNow,
            ConnectionScope(profileId));
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

    private static TradeCompanyCollaborationService CreateCollaboration(
        TradeOrder committed,
        long revision,
        ProfileSyncLocalStateService localState,
        ProfileSyncService profileSync,
        TradeOperationsPersistenceService persistence,
        HostedOrderProjectionStore store) =>
        new(
            new TradeCompanyCollaborationClient(
                new HttpClient(new PortablePublicationHandler(
                    committed,
                    revision,
                    () => { }))
                {
                    BaseAddress = new Uri(Host)
                },
                localState),
            persistence,
            localState,
            profileSync,
            store);

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

    private static string ConnectionScope(string profileId) =>
        $"{ProfileHostClient.NormalizeHostUrl(Host)}|{profileId}";

    private static TradeOrder CreateOrder(Guid orderId, Guid companyProfileId, string title) =>
        new()
        {
            Id = orderId,
            CompanyProfileId = companyProfileId,
            Title = title
        };

    private static CompanyCommissionOwnerProjection Owner(
        TradeOrder order,
        long objectRevision,
        long companyRevision) =>
        new()
        {
            Order = order,
            ObjectRevision = new CompanyRecordRevision(objectRevision),
            CompanyRevision = new CompanyRecordRevision(companyRevision)
        };

    private static TradeOrder CreatePublishedOrder(TradeOrder source, long revision)
    {
        var published = TradeOrderWorkflow.CopyOrder(source);
        published.CommissionPublication = new TradeCommissionPublication
        {
            PublicId = "public-id",
            PublicUrl = "https://profiles.example/brief?id=public-id",
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

    private sealed class RecordingOrderAdapter(ProfileSyncObjectEnvelope? local)
        : IProfileSyncCollectionAdapter
    {
        public string Collection => ProfileSyncCollections.TradeOrders;
        public int DeleteCount { get; private set; }
        public Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProfileSyncObjectEnvelope>>(
                local == null ? [] : [local]);
        public Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
        {
            DeleteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RevisionZeroDeletionHandler(Guid orderId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/profile-host/changes"))
            {
                throw new NotSupportedException(request.RequestUri.ToString());
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ProfileSyncChangesResponse
                {
                    ServerRevision = 1,
                    HasMore = false,
                    Objects =
                    [
                        new ProfileSyncObjectEnvelope
                        {
                            Collection = ProfileSyncCollections.TradeOrders,
                            ObjectId = orderId.ToString("D"),
                            Revision = 1,
                            Deleted = true,
                            UpdatedAtUtc = DateTime.UtcNow
                        }
                    ]
                })
            });
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
                EditorToken = string.Empty,
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
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier == "IndexedDB.saveTradeOrder")
            {
                return SaveTradeOrderAsync<TValue>((TradeOrder)args![0]!);
            }
            if (identifier == "IndexedDB.deleteTradeOrder")
            {
                return DeleteTradeOrderAsync<TValue>((Guid)args![0]!);
            }
            object? result = identifier switch
            {
                "IndexedDB.loadAllSettings" => new Dictionary<string, string>(settings, StringComparer.Ordinal),
                "IndexedDB.loadSetting" => settings.GetValueOrDefault((string)args![0]!),
                "IndexedDB.loadTradeCompanyProfiles" => _companyIds
                    .Select(companyId => new TradeCompanyProfile
                    {
                        Id = companyId,
                        Name = "Test company"
                    })
                    .ToList(),
                "IndexedDB.saveSettingsBatch" => SaveBatch((Dictionary<string, string>)args![0]!),
                "IndexedDB.saveSetting" => SaveSetting((string)args![0]!, (string)args[1]!),
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

        private async ValueTask<TValue> SaveTradeOrderAsync<TValue>(TradeOrder order)
        {
            if (BeforeSaveTradeOrderAsync != null)
            {
                await BeforeSaveTradeOrderAsync(order);
            }
            SaveTradeOrderCount++;
            DurableOrder = order;
            return (TValue)(object)true;
        }

        private async ValueTask<TValue> DeleteTradeOrderAsync<TValue>(Guid orderId)
        {
            if (BeforeDeleteTradeOrderAsync != null)
            {
                await BeforeDeleteTradeOrderAsync(orderId);
            }
            if (DurableOrder?.Id == orderId)
            {
                DurableOrder = null;
            }
            return (TValue)(object)true;
        }
    }
}
