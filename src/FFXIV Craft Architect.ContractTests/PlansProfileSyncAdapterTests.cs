using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class PlansProfileSyncAdapterTests
{
    private static async Task LinkedPlanDeletionRequiresTheSameOrderInTheDeletionBoundary()
    {
        var planId = Guid.NewGuid().ToString("D");
        var linkedOrderId = Guid.NewGuid();
        var runtime = new PlanStorageRuntime(CreatePlan(planId, "linked-content", linkedOrderId));
        var indexedDb = new IndexedDbService(runtime);
        var adapter = new PlansProfileSyncAdapter(indexedDb, new WebPlanPersistenceService(indexedDb));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.DeleteLocalObjectForOrderDeletionAsync(planId, Guid.NewGuid(), CancellationToken.None));
        Assert.Contains(planId, runtime.Plans);
        await adapter.DeleteLocalObjectForOrderDeletionAsync(planId, linkedOrderId, CancellationToken.None);
        Assert.DoesNotContain(planId, runtime.Plans);
    }

    [Fact]
    public async Task RemoteUnsealedReplayPreservesMatchingLocalSealWithoutWeakeningCollisionGuard()
    {
        await LinkedPlanDeletionRequiresTheSameOrderInTheDeletionBoundary();
        var planId = Guid.NewGuid().ToString("D");
        var linkedOrderId = Guid.NewGuid();
        var existing = CreatePlan(planId, "same-content", linkedOrderId);
        var runtime = new PlanStorageRuntime(existing);
        var indexedDb = new IndexedDbService(runtime);
        var adapter = new PlansProfileSyncAdapter(
            indexedDb,
            new WebPlanPersistenceService(indexedDb));
        var remote = CreatePlan(planId, "same-content", linkedOrderId: null);
        remote.CreatedAt = existing.CreatedAt.AddDays(-1);
        remote.ModifiedAt = existing.ModifiedAt.AddMinutes(-1);
        remote.SavedAt = existing.SavedAt.AddMinutes(-1);

        var promotion = await Assert.ThrowsAsync<ProfileSyncObjectReconciliationException>(() =>
            adapter.ApplyRemoteObjectAsync(
                PlansProfileSyncAdapter.ToSyncObject(remote, DateTime.UtcNow),
                CancellationToken.None));

        Assert.Equal(ProfileSyncObjectReconciliation.PromoteLocalAuthority, promotion.Reconciliation);
        Assert.Equal(0, runtime.SaveCount);
        Assert.Equal(linkedOrderId, runtime.Plan.LinkedOrderId);

        var conflicting = CreatePlan(planId, "different-content", linkedOrderId: null);
        var conflict = await Assert.ThrowsAsync<ProfileSyncObjectReconciliationException>(() =>
            adapter.ApplyRemoteObjectAsync(
                PlansProfileSyncAdapter.ToSyncObject(conflicting, DateTime.UtcNow),
                CancellationToken.None));
        Assert.Equal(ProfileSyncObjectReconciliation.ProtectedConflict, conflict.Reconciliation);
        Assert.Equal(ProfileSyncCollections.Plans, conflict.Collection);
        Assert.Equal(planId, conflict.ObjectId);
        Assert.Equal(0, runtime.SaveCount);
        Assert.Equal(linkedOrderId, runtime.Plan.LinkedOrderId);

        var hostedLinked = CreatePlan(planId, "hosted-linked-content", linkedOrderId);
        await adapter.AdoptProtectedRemoteObjectAsync(
            PlansProfileSyncAdapter.ToSyncObject(hostedLinked, DateTime.UtcNow),
            CancellationToken.None);
        Assert.Equal("hosted-linked-content", runtime.Plans[planId].PlanJson);
        var preserved = Assert.Single(runtime.Plans.Values, plan => plan.Id != planId);
        Assert.Equal("same-content", preserved.PlanJson);
        Assert.Null(preserved.LinkedOrderId);

        var unprotected = CreatePlan(Guid.NewGuid().ToString("D"), "new-content", linkedOrderId: null);
        runtime.FailSaves = true;
        var storageFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ApplyRemoteObjectAsync(
                PlansProfileSyncAdapter.ToSyncObject(unprotected, DateTime.UtcNow),
                CancellationToken.None));
        Assert.IsNotType<ProfileSyncObjectReconciliationException>(storageFailure);
    }

    [Fact]
    public async Task ProtectedPlanConflictDoesNotBlockLaterPagesAndSurvivesRestart()
    {
        const string host = "https://profiles.example/api/";
        var profileId = Guid.NewGuid().ToString("D");
        var poisonId = Guid.NewGuid().ToString("D");
        var promotionId = Guid.NewGuid().ToString("D");
        var sealedConflictId = Guid.NewGuid().ToString("D");
        var laterPlanId = Guid.NewGuid().ToString("D");
        var linkedOrderId = Guid.NewGuid();
        var poisonLocal = CreatePlan(poisonId, "local-authority", linkedOrderId);
        var runtime = new PlanStorageRuntime(poisonLocal, ConnectionSettings(host, profileId));
        var promotionLocal = CreatePlan(promotionId, "promotion-content", Guid.NewGuid());
        runtime.Plans[promotionId] = promotionLocal;
        var sealedConflictOrderId = Guid.NewGuid();
        runtime.Plans[sealedConflictId] = CreatePlan(
            sealedConflictId,
            "sealed-local",
            sealedConflictOrderId);
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(indexedDb, new ProfileHostClientOptions(host));
        await localState.LoadConnectionSettingsAsync();
        await localState.SaveLastSyncRevisionAsync(profileId, 513);
        await localState.SaveObjectRevisionAsync(profileId, ProfileSyncCollections.Plans, poisonId, 513);
        await localState.SaveLinkedPlanSealMigrationCompleteAsync(profileId);

        var poisonRemote = PlansProfileSyncAdapter.ToSyncObject(
            CreatePlan(poisonId, "remote-divergence", linkedOrderId: null),
            DateTime.UtcNow);
        poisonRemote.Revision = 514;
        var promotionRemotePlan = CreatePlan(promotionId, "promotion-content", linkedOrderId: null);
        promotionRemotePlan.CreatedAt = promotionLocal.CreatedAt.AddDays(-1);
        promotionRemotePlan.ModifiedAt = promotionLocal.ModifiedAt.AddMinutes(-1);
        promotionRemotePlan.SavedAt = promotionLocal.SavedAt.AddMinutes(-1);
        var promotionRemote = PlansProfileSyncAdapter.ToSyncObject(
            promotionRemotePlan,
            DateTime.UtcNow);
        promotionRemote.Revision = 515;
        var sealedConflictRemote = PlansProfileSyncAdapter.ToSyncObject(
            CreatePlan(sealedConflictId, "sealed-hosted", sealedConflictOrderId),
            DateTime.UtcNow);
        sealedConflictRemote.Revision = 516;
        var laterPlan = PlansProfileSyncAdapter.ToSyncObject(
            CreatePlan(laterPlanId, "later-plan", linkedOrderId: null),
            DateTime.UtcNow);
        laterPlan.Revision = 520;
        var laterOrder = new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.TradeOrders,
            ObjectId = linkedOrderId.ToString("D"),
            PayloadJson = JsonSerializer.Serialize(new TradeOrder
            {
                Id = linkedOrderId,
                CompanyProfileId = Guid.NewGuid(),
                Title = "Later order"
            }, ProfileSyncJson.CreateOptions()),
            Revision = 521,
            UpdatedAtUtc = DateTime.UtcNow
        };
        var handler = new PagedConflictHandler(
            poisonRemote,
            promotionRemote,
            sealedConflictRemote,
            laterPlan,
            laterOrder);
        var orderAdapter = new RecordingAdapter(ProfileSyncCollections.TradeOrders);
        var planAdapter = new PlansProfileSyncAdapter(
            indexedDb,
            new WebPlanPersistenceService(indexedDb));
        var service = CreateService(
            host,
            handler,
            localState,
            indexedDb,
            planAdapter,
            orderAdapter);

        await service.InitializeAsync();

        Assert.True(
            service.CurrentStatus.Stage == ProfileSyncStage.Ready,
            service.CurrentStatus.Message);
        Assert.Equal(521, service.CurrentStatus.LastSyncRevision);
        Assert.Contains(laterOrder.ObjectId, orderAdapter.AppliedObjectIds);
        Assert.Equal("later-plan", runtime.Plans[laterPlanId].PlanJson);
        Assert.Equal(1, handler.PromotionPutCount);
        Assert.True(handler.PromotionPayloadWasSealed);
        Assert.Equal(1, handler.LegacyDeleteCount);
        Assert.Equal(1, handler.LegacyRepublishCount);
        Assert.DoesNotContain(service.PendingSaves, pending => pending.ObjectId == poisonId);
        Assert.DoesNotContain(service.PendingSaves, pending => pending.ObjectId == promotionId);
        var conflict = Assert.Single(service.Conflicts);
        Assert.Equal(sealedConflictId, conflict.ObjectId);
        Assert.True(conflict.CanApplyRemote);
        Assert.False(conflict.CanKeepLocal);
        Assert.Contains(service.PendingSaves, pending => pending.ObjectId == sealedConflictId);

        var restarted = CreateService(
            host,
            handler,
            new ProfileSyncLocalStateService(indexedDb, new ProfileHostClientOptions(host)),
            indexedDb,
            planAdapter,
            new RecordingAdapter(ProfileSyncCollections.TradeOrders));
        await restarted.InitializeAsync();

        Assert.Equal(521, restarted.CurrentStatus.LastSyncRevision);
        Assert.Contains(restarted.PendingSaves, pending => pending.ObjectId == sealedConflictId);
        var restartedConflict = Assert.Single(restarted.Conflicts);
        Assert.True(restartedConflict.CanApplyRemote);
        Assert.False(restartedConflict.CanKeepLocal);

        await restarted.AcceptRemoteConflictAsync(restartedConflict);

        Assert.Empty(restarted.Conflicts);
        Assert.DoesNotContain(
            restarted.PendingSaves,
            pending => pending.ObjectId == sealedConflictId);
        Assert.Equal("sealed-hosted", runtime.Plans[sealedConflictId].PlanJson);
        Assert.Contains(
            runtime.Plans.Values,
            plan => plan.Id != sealedConflictId &&
                    plan.PlanJson == "sealed-local" &&
                    !plan.LinkedOrderId.HasValue);
    }

    [Fact]
    public async Task AcceptRemoteTombstoneDeletesInsteadOfApplyingEmptyPayload()
    {
        const string host = "https://profiles.example/api/";
        var profileId = Guid.NewGuid().ToString("D");
        var runtime = new PlanStorageRuntime(
            CreatePlan(Guid.NewGuid().ToString("D"), "fixture", linkedOrderId: null),
            ConnectionSettings(host, profileId));
        var indexedDb = new IndexedDbService(runtime);
        var adapter = new RecordingAdapter(ProfileSyncCollections.Settings);
        var service = new ProfileSyncService(
            new ProfileHostClient(
                new HttpClient(new UnusedHandler()) { BaseAddress = new Uri(host) },
                new ProfileHostClientOptions(host)),
            new ProfileSyncLocalStateService(indexedDb, new ProfileHostClientOptions(host)),
            new WebSettingsService(indexedDb),
            new HostedOrderProjectionStore(),
            [adapter]);
        var tombstone = new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.Settings,
            ObjectId = "ui.accent_color",
            PayloadJson = "{}",
            Revision = 9,
            Deleted = true,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await service.AcceptRemoteConflictAsync(new ProfileSyncConflict(
            tombstone.Collection,
            tombstone.ObjectId,
            LocalRevision: 8,
            RemoteRevision: 9,
            tombstone));

        Assert.Equal(1, adapter.DeleteCount);
        Assert.Empty(adapter.AppliedObjectIds);
    }

    [Fact]
    public async Task LegacyPlanRepairResumesFromRemoteTombstoneAfterInterruptedDelete()
    {
        const string host = "https://profiles.example/api/";
        var profileId = Guid.NewGuid().ToString("D");
        var planId = Guid.NewGuid().ToString("D");
        var runtime = new PlanStorageRuntime(
            CreatePlan(planId, "local-authority", Guid.NewGuid()),
            ConnectionSettings(host, profileId));
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(indexedDb, new ProfileHostClientOptions(host));
        await localState.LoadConnectionSettingsAsync();
        await localState.SaveLastSyncRevisionAsync(profileId, 517);
        await localState.SaveObjectRevisionAsync(profileId, ProfileSyncCollections.Plans, planId, 516);
        await localState.SavePendingSavesAsync(
            profileId,
            [new ProfileSyncPendingSave(ProfileSyncCollections.Plans, planId)]);
        await localState.SaveLinkedPlanSealMigrationCompleteAsync(profileId);
        var handler = new TombstoneResumeHandler(planId);
        var planAdapter = new PlansProfileSyncAdapter(
            indexedDb,
            new WebPlanPersistenceService(indexedDb));
        var service = new ProfileSyncService(
            new ProfileHostClient(
                new HttpClient(handler) { BaseAddress = new Uri(host) },
                new ProfileHostClientOptions(host)),
            localState,
            new WebSettingsService(indexedDb),
            new HostedOrderProjectionStore(),
            [planAdapter]);

        await service.InitializeAsync();

        Assert.Equal(ProfileSyncStage.Ready, service.CurrentStatus.Stage);
        Assert.Empty(service.PendingSaves);
        Assert.Empty(service.Conflicts);
        Assert.Equal(2, handler.PutCount);
        Assert.Equal(0, handler.DeleteCount);
        Assert.Equal(
            518,
            await localState.LoadObjectRevisionAsync(
                profileId,
                ProfileSyncCollections.Plans,
                planId));
    }

    private static ProfileSyncService CreateService(
        string host,
        HttpMessageHandler handler,
        ProfileSyncLocalStateService localState,
        IndexedDbService indexedDb,
        PlansProfileSyncAdapter planAdapter,
        RecordingAdapter orderAdapter) =>
        new(
            new ProfileHostClient(
                new HttpClient(handler) { BaseAddress = new Uri(host) },
                new ProfileHostClientOptions(host)),
            localState,
            new WebSettingsService(indexedDb),
            new HostedOrderProjectionStore(),
            [planAdapter, orderAdapter]);

    private static Dictionary<string, string> ConnectionSettings(string host, string profileId) =>
        new(StringComparer.Ordinal)
        {
            [ProfileSyncSettingsKeys.HostUrl] = JsonSerializer.Serialize(host),
            [ProfileSyncSettingsKeys.AccessKey] = JsonSerializer.Serialize("access-key"),
            [ProfileSyncSettingsKeys.RememberAccessKey] = JsonSerializer.Serialize(true),
            [ProfileSyncSettingsKeys.ConnectedProfileId] = JsonSerializer.Serialize(profileId),
            ["profileHost.connectedProfileName"] = JsonSerializer.Serialize("Test profile")
        };

    private static StoredPlan CreatePlan(string planId, string planJson, Guid? linkedOrderId) =>
        new()
        {
            Id = planId,
            Name = "Linked order plan",
            CreatedAt = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc),
            ModifiedAt = new DateTime(2026, 8, 2, 12, 1, 0, DateTimeKind.Utc),
            SavedAt = new DateTime(2026, 8, 2, 12, 2, 0, DateTimeKind.Utc),
            DataCenter = "Aether",
            PlanJson = planJson,
            LinkedOrderId = linkedOrderId
        };

    private sealed class PlanStorageRuntime : IJSRuntime
    {
        private readonly Dictionary<string, string> _settings;

        public PlanStorageRuntime(
            StoredPlan existing,
            Dictionary<string, string>? settings = null)
        {
            Plans[existing.Id] = existing;
            _settings = settings ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public Dictionary<string, StoredPlan> Plans { get; } = new(StringComparer.Ordinal);
        public StoredPlan Plan => Plans.Values.First();
        public int SaveCount { get; private set; }
        public bool FailSaves { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            object? result = identifier switch
            {
                "IndexedDB.loadPlan" => Plans.GetValueOrDefault((string)args![0]!),
                "IndexedDB.loadPlanSummaries" => Plans.Values.Select(plan => new StoredPlanSummary
                {
                    Id = plan.Id,
                    Name = plan.Name,
                    ModifiedAt = plan.ModifiedAt,
                    SavedAt = plan.SavedAt
                }).ToList(),
                "IndexedDB.savePlan" => Save((StoredPlan)args![0]!),
                "IndexedDB.savePlansBatch" => SavePlansBatch((IReadOnlyList<StoredPlan>)args![0]!),
                "IndexedDB.deletePlan" => DeletePlan((string)args![0]!),
                "IndexedDB.loadAllSettings" => new Dictionary<string, string>(_settings, StringComparer.Ordinal),
                "IndexedDB.loadSetting" => _settings.GetValueOrDefault((string)args![0]!),
                "IndexedDB.loadTradeCompanyProfiles" => new List<TradeCompanyProfile>(),
                "IndexedDB.saveSettingsBatch" => SaveBatch((Dictionary<string, string>)args![0]!),
                "IndexedDB.saveSetting" => SaveSetting((string)args![0]!, (string)args[1]!),
                _ => throw new NotSupportedException(identifier)
            };
            return ValueTask.FromResult((TValue)result!);
        }

        private bool Save(StoredPlan plan)
        {
            SaveCount++;
            if (FailSaves)
            {
                return false;
            }
            Plans[plan.Id] = plan;
            return true;
        }

        private bool DeletePlan(string planId) => Plans.Remove(planId);

        private bool SaveBatch(Dictionary<string, string> values)
        {
            foreach (var (key, value) in values)
            {
                _settings[key] = value;
            }
            return true;
        }

        private bool SavePlansBatch(IReadOnlyList<StoredPlan> plans)
        {
            if (FailSaves)
            {
                return false;
            }
            foreach (var plan in plans)
            {
                Plans[plan.Id] = plan;
            }
            return true;
        }

        private bool SaveSetting(string key, string value)
        {
            _settings[key] = value;
            return true;
        }
    }

    private sealed class RecordingAdapter(string collection) : IProfileSyncCollectionAdapter
    {
        public string Collection { get; } = collection;
        public List<string> AppliedObjectIds { get; } = [];
        public int DeleteCount { get; private set; }

        public Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProfileSyncObjectEnvelope>>([]);

        public Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
        {
            AppliedObjectIds.Add(envelope.ObjectId);
            return Task.CompletedTask;
        }

        public Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
        {
            DeleteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class PagedConflictHandler(
        ProfileSyncObjectEnvelope poison,
        ProfileSyncObjectEnvelope promotion,
        ProfileSyncObjectEnvelope sealedConflict,
        ProfileSyncObjectEnvelope laterPlan,
        ProfileSyncObjectEnvelope laterOrder) : HttpMessageHandler
    {
        private bool _legacyDeleted;
        public int PromotionPutCount { get; private set; }
        public bool PromotionPayloadWasSealed { get; private set; }
        public int LegacyDeleteCount { get; private set; }
        public int LegacyRepublishCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.EndsWith("/profile-host/changes", StringComparison.Ordinal))
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var since = long.Parse(query["sinceRevision"]!);
                var response = since switch
                {
                    513 => Page(514, true, poison),
                    514 => Page(515, true, promotion),
                    515 => Page(516, true, sealedConflict),
                    516 => Page(520, true, laterPlan),
                    520 => Page(521, false, laterOrder),
                    _ => Page(521, false)
                };
                return Task.FromResult(response);
            }

            if (request.Method == HttpMethod.Put)
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(
                        $"/{promotion.ObjectId}",
                        StringComparison.Ordinal))
                {
                    PromotionPutCount++;
                    var put = request.Content!
                        .ReadFromJsonAsync<ProfileSyncPutRequest>(cancellationToken)
                        .GetAwaiter()
                        .GetResult()!;
                    PromotionPayloadWasSealed = ProfileSyncPlanPayloadCodec.Deserialize(
                        put.PayloadJson!,
                        promotion.ObjectId).LinkedOrderId.HasValue;
                    var committed = new ProfileSyncObjectEnvelope
                    {
                        Collection = promotion.Collection,
                        ObjectId = promotion.ObjectId,
                        Revision = 522,
                        PayloadJson = put.PayloadJson,
                        UpdatedAtUtc = DateTime.UtcNow
                    };
                    return Task.FromResult(Ok(new ProfileSyncPutResponse
                    {
                        Success = true,
                        ServerRevision = 522,
                        Object = committed
                    }));
                }

                if (request.RequestUri!.AbsolutePath.EndsWith(
                        $"/{poison.ObjectId}",
                        StringComparison.Ordinal))
                {
                    if (!_legacyDeleted)
                    {
                        var attemptedPromotion = request.Content!
                            .ReadFromJsonAsync<ProfileSyncPutRequest>(cancellationToken)
                            .GetAwaiter()
                            .GetResult()!;
                        Assert.Equal(514, attemptedPromotion.ExpectedRevision);
                        return Task.FromResult(Ok(new ProfileSyncPutResponse
                        {
                            Conflict = true,
                            ServerRevision = 521,
                            RemoteObject = poison,
                            ErrorCode = "linked_plan_promotion_mismatch"
                        }));
                    }

                    LegacyRepublishCount++;
                    var republish = request.Content!
                        .ReadFromJsonAsync<ProfileSyncPutRequest>(cancellationToken)
                        .GetAwaiter()
                        .GetResult()!;
                    Assert.Equal(523, republish.ExpectedRevision);
                    return Task.FromResult(Ok(new ProfileSyncPutResponse
                    {
                        Success = true,
                        ServerRevision = 524,
                        Object = new ProfileSyncObjectEnvelope
                        {
                            Collection = poison.Collection,
                            ObjectId = poison.ObjectId,
                            Revision = 524,
                            PayloadJson = poison.PayloadJson,
                            UpdatedAtUtc = DateTime.UtcNow
                        }
                    }));
                }

                return Task.FromResult(Ok(new ProfileSyncPutResponse
                {
                    Conflict = true,
                    ServerRevision = 521,
                    RemoteObject = sealedConflict,
                    ErrorCode = "immutable_plan_snapshot"
                }));
            }

            if (request.Method == HttpMethod.Delete &&
                request.RequestUri!.AbsolutePath.EndsWith(
                    $"/{poison.ObjectId}",
                    StringComparison.Ordinal))
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                Assert.Equal("514", query["expectedRevision"]);
                _legacyDeleted = true;
                LegacyDeleteCount++;
                return Task.FromResult(Ok(new ProfileSyncPutResponse
                {
                    Success = true,
                    ServerRevision = 523,
                    Object = new ProfileSyncObjectEnvelope
                    {
                        Collection = poison.Collection,
                        ObjectId = poison.ObjectId,
                        Revision = 523,
                        Deleted = true,
                        PayloadJson = "{}",
                        UpdatedAtUtc = DateTime.UtcNow
                    }
                }));
            }

            throw new NotSupportedException(request.RequestUri?.ToString());
        }

        private static HttpResponseMessage Page(
            long revision,
            bool hasMore,
            params ProfileSyncObjectEnvelope[] objects) =>
            Ok(new ProfileSyncChangesResponse
            {
                ServerRevision = revision,
                HasMore = hasMore,
                Objects = objects
            });

        private static HttpResponseMessage Ok<T>(T content) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(content) };
    }

    private sealed class UnusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException(request.RequestUri?.ToString());
    }

    private sealed class TombstoneResumeHandler(string planId) : HttpMessageHandler
    {
        public int PutCount { get; private set; }
        public int DeleteCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Ok(new ProfileSyncChangesResponse
                {
                    ServerRevision = 517,
                    HasMore = false
                }));
            }

            if (request.Method == HttpMethod.Delete)
            {
                DeleteCount++;
                throw new InvalidOperationException("Repair must resume without deleting twice.");
            }

            if (request.Method == HttpMethod.Put)
            {
                PutCount++;
                var put = request.Content!
                    .ReadFromJsonAsync<ProfileSyncPutRequest>(cancellationToken)
                    .GetAwaiter()
                    .GetResult()!;
                if (PutCount == 1)
                {
                    Assert.Equal(516, put.ExpectedRevision);
                    return Task.FromResult(Ok(new ProfileSyncPutResponse
                    {
                        Conflict = true,
                        ServerRevision = 517,
                        RemoteObject = new ProfileSyncObjectEnvelope
                        {
                            Collection = ProfileSyncCollections.Plans,
                            ObjectId = planId,
                            Revision = 517,
                            Deleted = true,
                            PayloadJson = "{}",
                            UpdatedAtUtc = DateTime.UtcNow
                        }
                    }));
                }

                Assert.Equal(517, put.ExpectedRevision);
                return Task.FromResult(Ok(new ProfileSyncPutResponse
                {
                    Success = true,
                    ServerRevision = 518,
                    Object = new ProfileSyncObjectEnvelope
                    {
                        Collection = ProfileSyncCollections.Plans,
                        ObjectId = planId,
                        Revision = 518,
                        PayloadJson = put.PayloadJson,
                        UpdatedAtUtc = DateTime.UtcNow
                    }
                }));
            }

            throw new NotSupportedException(request.RequestUri?.ToString());
        }

        private static HttpResponseMessage Ok<T>(T content) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(content) };
    }
}
