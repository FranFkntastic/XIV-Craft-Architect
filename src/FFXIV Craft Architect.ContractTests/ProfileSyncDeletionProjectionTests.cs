using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
using Microsoft.JSInterop;
namespace FFXIV_Craft_Architect.ContractTests;

public sealed class ProfileSyncDeletionProjectionTests
{
    private const string Host = "https://profiles.example/api/";
    [Fact]
    public async Task TombstoneMarkerReapsZombieRewriteAndBlocksStaleReplay()
    {
        var profileId = NewId();
        var companyProfileId = Guid.NewGuid();
        var order = CreateOrder(Guid.NewGuid(), companyProfileId, "Doomed draft");
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        runtime.AddCompany(companyProfileId);
        var indexedDb = new IndexedDbService(runtime);
        var localState = CreateLocalState(indexedDb);
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(profileId, false, 0, DateTime.UtcNow, ConnectionScope(profileId));
        var persistence = new TradeOperationsPersistenceService(
            indexedDb,
            new TradeCompanyProfilePackageService(),
            new TradeOrderArchiveSummaryStore(indexedDb));
        var adapter = new TradeOrderProfileSyncAdapter(
            persistence,
            store,
            localState,
            new TradeOrderArchiveSummaryStore(indexedDb));

        await adapter.ApplyRemoteObjectAsync(Envelope(order, 4), CancellationToken.None);
        await adapter.ApplyRemoteDeletionAsync(order.Id, companyProfileId, 6, CancellationToken.None);
        Assert.Empty(await persistence.LoadOrdersAsync(companyProfileId));

        await persistence.ApplyCanonicalOrderAsync(order);
        Assert.Single(await persistence.LoadOrdersAsync(companyProfileId));

        runtime.ResetReadCounts();
        await adapter.ReapResurrectedOrdersAsync(profileId, CancellationToken.None);
        Assert.Empty(await persistence.LoadOrdersAsync(companyProfileId));
        Assert.Equal(1, runtime.LoadTradeOrderCount);
        Assert.Equal(0, runtime.LoadTradeCompanyProfilesCount);

        await adapter.ApplyRemoteObjectAsync(Envelope(order, 5), CancellationToken.None);
        Assert.Empty(await persistence.LoadOrdersAsync(companyProfileId));

        await adapter.ApplyRemoteObjectAsync(Envelope(order, 8), CancellationToken.None);
        var revived = Assert.Single(await persistence.LoadOrdersAsync(companyProfileId));
        Assert.Equal(order.Id, revived.Id);
        Assert.Empty(await localState.LoadOrderTombstonesAsync(profileId));
    }

    [Fact]
    public async Task ObjectRevisionBatchReadsKnownValuesWithOneSettingsSnapshot()
    {
        var profileId = NewId();
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var localState = CreateLocalState(new IndexedDbService(runtime));
        var first = Guid.NewGuid().ToString("D");
        var second = Guid.NewGuid().ToString("D");

        await localState.SaveObjectRevisionAsync(
            profileId,
            ProfileSyncCollections.TradeOrders,
            first,
            4);
        await localState.SaveObjectRevisionAsync(
            profileId,
            ProfileSyncCollections.TradeOrders,
            second,
            7);

        runtime.ResetReadCounts();
        var revisions = await localState.LoadObjectRevisionsAsync(
            profileId,
            ProfileSyncCollections.TradeOrders,
            [first, second]);

        Assert.Equal(4, revisions[first]);
        Assert.Equal(7, revisions[second]);
        Assert.Equal(1, runtime.LoadAllSettingsCount);
        Assert.Equal(0, runtime.LoadSettingCount);
    }

    [Fact]
    public async Task TradeOrderHydrationReadsTheOrderStoreOnce()
    {
        var profileId = NewId();
        var companyProfileId = Guid.NewGuid();
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        runtime.AddCompany(companyProfileId);
        runtime.AddCompany(Guid.NewGuid());
        runtime.SeedOrder(CreateOrder(Guid.NewGuid(), companyProfileId, "Cached order"));
        var indexedDb = new IndexedDbService(runtime);
        var adapter = new TradeOrderProfileSyncAdapter(
            new TradeOperationsPersistenceService(
                indexedDb,
                new TradeCompanyProfilePackageService()),
            new HostedOrderProjectionStore(),
            CreateLocalState(indexedDb));

        var objects = await adapter.LoadLocalObjectsAsync(CancellationToken.None);

        Assert.Single(objects);
        Assert.Equal(1, runtime.LoadAllTradeOrdersCount);
        Assert.Equal(0, runtime.LoadTradeOrdersCount);
        Assert.Equal(0, runtime.LoadTradeCompanyProfilesCount);
    }

    [Fact]
    public async Task ArchivedOrderSummaryPersistsRevisionWithoutPublishingFullOrder()
    {
        var profileId = NewId();
        var companyProfileId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        runtime.AddCompany(companyProfileId);
        var indexedDb = new IndexedDbService(runtime);
        var localState = CreateLocalState(indexedDb);
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(profileId, false, 0, DateTime.UtcNow, ConnectionScope(profileId));
        var summaries = new TradeOrderArchiveSummaryStore(indexedDb);
        var adapter = new TradeOrderProfileSyncAdapter(
            new TradeOperationsPersistenceService(
                indexedDb,
                new TradeCompanyProfilePackageService(),
                summaries),
            store,
            localState,
            summaries);
        var summary = new TradeOrderArchiveSummary
        {
            OrderId = orderId,
            CompanyProfileId = companyProfileId,
            Title = "Archived order",
            Status = TradeOrderStatus.Completed,
            CommissionedAtUtc = DateTime.UtcNow,
            Outputs = [new("Tacos de Carne Asada", 3, true)]
        };

        await adapter.ApplyRemoteObjectAsync(new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.TradeOrders,
            ObjectId = orderId.ToString("D"),
            PayloadJson = string.Empty,
            SummaryJson = TradeOrderArchiveSummaryCodec.Serialize(summary),
            Revision = 7
        }, CancellationToken.None);

        var persisted = Assert.Single(await new TradeOrderArchiveSummaryStore(indexedDb).LoadAsync());
        Check(
            () => Assert.Equal(7, persisted.HostedRevision),
            () => Assert.Equal("Archived order", persisted.Summary.Title),
            () => Assert.Null(store.Get(orderId)),
            () => Assert.Null(runtime.DurableOrder));
        Assert.Equal(7, await localState.LoadObjectRevisionAsync(
            profileId,
            ProfileSyncCollections.TradeOrders,
            orderId.ToString("D")));
    }

    [Fact]
    public async Task FullArchivedOrderSupersedesStoredSummary()
    {
        var profileId = NewId();
        var companyProfileId = Guid.NewGuid();
        var order = CreateOrder(Guid.NewGuid(), companyProfileId, "Full archived order");
        order.Status = TradeOrderStatus.Completed;
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        runtime.AddCompany(companyProfileId);
        var indexedDb = new IndexedDbService(runtime);
        var localState = CreateLocalState(indexedDb);
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(profileId, false, 0, DateTime.UtcNow, ConnectionScope(profileId));
        var summaries = new TradeOrderArchiveSummaryStore(indexedDb);
        await summaries.UpsertAsync(new TradeOrderArchiveSummary
        {
            OrderId = order.Id,
            CompanyProfileId = companyProfileId,
            Title = "Summary title",
            Status = TradeOrderStatus.Completed
        }, 5, ConnectionScope(profileId));
        var adapter = new TradeOrderProfileSyncAdapter(
            new TradeOperationsPersistenceService(
                indexedDb,
                new TradeCompanyProfilePackageService(),
                summaries),
            store,
            localState,
            summaries);

        await adapter.ApplyRemoteObjectAsync(Envelope(order, 4), CancellationToken.None);
        Assert.Single(summaries.GetAll(ConnectionScope(profileId)));
        await adapter.ApplyRemoteObjectAsync(Envelope(order, 6), CancellationToken.None);

        Check(
            () => Assert.Empty(summaries.GetAll(ConnectionScope(profileId))),
            () => Assert.Equal(order.Title, store.Get(order.Id)?.Order?.Title),
            () => Assert.Equal(order.Id, runtime.DurableOrder?.Id));
    }

    [Fact]
    public async Task LocalOrderDeletionDropsStoredArchiveSummary()
    {
        var profileId = NewId();
        var otherProfileId = NewId();
        var orderId = Guid.NewGuid();
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var indexedDb = new IndexedDbService(runtime);
        var summaries = new TradeOrderArchiveSummaryStore(indexedDb);
        await summaries.UpsertAsync(new TradeOrderArchiveSummary
        {
            OrderId = orderId,
            CompanyProfileId = Guid.NewGuid(),
            Title = "Deleted archive",
            Status = TradeOrderStatus.Canceled
        }, 3, ConnectionScope(profileId));
        await summaries.UpsertAsync(new TradeOrderArchiveSummary
        {
            OrderId = orderId,
            CompanyProfileId = Guid.NewGuid(),
            Title = "Other profile archive",
            Status = TradeOrderStatus.Completed
        }, 4, ConnectionScope(otherProfileId));
        var persistence = new TradeOperationsPersistenceService(
            indexedDb,
            new TradeCompanyProfilePackageService(),
            summaries,
            CreateLocalState(indexedDb));

        Assert.True(await persistence.DeleteOrderAsync(orderId));
        Assert.Empty(summaries.GetAll(ConnectionScope(profileId)));
        Assert.Single(summaries.GetAll(ConnectionScope(otherProfileId)));
    }

    [Fact]
    public async Task CommittedOrderPutsAdoptWithoutAdvancingCursor()
    {
        foreach (var conflictFirst in new[] { false, true })
        {
            var f = CreatePutAdoptionFixture(conflictFirst); await f.Service.QueueLocalSaveAsync(ProfileSyncCollections.TradeOrders, Key(f.LocalOrder));
            await (conflictFirst ? f.Service.KeepLocalConflictAsync(Assert.Single(f.Service.Conflicts)) : Task.CompletedTask);
            var projection = f.Store.Get(f.LocalOrder.Id); Check(() => Assert.Equal(f.CommittedOrder.Title, projection?.Order?.Title), () => Assert.Equal(f.CommittedRevision, projection?.ObjectRevision), () => Assert.Equal(f.RetainedOrder.Title, f.Store.Get(f.RetainedOrder.Id)?.Order?.Title), () => Assert.True(f.Store.RestoreState.IsAuthoritative), () => Assert.Equal(f.CommittedOrder.Title, f.Runtime.DurableOrder?.Title), () => Assert.Equal(0, f.Service.CurrentStatus.LastSyncRevision), () => Assert.Empty(f.Service.PendingSaves), () => Assert.Empty(f.Service.Conflicts));
            Assert.Equal(f.CommittedRevision, await f.LocalState.LoadObjectRevisionAsync(f.ProfileId, ProfileSyncCollections.TradeOrders, Key(f.LocalOrder))); Assert.Equal(0, await f.LocalState.LoadLastSyncRevisionAsync(f.ProfileId));
        }
    }

    [Fact]
    public async Task ImmediateOrderSaveUsesOneTargetedLocalLookup()
    {
        var fixture = CreatePutAdoptionFixture(conflictFirst: false);
        fixture.Runtime.ResetReadCounts();

        await fixture.Service.QueueLocalSaveAsync(
            ProfileSyncCollections.TradeOrders,
            Key(fixture.LocalOrder));

        Assert.Equal(1, fixture.Runtime.LoadTradeOrderCount);
        Assert.Equal(0, fixture.Runtime.LoadTradeOrdersCount);
    }
    [Fact]
    public async Task ConfirmedDeletionColdStartsScopedTombstoneAndDeletesLocalOrder()
    {
        var profileId = NewId();
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Retiring order");
        var fixture = CreateDeletionFixture(profileId, order, 5);
        await fixture.Service.DeleteObjectAsync(ProfileSyncCollections.TradeOrders, Key(order));
        var tombstone = Assert.Single(fixture.Store.GetAll(order.CompanyProfileId));
        Check(() => Assert.Equal(order.Id, tombstone.OrderId), () => Assert.Equal(5, tombstone.ObjectRevision), () => Assert.True(tombstone.Deleted), () => Assert.Equal(1, fixture.Adapter.DeleteCount), () => Assert.Contains(TradeOrderWorkspaceCompositionPolicy.GetDeviceOnlyOrders([order], fixture.Store.GetAll(order.CompanyProfileId)), candidate => candidate.Id == order.Id));
    }
    [Fact]
    public async Task RevisionZeroDeletionWithoutLocalIdentityAdvancesWithoutInventingTenant()
    {
        var profileId = NewId();
        var orderId = Guid.NewGuid();
        var store = new HostedOrderProjectionStore();
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var indexedDb = new IndexedDbService(runtime);
        var localState = CreateLocalState(indexedDb);
        var adapter = new RecordingOrderAdapter(null);
        var service = new ProfileSyncService(
            CreateHostClient(RevisionZeroDeletionHandler(orderId)), localState,
            new WebSettingsService(indexedDb), store, [adapter]);
        await service.InitializeAsync();
        Check(() => Assert.Equal(1, service.CurrentStatus.LastSyncRevision), () => Assert.Equal(0, adapter.DeleteCount), () => Assert.Empty(store.GetAll()));
    }

    [Fact]
    public async Task MissingCompanyQuarantinesOnlyItsDependentOrder()
    {
        var profileId = NewId();
        var missingCompanyId = Guid.NewGuid();
        var orphanedOrder = CreateOrder(Guid.NewGuid(), missingCompanyId, "Orphaned order");
        var secondOrphanedOrder = CreateOrder(Guid.NewGuid(), missingCompanyId, "Second orphaned order");
        var currentOrder = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Current order");
        var orphanedEnvelope = Envelope(orphanedOrder, 1);
        var secondOrphanedEnvelope = Envelope(secondOrphanedOrder, 2);
        var currentEnvelope = Envelope(currentOrder, 3);
        var orderCollections = string.Join(",", ProfileSyncCollections.OrderAuthorityScope);
        var backgroundCollections = string.Join(",", ProfileSyncCollections.BackgroundScope);
        var recoveryRequestCount = 0;
        var handler = new StubHandler(request =>
        {
            Assert.EndsWith("/profile-host/changes", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            var collections = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["collections"];
            if (string.Equals(collections, orderCollections, StringComparison.Ordinal))
            {
                return Ok(new ProfileSyncChangesResponse
                {
                    Objects = [orphanedEnvelope, secondOrphanedEnvelope, currentEnvelope],
                    ServerRevision = 3
                });
            }
            if (collections == null)
            {
                recoveryRequestCount++;
                return Ok(new ProfileSyncChangesResponse { ServerRevision = 3 });
            }
            if (string.Equals(collections, backgroundCollections, StringComparison.Ordinal))
            {
                return Ok(new ProfileSyncChangesResponse { ServerRevision = 3 });
            }
            throw new InvalidOperationException($"Unexpected collection filter '{collections}'.");
        });
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var indexedDb = new IndexedDbService(runtime);
        var localState = CreateLocalState(indexedDb);
        var adapter = new MissingCompanyOrderAdapter(
            new HashSet<Guid> { orphanedOrder.Id, secondOrphanedOrder.Id },
            missingCompanyId);
        var service = new ProfileSyncService(
            CreateHostClient(handler),
            localState,
            new WebSettingsService(indexedDb),
            new HostedOrderProjectionStore(),
            [adapter, new EmptyCollectionAdapter(ProfileSyncCollections.Plans)]);

        await service.InitializeAsync();

        Check(
            () => Assert.Contains(currentOrder.Id, adapter.AppliedOrderIds),
            () => Assert.DoesNotContain(orphanedOrder.Id, adapter.AppliedOrderIds),
            () => Assert.DoesNotContain(secondOrphanedOrder.Id, adapter.AppliedOrderIds),
            () => Assert.Equal(3, service.CurrentStatus.LastSyncRevision),
            () => Assert.Equal(1, recoveryRequestCount),
            () => Assert.True(
                service.CurrentStatus.OrderScopeReady,
                JsonSerializer.Serialize(service.CurrentStatus)),
            () => Assert.True(service.CurrentStatus.HostReachable),
            () => Assert.Empty(service.PendingSaves),
            () => Assert.Empty(service.Conflicts));
        Assert.Equal(1, await localState.LoadObjectRevisionAsync(
            profileId,
            ProfileSyncCollections.TradeOrders,
            Key(orphanedOrder)));
        Assert.Equal(2, await localState.LoadObjectRevisionAsync(
            profileId,
            ProfileSyncCollections.TradeOrders,
            Key(secondOrphanedOrder)));
    }
    private static async Task ExpectedRevisionRefusesChangedOrderBeforeDeletion()
    {
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Published winner");
        var fixture = CreateDeletionFixture(NewId(), order, 5);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DeleteObjectsAsync([(ProfileSyncCollections.TradeOrders, Key(order))], [new ProfileSyncDeleteExpectation(ProfileSyncCollections.TradeOrders, Key(order), Revision: 3)]));
        Check(() => Assert.Contains("changed before deletion", failure.Message, StringComparison.OrdinalIgnoreCase), () => Assert.Equal(0, fixture.Adapter.DeleteCount));
        var profileId = NewId(); var plan = new ProfileSyncObjectEnvelope { Collection = ProfileSyncCollections.Plans, ObjectId = "generated-plan", Revision = 2 }; var remoteOrder = Envelope(order, 4); var deletions = new List<string>();
        var runtime = new StorageRuntime(ConnectionSettings(profileId)); var indexedDb = new IndexedDbService(runtime); var localState = CreateLocalState(indexedDb); var store = new HostedOrderProjectionStore();
        var service = new ProfileSyncService(CreateHostClient(new StubHandler(request => request.Method == HttpMethod.Get ? Ok(new ProfileHostBootstrapPayload { Objects = [plan, remoteOrder] }) : RecordConflict(request, deletions))), localState, new WebSettingsService(indexedDb), store, [new RecordingOrderAdapter(remoteOrder), new RecordingCollectionAdapter(ProfileSyncCollections.Plans, plan)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteObjectsAsync([(ProfileSyncCollections.Plans, plan.ObjectId), (ProfileSyncCollections.TradeOrders, Key(order))], [new ProfileSyncDeleteExpectation(ProfileSyncCollections.TradeOrders, Key(order), 4)]));
        Assert.Contains($"/{ProfileSyncCollections.TradeOrders}/", Assert.Single(deletions), StringComparison.Ordinal); Assert.Empty(await localState.LoadPendingOrderCleanupAsync(profileId));
        var retryProfileId = NewId(); var retryOrder = Envelope(order, 4); var retryOrderTombstone = Tombstone(Key(order), 5); var retryPlanTombstone = Tombstone(plan.ObjectId, 6, ProfileSyncCollections.Plans); var retryCall = 0; var retryBootstrapCall = 0; var retryPlanDeleteCall = 0; var failLocalPlanDelete = true;
        var retryRuntime = new StorageRuntime(ConnectionSettings(retryProfileId)); var retryIndexedDb = new IndexedDbService(retryRuntime); var retryLocalState = CreateLocalState(retryIndexedDb); var retryStore = new HostedOrderProjectionStore(); retryStore.BeginProfileRestore(retryProfileId, false, 0, DateTime.UtcNow, ConnectionScope(retryProfileId)); var retryOrderAdapter = new RecordingOrderAdapter(retryOrder);
        var retryHandler = new StubHandler(request =>
        {
            retryCall++;
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get &&
                path.EndsWith("/profile-host/changes", StringComparison.Ordinal))
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var collections = query["collections"];
                if (string.Equals(
                        collections,
                        string.Join(",", ProfileSyncCollections.OrderAuthorityScope),
                        StringComparison.Ordinal))
                {
                    return Ok(new ProfileSyncChangesResponse
                    {
                        Objects = [retryOrderTombstone],
                        ServerRevision = 5
                    });
                }
                if (string.Equals(
                        collections,
                        string.Join(",", ProfileSyncCollections.BackgroundScope),
                        StringComparison.Ordinal))
                {
                    return Ok(new ProfileSyncChangesResponse { ServerRevision = 5 });
                }
                throw new InvalidOperationException(
                    $"Unexpected changes collection filter '{collections}'.");
            }
            if (request.Method == HttpMethod.Get &&
                path.EndsWith("/profile-host/bootstrap/export", StringComparison.Ordinal))
            {
                return ++retryBootstrapCall switch
                {
                    1 => Ok(new ProfileHostBootstrapPayload { Objects = [plan, retryOrder] }),
                    2 => Ok(new ProfileHostBootstrapPayload { Objects = [plan, retryOrderTombstone] }),
                    3 => Ok(new ProfileHostBootstrapPayload { Objects = [retryPlanTombstone, retryOrderTombstone] }),
                    _ => throw new InvalidOperationException(
                        $"Unexpected retry bootstrap request {retryBootstrapCall}.")
                };
            }
            if (request.Method == HttpMethod.Delete &&
                path.Contains($"/{ProfileSyncCollections.TradeOrders}/", StringComparison.Ordinal))
            {
                return Ok(new ProfileSyncPutResponse
                {
                    Success = true,
                    ServerRevision = 5,
                    Object = retryOrderTombstone
                });
            }
            if (request.Method == HttpMethod.Delete &&
                path.Contains($"/{ProfileSyncCollections.Plans}/", StringComparison.Ordinal))
            {
                return ++retryPlanDeleteCall switch
                {
                    1 => Ok(new ProfileSyncPutResponse { Conflict = true }),
                    2 => Ok(new ProfileSyncPutResponse
                    {
                        Success = true,
                        ServerRevision = 6,
                        Object = retryPlanTombstone
                    }),
                    _ => throw new InvalidOperationException(
                        $"Unexpected retry plan deletion {retryPlanDeleteCall}.")
                };
            }
            throw new InvalidOperationException(
                $"Unexpected retry request {retryCall}: {request.Method} {request.RequestUri}.");
        }); var retryService = new ProfileSyncService(CreateHostClient(retryHandler), retryLocalState, new WebSettingsService(retryIndexedDb), retryStore, [retryOrderAdapter, new RecordingCollectionAdapter(ProfileSyncCollections.Plans, plan)]);
        var retryObjects = new[] { (ProfileSyncCollections.Plans, plan.ObjectId), (ProfileSyncCollections.TradeOrders, Key(order)) }; var retryExpectation = new[] { new ProfileSyncDeleteExpectation(ProfileSyncCollections.TradeOrders, Key(order), 4) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => retryService.DeleteObjectsAsync(retryObjects, retryExpectation)); Assert.Contains(Key(order), await retryLocalState.LoadPendingOrderCleanupAsync(retryProfileId));
        var reloadedService = new ProfileSyncService(CreateHostClient(retryHandler), retryLocalState, new WebSettingsService(retryIndexedDb), retryStore, [retryOrderAdapter, new RecordingCollectionAdapter(ProfileSyncCollections.Plans, plan)]); await reloadedService.SyncNowAsync(); Check(() => Assert.Equal(5, retryCall), () => Assert.Equal(0, retryOrderAdapter.DeleteCount), () => Assert.Equal(4, retryLocalState.LoadObjectRevisionAsync(retryProfileId, ProfileSyncCollections.TradeOrders, Key(order)).GetAwaiter().GetResult())); var secondStore = new HostedOrderProjectionStore(); var secondReloadedService = new ProfileSyncService(CreateHostClient(retryHandler), retryLocalState, new WebSettingsService(retryIndexedDb), secondStore, [retryOrderAdapter, new RecordingCollectionAdapter(ProfileSyncCollections.Plans, plan, () => failLocalPlanDelete ? (failLocalPlanDelete = false, Task.FromException(new InvalidOperationException("local plan delete failed"))).Item2 : Task.CompletedTask)]); await secondReloadedService.SyncNowAsync();
        Check(() => Assert.Equal(7, retryCall), () => Assert.Equal(4, secondStore.Get(order.Id)?.ObjectRevision), () => Assert.Equal(0, retryOrderAdapter.DeleteCount)); await Assert.ThrowsAsync<InvalidOperationException>(() => secondReloadedService.DeleteObjectsAsync(retryObjects)); Check(() => Assert.Equal(9, retryCall), () => Assert.Equal(0, retryOrderAdapter.DeleteCount)); Assert.Contains(Key(order), await retryLocalState.LoadPendingOrderCleanupAsync(retryProfileId)); var thirdReloadedService = new ProfileSyncService(CreateHostClient(retryHandler), retryLocalState, new WebSettingsService(retryIndexedDb), secondStore, [retryOrderAdapter, new RecordingCollectionAdapter(ProfileSyncCollections.Plans, retryPlanTombstone)]); await thirdReloadedService.DeleteObjectsAsync(retryObjects); Check(() => Assert.Equal(10, retryCall), () => Assert.Equal(1, retryOrderAdapter.DeleteCount), () => Assert.True(secondStore.Get(order.Id)?.Deleted)); Assert.Empty(await retryLocalState.LoadPendingOrderCleanupAsync(retryProfileId));
    }
    [Fact]
    public async Task DelayedStaleDeletionCannotOverwriteOrDeleteNewerProjection()
    {
        await ExpectedRevisionRefusesChangedOrderBeforeDeletion();
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
        Check(() => Assert.Same(newer, fixture.Store.Get(order.Id)?.Order), () => Assert.Equal(6, fixture.Store.Get(order.Id)?.ObjectRevision), () => Assert.Equal(0, fixture.Adapter.DeleteCount));
    }
    [Fact]
    public async Task ConfirmedDeletionCannotCrossCompanyIdentity()
    {
        var remoteOrder = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Remote company");
        var otherCompanyOrder = CreateOrder(remoteOrder.Id, Guid.NewGuid(), "Other company");
        var fixture = CreateDeletionFixture(NewId(), remoteOrder, 5);
        fixture.Store.TryPublishRemoteOrder(otherCompanyOrder, 4);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DeleteObjectAsync(ProfileSyncCollections.TradeOrders, Key(remoteOrder)));
        Check(() => Assert.Same(otherCompanyOrder, fixture.Store.Get(remoteOrder.Id)?.Order), () => Assert.Empty(fixture.Store.GetAll(remoteOrder.CompanyProfileId)), () => Assert.Equal(0, fixture.Adapter.DeleteCount));
    }
    [Fact]
    public async Task CollaborationResponseAfterProfileSwitchCannotPublishOrPersist()
    {
        var fixture = await Ready();
        var collaboration = fixture.CreateCollaboration(5, () => ChangeProfile(fixture));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collaboration.PublishPortableLinkAsync(fixture.Order, new CommissionBriefDocument()));
        Check(() => Assert.Equal(0, fixture.Runtime.SaveTradeOrderCount), () => Assert.Null(fixture.Store.Get(fixture.Order.Id)));

        var publishFixture = await Ready();
        var publish = publishFixture.CreateUnusedCollaboration(new StubHandler(request => Publication(request, publishFixture, () => ChangeProfile(publishFixture))));
        var publishResult = await publish.PublishToDiscordAsync(publishFixture.Order, new CommissionBriefDocument());
        Check(() => Assert.False(publishResult.Success), () => Assert.Contains("authority", publishResult.Message!, StringComparison.OrdinalIgnoreCase), () => Assert.Null(publish.GetPublication(publishFixture.Order.Id)));

        var retryFixture = await Ready();
        var retry = retryFixture.CreateUnusedCollaboration(new StubHandler(request => Publication(request, retryFixture, retryFixture.ReplaceHost)));
        var retryResult = await retry.RetryDiscordPublicationAsync(retryFixture.Order, "public-id");
        Check(() => Assert.False(retryResult.Success), () => Assert.Contains("authority", retryResult.Message!, StringComparison.OrdinalIgnoreCase), () => Assert.Null(retry.GetPublication(retryFixture.Order.Id)));

        var portableFixture = await Ready();
        var portable = portableFixture.CreateUnusedCollaboration(new StubHandler(request => Revoked(request, portableFixture, () => ChangeProfile(portableFixture))));
        var portableFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => portable.RevokePortableLinkAsync(portableFixture.Order, "public-id"));
        Assert.Contains("authority", portableFailure.Message, StringComparison.OrdinalIgnoreCase);

        var refreshFixture = await Ready();
        var refresh = refreshFixture.CreateUnusedCollaboration(new StubHandler(request => Publication(request, refreshFixture, () => ChangeProfile(refreshFixture))));
        var refreshFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => refresh.RefreshAsync(refreshFixture.Order.CompanyProfileId, refreshFixture.Order.Id));
        Check(() => Assert.Contains("authority", refreshFailure.Message, StringComparison.OrdinalIgnoreCase), () => Assert.Null(refresh.GetPublication(refreshFixture.Order.Id)));

        var ensureFixture = await Ready();
        var captured = (await ensureFixture.LocalState.LoadConnectionSettingsAsync()).Snapshot();
        ensureFixture.ReplaceHost();
        var ensureFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => ensureFixture.ProfileSync.EnsureHostedObjectRevisionAsync(ProfileSyncCollections.TradeOrders, Key(ensureFixture.Order), captured));
        Assert.Contains("authority", ensureFailure.Message, StringComparison.OrdinalIgnoreCase);

        var revokeFixture = await Ready();
        var revoke = revokeFixture.CreateUnusedCollaboration(new StubHandler(request => request.Method == HttpMethod.Delete ? Revoked(request, revokeFixture, revokeFixture.ReplaceHost) : Publication(request, revokeFixture, () => { })));
        var initial = await revoke.PublishToDiscordAsync(revokeFixture.Order, new CommissionBriefDocument());
        var revokeFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => revoke.RevokePublicationAsync(new(new(revokeFixture.Order.CompanyProfileId), revokeFixture.Order.Id, new(4)), "public-id"));
        Check(() => Assert.True(initial.Success), () => Assert.NotNull(initial.Publication), () => Assert.Contains("authority", revokeFailure.Message, StringComparison.OrdinalIgnoreCase), () => Assert.Null(revoke.GetPublication(revokeFixture.Order.Id)));

        static async Task<ProjectionFixture> Ready() { var candidate = new ProjectionFixture("Collaboration authority"); await candidate.PrepareCollaborationAsync(); return candidate; }
        static void ChangeProfile(ProjectionFixture candidate) { var profileId = NewId(); candidate.Store.BeginProfileRestore(profileId, false, 0, DateTime.UtcNow, ConnectionScope(profileId)); }
        static HttpResponseMessage Publication(HttpRequestMessage request, ProjectionFixture candidate, Action change) { AssertCapturedRequest(request); if (IsAdoptionRequest(request)) { return Adoption(candidate.Order, 4); } change(); return Ok(new { OrderId = candidate.Order.Id, PublicId = "public-id", Version = 1, PublishedAtUtc = DateTime.UtcNow, State = "Pending", DestinationLabel = "Test", Message = (string?)null }); }
        static HttpResponseMessage Revoked(HttpRequestMessage request, ProjectionFixture candidate, Action change) { AssertCapturedRequest(request); change(); return new(HttpStatusCode.NoContent); }
        static void AssertCapturedRequest(HttpRequestMessage request) { Assert.Equal(new Uri(Host).Host, request.RequestUri!.Host); Assert.Equal("access-key", request.Headers.GetValues("X-Profile-Key").Single()); }
    }
    [Fact]
    public async Task CollaborationRefreshReusesRecentPublicationResult()
    {
        var fixture = new ProjectionFixture("Collaboration refresh cache");
        await fixture.PrepareCollaborationAsync();
        var requestCount = 0;
        var collaboration = fixture.CreateUnusedCollaboration(new StubHandler(request =>
        {
            if (IsAdoptionRequest(request))
            {
                return Adoption(fixture.Order, 4);
            }
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        await collaboration.RefreshAsync(fixture.Order.CompanyProfileId, fixture.Order.Id);
        await collaboration.RefreshAsync(fixture.Order.CompanyProfileId, fixture.Order.Id);

        Assert.Equal(1, requestCount);
        Assert.Null(collaboration.GetPublication(fixture.Order.Id));
    }
    [Fact]
    public async Task PortablePublicationDoesNotImmediatelyReloadDiscordState()
    {
        var fixture = new ProjectionFixture("Portable publication refresh cache");
        await fixture.PrepareCollaborationAsync();
        var committed = fixture.PublishedOrder("Published portable commission");
        var requestCount = 0;
        var collaboration = fixture.CreateUnusedCollaboration(new StubHandler(request =>
        {
            if (IsAdoptionRequest(request))
            {
                return Adoption(fixture.Order, 4);
            }
            requestCount++;
            var publication = committed.CommissionPublication!;
            return Ok(new CommissionBriefCreateResponse
            {
                PublicId = publication.PublicId,
                PublicUrl = publication.PublicUrl!,
                EditorToken = string.Empty,
                Version = publication.Version,
                PublishedAtUtc = publication.PublishedAtUtc,
                OrderRecord = new(
                    new(committed.CompanyProfileId),
                    TradeCompanyRecordKinds.Order,
                    Key(committed),
                    JsonSerializer.Serialize(
                        committed,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    new(5),
                    DateTime.UtcNow)
            });
        }));

        await collaboration.PublishPortableLinkAsync(
            fixture.Order,
            new CommissionBriefDocument());
        await collaboration.RefreshAsync(
            fixture.Order.CompanyProfileId,
            fixture.Order.Id);

        Assert.Equal(1, requestCount);
    }
    [Fact]
    public async Task PortablePublicationReturnsTheInitialClaimCapability()
    {
        var fixture = new ProjectionFixture("Portable claim capability");
        await fixture.PrepareCollaborationAsync();
        var committed = fixture.PublishedOrder("Published portable claim");
        var publication = committed.CommissionPublication!;
        var claimUrl = $"{publication.PublicUrl}#claim={new string('a', 43)}";
        var collaboration = fixture.CreateUnusedCollaboration(new StubHandler(request =>
            IsAdoptionRequest(request)
                ? Adoption(fixture.Order, 4)
                : Ok(new CommissionBriefCreateResponse
                {
                    PublicId = publication.PublicId,
                    PublicUrl = publication.PublicUrl!,
                    ClaimUrl = claimUrl,
                    EditorToken = string.Empty,
                    Version = publication.Version,
                    PublishedAtUtc = publication.PublishedAtUtc,
                    OrderRecord = new(
                    new(committed.CompanyProfileId),
                    TradeCompanyRecordKinds.Order,
                    Key(committed),
                    JsonSerializer.Serialize(
                        committed,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    new(5),
                    DateTime.UtcNow)
                })));

        var link = await collaboration.PublishPortableLinkAsync(
            fixture.Order,
            new CommissionBriefDocument());
        var adopted = Assert.IsType<HostedOrderProjectionSnapshot>(
            fixture.Store.Get(fixture.Order.Id));
        var adoptedOrder = Assert.IsType<TradeOrder>(adopted.Order);

        Assert.Equal(claimUrl, link.Url);
        Assert.Equal(publication.PublicUrl, link.PublicUrl);
        Assert.Equal(committed.Title, adoptedOrder.Title);
        Assert.Equal(5, adopted.ObjectRevision);
    }
    [Fact]
    public async Task PortablePublicationSynchronizesMissingGeneratedPlanBeforeAdoption()
    {
        var profileId = NewId();
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Generated-plan publication");
        var planId = Guid.NewGuid().ToString("D");
        order.CraftPlanId = planId;
        order.CraftPlanName = "Sealed order plan";
        order.CraftPlanSavedAtUtc = DateTime.UtcNow;
        order.CraftPlanLinkKind = TradeOrderCraftPlanLinkKind.OrderGenerated;
        var plan = new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.Plans,
            ObjectId = planId,
            PayloadJson = "{}",
            UpdatedAtUtc = DateTime.UtcNow
        };
        var committed = CreatePublishedOrder(order, 4);
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
        var localState = CreateLocalState(indexedDb);
        await localState.LoadConnectionSettingsAsync();
        await localState.SaveObjectRevisionAsync(
            profileId,
            ProfileSyncCollections.TradeOrders,
            Key(order),
            4);
        var planPutCount = 0;
        var adoptionCount = 0;
        HttpResponseMessage Respond(HttpRequestMessage request)
        {
            if (request.Method == HttpMethod.Put)
            {
                planPutCount++;
                Assert.Equal(0, adoptionCount);
                plan.Revision = 5;
                return Ok(new ProfileSyncPutResponse
                {
                    Success = true,
                    ServerRevision = 5,
                    Object = plan
                });
            }
            if (IsAdoptionRequest(request))
            {
                adoptionCount++;
                Assert.Equal(1, planPutCount);
                return Adoption(order, 4);
            }

            Assert.Equal(2, adoptionCount);
            var publication = committed.CommissionPublication!;
            return Ok(new CommissionBriefCreateResponse
            {
                PublicId = publication.PublicId,
                PublicUrl = publication.PublicUrl!,
                EditorToken = string.Empty,
                Version = publication.Version,
                PublishedAtUtc = publication.PublishedAtUtc,
                OrderRecord = new(
                    new(committed.CompanyProfileId),
                    TradeCompanyRecordKinds.Order,
                    Key(committed),
                    JsonSerializer.Serialize(committed, ProfileSyncJson.CreateOptions()),
                    new(5),
                    DateTime.UtcNow)
            });
        }
        var profileSync = new ProfileSyncService(
            CreateHostClient(new StubHandler(Respond)),
            localState,
            new WebSettingsService(indexedDb),
            store,
            [new RecordingCollectionAdapter(ProfileSyncCollections.Plans, plan)]);
        SetReadyStatus(profileSync, profileId);
        var collaboration = new TradeCompanyCollaborationService(
            new TradeCompanyCollaborationClient(
                new HttpClient(new StubHandler(Respond)) { BaseAddress = new Uri(Host) },
                localState),
            new TradeOperationsPersistenceService(
                indexedDb,
                new TradeCompanyProfilePackageService()),
            localState,
            profileSync,
            store);

        var ownership = await collaboration.GetPublicationOwnershipAsync(order);
        await collaboration.PublishPortableLinkAsync(
            order,
            new CommissionBriefDocument());

        Check(
            () => Assert.NotNull(ownership),
            () => Assert.Equal(1, planPutCount),
            () => Assert.Equal(2, adoptionCount),
            () => Assert.Equal(profileId, profileSync.CurrentStatus.ProfileId),
            () => Assert.Equal(ProfileSyncStage.Ready, profileSync.CurrentStatus.Stage),
            () => Assert.Equal(4, profileSync.CurrentStatus.LastSyncRevision),
            () => Assert.Equal(
                5,
                localState.LoadObjectRevisionAsync(
                    profileId,
                    ProfileSyncCollections.Plans,
                    planId).GetAwaiter().GetResult()));
    }
    [Fact]
    public async Task DelayedCollaborationResponseCannotPersistOverNewerProjection()
    {
        var fixture = new ProjectionFixture("Revision four");
        await fixture.PrepareCollaborationAsync();
        var newer = fixture.OrderAt("Revision six");
        var collaboration = fixture.CreateCollaboration(5,
            () => Assert.True(fixture.Store.TryPublishRemoteOrder(newer, 6)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collaboration.PublishPortableLinkAsync(fixture.Order, new CommissionBriefDocument()));
        Check(() => Assert.Equal(0, fixture.Runtime.SaveTradeOrderCount), () => Assert.Same(newer, fixture.Store.Get(fixture.Order.Id)?.Order), () => Assert.Equal(6, fixture.Store.Get(fixture.Order.Id)?.ObjectRevision));
    }
    [Fact]
    public async Task CollaborationResponseFromReplacedHostScopeCannotPersist()
    {
        var fixture = new ProjectionFixture("Original host");
        await fixture.PrepareCollaborationAsync();
        var collaboration = fixture.CreateCollaboration(5, fixture.ReplaceHost);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collaboration.PublishPortableLinkAsync(fixture.Order, new CommissionBriefDocument()));
        var localRevision = await fixture.LoadRevisionAsync();
        Check(() => Assert.Equal(0, fixture.Runtime.SaveTradeOrderCount), () => Assert.Same(fixture.Order, fixture.Store.Get(fixture.Order.Id)?.Order), () => Assert.Equal(4, fixture.Store.Get(fixture.Order.Id)?.ObjectRevision), () => Assert.Equal(0, localRevision));
    }
    [Fact]
    public async Task CollaborationCannotSendAcrossCaseDistinctConnectionPath()
    {
        var profileId = NewId();
        var fixture = new ProjectionFixture(
            "Case-sensitive host path", profileId: profileId,
            connectionScope: $"https://profiles.example/API/|{profileId}");
        SetReadyStatus(fixture.ProfileSync, profileId);
        var collaboration = fixture.CreateUnusedCollaboration();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collaboration.PublishPortableLinkAsync(fixture.Order, new CommissionBriefDocument()));
        Check(() => Assert.Contains("authority", failure.Message, StringComparison.OrdinalIgnoreCase), () => Assert.Equal(0, fixture.Runtime.SaveTradeOrderCount));
    }
    [Fact]
    public async Task AdapterRejectsReplacementHostBeforeProjectionOrPersistence()
    {
        var fixture = new ProjectionFixture("Original host", addCompany: true);
        var replacement = fixture.OrderAt("Replacement host");
        await fixture.LocalState.LoadConnectionSettingsAsync();
        fixture.ReplaceHost();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Adapter.ApplyRemoteObjectAsync(Envelope(replacement, 5), default));
        var localRevision = await fixture.LoadRevisionAsync();
        Check(() => Assert.Contains("scope", failure.Message, StringComparison.OrdinalIgnoreCase), () => Assert.Equal(0, fixture.Runtime.SaveTradeOrderCount), () => Assert.Same(fixture.Order, fixture.Store.Get(fixture.Order.Id)?.Order), () => Assert.Equal(0, localRevision));
    }
    [Fact]
    public async Task AdapterHostReplacementDuringPersistenceCannotWriteReplacementRevisionNamespace()
    {
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
        var localRevision = await fixture.LoadRevisionAsync();
        Check(() => Assert.Contains("authority", failure.Message, StringComparison.OrdinalIgnoreCase), () => Assert.Equal("Revision five", fixture.Runtime.DurableOrder?.Title), () => Assert.Equal(5, fixture.Store.Get(fixture.Order.Id)?.ObjectRevision), () => Assert.Equal(0, localRevision));
    }
    [Fact]
    public async Task AdapterReconcilesDurableWinnerAfterOlderWriteFinishesLast()
    {
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
        var localRevision = await fixture.LoadRevisionAsync();
        Check(() => Assert.Equal("Revision six", fixture.Runtime.DurableOrder?.Title), () => Assert.Equal(6, localRevision));
    }
    [Fact]
    public async Task AdapterAlreadyCurrentReplayRepairsMissingDurableOrder()
    {
        var fixture = new ProjectionFixture("Revision five", revision: 5, addCompany: true);
        await fixture.LocalState.LoadConnectionSettingsAsync();
        await fixture.Adapter.ApplyRemoteObjectAsync(Envelope(fixture.Order, 5), default);
        Check(() => Assert.Same(fixture.Order, fixture.Runtime.DurableOrder), () => Assert.Equal(1, fixture.Runtime.SaveTradeOrderCount));
    }
    [Fact]
    public async Task AdapterTombstoneReconcilesNewerLiveOrderAfterBlockedDelete()
    {
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
        var localRevision = await fixture.LoadRevisionAsync();
        Check(() => Assert.Equal("Revision six", fixture.Runtime.DurableOrder?.Title), () => Assert.Equal(6, fixture.Store.Get(fixture.Order.Id)?.ObjectRevision), () => Assert.Equal(6, localRevision));
    }
    [Fact]
    public async Task CollaborationReconcilesDurableWinnerAfterOlderWriteFinishesLast()
    {
        var fixture = new ProjectionFixture("Revision four");
        var gate = fixture.BlockFirstSave("Revision five");
        await fixture.PrepareCollaborationAsync();
        var revisionFive = fixture.PublishedOrder("Revision five");
        var revisionSix = fixture.PublishedOrder("Revision six", 5);
        var olderService = fixture.CreateCollaboration(revisionFive, 5);
        var newerService = fixture.CreateCollaboration(revisionSix, 6);
        var older = olderService.PublishPortableLinkAsync(fixture.Order, new CommissionBriefDocument());
        await gate.Entered.Task;
        await newerService.PublishPortableLinkAsync(fixture.Order, new CommissionBriefDocument());
        gate.Release.SetResult();
        await older;
        var localRevision = await fixture.LoadRevisionAsync();
        Check(() => Assert.Equal("Revision six", fixture.Runtime.DurableOrder?.Title), () => Assert.Equal(6, localRevision));
    }
    [Fact]
    public async Task ConnectionScopeChangeDuringPersistenceCannotWriteReplacementRevisionNamespace()
    {
        var fixture = new ProjectionFixture("Revision four");
        var switched = false;
        fixture.Runtime.BeforeSaveTradeOrderAsync = candidate =>
        {
            if (!switched && candidate.Title == "Revision five")
            {
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
        var localRevision = await fixture.LoadRevisionAsync();
        Check(() => Assert.Contains("authority", failure.Message, StringComparison.OrdinalIgnoreCase), () => Assert.Equal("Revision five", fixture.Runtime.DurableOrder?.Title), () => Assert.Equal(5, fixture.Store.Get(fixture.Order.Id)?.ObjectRevision), () => Assert.Equal(0, localRevision));
    }
    [Fact]
    public void OwnerProjectionIsPreservedThenClearedAndRehydratedByRevision()
    {
        var fixture = new ProjectionFixture("Owner revision four");
        var ownerFour = Owner(fixture.Order, 4, 8);
        Assert.True(fixture.Store.TryPublishOwner(ownerFour));
        var authority = fixture.Store.CaptureAuthorityScope();
        Check(() => Assert.Equal(HostedOrderCommittedProjectionResult.AlreadyCurrent, fixture.Store.TryAdoptCommittedOrder(authority, fixture.Order, 4)), () => Assert.Same(ownerFour, fixture.Store.GetOwnerProjection(fixture.Order.Id)));
        var revisionFive = fixture.OrderAt("Owner pending");
        Check(() => Assert.Equal(HostedOrderCommittedProjectionResult.Adopted, fixture.Store.TryAdoptCommittedOrder(authority, revisionFive, 5)), () => Assert.Null(fixture.Store.GetOwnerProjection(fixture.Order.Id)));
        var ownerFive = Owner(revisionFive, 5, 9);
        Check(() => Assert.True(fixture.Store.TryPublishOwner(ownerFive)), () => Assert.Same(ownerFive, fixture.Store.GetOwnerProjection(fixture.Order.Id)));
    }
    private static DeletionFixture CreateDeletionFixture(
        string profileId, TradeOrder order, long responseRevision, Action? beforeDeleteResponse = null)
    {
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(profileId, false, 0, DateTime.UtcNow, ConnectionScope(profileId));
        var runtime = new StorageRuntime(ConnectionSettings(profileId));
        var indexedDb = new IndexedDbService(runtime);
        var localState = CreateLocalState(indexedDb);
        var envelope = Envelope(order, responseRevision - 1);
        var adapter = new RecordingOrderAdapter(envelope);
        var service = new ProfileSyncService(
            CreateHostClient(ProfileDeletionHandler(envelope, responseRevision, beforeDeleteResponse)),
            localState, new WebSettingsService(indexedDb), store, [adapter]);
        return new(service, store, adapter);
    }
    private static PutAdoptionFixture CreatePutAdoptionFixture(bool conflictFirst)
    {
        var profileId = NewId(); var companyProfileId = Guid.NewGuid(); var localOrder = CreateOrder(Guid.NewGuid(), companyProfileId, "Local order"); var committedRevision = conflictFirst ? 5 : 1;
        var committedOrder = CreateOrder(localOrder.Id, companyProfileId, conflictFirst ? "Host committed kept-local order" : "Host committed order"); var putCount = 0; var handler = new StubHandler(request => { Assert.Equal(HttpMethod.Put, request.Method); return Ok(conflictFirst && ++putCount == 1 ? new ProfileSyncPutResponse { Conflict = true, ServerRevision = committedRevision - 1, RemoteObject = Envelope(CreateOrder(localOrder.Id, companyProfileId, "Remote conflict"), committedRevision - 1) } : new ProfileSyncPutResponse { Success = true, ServerRevision = committedRevision, Object = Envelope(committedOrder, committedRevision) }); });
        var store = new HostedOrderProjectionStore(); store.BeginProfileRestore(profileId, true, 0, DateTime.UtcNow, ConnectionScope(profileId));
        var retainedOrder = CreateOrder(Guid.NewGuid(), companyProfileId, "Retained hosted order"); Assert.True(store.TryPublishRemoteOrder(retainedOrder, 1)); Assert.True(store.TryPublishRestoreState(store.RestoreState.Apply(new ProfileSyncStatus(true, true, 0, 0, 0, DateTime.UtcNow, "Synced") { ProfileId = profileId, Stage = ProfileSyncStage.Ready }, DateTime.UtcNow)));
        var runtime = new StorageRuntime(ConnectionSettings(profileId)); runtime.AddCompany(companyProfileId); runtime.SeedOrder(localOrder); var indexedDb = new IndexedDbService(runtime); var localState = CreateLocalState(indexedDb); var adapter = new TradeOrderProfileSyncAdapter(new TradeOperationsPersistenceService(indexedDb, new TradeCompanyProfilePackageService()), store, localState);
        return new(profileId, localOrder, retainedOrder, committedOrder, committedRevision, new ProfileSyncService(CreateHostClient(handler), localState, new WebSettingsService(indexedDb), store, [adapter]), store, localState, runtime);
    }
    private static ProfileSyncLocalStateService CreateLocalState(IndexedDbService indexedDb) =>
        new(indexedDb, new ProfileHostClientOptions(Host));
    private static ProfileHostClient CreateHostClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(Host) }, new ProfileHostClientOptions(Host));
    private static ProfileSyncService CreateProfileSync(
        ProfileSyncLocalStateService localState, IndexedDbService indexedDb, HostedOrderProjectionStore store) =>
        new(CreateHostClient(UnusedHandler()), localState, new WebSettingsService(indexedDb), store, []);
    private static TradeCompanyCollaborationService CreateCollaboration(
        TradeOrder committed, long revision, ProfileSyncLocalStateService localState,
        ProfileSyncService profileSync, TradeOperationsPersistenceService persistence,
        HostedOrderProjectionStore store, Action? beforeResponse = null) =>
        new(new TradeCompanyCollaborationClient(
                new HttpClient(PortablePublicationHandler(committed, revision, beforeResponse ?? (() => { })))
                { BaseAddress = new Uri(Host) }, localState),
            persistence, localState, profileSync, store);
    private static void SetReadyStatus(ProfileSyncService service, string profileId)
    {
        var property = typeof(ProfileSyncService).GetProperty(
            nameof(ProfileSyncService.CurrentStatus), BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(nameof(ProfileSyncService.CurrentStatus));
        property.SetValue(service, new ProfileSyncStatus(true, true, 4, 0, 0, DateTime.UtcNow, "Synced")
        {
            ProfileId = profileId,
            Stage = ProfileSyncStage.Ready
        });
    }
    private static Dictionary<string, string> ConnectionSettings(string profileId) =>
        new(StringComparer.Ordinal) { [ProfileSyncSettingsKeys.HostUrl] = JsonSerializer.Serialize(Host), [ProfileSyncSettingsKeys.AccessKey] = JsonSerializer.Serialize("access-key"), [ProfileSyncSettingsKeys.RememberAccessKey] = JsonSerializer.Serialize(true), [ProfileSyncSettingsKeys.ConnectedProfileId] = JsonSerializer.Serialize(profileId), ["profileHost.connectedProfileName"] = JsonSerializer.Serialize("Test profile") };
    private static string NewId() => Guid.NewGuid().ToString("D");
    private static string Key(TradeOrder order) => order.Id.ToString("D");
    private static void Check(params Action[] assertions) => Array.ForEach(assertions, assertion => assertion());
    private static string ConnectionScope(string profileId) =>
        $"{ProfileHostClient.NormalizeHostUrl(Host)}|{profileId}";
    private static TradeOrder CreateOrder(Guid orderId, Guid companyProfileId, string title) =>
        new() { Id = orderId, CompanyProfileId = companyProfileId, Title = title };
    private static CompanyCommissionOwnerProjection Owner(
        TradeOrder order, long objectRevision, long companyRevision) =>
        new() { Order = order, ObjectRevision = new(objectRevision), CompanyRevision = new(companyRevision) };
    private static TradeOrder CreatePublishedOrder(TradeOrder source, long revision, string? title = null)
    {
        var published = TradeOrderWorkflow.CopyOrder(source);
        published.Title = title ?? published.Title;
        published.CommissionPublication = new()
        {
            PublicId = "public-id",
            PublicUrl = "https://profiles.example/brief?id=public-id",
            Version = 1,
            PublishedAtUtc = DateTime.UtcNow,
            Ownership = new(new(source.CompanyProfileId), source.Id, new(revision))
        };
        return published;
    }
    private static ProfileSyncObjectEnvelope Envelope(TradeOrder order, long revision) =>
        new()
        {
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
            string? connectionScope = null, bool addCompany = false)
        {
            ProfileId = profileId ?? NewId();
            Order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), title);
            Store.BeginProfileRestore(ProfileId, false, revision, DateTime.UtcNow,
                connectionScope ?? ConnectionScope(ProfileId));
            Assert.True(Store.TryPublishRemoteOrder(Order, revision));
            Runtime = new(ConnectionSettings(ProfileId));
            if (addCompany)
            {
                Runtime.AddCompany(Order.CompanyProfileId);
            }

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
        public TradeOrder PublishedOrder(string title, long publicationRevision = 4) => CreatePublishedOrder(Order, publicationRevision, title);
        public async Task PrepareCollaborationAsync()
        {
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
        public TradeCompanyCollaborationService CreateUnusedCollaboration(HttpMessageHandler? handler = null) =>
            new(new TradeCompanyCollaborationClient(
                    new HttpClient(handler ?? UnusedHandler()) { BaseAddress = new Uri(Host) }, LocalState),
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
        public AsyncGate BlockFirstSave(string title)
        {
            var gate = new AsyncGate();
            var blocked = false;
            Runtime.BeforeSaveTradeOrderAsync = async candidate =>
            {
                if (blocked || candidate.Title != title)
                {
                    return;
                }

                blocked = true;
                gate.Entered.SetResult();
                await gate.Release.Task;
            };
            return gate;
        }
        public AsyncGate BlockDelete()
        {
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
    private sealed record PutAdoptionFixture(string ProfileId, TradeOrder LocalOrder, TradeOrder RetainedOrder, TradeOrder CommittedOrder, long CommittedRevision, ProfileSyncService Service, HostedOrderProjectionStore Store, ProfileSyncLocalStateService LocalState, StorageRuntime Runtime);
    private sealed class RecordingCollectionAdapter(string collection, ProfileSyncObjectEnvelope local, Func<Task>? delete = null) : IProfileSyncCollectionAdapter
    {
        public string Collection => collection;
        public Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProfileSyncObjectEnvelope>>([local]);
        public Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteLocalObjectAsync(string objectId, CancellationToken ct) => delete?.Invoke() ?? Task.CompletedTask;
    }
    private sealed class EmptyCollectionAdapter(string collection) : IProfileSyncCollectionAdapter
    {
        public string Collection => collection;
        public Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProfileSyncObjectEnvelope>>([]);
        public Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteLocalObjectAsync(string objectId, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class RecordingOrderAdapter(ProfileSyncObjectEnvelope? local) : IProfileSyncCollectionAdapter
    {
        public string Collection => ProfileSyncCollections.TradeOrders;
        public int DeleteCount { get; private set; }
        public Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProfileSyncObjectEnvelope>>(local == null ? [] : [local]);
        public Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
        {
            DeleteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class MissingCompanyOrderAdapter(
        IReadOnlySet<Guid> orphanedOrderIds,
        Guid missingCompanyId) : IProfileSyncCollectionAdapter
    {
        public string Collection => ProfileSyncCollections.TradeOrders;
        public List<Guid> AppliedOrderIds { get; } = [];
        public Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProfileSyncObjectEnvelope>>([]);
        public Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
        {
            var orderId = Guid.Parse(envelope.ObjectId);
            if (orphanedOrderIds.Contains(orderId))
            {
                throw new MissingTradeCompanyProfileException(
                    missingCompanyId,
                    "order",
                    envelope.ObjectId);
            }
            AppliedOrderIds.Add(orderId);
            return Task.CompletedTask;
        }
        public Task DeleteLocalObjectAsync(string objectId, CancellationToken ct) => Task.CompletedTask;
    }
    private static HttpMessageHandler RevisionZeroDeletionHandler(Guid orderId) =>
        new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/profile-host/changes")
            ? Ok(new ProfileSyncChangesResponse
            {
                ServerRevision = 1,
                Objects = [Tombstone(orderId.ToString("D"), 1)]
            })
            : throw new NotSupportedException(request.RequestUri.ToString()));
    private static HttpMessageHandler ProfileDeletionHandler(
        ProfileSyncObjectEnvelope remote, long responseRevision, Action? beforeDeleteResponse) =>
        new StubHandler(request => request.Method switch
        {
            _ when request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.EndsWith("/profile-host/bootstrap/export") =>
                Ok(new ProfileHostBootstrapPayload { Objects = [remote] }),
            _ when request.Method == HttpMethod.Delete => DeleteResponse(remote, responseRevision, beforeDeleteResponse),
            _ => throw new NotSupportedException(request.RequestUri?.ToString())
        });
    private static HttpMessageHandler PortablePublicationHandler(
        TradeOrder committed, long revision, Action beforeResponse) => new StubHandler(request =>
        {
            var publication = committed.CommissionPublication!;
            if (IsAdoptionRequest(request))
            {
                return Adoption(committed, publication.Ownership!.OrderRevision.Value);
            }
            beforeResponse();
            return Ok(new CommissionBriefCreateResponse
            {
                PublicId = publication.PublicId,
                PublicUrl = publication.PublicUrl!,
                EditorToken = string.Empty,
                Version = publication.Version,
                PublishedAtUtc = publication.PublishedAtUtc,
                OrderRecord = new(new(committed.CompanyProfileId), TradeCompanyRecordKinds.Order, Key(committed),
                    JsonSerializer.Serialize(committed, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    new(revision), DateTime.UtcNow)
            });
        });
    private static bool IsAdoptionRequest(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post &&
        request.RequestUri!.AbsolutePath.EndsWith("/adopt", StringComparison.Ordinal);
    private static HttpResponseMessage Adoption(TradeOrder order, long revision) =>
        Ok(new TradeCompanyOrderAdoptionResponse(
            new(
                new(order.CompanyProfileId),
                TradeCompanyRecordKinds.Order,
                Key(order),
                JsonSerializer.Serialize(order, ProfileSyncJson.CreateOptions()),
                new(revision),
                DateTime.UtcNow),
            null));
    private static HttpMessageHandler UnusedHandler() =>
        new StubHandler(request => throw new NotSupportedException(request.RequestUri?.ToString()));
    private static HttpResponseMessage DeleteResponse(
        ProfileSyncObjectEnvelope remote, long revision, Action? beforeResponse)
    {
        beforeResponse?.Invoke();
        return Ok(new ProfileSyncPutResponse
        {
            Success = true,
            ServerRevision = revision,
            Object = Tombstone(remote.ObjectId, revision, remote.Collection)
        });
    }
    private static HttpResponseMessage RecordConflict(HttpRequestMessage request, List<string> deletions)
    {
        deletions.Add(request.RequestUri!.AbsolutePath);
        return Ok(new ProfileSyncPutResponse { Conflict = true });
    }
    private static ProfileSyncObjectEnvelope Tombstone(
        string objectId, long revision, string collection = ProfileSyncCollections.TradeOrders) => new()
        {
            Collection = collection,
            ObjectId = objectId,
            Revision = revision,
            Deleted = true,
            UpdatedAtUtc = DateTime.UtcNow
        };
    private static HttpResponseMessage Ok<T>(T content) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(content) };
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
    private sealed class StorageRuntime(Dictionary<string, string> settings) : IJSRuntime
    {
        private readonly HashSet<Guid> _companyIds = [];
        private readonly Dictionary<string, TradeOrderArchiveSummaryRecord> _archiveSummaries = [];
        public int SaveTradeOrderCount { get; private set; }
        public int LoadAllSettingsCount { get; private set; }
        public int LoadSettingCount { get; private set; }
        public int LoadTradeCompanyProfilesCount { get; private set; }
        public int LoadTradeOrdersCount { get; private set; }
        public int LoadAllTradeOrdersCount { get; private set; }
        public int LoadTradeOrderCount { get; private set; }
        public TradeOrder? DurableOrder { get; private set; }
        public Func<TradeOrder, Task>? BeforeSaveTradeOrderAsync { get; set; }
        public Func<Guid, Task>? BeforeDeleteTradeOrderAsync { get; set; }
        public void SaveRawSetting(string key, string value) => settings[key] = value;
        public void AddCompany(Guid companyId) => _companyIds.Add(companyId);
        public void SeedOrder(TradeOrder order) => DurableOrder = order;
        public void ResetReadCounts()
        {
            LoadAllSettingsCount = 0;
            LoadSettingCount = 0;
            LoadTradeCompanyProfilesCount = 0;
            LoadTradeOrdersCount = 0;
            LoadAllTradeOrdersCount = 0;
            LoadTradeOrderCount = 0;
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "IndexedDB.saveTradeOrder")
            {
                return SaveTradeOrderAsync<TValue>((TradeOrder)args![0]!);
            }

            if (identifier == "IndexedDB.deleteTradeOrder")
            {
                return DeleteTradeOrderAsync<TValue>((Guid)args![0]!);
            }

            if (identifier == "IndexedDB.saveTradeOrderArchiveSummary")
            {
                var record = (TradeOrderArchiveSummaryRecord)args![0]!;
                _archiveSummaries[record.Id] = record;
                return ValueTask.FromResult((TValue)(object)true);
            }

            if (identifier == "IndexedDB.deleteTradeOrderArchiveSummary")
            {
                _archiveSummaries.Remove((string)args![0]!);
                return ValueTask.FromResult((TValue)(object)true);
            }

            object? result = identifier switch
            {
                "IndexedDB.loadAllSettings" => LoadAllSettings(),
                "IndexedDB.loadSetting" => LoadSetting((string)args![0]!),
                "IndexedDB.loadTradeCompanyProfiles" => LoadTradeCompanyProfiles(),
                "IndexedDB.loadTradeOrders" => LoadTradeOrders((Guid)args![0]!),
                "IndexedDB.loadAllTradeOrders" => LoadAllTradeOrders(),
                "IndexedDB.loadTradeOrder" => LoadTradeOrder((Guid)args![0]!),
                "IndexedDB.loadTradeOrderArchiveSummaries" => _archiveSummaries.Values.ToList(),
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
        private Dictionary<string, string> LoadAllSettings()
        {
            LoadAllSettingsCount++;
            return new Dictionary<string, string>(settings, StringComparer.Ordinal);
        }
        private string? LoadSetting(string key)
        {
            LoadSettingCount++;
            return settings.GetValueOrDefault(key);
        }
        private List<TradeCompanyProfile> LoadTradeCompanyProfiles()
        {
            LoadTradeCompanyProfilesCount++;
            return _companyIds.Select(companyId =>
                new TradeCompanyProfile { Id = companyId, Name = "Test company" }).ToList();
        }
        private TradeOrder? LoadTradeOrder(Guid orderId)
        {
            LoadTradeOrderCount++;
            return DurableOrder?.Id == orderId ? DurableOrder : null;
        }
        private List<TradeOrder> LoadTradeOrders(Guid companyProfileId)
        {
            LoadTradeOrdersCount++;
            return DurableOrder?.CompanyProfileId == companyProfileId
                ? [DurableOrder]
                : [];
        }

        private List<TradeOrder> LoadAllTradeOrders()
        {
            LoadAllTradeOrdersCount++;
            return DurableOrder == null ? [] : [DurableOrder];
        }
        private bool SaveSetting(string key, string value)
        {
            settings[key] = value;
            return true;
        }
        private async ValueTask<TValue> SaveTradeOrderAsync<TValue>(TradeOrder order)
        {
            await (BeforeSaveTradeOrderAsync?.Invoke(order) ?? Task.CompletedTask);
            SaveTradeOrderCount++;
            DurableOrder = order;
            return (TValue)(object)true;
        }
        private async ValueTask<TValue> DeleteTradeOrderAsync<TValue>(Guid orderId)
        {
            await (BeforeDeleteTradeOrderAsync?.Invoke(orderId) ?? Task.CompletedTask);
            DurableOrder = DurableOrder?.Id == orderId ? null : DurableOrder;
            return (TValue)(object)true;
        }
    }
}
