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
    public async Task CrossCompanyOwnerResponsePreservesTheLastKnownBoard()
    {
        var profileId = Guid.NewGuid().ToString("D");
        var order = CreateCommissionOrder();
        var runtime = new OwnerAdoptionRuntime(profileId);
        runtime.SeedDurableOrder(order);
        var indexedDb = new IndexedDbService(runtime);
        var localState = new ProfileSyncLocalStateService(
            indexedDb,
            new ProfileHostClientOptions(Host));
        await SeedOrderRevisionAsync(localState, order);
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
        var ownerHandler = new InvalidComparisonHandler();
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
            new AppState(),
            NullLogger<HostedOrderSyncCoordinator>.Instance);
        SetField(coordinator, "_activeProfileId", profileId);
        SetField(coordinator, "_session", new CancellationTokenSource());

        await coordinator.ReceiveProfileRevision(profileId, 1, "leader", 0);
        await ownerHandler.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (GetField<Task?>(coordinator, "_ownerAdoptionPass") is { } ownerPass)
        {
            await ownerPass.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Same(order, store.Get(order.Id)?.Order);
        Assert.Null(store.GetOwnerProjection(order.Id));
        Assert.Equal(HostedOrderDisplayState.LastKnown, store.Get(order.Id)?.DisplayState);
        Assert.Same(order, runtime.DurableOrder);
        Assert.Equal(0, runtime.SaveTradeOrderCount);
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

    private static CompanyCommissionOwnerProjection Projection(
        TradeOrder order,
        long objectRevision,
        long companyRevision) =>
        new()
        {
            Order = order,
            ObjectRevision = new CompanyRecordRevision(objectRevision),
            CompanyRevision = new CompanyRecordRevision(companyRevision),
            ProfileObjectRevision = new CompanyRecordRevision(objectRevision)
        };

    private static async Task SeedOrderRevisionAsync(
        ProfileSyncLocalStateService localState,
        TradeOrder order)
    {
        var connection = await localState.LoadConnectionSettingsAsync();
        await localState.SaveObjectRevisionAsync(
            connection,
            ProfileSyncCollections.TradeOrders,
            order.Id.ToString("D"),
            1);
    }

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

    private sealed class PriorityOwnerHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<Guid, CompanyCommissionOwnerProjection> _projections;
        private readonly List<Guid> _orderIds = [];
        private int _requestCount;

        public PriorityOwnerHandler(IEnumerable<TradeOrder> orders)
        {
            _projections = orders.ToDictionary(
                order => order.CompanyCommission!.CommissionId,
                order => Projection(order, 1, 1));
        }

        public IReadOnlyList<Guid> OrderIds
        {
            get
            {
                lock (_orderIds)
                {
                    return _orderIds.ToArray();
                }
            }
        }

        public IReadOnlyList<Guid> FirstBatch { get; private set; } = [];
        public int RequestCount => Volatile.Read(ref _requestCount);

        public TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.EndsWith("/owner-comparison", request.RequestUri!.AbsolutePath);
            var comparison = (await request.Content!
                .ReadFromJsonAsync<CompanyCommissionOwnerComparisonRequest>(
                    cancellationToken: cancellationToken))!;
            lock (_orderIds)
            {
                _orderIds.AddRange(comparison.Items.Select(item => item.OrderId));
            }
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                FirstBatch = comparison.Items.Select(item => item.OrderId).ToArray();
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CompanyCommissionOwnerComparisonResponse
                {
                    CompanyId = _projections.Values.First().Order.CompanyCommission!.CompanyId,
                    CompanyRevision = new(1),
                    VerifiedAtUtc = DateTime.UtcNow,
                    Items = comparison.Items.Select(item => Acknowledgement(
                        _projections[item.CommissionId].Order,
                        item)).ToArray()
                })
            };
        }
    }

    private sealed class InvalidComparisonHandler : HttpMessageHandler
    {
        public TaskCompletionSource Returned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var comparison = (await request.Content!
                .ReadFromJsonAsync<CompanyCommissionOwnerComparisonRequest>(
                    cancellationToken: cancellationToken))!;
            Returned.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CompanyCommissionOwnerComparisonResponse
                {
                    CompanyId = new(Guid.NewGuid()),
                    CompanyRevision = new(1),
                    VerifiedAtUtc = DateTime.UtcNow,
                    Items = comparison.Items.Select(item => new CompanyCommissionOwnerComparisonResult
                    {
                        OrderId = item.OrderId,
                        CommissionId = item.CommissionId,
                        Status = CompanyCommissionOwnerComparisonStatus.Missing
                    }).ToArray()
                })
            };
        }
    }

    private sealed class CompanyConcurrencyHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<Guid, TradeOrder> _orders;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _concurrency;
        private int _maximumConcurrency;
        private int _requestCount;

        public CompanyConcurrencyHandler(IEnumerable<TradeOrder> orders)
        {
            _orders = orders.ToDictionary(
                order => order.CompanyCommission!.CommissionId);
        }

        public int RequestCount => Volatile.Read(ref _requestCount);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public TaskCompletionSource AllReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var comparison = (await request.Content!
                .ReadFromJsonAsync<CompanyCommissionOwnerComparisonRequest>(
                    cancellationToken: cancellationToken))!;
            var concurrency = Interlocked.Increment(ref _concurrency);
            UpdateMaximumConcurrency(concurrency);
            if (concurrency == 4)
            {
                _release.TrySetResult();
            }
            await _release.Task.WaitAsync(cancellationToken);
            var order = _orders[comparison.Items[0].CommissionId];
            var response = ComparisonResponse(order, comparison.Items);
            Interlocked.Decrement(ref _concurrency);
            if (Interlocked.Increment(ref _requestCount) == 5)
            {
                AllReturned.TrySetResult();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(response)
            };
        }

        private void UpdateMaximumConcurrency(int concurrency)
        {
            var current = Volatile.Read(ref _maximumConcurrency);
            while (concurrency > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _maximumConcurrency,
                    concurrency,
                    current);
                if (observed == current)
                {
                    return;
                }
                current = observed;
            }
        }
    }

    private static CompanyCommissionOwnerComparisonResponse ComparisonResponse(
        TradeOrder order,
        IReadOnlyList<CompanyCommissionOwnerComparisonItem> items,
        long objectRevision = 1) =>
        new()
        {
            CompanyId = order.CompanyCommission!.CompanyId,
            CompanyRevision = new(1),
            VerifiedAtUtc = DateTime.UtcNow,
            Items = items.Select(item => Acknowledgement(
                order,
                item,
                objectRevision)).ToArray()
        };

    private static CompanyCommissionOwnerComparisonResult Acknowledgement(
        TradeOrder order,
        CompanyCommissionOwnerComparisonItem item,
        long objectRevision = 1) =>
        new()
        {
            OrderId = item.OrderId,
            CommissionId = item.CommissionId,
            Status = CompanyCommissionOwnerComparisonStatus.Unchanged,
            Receipt = new CompanyCommissionOwnerReceipt
            {
                OrderId = item.OrderId,
                CompanyId = order.CompanyCommission!.CompanyId,
                CommissionId = item.CommissionId,
                ProfileObjectRevision = item.ProfileObjectRevision,
                ObjectRevision = new(objectRevision),
                CompanyRevision = new(1),
                VerifiedAtUtc = DateTime.UtcNow
            }
        };

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
        public int SaveTradeOrderCount { get; private set; }

        public void SeedDurableOrder(TradeOrder order) => DurableOrder = order;

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
                "IndexedDB.applyHostedTradeOrderState" =>
                    ApplyHostedTradeOrderState(args!),
                "IndexedDB.applyHostedOwnerVerificationBatch" =>
                    ApplyOwnerVerificationBatch(args!),
                "IndexedDB.loadHostedOwnerSettings" =>
                    LoadOwnerSettings((IReadOnlyList<string>)args![0]!),
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
            SaveTradeOrderCount++;
            DurableOrder = order;
            return true;
        }

        private bool ApplyHostedTradeOrderState(object?[] args)
        {
            var order = args[0] as TradeOrder;
            var orderId = Guid.Parse((string)args[1]!);
            if ((bool)args[4]!)
            {
                DurableOrder = DurableOrder?.Id == orderId ? null : DurableOrder;
            }
            else
            {
                SaveOrder(order!);
            }
            _settings[(string)args[2]!] = (string)args[3]!;
            return true;
        }

        private bool ApplyOwnerVerificationBatch(object?[] args)
        {
            var expectedSettings = (Dictionary<string, string>)args[3]!;
            if (expectedSettings.Any(expected =>
                    !_settings.TryGetValue(expected.Key, out var actual) ||
                    !string.Equals(actual, expected.Value, StringComparison.Ordinal)))
            {
                return false;
            }
            foreach (var order in (IReadOnlyList<TradeOrder>)args[0]!)
            {
                SaveOrder(order);
            }
            foreach (var (key, value) in (Dictionary<string, string>)args[1]!)
            {
                _settings[key] = value;
            }
            foreach (var key in (IReadOnlyList<string>)args[2]!)
            {
                _settings.Remove(key);
            }
            return true;
        }

        private Dictionary<string, string> LoadOwnerSettings(IReadOnlyList<string> keys) =>
            keys
                .Where(_settings.ContainsKey)
                .ToDictionary(key => key, key => _settings[key], StringComparer.Ordinal);
    }

}
