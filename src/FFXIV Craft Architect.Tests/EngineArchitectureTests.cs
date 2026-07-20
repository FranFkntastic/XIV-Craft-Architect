using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
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
    public void CanonicalHash_NormalizesKnownSetValuedArraysOnly()
    {
        using var left = JsonDocument.Parse("""{"BlacklistedWorlds":[{"world":2},{"world":1}],"Items":[2,1]}""");
        using var right = JsonDocument.Parse("""{"Items":[2,1],"BlacklistedWorlds":[{"world":1},{"world":2}]}""");
        using var duplicateSetMember = JsonDocument.Parse("""{"Items":[2,1],"BlacklistedWorlds":[{"world":1},{"world":2},{"world":1}]}""");
        using var reorderedList = JsonDocument.Parse("""{"Items":[1,2],"BlacklistedWorlds":[{"world":1},{"world":2}]}""");

        Assert.Equal(EngineCanonicalHash.ComputeEngineInput(left.RootElement), EngineCanonicalHash.ComputeEngineInput(right.RootElement));
        Assert.Equal(EngineCanonicalHash.ComputeEngineInput(left.RootElement), EngineCanonicalHash.ComputeEngineInput(duplicateSetMember.RootElement));
        Assert.NotEqual(EngineCanonicalHash.ComputeEngineInput(left.RootElement), EngineCanonicalHash.ComputeEngineInput(reorderedList.RootElement));
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
    public void CanonicalHash_RejectsDuplicatePropertiesAndUnsupportedNumbers()
    {
        using var duplicateA = JsonDocument.Parse("""{"x":1,"x":2}""");
        using var duplicateB = JsonDocument.Parse("""{"x":2,"x":1}""");
        using var huge = JsonDocument.Parse("1e400");
        using var precise = JsonDocument.Parse("0.123456789012345678901234567890123456789");

        Assert.Throws<InvalidOperationException>(() => EngineCanonicalHash.Compute(duplicateA.RootElement));
        Assert.Throws<InvalidOperationException>(() => EngineCanonicalHash.Compute(duplicateB.RootElement));
        Assert.Throws<NotSupportedException>(() => EngineCanonicalHash.Compute(huge.RootElement));
        Assert.Throws<NotSupportedException>(() => EngineCanonicalHash.Compute(precise.RootElement));
    }

    [Fact]
    public void DeterministicSettings_DefensivelyCopyAndCannotBeMutated()
    {
        var source = new Dictionary<string, string> { ["mode"] = "one" };
        var settings = new EngineDeterministicSettings("1", source);
        source["mode"] = "two";
        source["other"] = "value";

        Assert.Equal("one", settings.Values["mode"]);
        Assert.False(settings.Values.ContainsKey("other"));
        var dictionaryView = Assert.IsAssignableFrom<IDictionary<string, string>>(settings.Values);
        Assert.Throws<NotSupportedException>(() => dictionaryView.Add("mutate", "blocked"));
        Assert.Empty(EngineDeterministicSettings.Default.Values);
        var roundTrip = JsonSerializer.Deserialize<EngineDeterministicSettings>(JsonSerializer.Serialize(settings));
        Assert.Equal("one", roundTrip!.Values["mode"]);
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
        Assert.InRange(runner.MaxObservedConcurrency, 2, 4);
    }

    [Fact]
    public void ExplicitSnapshots_PreserveOrderedGraphAndRouteSequences()
    {
        var graphA = new EngineExpandedGraphSnapshot("1", [],
        [
            new("root", "child-a", 0, 1),
            new("root", "child-b", 1, 1)
        ]);
        var graphB = graphA with { Edges = graphA.Edges.Reverse().ToArray() };
        var routeA = new EngineRouteSemanticSnapshot("1",
            [new(0, "Aether", "Alpha", [1, 2])],
            [
                new(0, 1, 1, 10, "market", MarketDataQualityBucket.Current, MarketDataAgeSource.UniversalisWorldUpload, 100),
                new(1, 2, 1, 20, "market", MarketDataQualityBucket.Current, MarketDataAgeSource.UniversalisWorldUpload, 100)
            ], 30, 1, 0, true);
        var routeB = routeA with { OrderedItems = routeA.OrderedItems.Reverse().ToArray() };

        Assert.NotEqual(EngineSemanticSnapshotHash.ExpandedGraph(graphA), EngineSemanticSnapshotHash.ExpandedGraph(graphB));
        Assert.NotEqual(EngineSemanticSnapshotHash.Route(routeA), EngineSemanticSnapshotHash.Route(routeB));
    }

    [Fact]
    public void AnalysisSemanticHash_ExcludesTimingsButBindsFreshnessEvidence()
    {
        var fast = CreateAnalysisResult(TimeSpan.FromMilliseconds(1), DateTime.UnixEpoch);
        var slow = CreateAnalysisResult(TimeSpan.FromSeconds(8), DateTime.UnixEpoch);
        var freshEvidence = CreateAnalysisResult(TimeSpan.FromSeconds(8), DateTime.UnixEpoch.AddYears(20));

        Assert.Equal(TestSnapshotProvider.ComputeAnalysisHash(fast), TestSnapshotProvider.ComputeAnalysisHash(slow));
        Assert.NotEqual(TestSnapshotProvider.ComputeAnalysisHash(fast), TestSnapshotProvider.ComputeAnalysisHash(freshEvidence));
    }

    [Fact]
    public void RouteSemanticHash_BindsFreshnessEvidenceAndDecisions()
    {
        var early = CreateRouteResult(DateTime.UnixEpoch, 100);
        var late = CreateRouteResult(DateTime.UnixEpoch.AddYears(20), 100);
        var changed = CreateRouteResult(DateTime.UnixEpoch.AddYears(20), 101);

        Assert.NotEqual(
            TestSnapshotProvider.ComputeRouteHash(early),
            TestSnapshotProvider.ComputeRouteHash(late));
        Assert.NotEqual(
            TestSnapshotProvider.ComputeRouteHash(early),
            TestSnapshotProvider.ComputeRouteHash(changed));
    }

    [Fact]
    public async Task ReferenceExecutor_RejectsStaleInputAndGraphBasis()
    {
        var engine = new ReferenceMarketProcurementEngine(
            Mock.Of<IMarketAnalysisExecutionService>(),
            Mock.Of<IProcurementRouteExecutionService>(),
            new RecordingSettlement(),
            new TestSnapshotProvider());
        var input = JsonSerializer.SerializeToElement(new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null));
        var request = CreateRequest(input);

        Assert.Equal(EngineTerminalStatus.Failed, (await engine.ExecuteAsync(request with { RootIntentHash = "stale" })).Status);
        Assert.Equal(EngineTerminalStatus.Failed, (await engine.ExecuteAsync(request with { ExpandedGraphHash = "stale" })).Status);
    }

    [Fact]
    public async Task ReferenceExecutor_RejectsUnsupportedBudgetsInputKindsAndEmptyOperations()
    {
        var engine = new ReferenceMarketProcurementEngine(
            Mock.Of<IMarketAnalysisExecutionService>(),
            Mock.Of<IProcurementRouteExecutionService>(),
            new RecordingSettlement(),
            new TestSnapshotProvider());
        var operation = JsonSerializer.SerializeToElement(new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null));
        var unsupportedBudget = CreateRequest(operation) with
        {
            Budgets = EngineExecutionBudgets.Default with { MaxCandidateEvaluations = 12 }
        };
        var unsupportedKind = CreateRequest(operation) with { InputKind = EngineInputKind.StructuredGraph };
        var empty = CreateRequest(JsonSerializer.SerializeToElement(new ReferenceEngineInput(null, null)));

        Assert.Equal(EngineTerminalStatus.Failed, (await engine.ExecuteAsync(unsupportedBudget)).Status);
        Assert.Equal(EngineTerminalStatus.Failed, (await engine.ExecuteAsync(unsupportedKind)).Status);
        Assert.Equal(EngineTerminalStatus.Failed, (await engine.ExecuteAsync(empty)).Status);
    }

    [Fact]
    public async Task ReferenceExecutor_NoOpSettlementCannotAttestSuccess()
    {
        var analysis = new Mock<IMarketAnalysisExecutionService>();
        analysis.Setup(service => service.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(CreateAnalysisResult());
        var engine = new ReferenceMarketProcurementEngine(
            analysis.Object,
            Mock.Of<IProcurementRouteExecutionService>(),
            new NoOpEngineTransactionSettlement(),
            new TestSnapshotProvider());
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var result = await engine.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Contains("Publishing", result.Failure!.Message, StringComparison.Ordinal);
        Assert.Equal(EnginePhase.Publishing.ToString(), result.Completion.TerminalEvidence["failedPhase"]);
    }

    [Fact]
    public async Task ReferenceExecutor_RecordsTerminalCleanupFailures()
    {
        var engine = new ReferenceMarketProcurementEngine(
            Mock.Of<IMarketAnalysisExecutionService>(),
            Mock.Of<IProcurementRouteExecutionService>(),
            new FailingCleanupSettlement(),
            new TestSnapshotProvider());
        var request = CreateRequest(JsonSerializer.SerializeToElement(new ReferenceEngineInput(null, null)));

        var result = await engine.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.StartsWith("failed:", result.Completion.TerminalEvidence["cleanup:ReleasingGate"], StringComparison.Ordinal);
        Assert.True(result.Completion.TerminalEvidence.ContainsKey("failureMessageHash"));
    }

    [Fact]
    public async Task ReferenceExecutor_MeasuresSettlementThroughTerminalEvidence()
    {
        var settlement = new RecordingSettlement();
        var analysisService = new Mock<IMarketAnalysisExecutionService>();
        analysisService
            .Setup(service => service.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(CreateAnalysisResult());
        var engine = new ReferenceMarketProcurementEngine(
            analysisService.Object,
            Mock.Of<IProcurementRouteExecutionService>(),
            settlement,
            new TestSnapshotProvider());
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var result = await engine.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Succeeded, result.Status);
        Assert.Equal(EnginePhase.Completed, result.Completion.TerminalPhase);
        Assert.False(string.IsNullOrWhiteSpace(result.Completion.FinalTransactionHash));
        Assert.Contains(EnginePhase.Publishing, settlement.Phases);
        Assert.Contains(EnginePhase.Persisting, settlement.Phases);
        Assert.Contains(EnginePhase.SettlingUi, settlement.Phases);
        Assert.Contains(EnginePhase.CapturingPostActionEvidence, settlement.Phases);
        Assert.All(settlement.Contexts, context => Assert.NotNull(context.Output.MarketAnalysis));
        Assert.All(settlement.Contexts, context => Assert.False(string.IsNullOrWhiteSpace(context.AnalysisResultHash)));
        Assert.Contains("phase:Publishing", result.Completion.TerminalEvidence.Keys);
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
    public async Task WorkerClient_RejectsContradictoryOrUnboundCompletionEvidence()
    {
        await AssertWorkerResultRejected((request, result) => result with
        {
            Completion = result.Completion with { RootIntentHash = "wrong" }
        });
        await AssertWorkerResultRejected((request, result) => result with
        {
            Completion = result.Completion with { ExpandedGraphHash = "wrong" }
        });
        await AssertWorkerResultRejected((request, result) => result with
        {
            Status = EngineTerminalStatus.Failed
        });
        await AssertWorkerResultRejected((request, result) => result with
        {
            Completion = result.Completion with { TerminalPhase = EnginePhase.Failed }
        });
        await AssertWorkerResultRejected((request, result) => result with
        {
            Completion = result.Completion with
            {
                TerminalEvidence = new Dictionary<string, string> { ["settlement"] = "complete" },
                FinalTransactionHash = EngineCanonicalHash.ComputeFinalTransactionHash(
                    request,
                    EngineTerminalStatus.Succeeded,
                    result.Completion.AnalysisResultHash,
                    result.Completion.ProcurementRouteResultHash,
                    new Dictionary<string, string> { ["settlement"] = "complete" })
            }
        });
        await AssertWorkerResultRejected((request, result) => result with
        {
            Completion = result.Completion with { FinalTransactionHash = "wrong" }
        });
        await AssertWorkerResultRejected((request, result) =>
        {
            var evidence = result.Completion.TerminalEvidence
                .Where(pair => pair.Key != "phase:Persisting")
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            return result with
            {
                Completion = result.Completion with
                {
                    TerminalEvidence = evidence,
                    FinalTransactionHash = EngineCanonicalHash.ComputeFinalTransactionHash(
                        request,
                        result.Status,
                        result.Completion.AnalysisResultHash,
                        result.Completion.ProcurementRouteResultHash,
                        evidence)
                }
            };
        });
    }

    [Fact]
    public async Task WorkerClient_RejectsFailureMessageAndRetryabilityTampering()
    {
        foreach (var mutate in new Func<EngineFailure, EngineFailure>[]
        {
            failure => failure with { Message = "tampered" },
            failure => failure with { IsRetryable = !failure.IsRetryable },
            failure => failure with { FailureType = "wrong" }
        })
        {
            var transport = new FakeWorkerTransport();
            await using var client = new EngineWorkerClient(transport);
            await client.StartAsync();
            var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "failure" }));
            var execution = client.ExecuteAsync(request);
            var result = CreateFailedResult(request);
            result = result with { Failure = mutate(result.Failure!) };
            transport.Emit(new EngineWorkerMessage("1", "result", request.TransactionId, JsonSerializer.SerializeToElement(result)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
        }
    }

    [Fact]
    public async Task WorkerClient_SerializesConcurrentStartAndIgnoresStaleMessages()
    {
        var transport = new FakeWorkerTransport { StartGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var client = new EngineWorkerClient(transport);
        var first = client.StartAsync();
        var second = client.StartAsync();
        transport.StartGate.SetResult();

        await Task.WhenAll(first, second);
        Assert.Equal(1, transport.StartCount);

        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "current" }));
        var execution = client.ExecuteAsync(request);
        var stale = CreateSuccessfulResult(request with { TransactionId = Guid.NewGuid() });
        transport.Emit(new EngineWorkerMessage("1", "result", stale.TransactionId, JsonSerializer.SerializeToElement(stale)));
        Assert.False(execution.IsCompleted);
        var current = CreateSuccessfulResult(request);
        transport.Emit(new EngineWorkerMessage("1", "result", request.TransactionId, JsonSerializer.SerializeToElement(current)));
        Assert.Equal(EngineTerminalStatus.Succeeded, (await execution).Status);
    }

    [Fact]
    public async Task WorkerClient_CancellationTimeoutFaultsAndReleasesPendingOwnership()
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport, TimeSpan.FromMilliseconds(20));
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "fixture" }));
        var execution = client.ExecuteAsync(request);

        await client.CancelAsync("timeout probe");

        await Assert.ThrowsAsync<TimeoutException>(() => execution);
        Assert.Equal(EngineWorkerLifecycleState.Faulted, client.State);
        Assert.Equal(1, transport.TerminateCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(
            CreateRequest(JsonSerializer.SerializeToElement(new { demand = "overlap" }))));
    }

    [Fact]
    public async Task WorkerClient_ThrowingProgressSubscriberDoesNotFaultExecution()
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        client.ProgressChanged += (_, _) => throw new InvalidOperationException("observer failure");
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "fixture" }));
        var execution = client.ExecuteAsync(request);
        transport.Emit(new EngineWorkerMessage("1", "progress", request.TransactionId,
            JsonSerializer.SerializeToElement(new EngineProgress(request.TransactionId, EnginePhase.Analyzing, 1, 2, "working"))));
        transport.Emit(new EngineWorkerMessage("1", "result", request.TransactionId,
            JsonSerializer.SerializeToElement(CreateSuccessfulResult(request))));

        Assert.Equal(EngineTerminalStatus.Succeeded, (await execution).Status);
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("result", true)]
    [InlineData("progress", true)]
    [InlineData("protocol-error", true)]
    public async Task WorkerClient_MalformedCorrelatedMessagesFaultExecution(string kind, bool nullPayload)
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "fixture" }));
        var execution = client.ExecuteAsync(request);
        var payload = nullPayload ? (JsonElement?)null : JsonSerializer.SerializeToElement(new { value = 1 });
        transport.Emit(new EngineWorkerMessage("1", kind, request.TransactionId, payload));

        await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
    }

    [Fact]
    public async Task WorkerClient_ResponseTimeoutFaultsPendingExecution()
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport, responseTimeout: TimeSpan.FromMilliseconds(20));
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "fixture" }));

        await Assert.ThrowsAsync<TimeoutException>(() => client.ExecuteAsync(request));
        Assert.Equal(EngineWorkerLifecycleState.Faulted, client.State);
    }

    [Fact]
    public async Task WorkerClient_ObsoleteStartupCannotResurrectAfterDispose()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeWorkerTransport { StartGate = gate };
        var client = new EngineWorkerClient(transport);
        var startup = client.StartAsync();
        await client.DisposeAsync();
        gate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.Equal(EngineWorkerLifecycleState.Stopped, client.State);
    }

    [Fact]
    public async Task WorkerClient_ForceRestartInvalidatesPendingStartupGeneration()
    {
        var transport = new FakeWorkerTransport
        {
            StartGates = new Queue<TaskCompletionSource>(
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)])
        };
        await using var client = new EngineWorkerClient(transport);
        var staleStartup = client.StartAsync();
        var restart = client.ForceTerminateAndRestartAsync();
        transport.StartGates.Dequeue().SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staleStartup);
        transport.StartGates.Dequeue().SetResult();
        await restart;

        Assert.Equal(EngineWorkerLifecycleState.Ready, client.State);
        Assert.Equal(2, transport.StartCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkerClient_TimeoutClearsOwnershipWhenTerminationThrows(bool cancellationTimeout)
    {
        var transport = new FakeWorkerTransport { ThrowOnTerminate = true };
        await using var client = new EngineWorkerClient(
            transport,
            cancellationTimeout: TimeSpan.FromMilliseconds(20),
            responseTimeout: TimeSpan.FromMilliseconds(20));
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "timeout" }));
        var execution = client.ExecuteAsync(request);
        if (cancellationTimeout)
        {
            await client.CancelAsync("timeout");
        }

        await Assert.ThrowsAsync<AggregateException>(() => execution);
        Assert.Equal(EngineWorkerLifecycleState.Faulted, client.State);
        Assert.Equal(1, transport.TerminateCount);
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
    public void StaticWorkerAssets_DeclareCapabilityProbeWithoutIsolationRequirement()
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
        var provider = new TestSnapshotProvider();
        var provisional = new EngineRequestEnvelope(
            "1",
            Guid.NewGuid(),
            EngineInputKind.RootIntent,
            input,
            basis,
            EngineDeterministicSettings.Default,
            EngineExecutionBudgets.Default,
            string.Empty,
            string.Empty,
            "analysis-basis-hash",
            "route-basis-hash");
        var prepared = provider.PrepareInput(provisional);
        return new EngineRequestEnvelope(
            "1",
            Guid.NewGuid(),
            EngineInputKind.RootIntent,
            input,
            basis,
            EngineDeterministicSettings.Default,
            EngineExecutionBudgets.Default,
            EngineSemanticSnapshotHash.RootIntent(prepared.RootIntent),
            EngineSemanticSnapshotHash.ExpandedGraph(prepared.ExpandedGraph),
            "analysis-basis-hash",
            "route-basis-hash");
    }

    private static async Task AssertWorkerResultRejected(
        Func<EngineRequestEnvelope, EngineResultEnvelope, EngineResultEnvelope> mutate)
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "fixture" }));
        var execution = client.ExecuteAsync(request);
        var result = mutate(request, CreateSuccessfulResult(request));
        transport.Emit(new EngineWorkerMessage("1", "result", request.TransactionId, JsonSerializer.SerializeToElement(result)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
    }

    private static EngineResultEnvelope CreateSuccessfulResult(EngineRequestEnvelope request)
    {
        var payload = JsonSerializer.SerializeToElement(new { route = "success" });
        var evidence = new Dictionary<string, string>
        {
            ["resultPayloadHash"] = EngineCanonicalHash.Compute(payload),
            ["settlement"] = "complete",
            ["phase:Publishing"] = "complete",
            ["phase:Persisting"] = "complete",
            ["phase:ReleasingGate"] = "complete",
            ["phase:SettlingUi"] = "complete",
            ["phase:CapturingPostActionEvidence"] = "complete"
        };
        var hash = EngineCanonicalHash.ComputeFinalTransactionHash(
            request,
            EngineTerminalStatus.Succeeded,
            "analysis-result",
            "route-result",
            evidence);
        var completion = new EngineCompletionEvidence(
            "1",
            request.TransactionId,
            EngineTerminalStatus.Succeeded,
            EnginePhase.Completed,
            request.Basis,
            request.RootIntentHash,
            request.ExpandedGraphHash,
            "analysis-result",
            "route-result",
            hash,
            evidence);
        return new EngineResultEnvelope("1", request.TransactionId, EngineTerminalStatus.Succeeded, payload, completion);
    }

    private static EngineResultEnvelope CreateFailedResult(EngineRequestEnvelope request)
    {
        var failure = new EngineFailure("failed", "failure message", false, EnginePhase.Analyzing, "TestFailure");
        var evidence = new Dictionary<string, string>
        {
            ["terminalCode"] = failure.Code,
            ["failedPhase"] = failure.FailedPhase.ToString(),
            ["failureType"] = failure.FailureType,
            ["failureMessageHash"] = EngineCanonicalHash.Compute(failure.Message),
            ["isRetryable"] = failure.IsRetryable.ToString()
        };
        var hash = EngineCanonicalHash.ComputeFinalTransactionHash(request, EngineTerminalStatus.Failed, string.Empty, string.Empty, evidence);
        var completion = new EngineCompletionEvidence(
            "1", request.TransactionId, EngineTerminalStatus.Failed, EnginePhase.Failed, request.Basis,
            request.RootIntentHash, request.ExpandedGraphHash, string.Empty, string.Empty, hash, evidence);
        return new EngineResultEnvelope("1", request.TransactionId, EngineTerminalStatus.Failed, null, completion, failure);
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

    private static ProcurementRouteExecutionResult CreateRouteResult(DateTime timestamp, long selectedCost)
    {
        var world = new WorldShoppingSummary
        {
            DataCenter = "Aether",
            WorldName = "Alpha",
            TotalCost = selectedCost,
            MarketUploadedAtUtc = timestamp,
            MarketDataAge = TimeSpan.FromMinutes(5)
        };
        var shopping = new DetailedShoppingPlan
        {
            ItemId = 1,
            Name = "Item",
            QuantityNeeded = 1,
            WorldOptions = [world],
            RecommendedWorld = world
        };
        return new ProcurementRouteExecutionResult(
            [shopping],
            [shopping],
            [],
            [],
            [],
            new MarketRouteDecision(0, null, 100, selectedCost, 0, 1, 1, 0, 0, true, "Aether"));
    }

    private static MarketAnalysisExecutionResult CreateAnalysisResult(TimeSpan? duration = null, DateTime? timestamp = null)
    {
        var cached = new CachedMarketData
        {
            ItemId = 1,
            DataCenter = "Aether",
            FetchedAtUnix = 100,
            DCAveragePrice = 123,
            Worlds =
            [
                new CachedWorldData
                {
                    WorldId = 10,
                    WorldName = "Alpha",
                    Listings = [new CachedListing { Quantity = 2, PricePerUnit = 50, ListingId = "listing-1" }]
                }
            ]
        };
        var evidence = new MarketEvidenceSet(
            new Dictionary<(int itemId, string dataCenter), CachedMarketData> { [(1, "Aether")] = cached },
            [(1, "Aether")],
            MarketFetchScope.SelectedDataCenter,
            ["Aether"],
            "Aether",
            "North-America",
            TimeSpan.FromMinutes(10),
            1,
            DateTime.UtcNow);
        var observed = timestamp ?? DateTime.UnixEpoch;
        var analysis = new MarketItemAnalysis
        {
            ItemId = 1,
            Name = "Item",
            QuantityNeeded = 2,
            LoadedAtUtc = observed,
            LastReconciledAtUtc = observed.AddMinutes(1),
            PriceEvaluation = new MarketPriceEvaluation { ItemId = 1, EvaluatedAtUtc = observed.AddMinutes(2) },
            Worlds =
            [
                new WorldMarketAnalysis
                {
                    DataCenter = "Aether",
                    WorldName = "Alpha",
                    FetchedAtUtc = observed.AddMinutes(3),
                    MarketUploadedAtUtc = observed.AddMinutes(4),
                    DataAge = TimeSpan.FromMinutes(5),
                    Listings = [new AnalyzedMarketListing { Quantity = 2, PricePerUnit = 50, LastReviewTimeUtc = observed.AddMinutes(6) }]
                }
            ]
        };
        var shopping = new DetailedShoppingPlan { ItemId = 1, Name = "Item", QuantityNeeded = 2, DCAveragePrice = 50 };
        return new MarketAnalysisExecutionResult(
            evidence,
            [analysis],
            [shopping],
            new MarketAnalysisExecutionTimings(duration ?? TimeSpan.Zero, duration ?? TimeSpan.Zero));
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
        private int _active;
        private int _maxObservedConcurrency;

        public int MaxObservedConcurrency => _maxObservedConcurrency;

        public async Task<(string ExpandedGraphHash, string ResultHash, bool RouteSucceeded)> RunAsync(
            EngineParityFixture fixture,
            int degreeOfParallelism,
            CancellationToken cancellationToken = default)
        {
            var demands = fixture.Input.GetProperty("demands").EnumerateArray()
                .Select(item => new
                {
                    ItemId = item.GetProperty("itemId").GetInt32(),
                    Quantity = item.GetProperty("quantity").GetInt32()
                })
                .ToArray();
            var graphNodes = demands.Select(demand => new
            {
                demand.ItemId,
                demand.Quantity,
                RecipeId = demand.ItemId * 101
            }).ToArray();
            var expandedGraphHash = EngineCanonicalHash.ComputeUnordered(graphNodes);
            var units = demands.Select(demand =>
                new EngineWorkUnit<(int ItemId, int Quantity, int UnitPrice)>(
                    $"item:{demand.ItemId}",
                    (demand.ItemId, demand.Quantity, demand.ItemId == 1 ? 100 : 80))).ToArray();
            var results = degreeOfParallelism == 1
                ? await DeterministicWorkUnits.ExecuteSequentialAsync(
                    units,
                    static (input, _) => Task.FromResult(new
                    {
                        input.ItemId,
                        input.Quantity,
                        Total = input.Quantity * input.UnitPrice,
                        World = input.ItemId == 1 ? "Alpha" : "Beta"
                    }),
                    cancellationToken)
                : await DeterministicWorkUnits.ExecuteBoundedParallelAsync(
                    units.Reverse(),
                    degreeOfParallelism,
                    async (input, ct) =>
                    {
                        var active = Interlocked.Increment(ref _active);
                        UpdateMaximum(ref _maxObservedConcurrency, active);
                        try
                        {
                            await Task.Delay(25, ct);
                            return new
                            {
                                input.ItemId,
                                input.Quantity,
                                Total = input.Quantity * input.UnitPrice,
                                World = input.ItemId == 1 ? "Alpha" : "Beta"
                            };
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _active);
                        }
                    },
                    cancellationToken);
            var resultHash = DeterministicWorkUnits.ComputeResultHash(
                degreeOfParallelism == 1 ? results : results.Reverse());
            return (expandedGraphHash, resultHash, demands.All(demand => demand.Quantity > 0));
        }

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            var observed = Volatile.Read(ref maximum);
            while (candidate > observed)
            {
                var prior = Interlocked.CompareExchange(ref maximum, candidate, observed);
                if (prior == observed)
                {
                    return;
                }
                observed = prior;
            }
        }
    }

    private sealed class TestSnapshotProvider : IReferenceEngineSemanticSnapshotProvider
    {
        public ReferenceEnginePreparedInput PrepareInput(EngineRequestEnvelope request)
        {
            var input = request.Input.Deserialize<ReferenceEngineInput>()
                ?? throw new InvalidOperationException("Unsupported reference input.");
            var demands = (input.MarketAnalysis?.Items ?? [])
                .Concat(input.ProcurementRoute?.ActiveProcurementItems ?? [])
                .GroupBy(item => item.ItemId)
                .OrderBy(group => group.Key)
                .Select(group => new EngineDemandSnapshot(
                    group.Key,
                    group.Sum(item => item.TotalQuantity),
                    group.Any(item => item.RequiresHq)))
                .ToArray();
            var root = new EngineRootIntentSnapshot("1", request.InputKind, demands, EngineCanonicalHash.Compute(request.Settings));
            var nodes = demands.Select((demand, index) => new EngineGraphNodeSnapshot(
                $"item:{demand.ItemId}", demand.ItemId, demand.Quantity, 1000 + demand.ItemId, "market"))
                .ToArray();
            var graph = new EngineExpandedGraphSnapshot("1", nodes, []);
            return new ReferenceEnginePreparedInput(input, root, graph);
        }

        public EngineAnalysisSemanticSnapshot CaptureAnalysis(MarketAnalysisExecutionResult result) =>
            new("1", result.Analyses
                .OrderBy(item => item.ItemId)
                .Select(item => new EngineAnalysisItemSnapshot(
                    item.ItemId,
                    item.QuantityNeeded,
                    item.Scope,
                    item.AnalysisScopeBaselineUnitPrice,
                    item.AnalysisScopeAverageUnitPrice,
                    item.AnalysisScopeMedianUnitPrice,
                    item.CompetitiveThresholdUnitPrice,
                    item.SaneThresholdUnitPrice,
                    item.WorstDataQualityBucket,
                    item.RequestedDataCenters.Order(StringComparer.Ordinal).ToArray(),
                    item.PresentDataCenters.Order(StringComparer.Ordinal).ToArray(),
                    item.MissingDataCenters.Order(StringComparer.Ordinal).ToArray(),
                    item.Worlds.Select((world, rank) => new EngineWorldAnalysisSnapshot(
                        rank,
                        world.DataCenter,
                        world.WorldName,
                        world.ActionableQuantity,
                        world.CostToCoverTotalGil,
                        world.CostToCoverUnitPrice,
                        world.CoverageBucket,
                        world.DataQualityBucket,
                        world.DataAgeSource,
                        world.MarketUploadedAtUtc is { } upload ? new DateTimeOffset(upload).ToUnixTimeMilliseconds() : null,
                        world.DataQualityScore)).ToArray()))
                .ToArray());

        public EngineRouteSemanticSnapshot CaptureRoute(ProcurementRouteExecutionResult result)
        {
            var orderedItems = result.ShoppingPlans.Select((plan, order) =>
            {
                var world = plan.RecommendedWorld;
                return new EngineRouteItemSnapshot(
                    order,
                    plan.ItemId,
                    plan.QuantityNeeded,
                    world?.TotalCost ?? 0,
                    world?.WorldName ?? "missing",
                    world?.MarketDataQualityBucket ?? MarketDataQualityBucket.Missing,
                    world?.MarketDataAgeSource ?? MarketDataAgeSource.Missing,
                    world?.MarketUploadedAtUtc is { } upload ? new DateTimeOffset(upload).ToUnixTimeMilliseconds() : null);
            }).ToArray();
            var stops = result.ShoppingPlans
                .Where(plan => plan.RecommendedWorld is not null)
                .GroupBy(plan => (plan.RecommendedWorld!.DataCenter, plan.RecommendedWorld.WorldName))
                .Select((group, order) => new EngineRouteStopSnapshot(
                    order,
                    group.Key.DataCenter,
                    group.Key.WorldName,
                    group.Select(plan => plan.ItemId).ToArray()))
                .ToArray();
            return new EngineRouteSemanticSnapshot(
                "1",
                stops,
                orderedItems,
                result.RouteDecision?.SelectedGilCost ?? orderedItems.Sum(item => item.TotalGil),
                result.RouteDecision?.SelectedWorldStops ?? stops.Length,
                result.RouteDecision?.SelectedDataCenterTransfers ?? 0,
                result.MissingItems.Count == 0);
        }

        public static string ComputeAnalysisHash(MarketAnalysisExecutionResult result) =>
            EngineSemanticSnapshotHash.Analysis(new TestSnapshotProvider().CaptureAnalysis(result));

        public static string ComputeRouteHash(ProcurementRouteExecutionResult result) =>
            EngineSemanticSnapshotHash.Route(new TestSnapshotProvider().CaptureRoute(result));
    }

    private sealed class RecordingSettlement : IEngineTransactionSettlement
    {
        public List<EnginePhase> Phases { get; } = [];

        public List<EngineSettlementContext> Contexts { get; } = [];

        public Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken)
        {
            Phases.Add(phase);
            Contexts.Add(context);
            return Task.FromResult(new EngineSettlementEvidence(true, $"{phase}:complete"));
        }
    }

    private sealed class FailingCleanupSettlement : IEngineTransactionSettlement
    {
        public Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken) =>
            phase == EnginePhase.ReleasingGate
                ? throw new InvalidOperationException("gate release failed")
                : Task.FromResult(new EngineSettlementEvidence(true, "complete"));
    }

    private sealed class FakeWorkerTransport : IEngineWorkerTransport
    {
        public event EventHandler<EngineWorkerMessage>? MessageReceived;

        public List<EngineWorkerMessage> Sent { get; } = [];

        public int StartCount { get; private set; }

        public int TerminateCount { get; private set; }

        public TaskCompletionSource? StartGate { get; init; }

        public Queue<TaskCompletionSource>? StartGates { get; init; }

        public bool ThrowOnTerminate { get; init; }

        public async Task<EngineWorkerCapability> StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            var gate = StartGates is { Count: > 0 } ? StartGates.Peek() : StartGate;
            if (gate is not null)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            return new EngineWorkerCapability("1", true, false, false, false);
        }

        public Task SendAsync(EngineWorkerMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task TerminateAsync(CancellationToken cancellationToken)
        {
            TerminateCount++;
            return ThrowOnTerminate
                ? Task.FromException(new InvalidOperationException("termination failed"))
                : Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Emit(EngineWorkerMessage message) => MessageReceived?.Invoke(this, message);
    }
}
