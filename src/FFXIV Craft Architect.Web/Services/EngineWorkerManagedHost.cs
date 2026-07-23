using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;

namespace CraftArchitectEngineWorker;

public sealed record EngineWorkerRuntimeProof(
    string ProtocolVersion,
    string RuntimeAssembly,
    string Challenge,
    string ChallengeHash,
    string ProofHash);

public static partial class ManagedHost
{
    private const string ProtocolVersion = "4";
    private static readonly JsonSerializerOptions WireJsonOptions = EngineJsonSerializerOptions.CreateWire();
    private static readonly IReferenceEngineSemanticSnapshotProvider Snapshots =
        new ReferenceEngineSemanticSnapshotProvider();
    private static readonly IMarketProcurementEngine Engine = CreateEngine();
    private static readonly object Sync = new();
    private static ActiveExecution? _activeExecution;

    [JSExport]
    [SupportedOSPlatform("browser")]
    public static string GetRuntimeProofJson(string challenge) =>
        JsonSerializer.Serialize(CreateRuntimeProof(challenge), WireJsonOptions);

    [JSExport]
    [SupportedOSPlatform("browser")]
    public static Task<string> ExecuteMessageJson(string messageJson) => ExecuteMessageJsonCore(messageJson);

    public static async Task<string> ExecuteMessageJsonCore(string messageJson)
    {
        var message = DeserializeMessage(messageJson, "execute");
        if (message.ExecutionId is not { } executionId ||
            message.TransactionId is not { } transactionId ||
            message.Payload is not { } payload)
        {
            throw new InvalidOperationException("Worker execution identity and payload are required.");
        }

        var executionRequest = payload.Deserialize<EngineWorkerExecutionRequest>(WireJsonOptions)
            ?? throw new InvalidOperationException("Worker execution request is empty.");
        var request = executionRequest.Request;
        if (request.TransactionId != transactionId)
        {
            throw new InvalidOperationException("Worker request transaction identity does not match its message envelope.");
        }

        var prepared = Snapshots.PrepareInput(request);
        if (prepared.Input.MarketAnalysis is not null || prepared.Input.ProcurementRoute is null)
        {
            throw new NotSupportedException(
                "The browser Worker currently accepts procurement-only requests with complete embedded market evidence.");
        }

        if (executionRequest.HostGeneration <= 0 || executionRequest.HostExecutionId != executionId)
        {
            throw new InvalidOperationException("Worker host execution identity does not match its message envelope.");
        }

        var cancellation = new CancellationTokenSource();
        var active = new ActiveExecution(
            message.Generation,
            executionRequest.HostGeneration,
            executionId,
            transactionId,
            cancellation);
        lock (Sync)
        {
            if (_activeExecution is not null)
            {
                cancellation.Dispose();
                throw new InvalidOperationException("The managed engine Worker is already executing.");
            }
            _activeExecution = active;
        }

        try
        {
            var progress = new SynchronousProgress<EngineProgress>(value =>
                PostMessage(new EngineWorkerMessage(
                    ProtocolVersion,
                    "progress",
                    message.Generation,
                    executionId,
                    transactionId,
                    JsonSerializer.SerializeToElement(value, WireJsonOptions))));
            var result = await Engine.ComputeAsync(
                executionRequest.HostGeneration,
                executionId,
                request,
                progress,
                cancellation.Token);
            return JsonSerializer.Serialize(
                new EngineWorkerMessage(
                    ProtocolVersion,
                    EngineWorkerClient.ComputationResultMessageKind,
                    message.Generation,
                    executionId,
                    transactionId,
                    JsonSerializer.SerializeToElement(result, WireJsonOptions)),
                WireJsonOptions);
        }
        finally
        {
            lock (Sync)
            {
                if (ReferenceEquals(_activeExecution, active))
                {
                    _activeExecution = null;
                }
            }
            cancellation.Dispose();
        }
    }

    [JSExport]
    [SupportedOSPlatform("browser")]
    public static bool CancelMessageJson(string messageJson)
    {
        var message = DeserializeMessage(messageJson, "cancel");
        var cancellation = message.Payload?.Deserialize<EngineCancelRequest>(WireJsonOptions)
            ?? throw new InvalidOperationException("Worker cancellation payload is required.");
        lock (Sync)
        {
            if (_activeExecution is not { } active ||
                !active.Matches(
                    message.Generation,
                    cancellation.Generation,
                    cancellation.ExecutionId,
                    cancellation.TransactionId))
            {
                return false;
            }
            active.Cancellation.Cancel();
            return true;
        }
    }

    [JSExport]
    [SupportedOSPlatform("browser")]
    public static string GetAcceptanceExecuteMessageJson(int generation) =>
        GetAcceptanceExecuteMessageJsonCore(generation);

    public static string GetAcceptanceExecuteMessageJsonCore(int generation, bool includeEvidence = true)
    {
        var transactionId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var request = CreateAcceptanceRequest(transactionId, includeEvidence);
        return JsonSerializer.Serialize(
            new EngineWorkerMessage(
                ProtocolVersion,
                "execute",
                generation,
                executionId,
                transactionId,
                JsonSerializer.SerializeToElement(
                    new EngineWorkerExecutionRequest(generation, executionId, request),
                    WireJsonOptions)),
            WireJsonOptions);
    }

    [JSExport]
    [SupportedOSPlatform("browser")]
    public static void RunAcceptanceHang()
    {
        while (true)
        {
            Thread.SpinWait(16_384);
        }
    }

    public static EngineWorkerRuntimeProof CreateRuntimeProof(string challenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);
        if (challenge.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(challenge), "The runtime proof challenge is too long.");
        }

        var runtimeAssembly = typeof(ManagedHost).Assembly.GetName().Name
            ?? throw new InvalidOperationException("The worker runtime assembly has no identity.");
        var challengeHash = EngineCanonicalHash.Compute(challenge);
        var proofHash = EngineCanonicalHash.Compute(new
        {
            Domain = "engine-worker-runtime-proof-v1",
            ProtocolVersion,
            RuntimeAssembly = runtimeAssembly,
            Challenge = challenge,
            ChallengeHash = challengeHash
        });
        return new EngineWorkerRuntimeProof(
            ProtocolVersion,
            runtimeAssembly,
            challenge,
            challengeHash,
            proofHash);
    }

    private static EngineWorkerMessage DeserializeMessage(string messageJson, string expectedKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageJson);
        var message = JsonSerializer.Deserialize<EngineWorkerMessage>(messageJson, WireJsonOptions)
            ?? throw new InvalidOperationException("Worker message is empty.");
        if (!string.Equals(message.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal) ||
            !string.Equals(message.Kind, expectedKind, StringComparison.Ordinal) ||
            message.Generation <= 0)
        {
            throw new InvalidOperationException("Worker message protocol, kind, or generation is invalid.");
        }
        return message;
    }

    private static IMarketProcurementEngine CreateEngine()
    {
        var unavailableAnalysis = new UnavailableMarketAnalysisExecutionService();
        var shopping = new MarketShoppingService(new UnavailableMarketCacheService());
        var procurement = new ProcurementRouteExecutionService(
            new MarketEvidenceReconciliationService(unavailableAnalysis),
            shopping);
        return new ReferenceMarketProcurementEngine(unavailableAnalysis, procurement, Snapshots);
    }

    private static EngineRequestEnvelope CreateAcceptanceRequest(Guid transactionId, bool includeEvidence)
    {
        var now = DateTime.UtcNow;
        var worlds = Enumerable.Range(0, 8).Select(index => $"World {index}").ToArray();
        var items = Enumerable.Range(0, 8)
            .Select(index => new MaterialAggregate
            {
                ItemId = 10_000 + index,
                Name = $"Bounded item {index}",
                TotalQuantity = 1
            })
            .ToArray();
        var plans = items.Select((item, itemIndex) =>
        {
            var options = worlds.Select((world, worldIndex) =>
            {
                var price = 100L + ((itemIndex * 17 + worldIndex * 11) % 73);
                return new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = world,
                    TotalCost = price,
                    AveragePricePerUnit = price,
                    TotalQuantityPurchased = 1,
                    HasSufficientStock = true,
                    MarketDataQualityBucket = MarketDataQualityBucket.Current,
                    MarketDataAgeSource = MarketDataAgeSource.UniversalisWorldUpload,
                    MarketUploadedAtUtc = now,
                    Listings =
                    [
                        new ShoppingListingEntry
                        {
                            Quantity = 1,
                            NeededFromStack = 1,
                            PricePerUnit = price,
                            RetainerName = $"Retainer {itemIndex}-{worldIndex}"
                        }
                    ]
                };
            }).ToList();
            return new DetailedShoppingPlan
            {
                ItemId = item.ItemId,
                Name = item.Name,
                QuantityNeeded = item.TotalQuantity,
                WorldOptions = options,
                RecommendedWorld = options.MinBy(option => option.TotalCost)
            };
        }).ToArray();
        var analyses = items.Select(item => new MarketItemAnalysis
        {
            ItemId = item.ItemId,
            Name = item.Name,
            QuantityNeeded = item.TotalQuantity,
            Scope = MarketFetchScope.SelectedDataCenter,
            LoadedAtUtc = now,
            RequestedDataCenters = ["Aether"],
            PresentDataCenters = ["Aether"],
            Worlds = worlds.Select(world => new WorldMarketAnalysis
            {
                DataCenter = "Aether",
                WorldName = world,
                MarketUploadedAtUtc = now,
                DataQualityBucket = MarketDataQualityBucket.Current
            }).ToList()
        }).ToArray();
        var input = new ReferenceEngineInput(
            null,
            new ProcurementRouteExecutionRequest
            {
                ActiveProcurementItems = items,
                SourceShoppingPlans = includeEvidence ? plans : [],
                SourceMarketAnalyses = includeEvidence ? analyses : [],
                Scope = MarketFetchScope.SelectedDataCenter,
                SelectedDataCenter = "Aether",
                SelectedRegion = "North America",
                ProcurementConfig = new MarketAnalysisConfig
                {
                    TravelTolerance = 0,
                    TravelPriority = MarketTravelPriority.WorldVisitsFirst
                },
                ExpectedWorldsByDataCenter = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["Aether"] = worlds
                }
            });
        var inputElement = JsonSerializer.SerializeToElement(input, WireJsonOptions);
        var basis = new EngineBasisSet(
            new EngineBasisIdentity("plan", "1", "acceptance-plan"),
            new EngineBasisIdentity("session", "1", "acceptance-session"),
            new EngineBasisIdentity("publication", "1", "acceptance-publication"),
            new EngineBasisIdentity("route", "1", "acceptance-route"));
        var provisional = new EngineRequestEnvelope(
            "1",
            transactionId,
            EngineInputKind.RootIntent,
            inputElement,
            basis,
            EngineDeterministicSettings.Default,
            EngineExecutionBudgets.Default,
            string.Empty,
            string.Empty,
            "acceptance-analysis-basis",
            "acceptance-route-basis");
        var prepared = Snapshots.PrepareInput(provisional);
        return provisional with
        {
            RootIntentHash = EngineSemanticSnapshotHash.RootIntent(prepared.RootIntent),
            ExpandedGraphHash = EngineSemanticSnapshotHash.ExpandedGraph(prepared.ExpandedGraph)
        };
    }

    private static void PostMessage(EngineWorkerMessage message)
    {
        if (OperatingSystem.IsBrowser())
        {
            PostMessageToWorker(JsonSerializer.Serialize(message, WireJsonOptions));
        }
    }

    [JSImport("postMessage", "engine-worker")]
    [SupportedOSPlatform("browser")]
    private static partial void PostMessageToWorker(string messageJson);

    private sealed record ActiveExecution(
        long WorkerGeneration,
        long HostGeneration,
        Guid ExecutionId,
        Guid TransactionId,
        CancellationTokenSource Cancellation)
    {
        public bool Matches(
            long workerGeneration,
            long hostGeneration,
            Guid executionId,
            Guid transactionId) =>
            WorkerGeneration == workerGeneration &&
            HostGeneration == hostGeneration &&
            ExecutionId == executionId &&
            TransactionId == transactionId;
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class UnavailableMarketAnalysisExecutionService : IMarketAnalysisExecutionService
    {
        public Task<MarketAnalysisExecutionResult> ExecuteAsync(
            MarketAnalysisExecutionRequest request,
            IProgress<string>? progress = null,
            CancellationToken ct = default,
            MarketAnalysisExecutionOptions? executionOptions = null) =>
            Task.FromException<MarketAnalysisExecutionResult>(new InvalidOperationException(
                "The procurement Worker requires complete reusable market evidence and cannot refresh it."));
    }

    private sealed class UnavailableMarketCacheService : IMarketCacheService
    {
        public Task<CachedMarketData?> GetAsync(int itemId, string dataCenter, TimeSpan? maxAge = null) => Unavailable<CachedMarketData?>();
        public Task<(CachedMarketData? Data, bool IsStale)> GetWithStaleAsync(int itemId, string dataCenter, TimeSpan? maxAge = null) => Unavailable<(CachedMarketData?, bool)>();
        public Task<IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData>> GetManyAsync(IReadOnlyCollection<(int itemId, string dataCenter)> requests, TimeSpan? maxAge = null) => Unavailable<IReadOnlyDictionary<(int, string), CachedMarketData>>();
        public Task SetAsync(int itemId, string dataCenter, CachedMarketData data) => Unavailable();
        public Task<bool> HasValidCacheAsync(int itemId, string dataCenter, TimeSpan? maxAge = null) => Unavailable<bool>();
        public Task<List<(int itemId, string dataCenter)>> GetMissingAsync(List<(int itemId, string dataCenter)> requests, TimeSpan? maxAge = null) => Unavailable<List<(int, string)>>();
        public Task<int> CleanupStaleAsync(TimeSpan maxAge) => Unavailable<int>();
        public Task<CacheStats> GetStatsAsync() => Unavailable<CacheStats>();
        public Task<int> EnsurePopulatedAsync(List<(int itemId, string dataCenter)> requests, TimeSpan? maxAge = null, IProgress<string>? progress = null, CancellationToken ct = default) => Unavailable<int>();
        public Task<int> RefreshRequestedAsync(List<(int itemId, string dataCenter)> requests, IProgress<string>? progress = null, CancellationToken ct = default) => Unavailable<int>();

        private static Task Unavailable() => Task.FromException(CreateException());
        private static Task<T> Unavailable<T>() => Task.FromException<T>(CreateException());
        private static InvalidOperationException CreateException() => new(
            "The procurement Worker cannot access market cache or network evidence.");
    }
}
