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

public sealed class HostedOrderProjectionStoreTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ProjectionStoreScenario.CanonicalRevisionAndTombstone)]
    [InlineData(ProjectionStoreScenario.CompanyProfileIsImmutable)]
    [InlineData(ProjectionStoreScenario.ProfileResetClearsRevisionHistory)]
    [InlineData(ProjectionStoreScenario.OwnerUpgradeAtSameRevision)]
    [InlineData(ProjectionStoreScenario.SameProfileReconnect)]
    [InlineData(ProjectionStoreScenario.ScopeChange)]
    [InlineData(ProjectionStoreScenario.RestoreRevisionCannotRollBack)]
    [InlineData(ProjectionStoreScenario.CompanySnapshotComposition)]
    [InlineData(ProjectionStoreScenario.SameProfileConnectionReplacement)]
    [InlineData(ProjectionStoreScenario.ConnectionScopePathCase)]
    [InlineData(ProjectionStoreScenario.SameRevisionOwnerPersistence)]
    [InlineData(ProjectionStoreScenario.LiveTombstonePersistence)]
    [InlineData(ProjectionStoreScenario.OwnerTombstonePersistence)]
    [InlineData(ProjectionStoreScenario.CenterOperationWinner)]
    [InlineData(ProjectionStoreScenario.CenterOperationAuthoritySwitch)]
    public async Task ProjectionStorePreservesCanonicalIdentityAndRestoreTruth(
        ProjectionStoreScenario scenario)
    {
        switch (scenario)
        {
            case ProjectionStoreScenario.CanonicalRevisionAndTombstone:
                NewerCanonicalOrderWinsAndTombstoneCannotRollBack();
                break;
            case ProjectionStoreScenario.CompanyProfileIsImmutable:
                SameOrderCannotMoveBetweenCompanyProfiles();
                break;
            case ProjectionStoreScenario.ProfileResetClearsRevisionHistory:
                TombstoneWinsSameRevisionAndProfileResetClearsHistory();
                break;
            case ProjectionStoreScenario.OwnerUpgradeAtSameRevision:
                SameObjectRevisionOwnerUpgradeIsAcceptedAndNotified();
                break;
            case ProjectionStoreScenario.SameProfileReconnect:
                SameProfileReconnectRetainsReadyProjection();
                break;
            case ProjectionStoreScenario.ScopeChange:
                ScopeChangeClearsProjectionAndDoesNotCarryRevisionFloor();
                break;
            case ProjectionStoreScenario.RestoreRevisionCannotRollBack:
                RestoreStateCannotRollBackAppliedRevision();
                break;
            case ProjectionStoreScenario.CompanySnapshotComposition:
                SharedSnapshotCanBeComposedWithoutAPageCache();
                break;
            case ProjectionStoreScenario.SameProfileConnectionReplacement:
                SameProfileConnectionReplacementInvalidatesCapturedAuthorityAndRevisionFloor();
                break;
            case ProjectionStoreScenario.ConnectionScopePathCase:
                ConnectionScopePathCaseIsAuthoritySignificant();
                break;
            case ProjectionStoreScenario.SameRevisionOwnerPersistence:
                await PersistenceReconcilesSameRevisionOwnerUpgrade();
                break;
            case ProjectionStoreScenario.LiveTombstonePersistence:
                await PersistenceReconcilesNewerTombstoneAfterBlockedLiveWrite();
                break;
            case ProjectionStoreScenario.OwnerTombstonePersistence:
                await OwnerPersistenceReconcilesTombstoneThatArrivesDuringDurableWrite();
                break;
            case ProjectionStoreScenario.CenterOperationWinner:
                await CenterOperationReconcilesNewerOwnerDuringDurableWrite();
                break;
            case ProjectionStoreScenario.CenterOperationAuthoritySwitch:
                await CenterOperationRejectsHostAndProfileReplacement();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static void SameProfileConnectionReplacementInvalidatesCapturedAuthorityAndRevisionFloor()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Old authority");
        var resets = 0;
        store.Reset += () => resets++;
        store.BeginProfileRestore(
            profileId,
            false,
            20,
            Now,
            $"https://first.example/|{profileId}");
        Assert.True(store.TryPublishRemoteOrder(order, 20));
        var oldAuthority = store.CaptureAuthorityScope();

        store.BeginProfileRestore(
            profileId,
            false,
            2,
            Now.AddSeconds(1),
            $"https://replacement.example/|{profileId}");

        Assert.Null(store.Get(order.Id));
        Assert.Equal(1, resets);
        Assert.Equal(2, store.RestoreState.LastAppliedRevision);
        Assert.Equal(HostedOrderRestoreStage.ScopeChanging, store.RestoreState.Stage);
        Assert.Equal(
            HostedOrderCommittedProjectionResult.ScopeChanged,
            store.TryAdoptCommittedOrder(oldAuthority, order, 21));

        var replacement = CreateOrder(order.Id, order.CompanyProfileId, "New authority");
        Assert.True(store.TryPublishRemoteOrder(replacement, 3));
        store.BeginProfileRestore(
            profileId,
            false,
            1,
            Now.AddSeconds(2),
            $"https://replacement.example/|{profileId}");

        Assert.Same(replacement, store.Get(order.Id)?.Order);
        Assert.Equal(3, store.RestoreState.LastAppliedRevision);
        Assert.Equal(1, resets);
    }

    private static void ConnectionScopePathCaseIsAuthoritySignificant()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Upper path authority");
        store.BeginProfileRestore(
            profileId,
            false,
            8,
            Now,
            $"https://profiles.example/api/A|{profileId}");
        Assert.True(store.TryPublishRemoteOrder(order, 8));
        var upperPathAuthority = store.CaptureAuthorityScope();

        store.BeginProfileRestore(
            profileId,
            false,
            1,
            Now.AddSeconds(1),
            $"https://profiles.example/api/a|{profileId}");

        Assert.Null(store.Get(order.Id));
        Assert.Equal(1, store.RestoreState.LastAppliedRevision);
        Assert.Equal(
            HostedOrderCommittedProjectionResult.ScopeChanged,
            store.TryAdoptCommittedOrder(upperPathAuthority, order, 9));
    }

    private static async Task PersistenceReconcilesSameRevisionOwnerUpgrade()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Canonical");
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            Now,
            $"https://profiles.example/|{profileId}");
        Assert.True(store.TryPublishRemoteOrder(order, 4));
        var authority = store.CaptureAuthorityScope();
        var firstWriteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new List<HostedOrderProjectionSnapshot>();

        var persistence = store.AdoptAndPersistCommittedOrderAsync(
            authority,
            order,
            4,
            async candidate =>
            {
                persisted.Add(candidate);
                if (persisted.Count == 1)
                {
                    firstWriteEntered.SetResult();
                    await releaseFirstWrite.Task;
                }
            });
        await firstWriteEntered.Task;
        var owner = new CompanyCommissionOwnerProjection
        {
            Order = order,
            ObjectRevision = new CompanyRecordRevision(4),
            CompanyRevision = new CompanyRecordRevision(9)
        };
        Assert.True(store.TryPublishOwner(owner));
        releaseFirstWrite.SetResult();

        Assert.Equal(HostedOrderCommittedProjectionResult.AlreadyCurrent, await persistence);
        Assert.Equal(2, persisted.Count);
        Assert.Null(persisted[0].OwnerProjection);
        Assert.Same(owner, persisted[1].OwnerProjection);
        Assert.Equal(4, store.RestoreState.LastAppliedRevision);
    }

    private static async Task PersistenceReconcilesNewerTombstoneAfterBlockedLiveWrite()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Live revision");
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            Now,
            $"https://profiles.example/|{profileId}");
        var authority = store.CaptureAuthorityScope();
        var firstWriteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new List<HostedOrderProjectionSnapshot>();

        var persistence = store.AdoptAndPersistCommittedOrderAsync(
            authority,
            order,
            5,
            async candidate =>
            {
                persisted.Add(candidate);
                if (persisted.Count == 1)
                {
                    firstWriteEntered.SetResult();
                    await releaseFirstWrite.Task;
                }
            });
        await firstWriteEntered.Task;
        Assert.True(store.TryPublishTombstone(order.Id, 6, order.CompanyProfileId));
        releaseFirstWrite.SetResult();

        Assert.Equal(HostedOrderCommittedProjectionResult.Adopted, await persistence);
        Assert.Equal([false, true], persisted.Select(candidate => candidate.Deleted));
        Assert.True(store.Get(order.Id)?.Deleted);
        Assert.Equal(6, store.RestoreState.LastAppliedRevision);
    }

    private static async Task OwnerPersistenceReconcilesTombstoneThatArrivesDuringDurableWrite()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Owner candidate");
        store.BeginProfileRestore(
            profileId,
            false,
            4,
            Now,
            $"https://profiles.example/|{profileId}");
        Assert.True(store.TryPublishRemoteOrder(order, 4));
        var authority = store.CaptureAuthorityScope();
        var owner = new CompanyCommissionOwnerProjection
        {
            Order = order,
            ObjectRevision = new CompanyRecordRevision(4),
            CompanyRevision = new CompanyRecordRevision(9)
        };
        var firstWriteEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new List<HostedOrderProjectionSnapshot>();

        var persistence = store.AdoptAndPersistCommittedOwnerAsync(
            authority,
            owner,
            async candidate =>
            {
                persisted.Add(candidate);
                if (persisted.Count == 1)
                {
                    firstWriteEntered.SetResult();
                    await releaseFirstWrite.Task;
                }
            });
        await firstWriteEntered.Task;
        Assert.True(store.TryPublishTombstone(order.Id, 5, order.CompanyProfileId));
        releaseFirstWrite.SetResult();

        Assert.Equal(HostedOrderCommittedProjectionResult.Adopted, await persistence);
        Assert.Equal([false, true], persisted.Select(candidate => candidate.Deleted));
        Assert.True(store.Get(order.Id)?.Deleted);
        Assert.Equal(5, store.RestoreState.LastAppliedRevision);
    }

    private static async Task CenterOperationReconcilesNewerOwnerDuringDurableWrite()
    {
        var fixture = await CenterOperationFixture.CreateAsync();
        fixture.Handler.Projection = fixture.Owner("Revision five", 5, 9);
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.BeforeSaveTradeOrderAsync = async order => { if (order.Title != "Revision five") return; writeEntered.TrySetResult(); await releaseWrite.Task; };
        var operation = fixture.Service.AcceptDeliveryAsync(fixture.Current);
        await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Store.TryPublishOwner(fixture.Owner("Revision six", 6, 10)));
        releaseWrite.SetResult();
        Assert.True((await operation).Success);
        Assert.Equal("Revision six", fixture.Runtime.DurableOrder?.Title);
        Assert.Equal(6, fixture.Store.Get(fixture.Current.Order.Id)?.ObjectRevision);
        Assert.Equal(6, await fixture.LocalState.LoadObjectRevisionAsync(ProfileSyncCollections.TradeOrders, fixture.Current.Order.Id.ToString("D")));
    }

    private static async Task CenterOperationRejectsHostAndProfileReplacement()
    {
        foreach (var replaceProfile in new[] { false, true })
        {
            var fixture = await CenterOperationFixture.CreateAsync();
            fixture.Handler.Projection = fixture.Owner("Revision five", 5, 9);
            fixture.Handler.BeforeResponse = () =>
            {
                if (replaceProfile) { var nextProfileId = Guid.NewGuid().ToString("D");
                    fixture.Runtime.SaveRawSetting(ProfileSyncSettingsKeys.ConnectedProfileId, JsonSerializer.Serialize(nextProfileId));
                    fixture.Store.BeginProfileRestore(nextProfileId, false, 0, Now, ConnectionScope(nextProfileId, CenterOperationFixture.Host)); }
                else fixture.Runtime.SaveRawSetting(ProfileSyncSettingsKeys.HostUrl, JsonSerializer.Serialize("https://replacement.example/api/"));
            };
            var result = await fixture.Service.AcceptDeliveryAsync(fixture.Current);
            Assert.False(result.Success);
            Assert.Contains("authority", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, fixture.Runtime.SaveTradeOrderCount);
        }
    }

    private static void NewerCanonicalOrderWinsAndTombstoneCannotRollBack()
    {
        var store = new HostedOrderProjectionStore();
        var orderId = Guid.NewGuid();
        var companyProfileId = Guid.NewGuid();
        var revisions = new List<long>();
        store.Changed += projection => revisions.Add(projection.ObjectRevision);

        Assert.True(store.TryPublishRemoteOrder(
            CreateOrder(orderId, companyProfileId, "Revision two"),
            objectRevision: 2));
        Assert.False(store.TryPublishRemoteOrder(
            CreateOrder(orderId, companyProfileId, "Stale revision one"),
            objectRevision: 1));
        Assert.True(store.TryPublishTombstone(orderId, objectRevision: 3));
        Assert.False(store.TryPublishRemoteOrder(
            CreateOrder(orderId, companyProfileId, "Stale after delete"),
            objectRevision: 2));

        var current = Assert.IsType<HostedOrderProjectionSnapshot>(store.Get(orderId));
        Assert.True(current.Deleted);
        Assert.Equal(3, current.ObjectRevision);
        Assert.Equal([2L, 3L], revisions);
    }

    private static void SameOrderCannotMoveBetweenCompanyProfiles()
    {
        var store = new HostedOrderProjectionStore();
        var orderId = Guid.NewGuid();
        Assert.True(store.TryPublishRemoteOrder(
            CreateOrder(orderId, Guid.NewGuid(), "Original company"),
            objectRevision: 1));

        Assert.Throws<InvalidOperationException>(() =>
            store.TryPublishRemoteOrder(
                CreateOrder(orderId, Guid.NewGuid(), "Different company"),
                objectRevision: 2));
    }

    private static void TombstoneWinsSameRevisionAndProfileResetClearsHistory()
    {
        var store = new HostedOrderProjectionStore();
        var orderId = Guid.NewGuid();
        var companyProfileId = Guid.NewGuid();
        store.ResetForProfile("profile-one");

        Assert.True(store.TryPublishRemoteOrder(
            CreateOrder(orderId, companyProfileId, "Live"),
            objectRevision: 3));
        Assert.True(store.TryPublishTombstone(orderId, objectRevision: 3));
        Assert.False(store.TryPublishRemoteOrder(
            CreateOrder(orderId, companyProfileId, "Resurrected"),
            objectRevision: 3));

        store.ResetForProfile("profile-two");
        Assert.Null(store.Get(orderId));
        Assert.True(store.TryPublishRemoteOrder(
            CreateOrder(orderId, Guid.NewGuid(), "New profile"),
            objectRevision: 1));
    }

    private static void SameObjectRevisionOwnerUpgradeIsAcceptedAndNotified()
    {
        var store = new HostedOrderProjectionStore();
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Canonical");
        var notifications = 0;
        store.Changed += _ => notifications++;

        Assert.True(store.TryPublishRemoteOrder(order, objectRevision: 4));
        Assert.True(store.TryPublishOwner(new CompanyCommissionOwnerProjection
        {
            Order = order,
            ObjectRevision = new CompanyRecordRevision(4),
            CompanyRevision = new CompanyRecordRevision(9)
        }));

        Assert.NotNull(store.GetOwnerProjection(order.Id));
        Assert.Equal(2, notifications);
    }

    private static void SameProfileReconnectRetainsReadyProjection()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        var connectionScopeId = $"https://profiles.example/|{profileId}";
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Trusted order");
        store.BeginProfileRestore(profileId, false, 0, Now, connectionScopeId);
        Assert.True(store.TryPublishRemoteOrder(order, objectRevision: 4));
        Assert.True(store.TryPublishRestoreState(store.RestoreState.Apply(
            ReadyStatus(profileId, revision: 4),
            Now.AddSeconds(1))));

        store.BeginProfileRestore(profileId, false, 4, Now.AddSeconds(2), connectionScopeId);

        Assert.Same(order, store.Get(order.Id)?.Order);
        Assert.Equal(HostedOrderRestoreStage.Reconnecting, store.RestoreState.Stage);
        Assert.True(store.RestoreState.ShowsCompleteProjection);
    }

    private static void ScopeChangeClearsProjectionAndDoesNotCarryRevisionFloor()
    {
        var store = new HostedOrderProjectionStore();
        var firstProfile = Guid.NewGuid().ToString("D");
        var secondProfile = Guid.NewGuid().ToString("D");
        var order = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Old scope");
        store.BeginProfileRestore(
            firstProfile,
            false,
            20,
            Now,
            $"https://profiles.example/|{firstProfile}");
        Assert.True(store.TryPublishRemoteOrder(order, objectRevision: 20));

        store.BeginProfileRestore(
            secondProfile,
            false,
            2,
            Now.AddSeconds(1),
            $"https://profiles.example/|{secondProfile}");

        Assert.Null(store.Get(order.Id));
        Assert.Equal(HostedOrderRestoreStage.ScopeChanging, store.RestoreState.Stage);
        Assert.Equal(2, store.RestoreState.LastAppliedRevision);
        Assert.False(store.RestoreState.CanShowAuthoritativeEmpty);
    }

    private static void RestoreStateCannotRollBackAppliedRevision()
    {
        var store = new HostedOrderProjectionStore();
        var profileId = Guid.NewGuid().ToString("D");
        store.BeginProfileRestore(
            profileId,
            false,
            7,
            Now,
            $"https://profiles.example/|{profileId}");

        Assert.False(store.TryPublishRestoreState(store.RestoreState with
        {
            LastAppliedRevision = 6,
            Stage = HostedOrderRestoreStage.Ready
        }));
        Assert.Equal(7, store.RestoreState.LastAppliedRevision);
        Assert.False(store.RestoreState.IsAuthoritative);
    }

    private static void SharedSnapshotCanBeComposedWithoutAPageCache()
    {
        var store = new HostedOrderProjectionStore();
        var companyProfileId = Guid.NewGuid();
        var included = CreateOrder(Guid.NewGuid(), companyProfileId, "Included");
        var other = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), "Other company");
        Assert.True(store.TryPublishRemoteOrder(included, 2));
        Assert.True(store.TryPublishRemoteOrder(other, 3));

        var snapshot = Assert.Single(store.GetAll(companyProfileId));

        Assert.Same(included, snapshot.Order);
        Assert.Equal(2, store.GetAll().Count);
    }

    private static ProfileSyncStatus ReadyStatus(string profileId, long revision) =>
        new(
            IsConnected: true,
            HostReachable: true,
            LastSyncRevision: revision,
            PendingCount: 0,
            ConflictCount: 0,
            LastSyncedAtUtc: Now,
            Message: "Synced")
        {
            ProfileId = profileId,
            Stage = ProfileSyncStage.Ready
        };

    private static TradeOrder CreateOrder(
        Guid orderId,
        Guid companyProfileId,
        string title) =>
        new()
        {
            Id = orderId,
            CompanyProfileId = companyProfileId,
            Title = title
        };

    private static string ConnectionScope(string profileId, string host) =>
        $"{ProfileHostClient.NormalizeHostUrl(host)}|{profileId}";

    private sealed record CenterOperationFixture(CompanyCommissionOwnerProjection Current, HostedOrderProjectionStore Store, CenterOperationRuntime Runtime,
        ProfileSyncLocalStateService LocalState, OwnerMutationHandler Handler, TradeCommissionOperationsService Service)
    {
        public const string Host = "https://profiles.example/api/";
        public CompanyCommissionOwnerProjection Owner(string title, long orderRevision, long companyRevision) => new()
            { Order = new TradeOrder { Id = Current.Order.Id, CompanyProfileId = Current.Order.CompanyProfileId, Title = title, CompanyCommission = Current.Order.CompanyCommission },
                ObjectRevision = new(orderRevision), CompanyRevision = new(companyRevision) };
        public static async Task<CenterOperationFixture> CreateAsync()
        {
            var profileId = Guid.NewGuid().ToString("D");
            var current = new CompanyCommissionOwnerProjection { Order = CreateCommissionOrder(Guid.NewGuid()), ObjectRevision = new(4), CompanyRevision = new(8) };
            var runtime = new CenterOperationRuntime(profileId);
            var indexedDb = new IndexedDbService(runtime);
            var localState = new ProfileSyncLocalStateService(indexedDb, new ProfileHostClientOptions(Host));
            await localState.LoadConnectionSettingsAsync();
            var store = new HostedOrderProjectionStore();
            store.BeginProfileRestore(profileId, false, 4, Now, ConnectionScope(profileId, Host));
            Assert.True(store.TryPublishOwner(current));
            var unusedClient = new HttpClient(new UnusedHandler()) { BaseAddress = new Uri(Host) };
            var profileSync = new ProfileSyncService(new ProfileHostClient(unusedClient, new ProfileHostClientOptions(Host)), localState, new WebSettingsService(indexedDb), store, []);
            typeof(ProfileSyncService).GetProperty(nameof(ProfileSyncService.CurrentStatus), BindingFlags.Instance | BindingFlags.Public)!.SetValue(
                profileSync, new ProfileSyncStatus(true, true, 4, 0, 0, Now, "Synced") { ProfileId = profileId, Stage = ProfileSyncStage.Ready });
            var handler = new OwnerMutationHandler { Projection = current };
            var service = new TradeCommissionOperationsService(new TradeCommissionOperationsClient(new HttpClient(handler) { BaseAddress = new Uri(Host) }, localState),
                new TradeCompanyCollaborationClient(unusedClient, localState), new TradeOperationsPersistenceService(indexedDb, new TradeCompanyProfilePackageService()),
                localState, profileSync, store, new AppState());
            return new(current, store, runtime, localState, handler, service);
        }
        private static TradeOrder CreateCommissionOrder(Guid companyProfileId) => new()
        {
            Id = Guid.NewGuid(), CompanyProfileId = companyProfileId, Title = "Revision four",
            CompanyCommission = new TradeCompanyCommission { CommissionId = Guid.NewGuid(), CompanyId = new(companyProfileId), CommissionerActorId = "commissioner",
                Reference = "TEST-001", CreatedAtUtc = Now, UpdatedAtUtc = Now, CurrentTermsVersion = 1,
                TermsVersions = [new CompanyCommissionTermsVersion { Version = 1, CreatedAtUtc = Now, CreatedBy = new("commissioner", CompanyCommissionActorKind.Commissioner),
                    Payment = new(CompanyCommissionPaymentSchedule.OnDelivery, "Test", 0, 0, 0, 0), PricingEvidence = new("test", "test", "test", Now) }],
                PublicMetadata = new() { PublicBriefId = "test-001", ViewState = CompanyCommissionPublicViewState.Published }, ActiveClaimCapabilityRevision = 1,
                Gates = new(new(CompanyCommissionClearanceState.NotRequired), new(CompanyCommissionClearanceState.NotRequired), new(CompanyCommissionClearanceState.NotRequired, [])),
                DeliveryReadiness = new(true), SettlementState = CompanyCommissionSettlementState.NotDue }
        };
    }
    private sealed class OwnerMutationHandler : HttpMessageHandler
    {
        public required CompanyCommissionOwnerProjection Projection { get; set; }
        public Action? BeforeResponse { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            BeforeResponse?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { Status = CompanyCommissionMutationStatus.Applied,
                Order = Projection.Order, Activity = (CompanyCommissionActivityEvent?)null, ErrorCode = (string?)null, ErrorMessage = (string?)null, Projection, ClaimUrl = (string?)null }) });
        }
    }
    private sealed class UnusedHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new NotSupportedException(request.RequestUri?.ToString());
    }
    private sealed class CenterOperationRuntime(string profileId) : IJSRuntime
    {
        private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal) {
            [ProfileSyncSettingsKeys.HostUrl] = JsonSerializer.Serialize(CenterOperationFixture.Host), [ProfileSyncSettingsKeys.AccessKey] = JsonSerializer.Serialize("access-key"),
            [ProfileSyncSettingsKeys.RememberAccessKey] = JsonSerializer.Serialize(true), [ProfileSyncSettingsKeys.ConnectedProfileId] = JsonSerializer.Serialize(profileId) };
        public int SaveTradeOrderCount { get; private set; } public TradeOrder? DurableOrder { get; private set; }
        public Func<TradeOrder, Task>? BeforeSaveTradeOrderAsync { get; set; }
        public void SaveRawSetting(string key, string value) => _settings[key] = value;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "IndexedDB.saveTradeOrder") return SaveOrderAsync<TValue>((TradeOrder)args![0]!);
            object? result = identifier switch { "IndexedDB.loadAllSettings" => new Dictionary<string, string>(_settings), "IndexedDB.loadSetting" => _settings.GetValueOrDefault((string)args![0]!),
                "IndexedDB.saveSettingsBatch" => SaveBatch((Dictionary<string, string>)args![0]!), "IndexedDB.saveSetting" => SaveSetting((string)args![0]!, (string)args[1]!), _ => throw new NotSupportedException(identifier) };
            return ValueTask.FromResult((TValue)result!);
        }
        private bool SaveBatch(Dictionary<string, string> values) { foreach (var (key, value) in values) _settings[key] = value; return true; }
        private bool SaveSetting(string key, string value) { _settings[key] = value; return true; }
        private async ValueTask<TValue> SaveOrderAsync<TValue>(TradeOrder order) {
            if (BeforeSaveTradeOrderAsync != null) await BeforeSaveTradeOrderAsync(order);
            SaveTradeOrderCount++; DurableOrder = order; return (TValue)(object)true;
        }
    }

    public enum ProjectionStoreScenario {
        CanonicalRevisionAndTombstone, CompanyProfileIsImmutable, ProfileResetClearsRevisionHistory, OwnerUpgradeAtSameRevision, SameProfileReconnect,
        ScopeChange, RestoreRevisionCannotRollBack, CompanySnapshotComposition, SameProfileConnectionReplacement, ConnectionScopePathCase,
        SameRevisionOwnerPersistence, LiveTombstonePersistence, OwnerTombstonePersistence, CenterOperationWinner, CenterOperationAuthoritySwitch }
}
