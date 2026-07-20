using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public sealed class EngineArchitectureTests
{
    [Fact]
    public void CanonicalHash_IsIndependentOfObjectAndDictionaryOrdering()
    {
        using var left = JsonDocument.Parse("""{"z":3,"settings":{"beta":"2","alpha":"1"},"a":1}""");
        using var right = JsonDocument.Parse("""{"a":1,"settings":{"alpha":"1","beta":"2"},"z":3}""");

        Assert.Equal(EngineCanonicalHash.Compute(left.RootElement), EngineCanonicalHash.Compute(right.RootElement));
    }

    [Fact]
    public void CanonicalHash_NormalizesEquivalentNumberSpellings()
    {
        using var integer = JsonDocument.Parse("1");
        using var decimalValue = JsonDocument.Parse("1.0");
        using var exponent = JsonDocument.Parse("1e0");

        Assert.Equal(EngineCanonicalHash.Compute(integer.RootElement), EngineCanonicalHash.Compute(decimalValue.RootElement));
        Assert.Equal(EngineCanonicalHash.Compute(integer.RootElement), EngineCanonicalHash.Compute(exponent.RootElement));
    }

    [Fact]
    public async Task WorkUnitMerge_HasSequentialAndShuffledParity()
    {
        var units = new[]
        {
            new EngineWorkUnit<int>("item:3", 3),
            new EngineWorkUnit<int>("item:1", 1),
            new EngineWorkUnit<int>("item:2", 2)
        };
        var sequential = await DeterministicWorkUnits.ExecuteSequentialAsync(
            units,
            static (value, _) => Task.FromResult(value * value));
        var shuffledCompletion = new[] { sequential[2], sequential[0], sequential[1] };

        Assert.Equal(
            DeterministicWorkUnits.ComputeResultHash(sequential),
            DeterministicWorkUnits.ComputeResultHash(shuffledCompletion));
    }

    [Fact]
    public void WorkUnitMerge_RejectsDuplicateStableKeys()
    {
        var results = new[]
        {
            new EngineWorkUnitResult<int>("duplicate", 1),
            new EngineWorkUnitResult<int>("duplicate", 2)
        };

        Assert.Throws<InvalidOperationException>(() => DeterministicWorkUnits.ComputeResultHash(results));
    }

    [Fact]
    public async Task SuccessfulRouteFixture_HasStableDegreeOneAndShuffledResultHashes()
    {
        var fixture = new EngineParityFixture(
            "two-item-successful-route",
            EngineInputKind.RootIntent,
            JsonSerializer.SerializeToElement(new
            {
                demands = new[]
                {
                    new { itemId = 2, quantity = 3 },
                    new { itemId = 1, quantity = 2 }
                }
            }),
            "9a453220daff0b8f177522ad0cd77506cb76ee938d0e39535076fac751a91b2a",
            "d9f54a5176030a7cefec0811aef3123c1fa4ae2b505cd2de797694193209919a",
            true);
        var runner = new RepresentativeFixtureRunner();

        var degreeOne = await runner.RunAsync(fixture, 1);
        var degreeMany = await runner.RunAsync(fixture, 4);

        Assert.Equal(fixture.ExpectedExpandedGraphHash, degreeOne.ExpandedGraphHash);
        Assert.Equal(fixture.ExpectedResultHash, degreeOne.ResultHash);
        Assert.True(degreeOne.RouteSucceeded);
        Assert.Equal(degreeOne, degreeMany);
    }

    [Fact]
    public async Task ReferenceExecutor_MeasuresSettlementThroughTerminalEvidence()
    {
        var settlement = new RecordingSettlement();
        var engine = new ReferenceMarketProcurementEngine(
            Mock.Of<IMarketAnalysisExecutionService>(),
            Mock.Of<IProcurementRouteExecutionService>(),
            settlement);
        var request = CreateRequest(JsonSerializer.SerializeToElement(new ReferenceEngineInput(null, null)));

        var result = await engine.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Succeeded, result.Status);
        Assert.Equal(EnginePhase.Completed, result.Completion.TerminalPhase);
        Assert.False(string.IsNullOrWhiteSpace(result.Completion.FinalTransactionHash));
        Assert.Contains(EnginePhase.Publishing, settlement.Phases);
        Assert.Contains(EnginePhase.Persisting, settlement.Phases);
        Assert.Contains(EnginePhase.SettlingUi, settlement.Phases);
        Assert.Contains(EnginePhase.CapturingPostActionEvidence, settlement.Phases);
    }

    [Fact]
    public async Task WorkerClient_SupportsStartProgressCancelAndValidatedCompletion()
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        var capability = await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "fixture" }));
        var progressSeen = false;
        client.ProgressChanged += (_, _) => progressSeen = true;

        var execution = client.ExecuteAsync(request);
        transport.Emit(new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            "progress",
            request.TransactionId,
            JsonSerializer.SerializeToElement(new EngineProgress(request.TransactionId, EnginePhase.Analyzing, 4, 12, "Analyzing."))));
        await client.CancelAsync("test cancellation");
        var terminal = CreateCancelledResult(request);
        transport.Emit(new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            "result",
            request.TransactionId,
            JsonSerializer.SerializeToElement(terminal)));
        var result = await execution;

        Assert.True(capability.DedicatedWorker);
        Assert.True(progressSeen);
        Assert.Equal(EngineTerminalStatus.Cancelled, result.Status);
        Assert.Equal(EngineWorkerLifecycleState.Ready, client.State);
        Assert.Contains(transport.Sent, message => message.Kind == "cancel");
    }

    [Fact]
    public async Task WorkerClient_ProtocolErrorFaultsPendingExecution()
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "fixture" }));

        var execution = client.ExecuteAsync(request);
        transport.Emit(new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            "protocol-error",
            request.TransactionId,
            JsonSerializer.SerializeToElement(new { code = "not-ready", message = "Worker host unavailable." })));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
        Assert.Contains("unavailable", exception.Message, StringComparison.Ordinal);
        Assert.Equal(EngineWorkerLifecycleState.Faulted, client.State);
    }

    [Fact]
    public async Task WorkerClient_ForceTerminateRestartsDedicatedWorker()
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();

        await client.ForceTerminateAndRestartAsync();

        Assert.Equal(2, transport.StartCount);
        Assert.Equal(1, transport.TerminateCount);
        Assert.Equal(EngineWorkerLifecycleState.Ready, client.State);
    }

    [Fact]
    public void StaticWorkerAssets_ProveDedicatedWorkerWithoutIsolationRequirement()
    {
        var root = LocateRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(root, "src", "FFXIV Craft Architect.Web", "wwwroot", "engine-worker.js"));
        var bootstrap = File.ReadAllText(Path.Combine(root, "src", "FFXIV Craft Architect.Web", "wwwroot", "engine-worker-bootstrap.js"));

        Assert.Contains("dedicatedWorker: true", worker, StringComparison.Ordinal);
        Assert.Contains("self.crossOriginIsolated === true", worker, StringComparison.Ordinal);
        Assert.Contains("typeof SharedArrayBuffer", worker, StringComparison.Ordinal);
        Assert.Contains("new Worker", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("requireCrossOriginIsolation", worker, StringComparison.Ordinal);
    }

    private static EngineRequestEnvelope CreateRequest(JsonElement input)
    {
        var basis = new EngineBasisSet(
            new EngineBasisIdentity("plan", "1", "plan-hash"),
            new EngineBasisIdentity("session", "1", "session-hash"),
            new EngineBasisIdentity("publication", "1", "publication-hash"),
            new EngineBasisIdentity("route", "1", "route-hash"));
        return new EngineRequestEnvelope(
            "1",
            Guid.NewGuid(),
            EngineInputKind.RootIntent,
            input,
            basis,
            EngineDeterministicSettings.Default,
            EngineExecutionBudgets.Default,
            "root-hash",
            "graph-hash",
            "analysis-basis-hash",
            "route-basis-hash");
    }

    private static EngineResultEnvelope CreateCancelledResult(EngineRequestEnvelope request)
    {
        var evidence = new Dictionary<string, string> { ["terminalCode"] = "cancelled" };
        var hash = EngineCanonicalHash.ComputeFinalTransactionHash(
            request,
            EngineTerminalStatus.Cancelled,
            string.Empty,
            string.Empty,
            evidence);
        var completion = new EngineCompletionEvidence(
            "1",
            request.TransactionId,
            EngineTerminalStatus.Cancelled,
            EnginePhase.Cancelled,
            request.Basis,
            request.RootIntentHash,
            request.ExpandedGraphHash,
            string.Empty,
            string.Empty,
            hash,
            evidence);
        return new EngineResultEnvelope("1", request.TransactionId, EngineTerminalStatus.Cancelled, null, completion);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class RepresentativeFixtureRunner : IEngineParityFixtureRunner
    {
        public async Task<(string ExpandedGraphHash, string ResultHash, bool RouteSucceeded)> RunAsync(
            EngineParityFixture fixture,
            int degreeOfParallelism,
            CancellationToken cancellationToken = default)
        {
            var graphNodes = new[]
            {
                new { ItemId = 1, Quantity = 2, RecipeId = 101 },
                new { ItemId = 2, Quantity = 3, RecipeId = 202 }
            };
            var expandedGraphHash = EngineCanonicalHash.ComputeUnordered(graphNodes);
            var units = new[]
            {
                new EngineWorkUnit<(int ItemId, int Quantity, int UnitPrice)>("item:2", (2, 3, 80)),
                new EngineWorkUnit<(int ItemId, int Quantity, int UnitPrice)>("item:1", (1, 2, 100))
            };
            var results = await DeterministicWorkUnits.ExecuteSequentialAsync(
                degreeOfParallelism == 1 ? units : units.Reverse(),
                static (input, _) => Task.FromResult(new
                {
                    input.ItemId,
                    input.Quantity,
                    Total = input.Quantity * input.UnitPrice,
                    World = input.ItemId == 1 ? "Alpha" : "Beta"
                }),
                cancellationToken);
            var resultHash = DeterministicWorkUnits.ComputeResultHash(
                degreeOfParallelism == 1 ? results : results.Reverse());
            return (expandedGraphHash, resultHash, true);
        }
    }

    private sealed class RecordingSettlement : IEngineTransactionSettlement
    {
        public List<EnginePhase> Phases { get; } = [];

        public Task SettleAsync(EnginePhase phase, EngineRequestEnvelope request, CancellationToken cancellationToken)
        {
            Phases.Add(phase);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkerTransport : IEngineWorkerTransport
    {
        public event EventHandler<EngineWorkerMessage>? MessageReceived;

        public List<EngineWorkerMessage> Sent { get; } = [];

        public int StartCount { get; private set; }

        public int TerminateCount { get; private set; }

        public Task<EngineWorkerCapability> StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.FromResult(new EngineWorkerCapability("1", true, false, false, false));
        }

        public Task SendAsync(EngineWorkerMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task TerminateAsync(CancellationToken cancellationToken)
        {
            TerminateCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Emit(EngineWorkerMessage message) => MessageReceived?.Invoke(this, message);
    }
}
