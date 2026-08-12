using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class HostedOrderSyncCoordinatorTests
{
    private const string Host = "https://profiles.example/api/";

    [Fact]
    public async Task SyncQueuesOneListOwnerAdoptionPassWithoutSelectionDemand()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateCommissionOrder();
        var runtime = new OwnerAdoptionRuntime(profileId);
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(
            indexedDb,
            new ProfileHostClientOptions(Host));
        var store = new HostedOrderProjectionStore();
        store.BeginProfileRestore(
            profileId,
            hasTrustedProjection: true,
            lastAppliedRevision: 0,
            DateTime.UtcNow,
            $"{ProfileHostClient.NormalizeHostUrl(Host)}|{profileId}");
        Assert.True(store.TryPublishRemoteOrder(order, 1));
        var profileSync = new ProfileSyncService(
            new ProfileHostClient(
                new HttpClient(new EmptyChangesHandler()),
                new ProfileHostClientOptions(Host)),
            localState,
            new WebSettingsService(indexedDb),
            store,
            []);
        var ownerHandler = new BlockingOwnerHandler(Projection(order, 1, 1));
        var appState = new AppState();
        var dataChangeCount = 0;
        appState.OnStateChanged += change =>
        {
            if (change.HasScope(AppStateChangeScope.TradeOperationsData))
            {
                Interlocked.Increment(ref dataChangeCount);
            }
        };
        await using var coordinator = new HostedOrderSyncCoordinator(
            runtime,
            profileSync,
            localState,
            store,
            new TradeCommissionOperationsClient(
                new HttpClient(ownerHandler) { BaseAddress = new Uri(Host) },
                localState),
            new TradeOperationsPersistenceService(
                indexedDb,
                new TradeCompanyProfilePackageService()),
            appState,
            NullLogger<HostedOrderSyncCoordinator>.Instance);
        SetField(coordinator, "_activeProfileId", profileId);
        SetField(coordinator, "_session", new CancellationTokenSource());
        var metadataRefreshCount = 0;
        profileSync.ProfileMetadataMayHaveChanged += () => metadataRefreshCount++;

        await coordinator.ReceiveProfileRevision(profileId, 1, "leader", 0);
        await ownerHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.ReceiveProfileRevision(profileId, 1, "leader", 0);
        Assert.Equal(1, ownerHandler.RequestCount);

        ownerHandler.Release.TrySetResult();
        await GetField<Task>(coordinator, "_ownerAdoptionPass")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, ownerHandler.RequestCount);
        Assert.Equal(3, dataChangeCount);
        Assert.Equal(1, metadataRefreshCount);
        Assert.Equal(1, store.GetOwnerProjection(order.Id)!.ObjectRevision.Value);
        Assert.Equal(order.Id, runtime.DurableOrder?.Id);
    }

    [Theory]
    [InlineData(OwnerProjectionScenario.AdoptionRequired)]
    [InlineData(OwnerProjectionScenario.AdoptionForbidden)]
    [InlineData(OwnerProjectionScenario.ValidProjection)]
    [InlineData(OwnerProjectionScenario.InvalidProjection)]
    public void OwnerProjectionAdoptionPreservesCanonicalIdentity(
        OwnerProjectionScenario scenario)
    {
        switch (scenario)
        {
            case OwnerProjectionScenario.AdoptionRequired:
                MissingOrStaleOwnerProjectionRequiresAdoption();
                OwnerAuthorizationFailuresUseABoundedRetryDelay();
                TabReplayUsesOwnCursorAndOrderScopeReadinessIsTruthful();
                break;
            case OwnerProjectionScenario.AdoptionForbidden:
                DeletedAndNonCommissionOrdersNeverRequireAdoption();
                break;
            case OwnerProjectionScenario.ValidProjection:
                MatchingProjectionAtCurrentOrNewerRevisionIsAccepted();
                break;
            case OwnerProjectionScenario.InvalidProjection:
                StaleOrWrongIdentityProjectionIsRejected();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static void OwnerAuthorizationFailuresUseABoundedRetryDelay()
    {
        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(ShouldDeferOwnerAuthorizationRetry(now.AddMinutes(5), now));
        Assert.False(ShouldDeferOwnerAuthorizationRetry(now, now));
        Assert.False(ShouldDeferOwnerAuthorizationRetry(now.AddTicks(-1), now));
    }

    private static void MissingOrStaleOwnerProjectionRequiresAdoption()
    {
        var order = CreateCommissionOrder();
        var missing = Snapshot(order, objectRevision: 5, owner: null);
        var stale = Snapshot(order, objectRevision: 5, owner: Projection(order, 4, 8));
        var current = Snapshot(order, objectRevision: 5, owner: Projection(order, 5, 8));

        Assert.True(NeedsOwnerAdoption(missing));
        Assert.True(NeedsOwnerAdoption(stale));
        Assert.False(NeedsOwnerAdoption(current));
    }

    private static void DeletedAndNonCommissionOrdersNeverRequireAdoption()
    {
        var commissionOrder = CreateCommissionOrder();
        var ordinaryOrder = new TradeOrder
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = Guid.NewGuid(),
            Title = "Ordinary order"
        };

        Assert.False(NeedsOwnerAdoption(
            Snapshot(commissionOrder, 5, null) with { Deleted = true }));
        Assert.False(NeedsOwnerAdoption(
            Snapshot(ordinaryOrder, 5, null)));
    }

    private static void MatchingProjectionAtCurrentOrNewerRevisionIsAccepted()
    {
        var order = CreateCommissionOrder();
        var expected = Snapshot(order, objectRevision: 5, owner: null);

        ValidateOwnerProjection(
            expected,
            Projection(order, objectRevision: 5, companyRevision: 8));
        ValidateOwnerProjection(
            expected,
            Projection(order, objectRevision: 6, companyRevision: 9));
    }

    private static void StaleOrWrongIdentityProjectionIsRejected()
    {
        var order = CreateCommissionOrder();
        var expected = Snapshot(order, objectRevision: 5, owner: null);
        var wrongOrder = CreateCommissionOrder(
            orderId: Guid.NewGuid(),
            companyProfileId: order.CompanyProfileId,
            companyId: order.CompanyCommission!.CompanyId,
            commissionId: order.CompanyCommission.CommissionId);
        var wrongCommission = CreateCommissionOrder(
            orderId: order.Id,
            companyProfileId: order.CompanyProfileId,
            companyId: order.CompanyCommission.CompanyId,
            commissionId: Guid.NewGuid());
        var wrongProfile = CreateCommissionOrder(
            orderId: order.Id,
            companyProfileId: Guid.NewGuid(),
            companyId: order.CompanyCommission.CompanyId,
            commissionId: order.CompanyCommission.CommissionId);
        var wrongCompany = CreateCommissionOrder(
            orderId: order.Id,
            companyProfileId: order.CompanyProfileId,
            companyId: new CompanyId(Guid.NewGuid()),
            commissionId: order.CompanyCommission.CommissionId);

        Assert.Throws<InvalidOperationException>(() =>
            ValidateOwnerProjection(
                expected,
                Projection(order, objectRevision: 4, companyRevision: 8)));
        Assert.Throws<InvalidOperationException>(() =>
            ValidateOwnerProjection(
                expected,
                Projection(wrongOrder, objectRevision: 5, companyRevision: 8)));
        Assert.Throws<InvalidOperationException>(() =>
            ValidateOwnerProjection(
                expected,
                Projection(wrongCommission, objectRevision: 5, companyRevision: 8)));
        Assert.Throws<InvalidOperationException>(() =>
            ValidateOwnerProjection(
                expected,
                Projection(wrongProfile, objectRevision: 5, companyRevision: 8)));
        Assert.Throws<InvalidOperationException>(() =>
            ValidateOwnerProjection(
                expected,
                Projection(wrongCompany, objectRevision: 5, companyRevision: 8)));
        Assert.Throws<InvalidOperationException>(() =>
            ValidateOwnerProjection(
                expected,
                Projection(order, objectRevision: 5, companyRevision: 0)));
    }

    private static void TabReplayUsesOwnCursorAndOrderScopeReadinessIsTruthful()
    {
        Assert.Equal(405L, ResolveSyncStartRevision(446, 405));
        Assert.Equal(400L, ResolveSyncStartRevision(400, 405));
        Assert.Equal(446L, ResolveSyncStartRevision(446, null));

        const string profileId = "7aaf1a42-43d0-4c63-a167-83e647ec04d1";
        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var state = HostedOrderRestoreState.BeginProfile(
                profileId,
                hasTrustedProjection: false,
                lastAppliedRevision: 405,
                scopeChanged: false,
                now)
            .Apply(
                new ProfileSyncStatus(
                    true,
                    true,
                    446,
                    0,
                    0,
                    now,
                    "Orders restored")
                {
                    ProfileId = profileId,
                    Stage = ProfileSyncStage.ApplyingChanges,
                    OrderScopeReady = true
                },
                now.AddSeconds(1));

        Assert.Equal(HostedOrderRestoreStage.Ready, state.Stage);
        Assert.True(state.HasTrustedProjection);
        Assert.True(state.CanMutate);

        state = state.Apply(
            new ProfileSyncStatus(
                true,
                false,
                446,
                0,
                0,
                now,
                "Background sync interrupted")
            {
                ProfileId = profileId,
                Stage = ProfileSyncStage.Failed,
                Failure = ProfileSyncFailure.Offline,
                OrderScopeReady = true
            },
            now.AddSeconds(2));

        Assert.Equal(HostedOrderRestoreStage.Ready, state.Stage);
        Assert.True(state.ShowsCompleteProjection);
        Assert.False(state.CanMutate);
    }

    private static bool NeedsOwnerAdoption(HostedOrderProjectionSnapshot snapshot) =>
        (bool)InvokePolicy(nameof(NeedsOwnerAdoption), snapshot)!;

    private static bool ShouldDeferOwnerAuthorizationRetry(
        DateTime retryAfterUtc,
        DateTime nowUtc) =>
        (bool)InvokePolicy(
            nameof(ShouldDeferOwnerAuthorizationRetry),
            retryAfterUtc,
            nowUtc)!;

    private static void ValidateOwnerProjection(
        HostedOrderProjectionSnapshot expected,
        CompanyCommissionOwnerProjection projection) =>
        InvokePolicy(nameof(ValidateOwnerProjection), expected, projection);

    private static long ResolveSyncStartRevision(
        long persistedRevision,
        long? replayAfterRevision) =>
        (long)InvokeSyncPolicy(
            nameof(ResolveSyncStartRevision),
            persistedRevision,
            replayAfterRevision)!;

    private static object? InvokePolicy(string name, params object[] arguments)
    {
        var method = typeof(HostedOrderSyncCoordinator).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(HostedOrderSyncCoordinator).FullName, name);
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw exception.InnerException;
        }
    }

    private static object? InvokeSyncPolicy(string name, params object?[] arguments)
    {
        var method = typeof(ProfileSyncService).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(ProfileSyncService).FullName, name);
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw exception.InnerException;
        }
    }

    private static void SetField<T>(
        HostedOrderSyncCoordinator coordinator,
        string name,
        T value)
    {
        var field = typeof(HostedOrderSyncCoordinator).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(HostedOrderSyncCoordinator).FullName, name);
        field.SetValue(coordinator, value);
    }

    private static T GetField<T>(
        HostedOrderSyncCoordinator coordinator,
        string name)
    {
        var field = typeof(HostedOrderSyncCoordinator).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(HostedOrderSyncCoordinator).FullName, name);
        return (T)field.GetValue(coordinator)!;
    }

    private static HostedOrderProjectionSnapshot Snapshot(
        TradeOrder order,
        long objectRevision,
        CompanyCommissionOwnerProjection? owner) =>
        new(
            order.Id,
            order.CompanyProfileId,
            objectRevision,
            owner?.CompanyRevision.Value,
            order,
            owner,
            Deleted: false);

    private static CompanyCommissionOwnerProjection Projection(
        TradeOrder order,
        long objectRevision,
        long companyRevision) =>
        new()
        {
            Order = order,
            ObjectRevision = new CompanyRecordRevision(objectRevision),
            CompanyRevision = new CompanyRecordRevision(companyRevision)
        };

    private static TradeOrder CreateCommissionOrder(
        Guid? orderId = null,
        Guid? companyProfileId = null,
        CompanyId? companyId = null,
        Guid? commissionId = null)
    {
        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        return new TradeOrder
        {
            Id = orderId ?? Guid.NewGuid(),
            CompanyProfileId = companyProfileId ?? Guid.NewGuid(),
            Title = "Canonical commission",
            CompanyCommission = new TradeCompanyCommission
            {
                CommissionId = commissionId ?? Guid.NewGuid(),
                CompanyId = companyId ?? new CompanyId(Guid.NewGuid()),
                CommissionerActorId = "commissioner",
                Reference = "TEST-001",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CurrentTermsVersion = 1,
                TermsVersions =
                [
                    new CompanyCommissionTermsVersion
                    {
                        Version = 1,
                        CreatedAtUtc = now,
                        CreatedBy = new("commissioner", CompanyCommissionActorKind.Commissioner),
                        Payment = new(
                            CompanyCommissionPaymentSchedule.OnDelivery,
                            "Test",
                            0,
                            0,
                            0,
                            0),
                        PricingEvidence = new("test", "test", "test", now)
                    }
                ],
                PublicMetadata = new CompanyCommissionPublicMetadata
                {
                    PublicBriefId = "test-001",
                    ViewState = CompanyCommissionPublicViewState.Published
                },
                ActiveClaimCapabilityRevision = 1,
                Gates = new CompanyCommissionGateState(
                    new CompanyCommissionIdentityClearance(CompanyCommissionClearanceState.NotRequired),
                    new CompanyCommissionPaymentClearance(CompanyCommissionClearanceState.NotRequired),
                    new CompanyCommissionMaterialClearance(
                        CompanyCommissionClearanceState.NotRequired,
                        [])),
                DeliveryReadiness = new CompanyCommissionDeliveryReadiness(false),
                SettlementState = CompanyCommissionSettlementState.NotDue
            }
        };
    }

    public enum OwnerProjectionScenario
    {
        AdoptionRequired,
        AdoptionForbidden,
        ValidProjection,
        InvalidProjection
    }

    private sealed class EmptyChangesHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.EndsWith("/profile-host/changes", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ProfileSyncChangesResponse
                {
                    ServerRevision = 1
                })
            });
        }
    }

    private sealed class BlockingOwnerHandler(
        CompanyCommissionOwnerProjection projection) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.EndsWith("/owner", request.RequestUri!.AbsolutePath);
            Interlocked.Increment(ref _requestCount);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(projection)
            };
        }
    }

    private sealed class OwnerAdoptionRuntime(string profileId) : IJSRuntime
    {
        private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal)
        {
            [ProfileSyncSettingsKeys.HostUrl] = JsonSerializer.Serialize(Host),
            [ProfileSyncSettingsKeys.AccessKey] = JsonSerializer.Serialize("access-key"),
            [ProfileSyncSettingsKeys.RememberAccessKey] = JsonSerializer.Serialize(true),
            [ProfileSyncSettingsKeys.ConnectedProfileId] = JsonSerializer.Serialize(profileId)
        };

        public TradeOrder? DurableOrder { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            object? result = identifier switch
            {
                "IndexedDB.loadAllSettings" => new Dictionary<string, string>(_settings),
                "IndexedDB.loadSetting" => _settings.GetValueOrDefault((string)args![0]!),
                "IndexedDB.saveSettingsBatch" => SaveBatch((Dictionary<string, string>)args![0]!),
                "IndexedDB.saveSetting" => SaveSetting((string)args![0]!, (string)args[1]!),
                "IndexedDB.loadAllTradeOrders" => new List<TradeOrder>(),
                "IndexedDB.saveTradeOrder" => SaveOrder((TradeOrder)args![0]!),
                "IndexedDB.deleteTradeOrder" => true,
                _ => throw new NotSupportedException(identifier)
            };
            return ValueTask.FromResult((TValue)result!);
        }

        private bool SaveBatch(Dictionary<string, string> values)
        {
            foreach (var (key, value) in values)
            {
                _settings[key] = value;
            }
            return true;
        }

        private bool SaveSetting(string key, string value)
        {
            _settings[key] = value;
            return true;
        }

        private bool SaveOrder(TradeOrder order)
        {
            DurableOrder = order;
            return true;
        }
    }

}
