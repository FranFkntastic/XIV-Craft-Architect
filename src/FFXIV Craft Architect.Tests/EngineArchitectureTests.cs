using System.Diagnostics;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.Extensions.DependencyInjection;
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
    public void CanonicalHash_BindsCompleteNestedPublicationShape()
    {
        var publishedAt = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
        var baseline = new
        {
            Analyses = new[] { new { ItemId = 1, Name = "Material", Warning = (string?)null } },
            ShoppingPlans = new[] { new { ItemId = 1, MarketDataWarning = (string?)null } },
            RecipeBasis = new { SchemaVersion = 1, Operations = new[] { "node-1" } },
            Scope = new { DataCenters = new[] { "Aether" }, PublishedAtUtc = publishedAt }
        };
        var baselineHash = EngineCanonicalHash.Compute(baseline);

        Assert.NotEqual(baselineHash, EngineCanonicalHash.Compute(new
        {
            Analyses = new[] { new { ItemId = 1, Name = "Renamed", Warning = (string?)null } },
            baseline.ShoppingPlans,
            baseline.RecipeBasis,
            baseline.Scope
        }));
        Assert.NotEqual(baselineHash, EngineCanonicalHash.Compute(new
        {
            baseline.Analyses,
            ShoppingPlans = new[] { new { ItemId = 1, MarketDataWarning = "stale" } },
            baseline.RecipeBasis,
            baseline.Scope
        }));
        Assert.NotEqual(baselineHash, EngineCanonicalHash.Compute(new
        {
            baseline.Analyses,
            baseline.ShoppingPlans,
            RecipeBasis = new { SchemaVersion = 1, Operations = new[] { "node-2" } },
            baseline.Scope
        }));
        Assert.NotEqual(baselineHash, EngineCanonicalHash.Compute(new
        {
            baseline.Analyses,
            baseline.ShoppingPlans,
            baseline.RecipeBasis,
            Scope = new { DataCenters = new[] { "Primal" }, PublishedAtUtc = publishedAt }
        }));
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
                new(0, 1, 1, 10,
                [
                    new(0, "Aether", "Alpha", 1, 10, "market-single", MarketDataQualityBucket.Current, MarketDataAgeSource.UniversalisWorldUpload, 100)
                ], null, null),
                new(1, 2, 1, 20,
                [
                    new(0, "Aether", "Alpha", 1, 20, "market-single", MarketDataQualityBucket.Current, MarketDataAgeSource.UniversalisWorldUpload, 100)
                ], null, null)
            ], 30, 1, 0, true, null);
        var routeB = routeA with { OrderedItems = routeA.OrderedItems.Reverse().ToArray() };

        Assert.NotEqual(EngineSemanticSnapshotHash.ExpandedGraph(graphA), EngineSemanticSnapshotHash.ExpandedGraph(graphB));
        Assert.NotEqual(EngineSemanticSnapshotHash.Route(routeA), EngineSemanticSnapshotHash.Route(routeB));
    }

    [Fact]
    public void ProductionSnapshotProvider_CanonicalizesCombinedDemandInput()
    {
        var input = new ReferenceEngineInput(
            new MarketAnalysisExecutionRequest
            {
                Items =
                [
                    new MaterialAggregate { ItemId = 2, TotalQuantity = 3 },
                    new MaterialAggregate { ItemId = 1, TotalQuantity = 2 }
                ]
            },
            new ProcurementRouteExecutionRequest
            {
                ActiveProcurementItems =
                [
                    new MaterialAggregate { ItemId = 2, TotalQuantity = 4, RequiresHq = true },
                    new MaterialAggregate { ItemId = 3, TotalQuantity = 1 }
                ],
                BlacklistedWorlds = new HashSet<MarketWorldKey>
                {
                    new("Aether", "Alpha")
                },
                ExcludedItemWorlds = new HashSet<MarketItemWorldKey>
                {
                    new(2, new MarketWorldKey("Aether", "Beta"))
                }
            });
        var inputJson = JsonSerializer.SerializeToElement(
            input,
            EngineJsonSerializerOptions.CreateWire());
        var settings = new EngineDeterministicSettings(
            "projection-test",
            new Dictionary<string, string> { ["mode"] = "deterministic" });
        var request = CreateRequest(inputJson) with { Settings = settings };

        var prepared = new ReferenceEngineSemanticSnapshotProvider().PrepareInput(request);

        Assert.Equal("1", prepared.RootIntent.SchemaVersion);
        Assert.Equal(EngineInputKind.RootIntent, prepared.RootIntent.InputKind);
        Assert.Equal(EngineCanonicalHash.Compute(settings), prepared.RootIntent.DeterministicSettingsHash);
        Assert.Collection(
            prepared.RootIntent.Demands,
            demand => Assert.Equal(new EngineDemandSnapshot(1, 2, false), demand),
            demand => Assert.Equal(new EngineDemandSnapshot(2, 7, true), demand),
            demand => Assert.Equal(new EngineDemandSnapshot(3, 1, false), demand));
        Assert.Collection(
            prepared.ExpandedGraph.Nodes,
            node => Assert.Equal(new EngineGraphNodeSnapshot("item:1", 1, 2, 1001, "market"), node),
            node => Assert.Equal(new EngineGraphNodeSnapshot("item:2", 2, 7, 1002, "market"), node),
            node => Assert.Equal(new EngineGraphNodeSnapshot("item:3", 3, 1, 1003, "market"), node));
        Assert.Empty(prepared.ExpandedGraph.Edges);
        Assert.Equal(2, prepared.Input.MarketAnalysis!.Items.Count);
        Assert.Equal(2, prepared.Input.ProcurementRoute!.ActiveProcurementItems.Count);
        Assert.Contains(new MarketWorldKey("Aether", "Alpha"), prepared.Input.ProcurementRoute.BlacklistedWorlds);
        Assert.Contains(
            new MarketItemWorldKey(2, new MarketWorldKey("Aether", "Beta")),
            prepared.Input.ProcurementRoute.ExcludedItemWorlds);
    }

    [Fact]
    public void WireSerializer_CanonicalizesReadOnlySetOrder()
    {
        IReadOnlySet<string> forward = new SortedSet<string>(
            ["Alpha", "Beta", "Gamma"],
            StringComparer.Ordinal);
        IReadOnlySet<string> reverse = new SortedSet<string>(
            ["Alpha", "Beta", "Gamma"],
            Comparer<string>.Create((left, right) => StringComparer.Ordinal.Compare(right, left)));
        var options = EngineJsonSerializerOptions.CreateWire();

        Assert.NotEqual(string.Join(',', forward), string.Join(',', reverse));
        Assert.Equal(
            JsonSerializer.Serialize(forward, options),
            JsonSerializer.Serialize(reverse, options));
    }

    [Theory]
    [InlineData(
        EngineInputKind.RootIntent,
        true,
        false,
        "Analyzing,Publishing,Persisting,SettlingUi,CapturingPostActionEvidence,ReleasingGate",
        "Publishing,Persisting,SettlingUi,CapturingPostActionEvidence,ReleasingGate")]
    [InlineData(
        EngineInputKind.RootIntent,
        false,
        true,
        "Reconciling,Publishing,SettlingRoute,Persisting,SettlingUi,CapturingPostActionEvidence,ReleasingGate",
        "Publishing,SettlingRoute,Persisting,SettlingUi,CapturingPostActionEvidence,ReleasingGate")]
    [InlineData(
        EngineInputKind.RootIntent,
        true,
        true,
        "Analyzing,Reconciling,Publishing,SettlingRoute,Persisting,SettlingUi,CapturingPostActionEvidence,ReleasingGate",
        "Publishing,SettlingRoute,Persisting,SettlingUi,CapturingPostActionEvidence,ReleasingGate")]
    [InlineData(
        EngineInputKind.RestoredSession,
        true,
        false,
        "Analyzing,Publishing,Persisting,SettlingUi,CapturingPostActionEvidence,CapturingRestorationEvidence,ReleasingGate",
        "Publishing,Persisting,SettlingUi,CapturingPostActionEvidence,CapturingRestorationEvidence,ReleasingGate")]
    [InlineData(
        EngineInputKind.RestoredSession,
        false,
        true,
        "Reconciling,Publishing,SettlingRoute,Persisting,SettlingUi,CapturingPostActionEvidence,CapturingRestorationEvidence,ReleasingGate",
        "Publishing,SettlingRoute,Persisting,SettlingUi,CapturingPostActionEvidence,CapturingRestorationEvidence,ReleasingGate")]
    public void SuccessPhasePolicy_IsOperationAndInputKindSpecific(
        EngineInputKind inputKind,
        bool includesMarketAnalysis,
        bool includesProcurementRoute,
        string expectedRequiredEvidence,
        string expectedSettlement)
    {
        var requirements = EngineSuccessPhasePolicy.Resolve(
            inputKind,
            includesMarketAnalysis,
            includesProcurementRoute);

        Assert.Equal(expectedRequiredEvidence, string.Join(',', requirements.RequiredEvidencePhases));
        Assert.Equal(expectedSettlement, string.Join(',', requirements.SettlementPhases));
    }

    [Fact]
    public void AnalysisSemanticHash_ExcludesTimingsButBindsFreshnessEvidence()
    {
        var fast = CreateAnalysisResult(TimeSpan.FromMilliseconds(1), DateTime.UnixEpoch);
        var slow = CreateAnalysisResult(TimeSpan.FromSeconds(8), DateTime.UnixEpoch);
        var freshEvidence = CreateAnalysisResult(TimeSpan.FromSeconds(8), DateTime.UnixEpoch.AddYears(20));

        Assert.Equal(ComputeAnalysisHash(fast), ComputeAnalysisHash(slow));
        Assert.NotEqual(ComputeAnalysisHash(fast), ComputeAnalysisHash(freshEvidence));
    }

    [Fact]
    public void AnalysisSemanticHash_BindsShoppingRecommendations()
    {
        var alpha = CreateAnalysisResult();
        var beta = CreateAnalysisResult();
        alpha.ShoppingPlans[0].RecommendedWorld = CreateWorld("Alpha", 100);
        beta.ShoppingPlans[0].RecommendedWorld = CreateWorld("Beta", 100);

        Assert.NotEqual(ComputeAnalysisHash(alpha), ComputeAnalysisHash(beta));
    }

    [Fact]
    public void AnalysisSemanticHash_BindsShoppingFailuresAndWarnings()
    {
        var baseline = CreateAnalysisResult();
        var failed = CreateAnalysisResult();
        var warned = CreateAnalysisResult();
        failed.ShoppingPlans[0].Error = "No supported acquisition was found.";
        warned.ShoppingPlans[0].MarketDataWarning = "Market evidence is aging.";

        Assert.NotEqual(ComputeAnalysisHash(baseline), ComputeAnalysisHash(failed));
        Assert.NotEqual(ComputeAnalysisHash(baseline), ComputeAnalysisHash(warned));
    }

    [Fact]
    public void RouteSemanticHash_BindsFreshnessEvidenceAndDecisions()
    {
        var early = CreateRouteResult(DateTime.UnixEpoch, 100);
        var late = CreateRouteResult(DateTime.UnixEpoch.AddYears(20), 100);
        var changed = CreateRouteResult(DateTime.UnixEpoch.AddYears(20), 101);

        Assert.NotEqual(
            ComputeRouteHash(early),
            ComputeRouteHash(late));
        Assert.NotEqual(
            ComputeRouteHash(early),
            ComputeRouteHash(changed));
    }

    [Fact]
    public void RouteSemanticHash_BindsRouteDecisionPolicyAndEvidence()
    {
        var baseline = CreateRouteResult(DateTime.UnixEpoch, 100);
        var decision = baseline.RouteDecision!;
        var changedResults = new[]
        {
            baseline with
            {
                RouteDecision = decision with { SelectedEvidencePenalty = 1 }
            },
            baseline with
            {
                RouteDecision = decision with { TravelPriority = MarketTravelPriority.WorldVisitsFirst }
            },
            baseline with
            {
                RouteDecision = decision with { AcquisitionSearchWasTruncated = true }
            },
            baseline with
            {
                RouteDecision = decision with
                {
                    ItemDecisions = [new MarketRouteItemDecision(1, "Item", 90, 100)]
                }
            }
        };
        var baselineHash = ComputeRouteHash(baseline);

        Assert.All(changedResults, result => Assert.NotEqual(baselineHash, ComputeRouteHash(result)));
    }

    [Fact]
    public void RouteSemanticHash_BindsSplitWorldSelections()
    {
        var alphaBeta = CreateSplitRouteResult("Alpha", "Beta");
        var gammaDelta = CreateSplitRouteResult("Gamma", "Delta");
        var snapshot = new ReferenceEngineSemanticSnapshotProvider().CaptureRoute(alphaBeta);

        Assert.NotEqual(ComputeRouteHash(alphaBeta), ComputeRouteHash(gammaDelta));
        Assert.Equal(["Alpha", "Beta"], snapshot.OrderedItems[0].SelectedLegs.Select(leg => leg.World));
        Assert.All(snapshot.OrderedItems[0].SelectedLegs, leg => Assert.Equal("market-split", leg.AcquisitionSource));
        Assert.Equal(["Alpha", "Beta"], snapshot.OrderedStops.Select(stop => stop.World));
    }

    [Fact]
    public void RouteSemanticHash_BindsCoverageWorldSelections()
    {
        var alphaBeta = CreateCoverageRouteResult("Alpha", "Beta");
        var gammaDelta = CreateCoverageRouteResult("Gamma", "Delta");
        var snapshot = new ReferenceEngineSemanticSnapshotProvider().CaptureRoute(alphaBeta);

        Assert.NotEqual(ComputeRouteHash(alphaBeta), ComputeRouteHash(gammaDelta));
        Assert.Equal(["Alpha", "Beta"], snapshot.OrderedItems[0].SelectedLegs.Select(leg => leg.World));
        Assert.All(snapshot.OrderedItems[0].SelectedLegs, leg => Assert.Equal("market-coverage", leg.AcquisitionSource));
    }

    [Fact]
    public async Task ReferenceComputation_RejectsStaleInputAndGraphBasis()
    {
        var engine = new ReferenceMarketProcurementEngine(
            Mock.Of<IMarketAnalysisExecutionService>(),
            Mock.Of<IProcurementRouteExecutionService>(),
            new ReferenceEngineSemanticSnapshotProvider());
        var input = JsonSerializer.SerializeToElement(new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null));
        var request = CreateRequest(input);

        Assert.Equal(EngineComputationStatus.Failed, (await engine.ComputeAsync(1, Guid.NewGuid(), request with { RootIntentHash = "stale" })).Status);
        Assert.Equal(EngineComputationStatus.Failed, (await engine.ComputeAsync(2, Guid.NewGuid(), request with { ExpandedGraphHash = "stale" })).Status);
    }

    [Fact]
    public async Task ReferenceComputation_RejectsIncompleteProcurementRoutes()
    {
        var routeService = new Mock<IProcurementRouteExecutionService>();
        routeService
            .Setup(service => service.AnalyzeAsync(
                It.IsAny<ProcurementRouteExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(CreateRouteResult(DateTime.UnixEpoch, 100) with { IsComplete = false });
        var engine = new ReferenceMarketProcurementEngine(
            Mock.Of<IMarketAnalysisExecutionService>(),
            routeService.Object,
            new ReferenceEngineSemanticSnapshotProvider());
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(null, new ProcurementRouteExecutionRequest())));

        var result = await engine.ComputeAsync(1, Guid.NewGuid(), request);

        Assert.Equal(EngineComputationStatus.Failed, result.Status);
        Assert.Contains("viable acquisition", result.Failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("phase:Publishing", result.ComputationEvidence.Keys);
    }

    [Fact]
    public async Task ReferenceComputation_RejectsUnsupportedBudgetsInputKindsAndEmptyOperations()
    {
        var engine = new ReferenceMarketProcurementEngine(
            Mock.Of<IMarketAnalysisExecutionService>(),
            Mock.Of<IProcurementRouteExecutionService>(),
            new ReferenceEngineSemanticSnapshotProvider());
        var operation = JsonSerializer.SerializeToElement(new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null));
        var unsupportedBudget = CreateRequest(operation) with
        {
            Budgets = EngineExecutionBudgets.Default with { MaxCandidateEvaluations = 12 }
        };
        var unsupportedKind = CreateRequest(operation) with { InputKind = EngineInputKind.StructuredGraph };
        var empty = CreateRequest(JsonSerializer.SerializeToElement(new ReferenceEngineInput(null, null)));

        Assert.Equal(EngineComputationStatus.Failed, (await engine.ComputeAsync(1, Guid.NewGuid(), unsupportedBudget)).Status);
        Assert.Equal(EngineComputationStatus.Failed, (await engine.ComputeAsync(2, Guid.NewGuid(), unsupportedKind)).Status);
        Assert.Equal(EngineComputationStatus.Failed, (await engine.ComputeAsync(3, Guid.NewGuid(), empty)).Status);
    }

    [Fact]
    public async Task DeterministicComputation_CannotProduceFinalTransactionSuccess()
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
            new ReferenceEngineSemanticSnapshotProvider());
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var result = await engine.ComputeAsync(7, Guid.NewGuid(), request);

        Assert.Equal(EngineComputationStatus.Completed, result.Status);
        Assert.Equal(7, result.Generation);
        Assert.False(result.ComputationEvidence.ContainsKey("settlement"));
        Assert.DoesNotContain("phase:Publishing", result.ComputationEvidence.Keys);
    }

    [Fact]
    public async Task Host_PreservesActualComputationCancellationPhase()
    {
        using var cancellation = new CancellationTokenSource();
        var analysis = new Mock<IMarketAnalysisExecutionService>();
        analysis.Setup(service => service.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<MarketAnalysisExecutionResult>(cancellation.Token);
            });
        var engine = new ReferenceMarketProcurementEngine(
            analysis.Object,
            Mock.Of<IProcurementRouteExecutionService>(),
            new ReferenceEngineSemanticSnapshotProvider());
        var host = EngineExecutionHost.CreateForTesting(
            new InProcessEngineExecutionTransport(engine),
            new RecordingSettlement(),
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var result = await host.ExecuteAsync(request, cancellationToken: cancellation.Token);

        Assert.Equal(EngineTerminalStatus.Cancelled, result.Status);
        Assert.Equal(EnginePhase.Analyzing.ToString(), result.Completion.TerminalEvidence["failedPhase"]);
    }

    [Fact]
    public async Task Host_NoOpSettlementCannotAttestSuccess()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) => CreateCompletedComputation(current, generation, executionId)),
            new NoOpEngineTransactionSettlement(),
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Equal("gate-release-failed", result.Failure!.Code);
        Assert.True(result.Failure.IsRetryable);
        Assert.Equal("incomplete", result.Completion.TerminalEvidence["settlement"]);
    }

    [Fact]
    public async Task Host_RejectsStaleComputationGenerationBeforeSettlement()
    {
        var settlement = new RecordingSettlement();
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) => CreateCompletedComputation(current, generation - 1, executionId)),
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Contains("Stale", result.Failure!.Message, StringComparison.Ordinal);
        Assert.Equal([EnginePhase.ReleasingGate], settlement.Phases);
        Assert.Contains("cleanup:ReleasingGate", result.Completion.TerminalEvidence.Keys);
    }

    [Fact]
    public async Task Host_RejectsEveryAuthoritativeRequestBindingMutation()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var mutations = new Func<EngineComputationResult, EngineComputationResult>[]
        {
            result => result with { TransactionId = Guid.NewGuid() },
            result => result with { Basis = result.Basis with { Plan = result.Basis.Plan with { Hash = "wrong" } } },
            result => result with { RequestInputHash = "wrong" },
            result => result with { Budgets = result.Budgets with { MaxWorkUnits = result.Budgets.MaxWorkUnits + 1 } },
            result => result with { RootIntentHash = "wrong" },
            result => result with { ExpandedGraphHash = "wrong" },
            result => result with { AnalysisBasisHash = "wrong" },
            result => result with { RouteBasisHash = "wrong" }
        };

        foreach (var mutate in mutations)
        {
            var settlement = new RecordingSettlement();
            var host = EngineExecutionHost.CreateForTesting(
                new StubEngineExecutionTransport((generation, executionId, current) =>
                    mutate(CreateCompletedComputation(current, generation, executionId))),
                settlement,
                new InMemoryEngineTransactionLedger(),
                new ReferenceEngineSemanticSnapshotProvider());

            var result = await host.ExecuteAsync(request);

            Assert.Equal(EngineTerminalStatus.Failed, result.Status);
            Assert.Contains("authoritative request", result.Failure!.Message, StringComparison.Ordinal);
            Assert.Equal([EnginePhase.ReleasingGate], settlement.Phases);
        }
    }

    [Fact]
    public async Task HostRecreation_ExactReplayReturnsStableResultWithoutEffects()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var ledger = new InMemoryEngineTransactionLedger();
        var firstTransport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var firstHost = EngineExecutionHost.CreateForTesting(
            firstTransport,
            new RecordingSettlement(),
            ledger,
            new ReferenceEngineSemanticSnapshotProvider());
        var first = await firstHost.ExecuteAsync(request);

        var secondSettlement = new RecordingSettlement();
        var secondTransport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var recreatedHost = EngineExecutionHost.CreateForTesting(
            secondTransport,
            secondSettlement,
            ledger,
            new ReferenceEngineSemanticSnapshotProvider());

        var replay = await recreatedHost.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Succeeded, replay.Status);
        Assert.Equal(first.Completion.FinalTransactionHash, replay.Completion.FinalTransactionHash);
        Assert.Equal(first.Completion.TerminalEvidence, replay.Completion.TerminalEvidence);
        Assert.Equal(1, firstTransport.ExecutionCount);
        Assert.Equal(0, secondTransport.ExecutionCount);
        Assert.Equal([EnginePhase.ReleasingGate], secondSettlement.Phases);
    }

    [Fact]
    public async Task Host_DerivesSemanticHashesFromTransportedPayload()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var settlement = new RecordingSettlement();
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) =>
            {
                var original = CreateCompletedComputation(current, generation, executionId);
                var transported = original.Result!.Value.Deserialize<ReferenceEngineResultSnapshot>(
                    EngineJsonSerializerOptions.CreateWire())!;
                var payload = JsonSerializer.SerializeToElement(
                    transported with { MarketAnalysis = transported.MarketAnalysis! with { SchemaVersion = "tampered" } },
                    EngineJsonSerializerOptions.CreateWire());
                var evidence = new Dictionary<string, string>(original.ComputationEvidence)
                {
                    ["resultPayloadHash"] = EngineCanonicalHash.Compute(payload)
                };
                return RehashComputation(current, original with
                {
                    Result = payload,
                    ComputationEvidence = evidence
                });
            }),
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Contains("semantic result hashes", result.Failure!.Message, StringComparison.Ordinal);
        Assert.Equal([EnginePhase.ReleasingGate], settlement.Phases);
    }

    [Fact]
    public async Task Host_RejectsLiteralComputationHashClaim()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId) with { ComputationHash = "trusted-literal" }),
            new RecordingSettlement(),
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Contains("computation hash validation", result.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_CancellationBeforeCommitCancelsAndReleasesGate()
    {
        using var cancellation = new CancellationTokenSource();
        var settlement = new RecordingSettlement
        {
            OnPhase = phase =>
            {
                if (phase == EnginePhase.Publishing)
                {
                    cancellation.Cancel();
                }
            }
        };
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var host = CreateHost(settlement);

        var result = await host.ExecuteAsync(request, cancellationToken: cancellation.Token);

        Assert.Equal(EngineTerminalStatus.Cancelled, result.Status);
        Assert.Equal([EnginePhase.Publishing, EnginePhase.ReleasingGate], settlement.Phases);
        Assert.False(result.Completion.TerminalEvidence.ContainsKey("commitPoint"));
    }

    [Fact]
    public async Task Host_CancellationAfterCommitCompletesSettlement()
    {
        using var cancellation = new CancellationTokenSource();
        var settlement = new RecordingSettlement
        {
            OnPhase = phase =>
            {
                if (phase == EnginePhase.Persisting)
                {
                    cancellation.Cancel();
                }
            }
        };
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var host = CreateHost(settlement);

        var result = await host.ExecuteAsync(request, cancellationToken: cancellation.Token);

        Assert.Equal(EngineTerminalStatus.Succeeded, result.Status);
        Assert.Equal(EnginePhase.Persisting.ToString(), result.Completion.TerminalEvidence["commitPoint"]);
        Assert.Equal(
            [EnginePhase.Publishing, EnginePhase.Persisting, EnginePhase.SettlingUi,
                EnginePhase.CapturingPostActionEvidence, EnginePhase.ReleasingGate],
            settlement.Phases);
    }

    [Fact]
    public async Task Host_CancelledPersistenceAcknowledgementObservesDurableCommit()
    {
        using var cancellation = new CancellationTokenSource();
        var settlement = new CancelledCommitAcknowledgementSettlement(cancellation);
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId)),
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request, cancellationToken: cancellation.Token);

        Assert.Equal(EngineTerminalStatus.Succeeded, result.Status);
        Assert.Equal("committed", result.Completion.TerminalEvidence["commitState"]);
        Assert.Equal(1, settlement.PersistenceDeliveryCount);
        Assert.Equal(1, settlement.PersistenceObservationCount);
    }

    [Fact]
    public async Task Host_SettlementIsIdempotentPerTransaction()
    {
        var settlement = new RecordingSettlement();
        var transport = new StubEngineExecutionTransport((generation, executionId, request) => CreateCompletedComputation(request, generation, executionId));
        var host = EngineExecutionHost.CreateForTesting(
            transport,
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var first = host.ExecuteAsync(request);
        var second = host.ExecuteAsync(request);
        var results = await Task.WhenAll(first, second);

        Assert.Same(first, second);
        Assert.Same(results[0], results[1]);
        Assert.Equal(1, transport.ExecutionCount);
        Assert.Equal(5, settlement.Phases.Count);
    }

    [Fact]
    public async Task Host_PersistenceFailureCannotReleaseSuccessOrContinueUiSettlement()
    {
        var settlement = new RecordingSettlement { FailPhase = EnginePhase.Persisting };
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var result = await CreateHost(settlement).ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Equal(
            [EnginePhase.Publishing, EnginePhase.Persisting, EnginePhase.ReleasingGate],
            settlement.Phases);
        Assert.Equal("pre-commit-visible-effect-failure", result.Failure!.Code);
        Assert.Equal("incomplete", result.Completion.TerminalEvidence["settlement"]);
        Assert.Equal("not-committed", result.Completion.TerminalEvidence["commitState"]);
        Assert.DoesNotContain("phase:ReleasingGate", result.Completion.TerminalEvidence.Keys);
    }

    [Fact]
    public async Task Host_PostCommitFailureIsReportedAsIncompleteAndGateReleaseIsNotDuplicated()
    {
        var settlement = new RecordingSettlement { FailPhase = EnginePhase.SettlingUi };
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var result = await CreateHost(settlement).ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Equal("settlement-incomplete-after-commit", result.Failure!.Code);
        Assert.Equal("committed", result.Completion.TerminalEvidence["commitState"]);
        Assert.Equal("incomplete", result.Completion.TerminalEvidence["settlement"]);
        Assert.Equal(1, settlement.Phases.Count(phase => phase == EnginePhase.ReleasingGate));
    }

    [Fact]
    public async Task Host_RetriesFailedGateReleaseIdempotently()
    {
        var settlement = new RecordingSettlement { FailPhase = EnginePhase.ReleasingGate };
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var result = await CreateHost(settlement).ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Equal("gate-release-failed", result.Failure!.Code);
        Assert.True(result.Failure.IsRetryable);
        Assert.Equal(2, settlement.Phases.Count(phase => phase == EnginePhase.ReleasingGate));
    }

    [Fact]
    public async Task Host_KnownFailedGateReleaseCanRetryAfterClaimRelease()
    {
        var settlement = new RecoveringGateSettlement();
        var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var host = EngineExecutionHost.CreateForTesting(
            transport,
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var failedRelease = await host.ExecuteAsync(request);
        var retry = await host.ExecuteAsync(request);

        Assert.Equal("gate-release-failed", failedRelease.Failure!.Code);
        Assert.True(failedRelease.Failure.IsRetryable);
        Assert.Equal(EngineTerminalStatus.Succeeded, retry.Status);
        Assert.Equal(3, settlement.GateAttempts);
        Assert.Equal(2, transport.ExecutionCount);
    }

    [Fact]
    public async Task Host_SuccessSettlementReleasesGateLast()
    {
        var settlement = new RecordingSettlement();
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), new ProcurementRouteExecutionRequest())));

        var result = await CreateHost(settlement).ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Succeeded, result.Status);
        Assert.Equal(
            [EnginePhase.Publishing, EnginePhase.SettlingRoute, EnginePhase.Persisting,
                EnginePhase.SettlingUi, EnginePhase.CapturingPostActionEvidence, EnginePhase.ReleasingGate],
            settlement.Phases);
        Assert.Equal("complete", result.Completion.TerminalEvidence["settlement"]);
    }

    [Fact]
    public void EngineFoundationServices_DoNotImplyHostReadiness()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IMarketAnalysisExecutionService>());
        services.AddSingleton(Mock.Of<IProcurementRouteExecutionService>());
        services.AddCraftArchitectEngine();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.IsType<ReferenceEngineSemanticSnapshotProvider>(
            scope.ServiceProvider.GetRequiredService<IReferenceEngineSemanticSnapshotProvider>());
        Assert.IsType<ReferenceMarketProcurementEngine>(
            scope.ServiceProvider.GetRequiredService<IMarketProcurementEngine>());
        Assert.IsType<InProcessEngineExecutionTransport>(
            scope.ServiceProvider.GetRequiredService<IEngineExecutionTransport>());
        Assert.Null(scope.ServiceProvider.GetService<IEngineTransactionSettlement>());
        Assert.Null(scope.ServiceProvider.GetService<IEngineTransactionLedger>());
        Assert.Null(scope.ServiceProvider.GetService<IEngineExecutionHost>());
        var program = File.ReadAllText(Path.Combine(
            LocateRepositoryRoot(),
            "src",
            "FFXIV Craft Architect.Web",
            "Program.cs"));
        Assert.DoesNotContain("AddCraftArchitectEngine", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_PublicActivationRejectsNonDurableLedger()
    {
        var exception = Assert.Throws<ArgumentException>(() => new EngineExecutionHost(
            new StubEngineExecutionTransport((generation, executionId, request) =>
                CreateCompletedComputation(request, generation, executionId)),
            new RecordingSettlement(),
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider()));

        Assert.Contains("durably preserve", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InMemoryLedger_FencesCompletionAndReleaseByClaimToken()
    {
        var ledger = new InMemoryEngineTransactionLedger();
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var requestHash = EngineCanonicalHash.Compute(request, EngineJsonSerializerOptions.CreateWire());
        var first = await ledger.ClaimAsync(request.TransactionId, requestHash, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(first.ClaimToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.ReleaseAsync(
            request.TransactionId,
            requestHash,
            "stale-owner",
            CancellationToken.None).AsTask());
        await ledger.ReleaseAsync(request.TransactionId, requestHash, first.ClaimToken!, CancellationToken.None);

        var second = await ledger.ClaimAsync(request.TransactionId, requestHash, CancellationToken.None);
        Assert.NotEqual(first.ClaimToken, second.ClaimToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.CompleteAsync(
            request.TransactionId,
            requestHash,
            first.ClaimToken!,
            CreateSuccessfulResult(request),
            CancellationToken.None).AsTask());
        await ledger.CompleteAsync(
            request.TransactionId,
            requestHash,
            second.ClaimToken!,
            CreateSuccessfulResult(request),
            CancellationToken.None);

        var replay = await ledger.ClaimAsync(request.TransactionId, requestHash, CancellationToken.None);
        Assert.Equal(EngineTransactionClaimDisposition.TerminalReplay, replay.Disposition);
        Assert.Null(replay.ClaimToken);
    }

    [Fact]
    public async Task InMemoryLedger_DetachesTerminalEvidenceBeforeStorage()
    {
        var ledger = new InMemoryEngineTransactionLedger();
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var requestHash = EngineCanonicalHash.Compute(request, EngineJsonSerializerOptions.CreateWire());
        var claim = await ledger.ClaimAsync(request.TransactionId, requestHash, CancellationToken.None);
        var source = CreateSuccessfulResult(request);
        var mutableEvidence = Assert.IsType<Dictionary<string, string>>(source.Completion.TerminalEvidence);

        await ledger.CompleteAsync(
            request.TransactionId,
            requestHash,
            claim.ClaimToken!,
            source,
            CancellationToken.None);
        mutableEvidence["phase:Analyzing"] = "mutated-after-write";

        var replay = await ledger.ClaimAsync(request.TransactionId, requestHash, CancellationToken.None);
        var stored = replay.TerminalResult!;
        Assert.Equal("complete", stored.Completion.TerminalEvidence["phase:Analyzing"]);
        AssertEvidenceCannotBeMutated(stored.Completion.TerminalEvidence);
    }

    [Fact]
    public async Task Host_AbandonedClaimIsFencedTerminalizedAndReplayedWithoutExternalEffects()
    {
        var ledger = new InMemoryEngineTransactionLedger();
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var requestHash = EngineCanonicalHash.Compute(request, EngineJsonSerializerOptions.CreateWire());
        var lostClaim = await ledger.ClaimAsync(request.TransactionId, requestHash, CancellationToken.None);
        ledger.MarkClaimAbandoned(request.TransactionId, requestHash, lostClaim.ClaimToken!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.CompleteAsync(
            request.TransactionId,
            requestHash,
            lostClaim.ClaimToken!,
            CreateSuccessfulResult(request),
            CancellationToken.None).AsTask());
        var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var settlement = new RecordingSettlement();
        var host = EngineExecutionHost.CreateForTesting(
            transport,
            settlement,
            ledger,
            new ReferenceEngineSemanticSnapshotProvider());

        var abandoned = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Indeterminate, abandoned.Status);
        Assert.Equal("transaction-abandoned-after-crash", abandoned.Failure!.Code);
        Assert.Equal("abandoned-after-crash", abandoned.Completion.TerminalEvidence["replayStatus"]);
        Assert.False(abandoned.Failure.IsRetryable);
        Assert.Equal(0, transport.ExecutionCount);
        Assert.Equal([EnginePhase.ReleasingGate], settlement.Phases);

        var replayTransport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var replaySettlement = new RecordingSettlement();
        var replay = await EngineExecutionHost.CreateForTesting(
            replayTransport,
            replaySettlement,
            ledger,
            new ReferenceEngineSemanticSnapshotProvider()).ExecuteAsync(request);

        Assert.Equal(abandoned.Completion.FinalTransactionHash, replay.Completion.FinalTransactionHash);
        Assert.Equal(abandoned.Completion.TerminalEvidence, replay.Completion.TerminalEvidence);
        Assert.Equal(0, replayTransport.ExecutionCount);
        Assert.Equal([EnginePhase.ReleasingGate], replaySettlement.Phases);
    }

    [Fact]
    public async Task Host_AbandonedClaimLedgerFailureDoesNotEscapeOrInvokeExternalEffects()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var settlement = new RecordingSettlement();
        var host = new EngineExecutionHost(
            transport,
            settlement,
            new AbandonedWriteFailureLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
        Assert.Equal("transaction-ledger-write-indeterminate", result.Failure!.Code);
        Assert.Equal("transaction-abandoned-after-crash", result.Completion.TerminalEvidence["ledgerWrite:originalTerminalCode"]);
        Assert.Equal(0, transport.ExecutionCount);
        Assert.Equal([EnginePhase.ReleasingGate], settlement.Phases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_AbandonedReplayCleanupIsLimitedToCurrentInvocationOwner(bool ledgerWriteFails)
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var requestHash = EngineCanonicalHash.Compute(request, EngineJsonSerializerOptions.CreateWire());
        IEngineTransactionLedger ledger;
        if (ledgerWriteFails)
        {
            ledger = new AbandonedWriteFailureLedger();
        }
        else
        {
            var memory = new InMemoryEngineTransactionLedger();
            var claim = await memory.ClaimAsync(request.TransactionId, requestHash, CancellationToken.None);
            memory.MarkClaimAbandoned(request.TransactionId, requestHash, claim.ClaimToken!);
            ledger = memory;
        }
        var settlement = new InvocationFencedGateSettlement();
        Assert.NotNull(settlement.TryRegisterInvocationCleanupOwnership(
            new EngineInvocationCleanupRegistration(request, "existing-owner")));
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId)),
            settlement,
            ledger,
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
        Assert.True(settlement.IsGateHeld);
        Assert.Equal(0, settlement.SettlementCalls);
        Assert.Equal(0, settlement.GateReleaseCount);
    }

    [Fact]
    public async Task Host_RejectsFaultedAndMalformedClaimsBeforeComputationAndReleasesGate()
    {
        var claimFactories = new Func<string, EngineTransactionClaim?>[]
        {
            _ => throw new InvalidOperationException("injected claim failure"),
            _ => null,
            hash => new EngineTransactionClaim((EngineTransactionClaimDisposition)999, hash),
            hash => new EngineTransactionClaim(EngineTransactionClaimDisposition.Claimed, hash),
            _ => new EngineTransactionClaim(
                EngineTransactionClaimDisposition.Claimed,
                "not-a-canonical-hash",
                ClaimToken: "owner"),
            _ => new EngineTransactionClaim(
                EngineTransactionClaimDisposition.Claimed,
                new string('a', 64),
                ClaimToken: "owner"),
            hash => new EngineTransactionClaim(EngineTransactionClaimDisposition.TerminalReplay, hash),
            hash => new EngineTransactionClaim(EngineTransactionClaimDisposition.AbandonedReplay, hash),
            hash => new EngineTransactionClaim(
                EngineTransactionClaimDisposition.AbandonedReplay,
                hash,
                CreateSuccessfulResult(CreateRequest(JsonSerializer.SerializeToElement(
                    new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)))),
                "owner")
        };

        foreach (var claimFactory in claimFactories)
        {
            var transport = new StubEngineExecutionTransport((generation, executionId, request) =>
                CreateCompletedComputation(request, generation, executionId));
            var settlement = new RecordingSettlement();
            var host = new EngineExecutionHost(
                transport,
                settlement,
                new ClaimResponseLedger(claimFactory),
                new ReferenceEngineSemanticSnapshotProvider());
            var request = CreateRequest(JsonSerializer.SerializeToElement(
                new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

            var result = await host.ExecuteAsync(request);

            Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
            Assert.Equal("transaction-ledger-claim-indeterminate", result.Failure!.Code);
            Assert.Equal(0, transport.ExecutionCount);
            Assert.Equal([EnginePhase.ReleasingGate], settlement.Phases);
        }
    }

    [Fact]
    public async Task Host_ClaimFaultRemainsIndeterminateWhenGateCleanupCannotBeConfirmed()
    {
        var transport = new StubEngineExecutionTransport((generation, executionId, request) =>
            CreateCompletedComputation(request, generation, executionId));
        var host = new EngineExecutionHost(
            transport,
            new NoOpEngineTransactionSettlement(),
            new ClaimResponseLedger(_ => throw new InvalidOperationException("injected claim failure")),
            new ReferenceEngineSemanticSnapshotProvider());
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
        Assert.Equal("gate-release-indeterminate", result.Failure!.Code);
        Assert.Equal(0, transport.ExecutionCount);
    }

    [Theory]
    [InlineData(PrevalidationTransportBehavior.Unsupported)]
    [InlineData(PrevalidationTransportBehavior.Throwing)]
    public async Task Host_BoundsGateCleanupBeforeComputationValidation(
        PrevalidationTransportBehavior behavior)
    {
        using var cancellation = new CancellationTokenSource();
        var settlement = new HangingGateSettlement();
        var host = EngineExecutionHost.CreateForTesting(
            new PrevalidationTransport(behavior),
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider(),
            new EngineExecutionHostOptions(4, TimeSpan.FromMilliseconds(25)));
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var started = DateTime.UtcNow;
        var result = await host.ExecuteAsync(request, cancellationToken: cancellation.Token);

        Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
        Assert.Equal("gate-release-indeterminate", result.Failure!.Code);
        Assert.Equal(1, settlement.ReleaseAttempts);
        Assert.StartsWith("failed:", result.Completion.TerminalEvidence["cleanup:ReleasingGate"], StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Host_PreClaimCancellationBoundsGateCleanupAndDoesNotCacheTransientOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var settlement = new HangingGateSettlement();
        var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var host = EngineExecutionHost.CreateForTesting(
            transport,
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider(),
            new EngineExecutionHostOptions(
                4,
                TimeSpan.FromMilliseconds(25),
                MaxConcurrentExecutions: 1));
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var result = await host.ExecuteAsync(request, cancellationToken: cancellation.Token);
        var retry = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
        Assert.Equal("gate-release-indeterminate", result.Failure!.Code);
        Assert.Equal("unclaimed-transient", result.Completion.TerminalEvidence["replayStatus"]);
        Assert.Equal("gate-release-indeterminate", retry.Failure!.Code);
        Assert.Equal(2, settlement.ReleaseAttempts);
        Assert.Equal(0, transport.ExecutionCount);
    }

    [Fact]
    public async Task Host_BurstIsRejectedBeforePreSlotExecutionTrackingCanGrow()
    {
        using var release = new ManualResetEventSlim();
        var transport = new SynchronouslyBlockingTransport(release);
        var settlement = new ConcurrentSettlement();
        var host = EngineExecutionHost.CreateForTesting(
            transport,
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider(),
            new EngineExecutionHostOptions(
                8,
                TimeSpan.FromSeconds(1),
                MaxConcurrentExecutions: 1,
                ComputationTimeout: TimeSpan.FromSeconds(2)));
        var firstRequest = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var first = host.ExecuteAsync(firstRequest);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var burst = Enumerable.Range(0, 12)
            .Select(_ => host.ExecuteAsync(CreateRequest(JsonSerializer.SerializeToElement(
                new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)))))
            .ToArray();
        var trackedField = typeof(EngineExecutionHost).GetField(
            "_executions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var tracked = Assert.IsAssignableFrom<System.Collections.IDictionary>(trackedField.GetValue(host));

        Assert.Single(tracked.Keys.Cast<object>());
        Assert.All(burst, task => Assert.Equal("execution-capacity-exhausted", task.Result.Failure!.Code));
        Assert.Equal(12, settlement.GateReleaseCount);

        release.Set();
        Assert.Equal(EngineTerminalStatus.Succeeded, (await first).Status);
        Assert.Equal(13, settlement.GateReleaseCount);
    }

    [Fact]
    public async Task Host_CapacityCleanupIsGloballyBoundedAndRejectsCleanupOverload()
    {
        using var releaseExecution = new ManualResetEventSlim();
        var transport = new SynchronouslyBlockingTransport(releaseExecution);
        var settlement = new BlockingCapacityCleanupSettlement();
        var host = EngineExecutionHost.CreateForTesting(
            transport,
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider(),
            new EngineExecutionHostOptions(
                16,
                TimeSpan.FromSeconds(2),
                MaxConcurrentExecutions: 1,
                ComputationTimeout: TimeSpan.FromSeconds(2),
                MaxConcurrentCleanupOperations: 2));
        var first = host.ExecuteAsync(CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null))));
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var admitted = new[]
        {
            host.ExecuteAsync(CreateRequest(JsonSerializer.SerializeToElement(
                new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)))),
            host.ExecuteAsync(CreateRequest(JsonSerializer.SerializeToElement(
                new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null))))
        };
        await settlement.TwoCleanupsEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var overloaded = Enumerable.Range(0, 4)
            .Select(_ => host.ExecuteAsync(CreateRequest(JsonSerializer.SerializeToElement(
                new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)))))
            .ToArray();

        Assert.All(overloaded, task => Assert.Equal("cleanup-capacity-exhausted", task.Result.Failure!.Code));
        Assert.Equal(2, settlement.MaximumConcurrentCleanups);

        settlement.ReleaseCleanups();
        Assert.All(await Task.WhenAll(admitted), result => Assert.Equal("execution-capacity-exhausted", result.Failure!.Code));
        releaseExecution.Set();
        Assert.Equal(EngineTerminalStatus.Succeeded, (await first).Status);
        Assert.Equal(2, settlement.MaximumConcurrentCleanups);
    }

    [Fact]
    public async Task Host_EvictionUsesLedgerReplayWithoutReapplyingSettlement()
    {
        var settlement = new RecordingSettlement();
        var transport = new StubEngineExecutionTransport((generation, executionId, request) =>
            CreateCompletedComputation(request, generation, executionId));
        var host = EngineExecutionHost.CreateForTesting(
            transport,
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider(),
            new EngineExecutionHostOptions(1, TimeSpan.FromSeconds(1)));
        var first = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var second = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var original = await host.ExecuteAsync(first);
        Assert.Equal(EngineTerminalStatus.Succeeded, original.Status);
        Assert.Equal(EngineTerminalStatus.Succeeded, (await host.ExecuteAsync(second)).Status);
        var replay = await host.ExecuteAsync(first);
        Assert.Equal(EngineTerminalStatus.Succeeded, replay.Status);

        Assert.Equal(2, transport.ExecutionCount);
        Assert.Equal(10, settlement.Phases.Count);
        Assert.Equal(10, settlement.Contexts.Select(context => context.PhaseDeliveryId).Distinct().Count());
        Assert.Equal(original.Completion.FinalTransactionHash, replay.Completion.FinalTransactionHash);
        Assert.Equal(original.Completion.TerminalEvidence, replay.Completion.TerminalEvidence);
    }

    [Fact]
    public async Task Host_ConflictingReuseAfterEvictionFailsBeforeEffects()
    {
        var settlement = new RecordingSettlement();
        var transport = new StubEngineExecutionTransport((generation, executionId, request) =>
            CreateCompletedComputation(request, generation, executionId));
        var host = EngineExecutionHost.CreateForTesting(
            transport,
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider(),
            new EngineExecutionHostOptions(1, TimeSpan.FromSeconds(1)));
        var original = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var eviction = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var conflicting = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(null, new ProcurementRouteExecutionRequest()))) with
        {
            TransactionId = original.TransactionId
        };

        Assert.Equal(EngineTerminalStatus.Succeeded, (await host.ExecuteAsync(original)).Status);
        Assert.Equal(EngineTerminalStatus.Succeeded, (await host.ExecuteAsync(eviction)).Status);
        var conflict = await host.ExecuteAsync(conflicting);

        Assert.Equal(EngineTerminalStatus.Failed, conflict.Status);
        Assert.Equal("transaction-id-conflict", conflict.Failure!.Code);
        Assert.Equal(2, transport.ExecutionCount);
        Assert.Equal(11, settlement.Phases.Count);
    }

    [Fact]
    public async Task HostRecreation_ConflictingReuseFailsBeforeEffects()
    {
        var ledger = new InMemoryEngineTransactionLedger();
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var firstHost = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId)),
            new RecordingSettlement(),
            ledger,
            new ReferenceEngineSemanticSnapshotProvider());
        Assert.Equal(EngineTerminalStatus.Succeeded, (await firstHost.ExecuteAsync(request)).Status);

        var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var settlement = new RecordingSettlement();
        var recreatedHost = EngineExecutionHost.CreateForTesting(
            transport,
            settlement,
            ledger,
            new ReferenceEngineSemanticSnapshotProvider());
        var conflicting = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(null, new ProcurementRouteExecutionRequest()))) with
        {
            TransactionId = request.TransactionId
        };

        var result = await recreatedHost.ExecuteAsync(conflicting);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Equal("transaction-id-conflict", result.Failure!.Code);
        Assert.Equal(0, transport.ExecutionCount);
        Assert.Equal([EnginePhase.ReleasingGate], settlement.Phases);
    }

    [Fact]
    public async Task Host_MalformedCommittedAcknowledgementIsReplayBlockingIndeterminate()
    {
        var ledger = new InMemoryEngineTransactionLedger();
        var settlement = new MalformedCommitSettlement();
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId)),
            settlement,
            ledger,
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
        Assert.Equal("committed-indeterminate", result.Failure!.Code);
        Assert.Equal("committed", result.Completion.TerminalEvidence["commitState"]);
        Assert.DoesNotContain(EnginePhase.SettlingUi, settlement.Phases);
        Assert.Equal(1, settlement.PersistenceAttempts);

        var replaySettlement = new RecordingSettlement();
        var replayTransport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var replayHost = EngineExecutionHost.CreateForTesting(
            replayTransport,
            replaySettlement,
            ledger,
            new ReferenceEngineSemanticSnapshotProvider());
        var replay = await replayHost.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Indeterminate, replay.Status);
        Assert.Equal("committed-indeterminate", replay.Failure!.Code);
        Assert.Equal(result.Completion.FinalTransactionHash, replay.Completion.FinalTransactionHash);
        Assert.Equal(0, replayTransport.ExecutionCount);
        Assert.Equal([EnginePhase.ReleasingGate], replaySettlement.Phases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_GateReleaseRetriesAfterNotAppliedOrThrowAndCanSucceed(bool throwFirstAttempt)
    {
        var settlement = new RetryGateSettlement(throwFirstAttempt);
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId)),
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Succeeded, result.Status);
        Assert.Equal(2, settlement.GateAttempts);
        Assert.Equal("ReleasingGate:complete", result.Completion.TerminalEvidence["phase:ReleasingGate"]);
    }

    [Fact]
    public async Task Host_HangingPostCommitPhaseIsIndeterminateAndRetainsConcurrencySlot()
    {
        var settlement = new HangingPostCommitSettlement();
        var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var host = EngineExecutionHost.CreateForTesting(
            transport,
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider(),
            new EngineExecutionHostOptions(
                8,
                TimeSpan.FromMilliseconds(25),
                MaxConcurrentExecutions: 1,
                ComputationTimeout: TimeSpan.FromSeconds(1),
                SettlementPhaseTimeout: TimeSpan.FromMilliseconds(25),
                LedgerWriteTimeout: TimeSpan.FromSeconds(1)));
        var firstRequest = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var secondRequest = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var first = await host.ExecuteAsync(firstRequest);
        var second = await host.ExecuteAsync(secondRequest);

        Assert.Equal(EngineTerminalStatus.Indeterminate, first.Status);
        Assert.Equal("settlement-phase-timeout-indeterminate", first.Failure!.Code);
        Assert.Equal("committed", first.Completion.TerminalEvidence["commitState"]);
        Assert.Equal(EngineTerminalStatus.Failed, second.Status);
        Assert.Equal("execution-capacity-exhausted", second.Failure!.Code);
        Assert.Equal(1, transport.ExecutionCount);
        Assert.Equal(1, settlement.HangingPhaseAttempts);
    }

    [Fact]
    public async Task Host_SynchronousBlockingTransportDoesNotFreezeCallerOrSpawnPastCapacity()
    {
        using var release = new ManualResetEventSlim();
        var transport = new SynchronouslyBlockingTransport(release);
        var host = CreateBoundedHost(transport, new RecordingSettlement(), new InMemoryEngineTransactionLedger());
        var firstRequest = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var secondRequest = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var stopwatch = Stopwatch.StartNew();
        var execution = host.ExecuteAsync(firstRequest);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var first = await execution;
        var second = await host.ExecuteAsync(secondRequest);

        Assert.Equal("computation-timeout-indeterminate", first.Failure!.Code);
        Assert.Equal("execution-capacity-exhausted", second.Failure!.Code);
        Assert.Equal(1, transport.ExecutionCount);
        release.Set();
    }

    [Fact]
    public async Task Host_SynchronousBlockingLedgerClaimIsBoundedBeforeAnyExecution()
    {
        using var release = new ManualResetEventSlim();
        var ledger = new SynchronouslyBlockingClaimLedger(release);
        var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var host = CreateBoundedHost(transport, new RecordingSettlement(), ledger);
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var stopwatch = Stopwatch.StartNew();
        var execution = host.ExecuteAsync(request);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        await ledger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var result = await execution;

        Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
        Assert.Equal("transaction-ledger-claim-indeterminate", result.Failure!.Code);
        Assert.Equal(0, transport.ExecutionCount);
        release.Set();
    }

    [Fact]
    public async Task Host_SynchronousBlockingSettlementIsBoundedAndRetainsCapacity()
    {
        using var release = new ManualResetEventSlim();
        var settlement = new SynchronouslyBlockingSettlement(release);
        var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var host = CreateBoundedHost(transport, settlement, new InMemoryEngineTransactionLedger());
        var firstRequest = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var secondRequest = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var execution = host.ExecuteAsync(firstRequest);
        await settlement.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var first = await execution;
        var second = await host.ExecuteAsync(secondRequest);

        Assert.Equal("settlement-phase-timeout-indeterminate", first.Failure!.Code);
        Assert.Equal("execution-capacity-exhausted", second.Failure!.Code);
        Assert.Equal(1, settlement.BlockingAttempts);
        release.Set();
    }

    [Fact]
    public async Task Host_BlockingProgressObserverCannotDelayTerminalizationOrHoldCapacity()
    {
        using var release = new ManualResetEventSlim();
        var observer = new BlockingProgressObserver(release);
        var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var host = CreateBoundedHost(transport, new RecordingSettlement(), new InMemoryEngineTransactionLedger());
        var firstRequest = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var secondRequest = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        try
        {
            var execution = host.ExecuteAsync(firstRequest, observer);
            await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var first = await execution.WaitAsync(TimeSpan.FromSeconds(2));
            var second = await host.ExecuteAsync(secondRequest).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(EngineTerminalStatus.Succeeded, first.Status);
            Assert.Equal(EngineTerminalStatus.Succeeded, second.Status);
            Assert.Equal(2, transport.ExecutionCount);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Host_ThrowingProgressObserverCannotChangeTerminalResult()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new DelegateProgress<EngineProgress>(_ =>
        {
            observed.TrySetResult();
            throw new InvalidOperationException("observer failure");
        });
        var host = CreateBoundedHost(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId)),
            new RecordingSettlement(),
            new InMemoryEngineTransactionLedger());
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var result = await host.ExecuteAsync(request, observer).WaitAsync(TimeSpan.FromSeconds(2));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(EngineTerminalStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Host_LedgerWriteIndeterminateRetainsEvidenceAndRetryConvergesFromLedger()
    {
        using var release = new ManualResetEventSlim();
        var ledger = new DelayedWriteLedger(release);
        var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var settlement = new RecordingSettlement();
        var host = CreateBoundedHost(transport, settlement, ledger);
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        var execution = host.ExecuteAsync(request);
        await ledger.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var indeterminate = await execution;

        Assert.Equal("transaction-ledger-write-indeterminate", indeterminate.Failure!.Code);
        Assert.Equal("Succeeded", indeterminate.Completion.TerminalEvidence["ledgerWrite:originalStatus"]);
        Assert.Equal("committed", indeterminate.Completion.TerminalEvidence["commitState"]);
        Assert.True(indeterminate.Completion.TerminalEvidence.ContainsKey("computationHash"));
        Assert.True(indeterminate.Completion.TerminalEvidence.ContainsKey("resultPayloadHash"));
        Assert.False(string.IsNullOrWhiteSpace(indeterminate.Completion.AnalysisResultHash));
        Assert.False(string.IsNullOrWhiteSpace(
            indeterminate.Completion.TerminalEvidence["ledgerWrite:originalFinalTransactionHash"]));
        Assert.Equal(EnginePhase.ReleasingGate, indeterminate.Failure.FailedPhase);
        Assert.All(
            ledger.WrittenResult!.Completion.TerminalEvidence,
            pair => Assert.Equal(
                pair.Value,
                indeterminate.Completion.TerminalEvidence[$"ledgerWrite:originalEvidence:{pair.Key}"]));
        var validated = await new EngineExecutionHost(
            transport,
            new RecordingSettlement(),
            new TerminalReplayLedger(indeterminate),
            new ReferenceEngineSemanticSnapshotProvider()).ExecuteAsync(request);
        Assert.Equal(indeterminate.Completion.FinalTransactionHash, validated.Completion.FinalTransactionHash);
        Assert.Equal("transaction-ledger-write-indeterminate", validated.Failure!.Code);

        release.Set();
        await ledger.WriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        EngineResultEnvelope replay;
        do
        {
            await Task.Delay(10);
            replay = await host.ExecuteAsync(request);
        }
        while (replay.Failure?.Code == "execution-capacity-exhausted");

        Assert.Equal(EngineTerminalStatus.Succeeded, replay.Status);
        Assert.Equal(1, transport.ExecutionCount);
        Assert.Equal(5, settlement.Phases.Count);
    }

    [Fact]
    public async Task Host_LedgerWriteIndeterminatePreservesStructuredCancellationEvidence()
    {
        using var release = new ManualResetEventSlim();
        var ledger = new DelayedWriteLedger(release);
        var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCancelledComputation(current, generation, executionId, EnginePhase.Analyzing));
        var host = CreateBoundedHost(transport, new RecordingSettlement(), ledger);
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));

        try
        {
            var execution = host.ExecuteAsync(request);
            await ledger.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
            Assert.Equal("Cancelled", result.Completion.TerminalEvidence["ledgerWrite:originalStatus"]);
            Assert.Equal("Cancelled", result.Completion.TerminalEvidence["ledgerWrite:originalTerminalPhase"]);
            Assert.Equal("cancelled", result.Completion.TerminalEvidence["ledgerWrite:originalTerminalCode"]);
            Assert.Equal("Analyzing", result.Completion.TerminalEvidence["ledgerWrite:originalFailedPhase"]);
            Assert.Equal(bool.FalseString, result.Completion.TerminalEvidence["ledgerWrite:originalIsRetryable"]);
            Assert.Equal(EnginePhase.Analyzing, result.Failure!.FailedPhase);
            Assert.Null(ledger.WrittenResult!.Failure);
            Assert.All(
                ledger.WrittenResult.Completion.TerminalEvidence,
                pair => Assert.Equal(
                    pair.Value,
                    result.Completion.TerminalEvidence[$"ledgerWrite:originalEvidence:{pair.Key}"]));
            var validated = await new EngineExecutionHost(
                transport,
                new RecordingSettlement(),
                new TerminalReplayLedger(result),
                new ReferenceEngineSemanticSnapshotProvider()).ExecuteAsync(request);
            Assert.Equal(result.Completion.FinalTransactionHash, validated.Completion.FinalTransactionHash);
            Assert.Equal("transaction-ledger-write-indeterminate", validated.Failure!.Code);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Host_ActiveReplayIsNotCachedAndConvergesOnNextLedgerRead()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var ledger = new ActiveThenTerminalLedger(CreateSuccessfulResult(request));
        var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var settlement = new RecordingSettlement();
        var host = EngineExecutionHost.CreateForTesting(
            transport,
            settlement,
            ledger,
            new ReferenceEngineSemanticSnapshotProvider());

        var active = await host.ExecuteAsync(request);
        Assert.Equal("transaction-replay-in-progress", active.Failure!.Code);
        Assert.Equal([EnginePhase.ReleasingGate], settlement.Phases);

        var replay = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Succeeded, replay.Status);
        Assert.Equal(2, ledger.ClaimCount);
        Assert.Equal(0, transport.ExecutionCount);
        Assert.Equal([EnginePhase.ReleasingGate], settlement.Phases);
    }

    [Fact]
    public async Task Host_ActiveReplayReleasesFreshLocalGate()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var settlement = new RecordingSettlement();
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId)),
            settlement,
            new ActiveThenTerminalLedger(CreateSuccessfulResult(request)),
            new ReferenceEngineSemanticSnapshotProvider());

        var active = await host.ExecuteAsync(request);

        Assert.Equal("transaction-replay-in-progress", active.Failure!.Code);
        Assert.Equal([EnginePhase.ReleasingGate], settlement.Phases);
    }

    [Fact]
    public async Task Host_LiveActiveReplayPreservesTheRegisteredOwnerGate()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var settlement = new InvocationFencedGateSettlement();
        Assert.NotNull(settlement.TryRegisterInvocationCleanupOwnership(
            new EngineInvocationCleanupRegistration(request, "existing-owner")));
        var host = new EngineExecutionHost(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId)),
            settlement,
            new ActiveThenTerminalLedger(CreateSuccessfulResult(request)),
            new ReferenceEngineSemanticSnapshotProvider());

        var active = await host.ExecuteAsync(request);

        Assert.Equal("transaction-replay-in-progress", active.Failure!.Code);
        Assert.True(settlement.IsGateHeld);
        Assert.Equal(0, settlement.SettlementCalls);
        Assert.Equal(0, settlement.GateReleaseCount);
    }

    [Fact]
    public async Task Host_FreezesComputationAndTerminalEvidenceBeforeCachingAndHashing()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        Dictionary<string, string>? mutableComputationEvidence = null;
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) =>
            {
                var computation = CreateCompletedComputation(current, generation, executionId);
                mutableComputationEvidence = new Dictionary<string, string>(computation.ComputationEvidence);
                return computation with { ComputationEvidence = mutableComputationEvidence };
            }),
            new RecordingSettlement(),
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);
        var hash = result.Completion.FinalTransactionHash;
        mutableComputationEvidence!["phase:Analyzing"] = "mutated-after-validation";
        mutableComputationEvidence["injected"] = "mutable";

        Assert.Equal("complete", result.Completion.TerminalEvidence["phase:Analyzing"]);
        Assert.False(result.Completion.TerminalEvidence.ContainsKey("injected"));
        Assert.Equal(
            hash,
            EngineCanonicalHash.ComputeFinalTransactionHash(
                request,
                result.Status,
                result.Completion.AnalysisResultHash,
                result.Completion.ProcurementRouteResultHash,
                result.Completion.TerminalEvidence));
        AssertEvidenceCannotBeMutated(result.Completion.TerminalEvidence);

        var cached = await host.ExecuteAsync(request);
        Assert.Equal(hash, cached.Completion.FinalTransactionHash);
        Assert.Equal("complete", cached.Completion.TerminalEvidence["phase:Analyzing"]);
        AssertEvidenceCannotBeMutated(cached.Completion.TerminalEvidence);
    }

    [Fact]
    public async Task Host_ValidatedReplayDetachesAndFreezesMutableLedgerEvidence()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var source = CreateSuccessfulResult(request);
        var mutableEvidence = Assert.IsType<Dictionary<string, string>>(source.Completion.TerminalEvidence);
        var host = new EngineExecutionHost(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId)),
            new RecordingSettlement(),
            new TerminalReplayLedger(source),
            new ReferenceEngineSemanticSnapshotProvider());

        var replay = await host.ExecuteAsync(request);
        var replayHash = replay.Completion.FinalTransactionHash;
        mutableEvidence["phase:Analyzing"] = "mutated-after-replay";
        mutableEvidence["injected"] = "mutable";

        Assert.Equal("complete", replay.Completion.TerminalEvidence["phase:Analyzing"]);
        Assert.False(replay.Completion.TerminalEvidence.ContainsKey("injected"));
        Assert.Equal(
            replayHash,
            EngineCanonicalHash.ComputeFinalTransactionHash(
                request,
                replay.Status,
                replay.Completion.AnalysisResultHash,
                replay.Completion.ProcurementRouteResultHash,
                replay.Completion.TerminalEvidence));
        AssertEvidenceCannotBeMutated(replay.Completion.TerminalEvidence);
    }

    [Fact]
    public async Task Host_DisposedRequestHashFailureReturnsSafeTerminalAndCleansOwnedGate()
    {
        var document = JsonDocument.Parse("{\"marketAnalysis\":null,\"procurementRoute\":null}");
        var request = CreateRequest(document.RootElement);
        document.Dispose();
        var settlement = new InvocationFencedGateSettlement();
        var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
            CreateCompletedComputation(current, generation, executionId));
        var host = new EngineExecutionHost(
            transport,
            settlement,
            new ClaimResponseLedger(_ => throw new InvalidOperationException("ledger must not be called")),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
        Assert.Equal("canonical-request-validation-failed", result.Failure!.Code);
        Assert.Equal("canonical-hash-unavailable", result.Completion.TerminalEvidence["requestValidation"]);
        Assert.Equal(1, settlement.GateReleaseCount);
        Assert.False(settlement.IsGateHeld);
        Assert.Equal(0, transport.ExecutionCount);
        Assert.Equal(
            EngineCanonicalHash.ComputeRequestValidationFailureHash(
                request,
                result.Status,
                result.Completion.TerminalEvidence),
            result.Completion.FinalTransactionHash);
    }

    [Fact]
    public async Task Host_UnsupportedCanonicalNumberReturnsSafeTerminalAndCleansOwnedGate()
    {
        using var document = JsonDocument.Parse("{\"value\":1e1000}");
        var request = CreateRequest(document.RootElement.Clone());
        var settlement = new InvocationFencedGateSettlement();
        var host = new EngineExecutionHost(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId)),
            settlement,
            new ClaimResponseLedger(_ => throw new InvalidOperationException("ledger must not be called")),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
        Assert.Equal("canonical-request-validation-failed", result.Failure!.Code);
        Assert.Equal(1, settlement.GateReleaseCount);
        Assert.False(settlement.IsGateHeld);
    }

    [Fact]
    public async Task Host_RejectsTamperedLedgerPayloadStatusAndTerminalPhase()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var valid = CreateSuccessfulResult(request);
        var payload = valid.Result!.Value.Deserialize<ReferenceEngineResultSnapshot>(
            EngineJsonSerializerOptions.CreateWire())!;
        var tamperedPayload = JsonSerializer.SerializeToElement(
            payload with { MarketAnalysis = payload.MarketAnalysis! with { SchemaVersion = "tampered" } },
            EngineJsonSerializerOptions.CreateWire());
        var tamperedEvidence = new Dictionary<string, string>(valid.Completion.TerminalEvidence)
        {
            ["resultPayloadHash"] = EngineCanonicalHash.Compute(tamperedPayload)
        };
        var mutations = new EngineResultEnvelope[]
        {
            valid with
            {
                Result = tamperedPayload,
                Completion = valid.Completion with
                {
                    TerminalEvidence = tamperedEvidence,
                    FinalTransactionHash = EngineCanonicalHash.ComputeFinalTransactionHash(
                        request,
                        valid.Status,
                        valid.Completion.AnalysisResultHash,
                        valid.Completion.ProcurementRouteResultHash,
                        tamperedEvidence)
                }
            },
            valid with
            {
                Status = EngineTerminalStatus.Failed,
                Completion = valid.Completion with
                {
                    Status = EngineTerminalStatus.Failed,
                    TerminalPhase = EnginePhase.Failed
                }
            },
            valid with { Completion = valid.Completion with { TerminalPhase = EnginePhase.Persisting } }
        };

        foreach (var mutation in mutations)
        {
            var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId));
            var settlement = new RecordingSettlement();
            var host = EngineExecutionHost.CreateForTesting(
                transport,
                settlement,
                new TerminalReplayLedger(mutation),
                new ReferenceEngineSemanticSnapshotProvider());

            var result = await host.ExecuteAsync(request);

            Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
            Assert.Equal("transaction-ledger-corrupt", result.Failure!.Code);
            Assert.Equal(0, transport.ExecutionCount);
            Assert.Equal([EnginePhase.ReleasingGate], settlement.Phases);
        }
    }

    [Fact]
    public async Task Host_StrictlyRejectsMalformedCancelledLedgerReplay()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var valid = CreateCancelledResult(request, EnginePhase.Analyzing);
        var mutations = new List<EngineResultEnvelope>();

        void AddEvidenceMutation(Action<Dictionary<string, string>> mutate)
        {
            var evidence = new Dictionary<string, string>(valid.Completion.TerminalEvidence);
            mutate(evidence);
            mutations.Add(RehashTerminal(request, valid, evidence));
        }

        AddEvidenceMutation(evidence => evidence["terminalCode"] = "failed");
        AddEvidenceMutation(evidence => evidence["failedPhase"] = EnginePhase.Reconciling.ToString());
        AddEvidenceMutation(evidence => evidence["failedPhase"] = ((EnginePhase)999).ToString());
        AddEvidenceMutation(evidence => evidence["failureType"] = nameof(InvalidOperationException));
        AddEvidenceMutation(evidence => evidence["failureMessageHash"] = EngineCanonicalHash.Compute("tampered"));
        AddEvidenceMutation(evidence => evidence["isRetryable"] = bool.TrueString);
        AddEvidenceMutation(evidence => evidence.Remove("failureMessageHash"));
        mutations.Add(valid with
        {
            Failure = new EngineFailure(
                "cancelled",
                "The engine transaction was cancelled.",
                false,
                EnginePhase.Analyzing,
                nameof(OperationCanceledException))
        });

        foreach (var mutation in mutations)
        {
            var transport = new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId));
            var host = EngineExecutionHost.CreateForTesting(
                transport,
                new RecordingSettlement(),
                new TerminalReplayLedger(mutation),
                new ReferenceEngineSemanticSnapshotProvider());

            var result = await host.ExecuteAsync(request);

            Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
            Assert.Equal("transaction-ledger-corrupt", result.Failure!.Code);
            Assert.Equal(0, transport.ExecutionCount);
        }
    }

    [Theory]
    [InlineData(true, false, EnginePhase.Reconciling)]
    [InlineData(false, true, EnginePhase.Analyzing)]
    public async Task Host_RejectsComputationEvidenceOutsideRequestedOperationShape(
        bool includesMarketAnalysis,
        bool includesProcurementRoute,
        EnginePhase forbiddenPhase)
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(new ReferenceEngineInput(
            includesMarketAnalysis ? new MarketAnalysisExecutionRequest() : null,
            includesProcurementRoute ? new ProcurementRouteExecutionRequest() : null)));
        var settlement = new RecordingSettlement();
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) =>
            {
                var computation = CreateCompletedComputation(current, generation, executionId);
                var evidence = new Dictionary<string, string>(computation.ComputationEvidence)
                {
                    [$"phase:{forbiddenPhase}"] = "complete"
                };
                return RehashComputation(current, computation with { ComputationEvidence = evidence });
            }),
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Contains("operation shape", result.Failure!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(EnginePhase.Publishing, settlement.Phases);
    }

    [Fact]
    public async Task Host_RejectsOperationImpossibleCancellationAndFailurePhases()
    {
        var cases = new[]
        {
            (
                Request: CreateRequest(JsonSerializer.SerializeToElement(
                    new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null))),
                Build: (Func<EngineRequestEnvelope, long, Guid, EngineComputationResult>)((request, generation, executionId) =>
                    CreateCancelledComputation(request, generation, executionId, EnginePhase.Reconciling))),
            (
                Request: CreateRequest(JsonSerializer.SerializeToElement(
                    new ReferenceEngineInput(null, new ProcurementRouteExecutionRequest()))),
                Build: (Func<EngineRequestEnvelope, long, Guid, EngineComputationResult>)((request, generation, executionId) =>
                    CreateFailedComputation(request, generation, executionId, EnginePhase.Analyzing))),
            (
                Request: CreateRequest(JsonSerializer.SerializeToElement(
                    new ReferenceEngineInput(
                        new MarketAnalysisExecutionRequest(),
                        new ProcurementRouteExecutionRequest()))),
                Build: (Func<EngineRequestEnvelope, long, Guid, EngineComputationResult>)((request, generation, executionId) =>
                    CreateCancelledComputation(request, generation, executionId, EnginePhase.Reconciling)))
        };

        foreach (var testCase in cases)
        {
            var settlement = new RecordingSettlement();
            var host = EngineExecutionHost.CreateForTesting(
                new StubEngineExecutionTransport((generation, executionId, request) =>
                    testCase.Build(request, generation, executionId)),
                settlement,
                new InMemoryEngineTransactionLedger(),
                new ReferenceEngineSemanticSnapshotProvider());

            var result = await host.ExecuteAsync(testCase.Request);

            Assert.Equal(EngineTerminalStatus.Failed, result.Status);
            Assert.DoesNotContain(EnginePhase.Publishing, settlement.Phases);
        }
    }

    [Fact]
    public async Task Host_RejectsFailureAndPhaseEvidenceOutsideLedgerRequestShape()
    {
        var routeRequest = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(null, new ProcurementRouteExecutionRequest())));
        var failed = CreateFailedResult(routeRequest);
        var failure = failed.Failure! with { FailedPhase = EnginePhase.Analyzing };
        var failureEvidence = new Dictionary<string, string>(failed.Completion.TerminalEvidence)
        {
            ["failedPhase"] = EnginePhase.Analyzing.ToString()
        };
        var invalidFailure = RehashTerminal(
            routeRequest,
            failed with { Failure = failure },
            failureEvidence);

        var analysisRequest = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var succeeded = CreateSuccessfulResult(analysisRequest);
        var successEvidence = new Dictionary<string, string>(succeeded.Completion.TerminalEvidence)
        {
            ["phase:Reconciling"] = "complete"
        };
        var invalidEvidence = RehashTerminal(analysisRequest, succeeded, successEvidence);

        foreach (var testCase in new[]
                 {
                     (Request: routeRequest, Result: invalidFailure),
                     (Request: analysisRequest, Result: invalidEvidence)
                 })
        {
            var host = EngineExecutionHost.CreateForTesting(
                new StubEngineExecutionTransport((generation, executionId, request) =>
                    CreateCompletedComputation(request, generation, executionId)),
                new RecordingSettlement(),
                new TerminalReplayLedger(testCase.Result),
                new ReferenceEngineSemanticSnapshotProvider());

            var result = await host.ExecuteAsync(testCase.Request);

            Assert.Equal("transaction-ledger-corrupt", result.Failure!.Code);
        }
    }

    [Fact]
    public async Task Host_MalformedCompletedFinalPhaseCausesZeroSettlementEffects()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var settlement = new RecordingSettlement();
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                RehashComputation(
                    current,
                    CreateCompletedComputation(current, generation, executionId) with
                    {
                        FinalPhase = EnginePhase.Accepted
                    })),
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Contains("impossible final phase", result.Failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(EnginePhase.Publishing, settlement.Phases);
    }

    [Fact]
    public async Task Host_RejectsExactComputationContractVersionMismatch()
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var settlement = new RecordingSettlement();
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCompletedComputation(current, generation, executionId) with { ContractVersion = "1.0" }),
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Equal("contract-version-mismatch", result.Failure!.Code);
        Assert.DoesNotContain(EnginePhase.Publishing, settlement.Phases);
    }

    [Theory]
    [InlineData((EnginePhase)0)]
    [InlineData((EnginePhase)999)]
    [InlineData(EnginePhase.Completed)]
    [InlineData(EnginePhase.Cancelled)]
    [InlineData(EnginePhase.Failed)]
    [InlineData(EnginePhase.Indeterminate)]
    public async Task Host_RejectsUndefinedNumericAndTerminalCancellationPhases(EnginePhase phase)
    {
        var request = CreateRequest(JsonSerializer.SerializeToElement(
            new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null)));
        var settlement = new RecordingSettlement();
        var host = EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, current) =>
                CreateCancelledComputation(current, generation, executionId, phase)),
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

        var result = await host.ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Equal("invalid-cancellation-phase", result.Failure!.Code);
        Assert.DoesNotContain(EnginePhase.Publishing, settlement.Phases);
    }

    [Fact]
    public async Task BrowserWorkerExecutionTransport_IsExplicitlyUnsupported()
    {
        var transport = new UnsupportedBrowserWorkerEngineExecutionTransport();

        Assert.False(transport.Capability.IsSupported);
        Assert.Equal(EngineExecutionTransportKind.BrowserWorker, transport.Capability.Kind);
        await Assert.ThrowsAsync<NotSupportedException>(() => transport.ExecuteAsync(
            1,
            Guid.NewGuid(),
            CreateRequest(JsonSerializer.SerializeToElement(new { demand = "unsupported" }))));
    }

    [Fact]
    public async Task WorkerClient_DoesNotSendExecutionWhenCapabilityIsUnsupported()
    {
        var transport = new FakeWorkerTransport { ExecutionSupported = false };
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();

        await Assert.ThrowsAsync<NotSupportedException>(() => client.ExecuteAsync(
            CreateRequest(JsonSerializer.SerializeToElement(new { demand = "unsupported" }))));

        Assert.Empty(transport.Sent);
        Assert.Equal(EngineWorkerLifecycleState.Ready, client.State);
    }

    [Fact]
    public async Task WorkerClient_RejectsCapabilityForDifferentGeneration()
    {
        var transport = new FakeWorkerTransport { CapabilityGenerationOffset = 1 };
        await using var client = new EngineWorkerClient(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync());

        Assert.Equal(EngineWorkerLifecycleState.Faulted, client.State);
        Assert.Equal(1, transport.TerminateCount);
    }

    [Fact]
    public async Task WorkerClient_SupportsStartProgressCancelAndValidatedComputation()
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        var capability = await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "fixture" }));
        var progressSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ProgressChanged += (_, _) => progressSeen.TrySetResult();

        var execution = client.ExecuteAsync(request);
        transport.Emit(CreateWorkerMessage(
            transport,
            "progress",
            request.TransactionId,
            JsonSerializer.SerializeToElement(CreateWorkerProgress(
                transport,
                request.TransactionId,
                EnginePhase.Analyzing,
                4,
                 12,
                 "Analyzing."))));
        await client.CancelAsync("test cancellation");
        transport.Emit(CreateWorkerMessage(
            transport,
            EngineWorkerClient.ComputationResultMessageKind,
            request.TransactionId,
            JsonSerializer.SerializeToElement(CreateWorkerCancelledComputation(transport, request))));
        var result = await execution;
        await progressSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(capability.DedicatedWorker);
        Assert.Equal(EngineComputationStatus.Cancelled, result.Status);
        AssertEvidenceCannotBeMutated(result.ComputationEvidence);
        Assert.Equal(EngineWorkerLifecycleState.Ready, client.State);
        var executeMessage = Assert.Single(transport.Sent, message => message.Kind == "execute");
        var cancelMessage = Assert.Single(transport.Sent, message => message.Kind == "cancel");
        var cancelRequest = cancelMessage.Payload!.Value.Deserialize<EngineCancelRequest>(
            EngineJsonSerializerOptions.CreateWire())!;
        Assert.Equal(capability.Generation, executeMessage.Generation);
        Assert.NotEqual(Guid.Empty, executeMessage.ExecutionId);
        Assert.Equal(executeMessage.Generation, cancelMessage.Generation);
        Assert.Equal(executeMessage.ExecutionId, cancelMessage.ExecutionId);
        Assert.Equal(executeMessage.Generation, cancelRequest.Generation);
        Assert.Equal(executeMessage.ExecutionId, cancelRequest.ExecutionId);
    }

    [Theory]
    [InlineData((EnginePhase)0)]
    [InlineData((EnginePhase)999)]
    [InlineData(EnginePhase.Completed)]
    [InlineData(EnginePhase.Cancelled)]
    [InlineData(EnginePhase.Failed)]
    [InlineData(EnginePhase.Indeterminate)]
    public async Task WorkerClient_RejectsUndefinedNumericAndTerminalCancellationPhases(EnginePhase phase)
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "fixture" }));
        var execution = client.ExecuteAsync(request);
        transport.Emit(CreateWorkerMessage(
            transport,
            EngineWorkerClient.ComputationResultMessageKind,
            request.TransactionId,
            JsonSerializer.SerializeToElement(CreateWorkerCancelledComputation(transport, request, phase))));

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => execution);
        Assert.Contains("cancellation phase", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkerClient_ProtocolErrorFaultsPendingExecution()
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "fixture" }));

        var execution = client.ExecuteAsync(request);
        transport.Emit(CreateWorkerMessage(
            transport,
            "protocol-error",
            request.TransactionId,
            JsonSerializer.SerializeToElement(new { code = "not-ready", message = "Worker host unavailable." })));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
        Assert.Contains("unavailable", exception.Message, StringComparison.Ordinal);
        Assert.Equal(EngineWorkerLifecycleState.Faulted, client.State);
        Assert.Equal(1, transport.TerminateCount);
    }

    [Theory]
    [InlineData(EngineTerminalStatus.Failed)]
    [InlineData(EngineTerminalStatus.Indeterminate)]
    [InlineData(EngineTerminalStatus.Cancelled)]
    public async Task WorkerClient_RejectsSettlementAttestingTerminalEnvelopes(EngineTerminalStatus status)
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "settlement-attestation" }));
        var envelope = status switch
        {
            EngineTerminalStatus.Failed => CreateFailedResult(request),
            EngineTerminalStatus.Cancelled => CreateCancelledResult(request),
            EngineTerminalStatus.Indeterminate => CreateFailedResult(request) with
            {
                Status = EngineTerminalStatus.Indeterminate,
                Completion = CreateFailedResult(request).Completion with
                {
                    Status = EngineTerminalStatus.Indeterminate,
                    TerminalPhase = EnginePhase.Indeterminate
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

        var execution = client.ExecuteAsync(request);
        transport.Emit(CreateWorkerMessage(
            transport,
            EngineWorkerClient.ComputationResultMessageKind,
            request.TransactionId,
            JsonSerializer.SerializeToElement(envelope)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
        Assert.Contains("settlement envelope authority", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(EnginePhase.Publishing)]
    [InlineData(EnginePhase.Persisting)]
    [InlineData(EnginePhase.SettlingUi)]
    public async Task WorkerClient_RejectsComputationClaimingSettlementPhase(EnginePhase settlementPhase)
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "settlement-phase" }));
        var execution = client.ExecuteAsync(request);
        var computation = CreateWorkerCancelledComputation(transport, request);
        var evidence = new Dictionary<string, string>(computation.ComputationEvidence)
        {
            [$"phase:{settlementPhase}"] = "worker-attested"
        };
        computation = RehashComputation(request, computation with { ComputationEvidence = evidence });

        transport.Emit(CreateWorkerMessage(
            transport,
            EngineWorkerClient.ComputationResultMessageKind,
            request.TransactionId,
            JsonSerializer.SerializeToElement(computation)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
        Assert.Contains("settlement phases", exception.Message, StringComparison.OrdinalIgnoreCase);
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
        var staleRequest = request with { TransactionId = Guid.NewGuid() };
        transport.Emit(CreateWorkerMessage(
            transport,
            EngineWorkerClient.ComputationResultMessageKind,
            staleRequest.TransactionId,
            JsonSerializer.SerializeToElement(CreateWorkerCancelledComputation(transport, staleRequest))));
        Assert.False(execution.IsCompleted);
        transport.Emit(CreateWorkerMessage(
            transport,
            EngineWorkerClient.ComputationResultMessageKind,
            request.TransactionId,
            JsonSerializer.SerializeToElement(CreateWorkerCancelledComputation(transport, request))));
        Assert.Equal(EngineComputationStatus.Cancelled, (await execution).Status);
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("generation")]
    [InlineData("execution")]
    public async Task WorkerClient_IgnoresMismatchedEnvelopeIdentityBeforePayloadParsing(string mismatch)
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "envelope-identity" }));
        var execution = client.ExecuteAsync(request);
        var execute = transport.Sent.Last(message => message.Kind == "execute");
        var stale = new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            EngineWorkerClient.ComputationResultMessageKind,
            mismatch == "generation" ? execute.Generation + 1 : execute.Generation,
            mismatch == "execution" ? Guid.NewGuid() : execute.ExecutionId,
            mismatch == "transaction" ? Guid.NewGuid() : request.TransactionId,
            null);

        transport.Emit(stale);

        Assert.False(execution.IsCompleted);
        Assert.Equal(EngineWorkerLifecycleState.Running, client.State);

        transport.Emit(CreateWorkerMessage(
            transport,
            EngineWorkerClient.ComputationResultMessageKind,
            request.TransactionId,
            JsonSerializer.SerializeToElement(CreateWorkerCancelledComputation(transport, request))));
        Assert.Equal(EngineComputationStatus.Cancelled, (await execution).Status);
        Assert.Equal(EngineWorkerLifecycleState.Ready, client.State);
    }

    [Fact]
    public async Task WorkerClient_RestartRejectsOldGenerationMessagesForRetriedTransactionId()
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "retry" }));
        var oldExecution = client.ExecuteAsync(request);
        var oldExecuteMessage = transport.Sent.Last(message => message.Kind == "execute");

        await client.ForceTerminateAndRestartAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldExecution);

        var progressSeen = new TaskCompletionSource<EngineProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ProgressChanged += (_, progress) => progressSeen.TrySetResult(progress);
        var retriedExecution = client.ExecuteAsync(request);
        transport.Emit(new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            "progress",
            oldExecuteMessage.Generation,
            oldExecuteMessage.ExecutionId,
            request.TransactionId,
            JsonSerializer.SerializeToElement(new { malformed = true })));
        transport.Emit(new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            "protocol-error",
            oldExecuteMessage.Generation,
            oldExecuteMessage.ExecutionId,
            request.TransactionId,
            null));
        transport.Emit(new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            EngineWorkerClient.ComputationResultMessageKind,
            oldExecuteMessage.Generation,
            oldExecuteMessage.ExecutionId,
            request.TransactionId,
            JsonSerializer.SerializeToElement(new { malformed = true })));

        Assert.False(retriedExecution.IsCompleted);
        Assert.Equal(EngineWorkerLifecycleState.Running, client.State);

        var currentProgress = CreateWorkerProgress(
            transport,
            request.TransactionId,
            EnginePhase.Analyzing,
            1,
            12,
            "current");
        transport.Emit(CreateWorkerMessage(
            transport,
            "progress",
            request.TransactionId,
            JsonSerializer.SerializeToElement(currentProgress)));
        transport.Emit(CreateWorkerMessage(
            transport,
            EngineWorkerClient.ComputationResultMessageKind,
            request.TransactionId,
            JsonSerializer.SerializeToElement(CreateWorkerCancelledComputation(transport, request))));

        Assert.Equal(EngineComputationStatus.Cancelled, (await retriedExecution).Status);
        Assert.Equal(currentProgress, await progressSeen.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(EngineWorkerLifecycleState.Ready, client.State);
    }

    [Fact]
    public async Task WorkerClient_CallerCancellationAndResultArrivalLinearizeWithoutInvalidOperation()
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(
            transport,
            cancellationTimeout: TimeSpan.FromMilliseconds(20));
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "cancel-race" }));
        using var cancellation = new CancellationTokenSource();
        var execution = client.ExecuteAsync(request, cancellation.Token);
        transport.OnSend = message =>
        {
            if (message.Kind == "cancel")
            {
                transport.Emit(CreateWorkerMessage(
                    transport,
                    EngineWorkerClient.ComputationResultMessageKind,
                    request.TransactionId,
                    JsonSerializer.SerializeToElement(CreateWorkerCancelledComputation(transport, request))));
            }
        };

        cancellation.Cancel();

        var result = await execution;
        await Task.Delay(60);

        Assert.Equal(EngineComputationStatus.Cancelled, result.Status);
        Assert.Equal(EngineWorkerLifecycleState.Ready, client.State);
        Assert.Equal(0, transport.TerminateCount);
    }

    [Fact]
    public async Task WorkerClient_FaultRestartWaitsForDelayedOldWorkerTermination()
    {
        var terminationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeWorkerTransport { TerminateGate = terminationGate };
        var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "fault-restart" }));
        var execution = client.ExecuteAsync(request);
        transport.Emit(CreateWorkerMessage(
            transport,
            "protocol-error",
            request.TransactionId,
            JsonSerializer.SerializeToElement(new { code = "fault", message = "faulted" })));

        var restart = client.StartAsync();
        await Task.Delay(30);

        Assert.False(execution.IsCompleted);
        Assert.False(restart.IsCompleted);
        Assert.Equal(1, transport.StartCount);
        terminationGate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
        await restart;

        Assert.Equal(2, transport.StartCount);
        Assert.Equal(1, transport.MaxActiveWorkers);
        Assert.Equal(EngineWorkerLifecycleState.Ready, client.State);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task WorkerClient_ConcurrentRestartAndDisposalCannotResurrectTerminalState()
    {
        var terminationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeWorkerTransport { TerminateGate = terminationGate };
        var client = new EngineWorkerClient(transport);
        await client.StartAsync();

        var restart = client.ForceTerminateAndRestartAsync();
        var disposal = client.DisposeAsync().AsTask();
        terminationGate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restart);
        await disposal;

        Assert.Equal(1, transport.StartCount);
        Assert.Equal(EngineWorkerLifecycleState.Stopped, client.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.StartAsync());
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("generation")]
    [InlineData("execution")]
    public async Task WorkerClient_RejectsMalformedInnerProgressIdentity(string mismatch)
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "progress-identity" }));
        var forwarded = 0;
        client.ProgressChanged += (_, _) => Interlocked.Increment(ref forwarded);
        var execution = client.ExecuteAsync(request);
        var progress = CreateWorkerProgress(
            transport,
            request.TransactionId,
            EnginePhase.Analyzing,
            1,
            12,
            "working");
        progress = mismatch switch
        {
            "transaction" => progress with { TransactionId = Guid.NewGuid() },
            "generation" => progress with { Generation = progress.Generation + 1 },
            "execution" => progress with { ExecutionId = Guid.NewGuid() },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        transport.Emit(CreateWorkerMessage(
            transport,
            "progress",
            request.TransactionId,
            JsonSerializer.SerializeToElement(progress)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
        Assert.Contains("progress identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Volatile.Read(ref forwarded));
        Assert.Equal(EngineWorkerLifecycleState.Faulted, client.State);
    }

    [Theory]
    [InlineData(EnginePhase.Completed)]
    [InlineData(EnginePhase.Cancelled)]
    [InlineData(EnginePhase.Failed)]
    [InlineData(EnginePhase.Indeterminate)]
    public async Task WorkerClient_RejectsTerminalProgressPhase(EnginePhase phase)
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "terminal-progress" }));
        var execution = client.ExecuteAsync(request);
        var progress = CreateWorkerProgress(transport, request.TransactionId, phase, 1, 12, "invalid");

        transport.Emit(CreateWorkerMessage(
            transport,
            "progress",
            request.TransactionId,
            JsonSerializer.SerializeToElement(progress)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
        Assert.Contains("computation-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkerClient_RejectsNonMonotonicProgress()
    {
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "monotonic-progress" }));
        var execution = client.ExecuteAsync(request);
        transport.Emit(CreateWorkerMessage(
            transport,
            "progress",
            request.TransactionId,
            JsonSerializer.SerializeToElement(CreateWorkerProgress(
                transport,
                request.TransactionId,
                EnginePhase.Analyzing,
                4,
                12,
                "working"))));
        transport.Emit(CreateWorkerMessage(
            transport,
            "progress",
            request.TransactionId,
            JsonSerializer.SerializeToElement(CreateWorkerProgress(
                transport,
                request.TransactionId,
                EnginePhase.Analyzing,
                3,
                12,
                "regressed"))));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
        Assert.Contains("monotonic", exception.Message, StringComparison.OrdinalIgnoreCase);
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
        transport.Emit(CreateWorkerMessage(
            transport,
            "progress",
            request.TransactionId,
            JsonSerializer.SerializeToElement(CreateWorkerProgress(
                transport,
                request.TransactionId,
                EnginePhase.Analyzing,
                1,
                2,
                "working"))));
        transport.Emit(CreateWorkerMessage(
            transport,
            EngineWorkerClient.ComputationResultMessageKind,
            request.TransactionId,
            JsonSerializer.SerializeToElement(CreateWorkerCancelledComputation(transport, request))));

        Assert.Equal(EngineComputationStatus.Cancelled, (await execution).Status);
    }

    [Fact]
    public async Task WorkerClient_BlockingProgressSubscriberCannotDelayTerminalizationOrOtherObservers()
    {
        using var release = new ManualResetEventSlim();
        var blockingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var isolatedObserverCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeWorkerTransport();
        await using var client = new EngineWorkerClient(transport);
        await client.StartAsync();
        client.ProgressChanged += (_, _) =>
        {
            blockingEntered.TrySetResult();
            release.Wait();
        };
        client.ProgressChanged += (_, _) => isolatedObserverCalled.TrySetResult();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { demand = "fixture" }));

        try
        {
            var execution = client.ExecuteAsync(request);
            transport.Emit(CreateWorkerMessage(
                transport,
                "progress",
                request.TransactionId,
                JsonSerializer.SerializeToElement(CreateWorkerProgress(
                    transport,
                    request.TransactionId,
                    EnginePhase.Analyzing,
                    1,
                    2,
                    "working"))));
            await blockingEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await isolatedObserverCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            transport.Emit(CreateWorkerMessage(
                transport,
                EngineWorkerClient.ComputationResultMessageKind,
                request.TransactionId,
                JsonSerializer.SerializeToElement(CreateWorkerCancelledComputation(transport, request))));

            var result = await execution.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(EngineComputationStatus.Cancelled, result.Status);
        }
        finally
        {
            release.Set();
        }
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData(EngineWorkerClient.ComputationResultMessageKind, true)]
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
        transport.Emit(CreateWorkerMessage(transport, kind, request.TransactionId, payload));

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
    public async Task WorkerClient_ForeverHangingStartupIsBounded()
    {
        var transport = new ForeverHangingWorkerTransport(HangingWorkerOperation.Startup);
        var client = new EngineWorkerClient(
            transport,
            transportTimeout: TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync())
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(EngineWorkerLifecycleState.Quarantined, client.State);
        Assert.Equal(1, transport.StartCount);
        Assert.Equal(1, transport.TerminateCount);
        Assert.True(client.QuarantineEvidence!.StartupOutcomePending);
        await Assert.ThrowsAsync<EngineWorkerQuarantineException>(() => client.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task WorkerClient_LateStartupCompletionIsQuarantinedAndTerminatedBeforeReplacement()
    {
        var transport = new LateStartupWorkerTransport();
        await using var client = new EngineWorkerClient(
            transport,
            transportTimeout: TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync())
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, transport.TerminateCount);
        Assert.Equal(0, transport.ActiveWorkers);

        transport.CompleteStartup();
        await transport.LateWorkerTerminated.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, transport.TerminateCount);
        Assert.Equal(0, transport.ActiveWorkers);
        Assert.Equal(EngineWorkerLifecycleState.Faulted, client.State);

        await client.StartAsync();
        Assert.Equal(2, transport.StartCount);
        Assert.Equal(1, transport.ActiveWorkers);
    }

    [Fact]
    public async Task WorkerClient_UnknownLateStartupProhibitsReplacementWithinCallerDeadline()
    {
        var transport = new LateStartupWorkerTransport();
        await using var client = new EngineWorkerClient(
            transport,
            transportTimeout: TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync())
            .WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<Exception>(() => client.StartAsync())
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, transport.StartCount);

        transport.CompleteStartup();
        await transport.LateWorkerTerminated.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, transport.ActiveWorkers);
    }

    [Fact]
    public async Task WorkerClient_DisposalStaysBoundedWhileLateStartupRemainsQuarantined()
    {
        var transport = new LateStartupWorkerTransport();
        var client = new EngineWorkerClient(
            transport,
            transportTimeout: TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync())
            .WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<EngineWorkerQuarantineException>(() => client.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(EngineWorkerLifecycleState.Quarantined, client.State);
        Assert.Equal(0, transport.DisposeCount);

        transport.CompleteStartup();
        await transport.LateWorkerTerminated.WaitAsync(TimeSpan.FromSeconds(2));
        await transport.TransportDisposed.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, transport.ActiveWorkers);
        Assert.Equal(3, transport.TerminateCount);
        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(EngineWorkerLifecycleState.Stopped, client.State);
        Assert.True(client.QuarantineEvidence!.IsResolved);
        await client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.StartAsync());
    }

    [Fact]
    public async Task WorkerClient_LateWorkerTerminationFailureRetainsQuarantineAndProhibitsReplacementUntilRetry()
    {
        var transport = new LateStartupWorkerTransport { FailActiveWorkerTermination = true };
        var client = new EngineWorkerClient(
            transport,
            transportTimeout: TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync());
        transport.CompleteStartup();
        await transport.ActiveTerminationAttempted.WaitAsync(TimeSpan.FromSeconds(2));

        var unresolved = await client.RetryQuarantinedWorkerCleanupAsync();

        Assert.False(unresolved.IsResolved);
        Assert.True(unresolved.TerminationPending);
        Assert.Contains(unresolved.Failures, item => item.StartsWith("termination:", StringComparison.Ordinal));
        await Assert.ThrowsAsync<EngineWorkerQuarantineException>(() => client.StartAsync());
        Assert.Equal(1, transport.StartCount);

        transport.FailActiveWorkerTermination = false;
        var resolved = await client.RetryQuarantinedWorkerCleanupAsync();

        Assert.True(resolved.IsResolved);
        Assert.False(resolved.TerminationPending);
        await client.StartAsync();
        Assert.Equal(2, transport.StartCount);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task WorkerClient_LateWorkerDisposalFailureRetainsQuarantineUntilRetrySucceeds()
    {
        var transport = new LateStartupWorkerTransport { FailTransportDisposal = true };
        var client = new EngineWorkerClient(
            transport,
            transportTimeout: TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync());
        await Assert.ThrowsAsync<EngineWorkerQuarantineException>(() => client.DisposeAsync().AsTask());
        transport.CompleteStartup();
        await transport.DisposalAttempted.WaitAsync(TimeSpan.FromSeconds(2));

        var unresolved = await client.RetryQuarantinedWorkerCleanupAsync();

        Assert.False(unresolved.IsResolved);
        Assert.False(unresolved.TerminationPending);
        Assert.True(unresolved.TransportDisposalPending);
        Assert.Contains(unresolved.Failures, item => item.StartsWith("transport-disposal:", StringComparison.Ordinal));

        transport.FailTransportDisposal = false;
        var resolved = await client.RetryQuarantinedWorkerCleanupAsync();

        Assert.True(resolved.IsResolved);
        Assert.Equal(EngineWorkerLifecycleState.Stopped, client.State);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task WorkerClient_LateWorkerTerminationAndDisposalHangsRemainQuarantinedAndRetrySameOperations()
    {
        var transport = new LateStartupWorkerTransport
        {
            HangActiveWorkerTermination = true,
            HangTransportDisposal = true
        };
        var client = new EngineWorkerClient(
            transport,
            transportTimeout: TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync());
        await Assert.ThrowsAsync<EngineWorkerQuarantineException>(() => client.DisposeAsync().AsTask());
        transport.CompleteStartup();
        await transport.ActiveTerminationAttempted.WaitAsync(TimeSpan.FromSeconds(2));
        await transport.DisposalAttempted.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(75);

        var terminationAttempts = transport.TerminateCount;
        var disposalAttempts = transport.DisposeCount;
        var unresolved = await client.RetryQuarantinedWorkerCleanupAsync();

        Assert.False(unresolved.IsResolved);
        Assert.True(unresolved.TerminationPending);
        Assert.True(unresolved.TransportDisposalPending);
        Assert.Equal(terminationAttempts, transport.TerminateCount);
        Assert.Equal(disposalAttempts, transport.DisposeCount);

        transport.ReleaseCleanupHangs();
        var resolved = await client.RetryQuarantinedWorkerCleanupAsync();

        Assert.True(resolved.IsResolved);
        Assert.Equal(0, transport.ActiveWorkers);
        Assert.Equal(EngineWorkerLifecycleState.Stopped, client.State);
    }

    [Fact]
    public async Task WorkerClient_ForeverHangingExecutionDispatchIsBounded()
    {
        var transport = new ForeverHangingWorkerTransport(HangingWorkerOperation.ExecuteSend);
        await using var client = new EngineWorkerClient(
            transport,
            transportTimeout: TimeSpan.FromMilliseconds(25));
        await client.StartAsync();

        await Assert.ThrowsAsync<TimeoutException>(() => client.ExecuteAsync(
            CreateRequest(JsonSerializer.SerializeToElement(new { demand = "hanging-send" }))))
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(EngineWorkerLifecycleState.Faulted, client.State);
        Assert.Equal(1, transport.TerminateCount);
    }

    [Fact]
    public async Task WorkerClient_ForeverHangingCancellationDispatchIsBounded()
    {
        var transport = new ForeverHangingWorkerTransport(HangingWorkerOperation.CancellationSend);
        await using var client = new EngineWorkerClient(
            transport,
            transportTimeout: TimeSpan.FromMilliseconds(25));
        await client.StartAsync();
        var execution = client.ExecuteAsync(
            CreateRequest(JsonSerializer.SerializeToElement(new { demand = "hanging-cancel" })));

        await Assert.ThrowsAsync<TimeoutException>(() => client.CancelAsync("cancel"))
            .WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TimeoutException>(() => execution);

        Assert.Equal(EngineWorkerLifecycleState.Faulted, client.State);
        Assert.Equal(1, transport.TerminateCount);
    }

    [Fact]
    public async Task WorkerClient_UnknownForeverHangingTerminationBlocksReplacementStartup()
    {
        var transport = new ForeverHangingWorkerTransport(HangingWorkerOperation.Termination);
        var client = new EngineWorkerClient(
            transport,
            transportTimeout: TimeSpan.FromMilliseconds(25));
        await client.StartAsync();

        await Assert.ThrowsAsync<EngineWorkerQuarantineException>(() => client.ForceTerminateAndRestartAsync())
            .WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<EngineWorkerQuarantineException>(() => client.StartAsync())
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, transport.StartCount);
        Assert.Equal(EngineWorkerLifecycleState.Quarantined, client.State);
        Assert.True(client.QuarantineEvidence!.TerminationPending);
    }

    [Fact]
    public async Task WorkerClient_ForeverHangingRestartBarrierDoesNotWedgeStartupCaller()
    {
        var transport = new ForeverHangingWorkerTransport(HangingWorkerOperation.Termination);
        var client = new EngineWorkerClient(
            transport,
            transportTimeout: TimeSpan.FromMilliseconds(25));
        await client.StartAsync();

        var restart = client.ForceTerminateAndRestartAsync();
        var startup = client.StartAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => startup)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<EngineWorkerQuarantineException>(() => restart);
        Assert.Equal(1, transport.StartCount);
    }

    [Fact]
    public async Task WorkerClient_ForeverHangingTransportDisposalIsBounded()
    {
        var transport = new ForeverHangingWorkerTransport(HangingWorkerOperation.Disposal);
        var client = new EngineWorkerClient(
            transport,
            transportTimeout: TimeSpan.FromMilliseconds(25));
        await client.StartAsync();

        var failure = await Assert.ThrowsAsync<EngineWorkerQuarantineException>(() => client.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(EngineWorkerLifecycleState.Quarantined, client.State);
        Assert.True(failure.Evidence.TransportDisposalPending);
        Assert.Contains(failure.Evidence.Failures, item => item.StartsWith("transport-disposal:", StringComparison.Ordinal));
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
        var client = new EngineWorkerClient(
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
        Assert.Equal(EngineWorkerLifecycleState.Quarantined, client.State);
        Assert.Equal(1, transport.TerminateCount);
        Assert.True(client.QuarantineEvidence!.TerminationPending);
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
        Assert.Contains("executionSupported: false", worker, StringComparison.Ordinal);
        Assert.Contains("resultKind: computationResultKind", worker, StringComparison.Ordinal);
        Assert.Contains("const computationResultKind = \"computation-result\"", worker, StringComparison.Ordinal);
        Assert.Contains("generation: message.generation", worker, StringComparison.Ordinal);
        Assert.Contains("executionId: message.executionId", worker, StringComparison.Ordinal);
        Assert.Contains("new Worker", bootstrap, StringComparison.Ordinal);
        Assert.Contains("ping(generation)", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("requireCrossOriginIsolation", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("finalTransactionHash", worker, StringComparison.Ordinal);
    }

    private static EngineRequestEnvelope CreateRequest(JsonElement input)
    {
        var basis = new EngineBasisSet(
            new EngineBasisIdentity("plan", "1", "plan-hash"),
            new EngineBasisIdentity("session", "1", "session-hash"),
            new EngineBasisIdentity("publication", "1", "publication-hash"),
            new EngineBasisIdentity("route", "1", "route-hash"));
        var provider = new ReferenceEngineSemanticSnapshotProvider();
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

    private static EngineExecutionHost CreateHost(RecordingSettlement settlement) =>
        EngineExecutionHost.CreateForTesting(
            new StubEngineExecutionTransport((generation, executionId, request) => CreateCompletedComputation(request, generation, executionId)),
            settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

    private static void AssertEvidenceCannotBeMutated(IReadOnlyDictionary<string, string> evidence)
    {
        var dictionary = Assert.IsAssignableFrom<IDictionary<string, string>>(evidence);
        Assert.Throws<NotSupportedException>(() => dictionary.Add("mutation", "blocked"));
    }

    private static EngineExecutionHost CreateBoundedHost(
        IEngineExecutionTransport transport,
        IEngineTransactionSettlement settlement,
        IEngineTransactionLedger ledger) =>
        EngineExecutionHost.CreateForTesting(
            transport,
            settlement,
            ledger,
            new ReferenceEngineSemanticSnapshotProvider(),
            new EngineExecutionHostOptions(
                8,
                TimeSpan.FromMilliseconds(40),
                MaxConcurrentExecutions: 1,
                ComputationTimeout: TimeSpan.FromMilliseconds(40),
                SettlementPhaseTimeout: TimeSpan.FromMilliseconds(40),
                LedgerWriteTimeout: TimeSpan.FromMilliseconds(40),
                LedgerClaimTimeout: TimeSpan.FromMilliseconds(40)));

    private static EngineComputationResult CreateCompletedComputation(
        EngineRequestEnvelope request,
        long generation,
        Guid executionId)
    {
        var input = request.Input.Deserialize<ReferenceEngineInput>(EngineJsonSerializerOptions.CreateWire());
        var analysis = input?.MarketAnalysis is null
            ? null
            : new EngineAnalysisSemanticSnapshot("2", [], []);
        var route = input?.ProcurementRoute is null
            ? null
            : new EngineRouteSemanticSnapshot("2", [], [], 0, 0, 0, true, null);
        var payload = JsonSerializer.SerializeToElement(
            new ReferenceEngineResultSnapshot(analysis, route),
            EngineJsonSerializerOptions.CreateWire());
        var evidence = new Dictionary<string, string>
        {
            ["resultPayloadHash"] = EngineCanonicalHash.Compute(payload)
        };
        if (input?.MarketAnalysis is not null)
        {
            evidence["phase:Analyzing"] = "complete";
        }
        if (input?.ProcurementRoute is not null)
        {
            evidence["phase:Reconciling"] = "complete";
        }

        var analysisHash = analysis is null ? string.Empty : EngineSemanticSnapshotHash.Analysis(analysis);
        var routeHash = route is null ? string.Empty : EngineSemanticSnapshotHash.Route(route);
        var finalPhase = route is not null ? EnginePhase.Reconciling : EnginePhase.Analyzing;
        var computationHash = EngineCanonicalHash.ComputeComputationHash(
            generation,
            executionId,
            request,
            EngineComputationStatus.Completed,
            finalPhase,
            evidence["resultPayloadHash"],
            analysisHash,
            routeHash,
            evidence,
            null);

        return new EngineComputationResult(
            "1",
            generation,
            executionId,
            request.TransactionId,
            EngineComputationStatus.Completed,
            finalPhase,
            payload,
            request.Basis,
            EngineCanonicalHash.ComputeEngineInput(request.Input),
            request.Budgets,
            request.RootIntentHash,
            request.ExpandedGraphHash,
            request.AnalysisBasisHash,
            request.RouteBasisHash,
            analysisHash,
            routeHash,
            computationHash,
            evidence);
    }

    private static EngineComputationResult CreateCancelledComputation(
        EngineRequestEnvelope request,
        long generation,
        Guid executionId,
        EnginePhase phase)
    {
        const string message = "The computation was cancelled.";
        var evidence = new Dictionary<string, string>
        {
            ["terminalCode"] = "cancelled",
            ["failedPhase"] = phase.ToString(),
            ["failureType"] = nameof(OperationCanceledException),
            ["failureMessageHash"] = EngineCanonicalHash.Compute(message),
            ["isRetryable"] = bool.FalseString
        };
        var computationHash = EngineCanonicalHash.ComputeComputationHash(
            generation,
            executionId,
            request,
            EngineComputationStatus.Cancelled,
            phase,
            string.Empty,
            string.Empty,
            string.Empty,
            evidence,
            null);
        return new EngineComputationResult(
            request.ContractVersion,
            generation,
            executionId,
            request.TransactionId,
            EngineComputationStatus.Cancelled,
            phase,
            null,
            request.Basis,
            EngineCanonicalHash.ComputeEngineInput(request.Input),
            request.Budgets,
            request.RootIntentHash,
            request.ExpandedGraphHash,
            request.AnalysisBasisHash,
            request.RouteBasisHash,
            string.Empty,
            string.Empty,
            computationHash,
            evidence);
    }

    private static EngineComputationResult CreateFailedComputation(
        EngineRequestEnvelope request,
        long generation,
        Guid executionId,
        EnginePhase phase)
    {
        var failure = new EngineFailure("failed", "The computation failed.", false, phase, "TestFailure");
        var evidence = new Dictionary<string, string>
        {
            ["terminalCode"] = failure.Code,
            ["failedPhase"] = phase.ToString(),
            ["failureType"] = failure.FailureType,
            ["failureMessageHash"] = EngineCanonicalHash.Compute(failure.Message),
            ["isRetryable"] = bool.FalseString
        };
        var computationHash = EngineCanonicalHash.ComputeComputationHash(
            generation,
            executionId,
            request,
            EngineComputationStatus.Failed,
            phase,
            string.Empty,
            string.Empty,
            string.Empty,
            evidence,
            failure);
        return new EngineComputationResult(
            request.ContractVersion,
            generation,
            executionId,
            request.TransactionId,
            EngineComputationStatus.Failed,
            phase,
            null,
            request.Basis,
            EngineCanonicalHash.ComputeEngineInput(request.Input),
            request.Budgets,
            request.RootIntentHash,
            request.ExpandedGraphHash,
            request.AnalysisBasisHash,
            request.RouteBasisHash,
            string.Empty,
            string.Empty,
            computationHash,
            evidence,
            failure);
    }

    private static EngineComputationResult RehashComputation(
        EngineRequestEnvelope request,
        EngineComputationResult computation)
    {
        var payloadHash = computation.Result is { } payload
            ? EngineCanonicalHash.Compute(payload)
            : string.Empty;
        var computationHash = EngineCanonicalHash.ComputeComputationHash(
            computation.Generation,
            computation.ExecutionId,
            request,
            computation.Status,
            computation.FinalPhase,
            payloadHash,
            computation.AnalysisResultHash,
            computation.ProcurementRouteResultHash,
            computation.ComputationEvidence,
            computation.Failure);
        return computation with { ComputationHash = computationHash };
    }

    private static EngineResultEnvelope RehashTerminal(
        EngineRequestEnvelope request,
        EngineResultEnvelope result,
        IReadOnlyDictionary<string, string> evidence)
    {
        var completion = result.Completion with
        {
            TerminalEvidence = evidence,
            FinalTransactionHash = EngineCanonicalHash.ComputeFinalTransactionHash(
                request,
                result.Status,
                result.Completion.AnalysisResultHash,
                result.Completion.ProcurementRouteResultHash,
                evidence)
        };
        return result with { Completion = completion };
    }

    private static EngineComputationResult CreateWorkerCancelledComputation(
        FakeWorkerTransport transport,
        EngineRequestEnvelope request,
        EnginePhase cancellationPhase = EnginePhase.Accepted)
    {
        var execute = transport.Sent.Last(message => message.Kind == "execute");
        return CreateCancelledComputation(
            request,
            execute.Generation,
            execute.ExecutionId!.Value,
            cancellationPhase);
    }

    private static EngineWorkerMessage CreateWorkerMessage(
        FakeWorkerTransport transport,
        string kind,
        Guid transactionId,
        JsonElement? payload,
        long? generation = null,
        Guid? executionId = null)
    {
        var execute = transport.Sent.Last(message => message.Kind == "execute");
        return new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            kind,
            generation ?? execute.Generation,
            executionId ?? execute.ExecutionId,
            transactionId,
            payload);
    }

    private static EngineProgress CreateWorkerProgress(
        FakeWorkerTransport transport,
        Guid transactionId,
        EnginePhase phase,
        int completedWorkUnits,
        int totalWorkUnits,
        string message,
        long? generation = null,
        Guid? executionId = null)
    {
        var execute = transport.Sent.Last(sent => sent.Kind == "execute");
        return new EngineProgress(
            transactionId,
            generation ?? execute.Generation,
            executionId ?? execute.ExecutionId!.Value,
            phase,
            completedWorkUnits,
            totalWorkUnits,
            message);
    }

    private static EngineResultEnvelope CreateSuccessfulResult(EngineRequestEnvelope request)
    {
        var input = request.Input.Deserialize<ReferenceEngineInput>(
            EngineJsonSerializerOptions.CreateWire())
            ?? throw new InvalidOperationException("Cannot create successful fixture evidence for the engine request.");
        var analysis = input.MarketAnalysis is null
            ? null
            : new EngineAnalysisSemanticSnapshot("2", [], []);
        var route = input.ProcurementRoute is null
            ? null
            : new EngineRouteSemanticSnapshot("2", [], [], 0, 0, 0, true, null);
        var payload = JsonSerializer.SerializeToElement(
            new ReferenceEngineResultSnapshot(analysis, route),
            EngineJsonSerializerOptions.CreateWire());
        var evidence = new Dictionary<string, string>
        {
            ["resultPayloadHash"] = EngineCanonicalHash.Compute(payload),
            ["computationHash"] = new string('a', 64),
            ["settlement"] = "complete"
        };
        var requirements = EngineSuccessPhasePolicy.Resolve(
            request.InputKind,
            input.MarketAnalysis is not null,
            input.ProcurementRoute is not null);
        foreach (var phase in requirements.RequiredEvidencePhases)
        {
            evidence[$"phase:{phase}"] = "complete";
        }
        var analysisHash = analysis is null ? string.Empty : EngineSemanticSnapshotHash.Analysis(analysis);
        var routeHash = route is null ? string.Empty : EngineSemanticSnapshotHash.Route(route);
        var hash = EngineCanonicalHash.ComputeFinalTransactionHash(
            request,
            EngineTerminalStatus.Succeeded,
            analysisHash,
            routeHash,
            evidence);
        var completion = new EngineCompletionEvidence(
            "1",
            request.TransactionId,
            EngineTerminalStatus.Succeeded,
            EnginePhase.Completed,
            request.Basis,
            request.RootIntentHash,
            request.ExpandedGraphHash,
            analysisHash,
            routeHash,
            hash,
            evidence);
        return new EngineResultEnvelope("1", request.TransactionId, EngineTerminalStatus.Succeeded, payload, completion);
    }

    private static EngineResultEnvelope CreateFailedResult(EngineRequestEnvelope request)
    {
        var failure = new EngineFailure("failed", "failure message", false, EnginePhase.Accepted, "TestFailure");
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

    private static EngineResultEnvelope CreateCancelledResult(
        EngineRequestEnvelope request,
        EnginePhase cancellationPhase = EnginePhase.Accepted)
    {
        const string message = "The engine transaction was cancelled.";
        var evidence = new Dictionary<string, string>
        {
            ["terminalCode"] = "cancelled",
            ["failedPhase"] = cancellationPhase.ToString(),
            ["failureType"] = nameof(OperationCanceledException),
            ["failureMessageHash"] = EngineCanonicalHash.Compute(message),
            ["isRetryable"] = bool.FalseString
        };
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

    private static ProcurementRouteExecutionResult CreateSplitRouteResult(
        string firstWorld,
        string secondWorld)
    {
        var first = CreateWorld(firstWorld, 50);
        var second = CreateWorld(secondWorld, 50);
        var shopping = new DetailedShoppingPlan
        {
            ItemId = 1,
            Name = "Item",
            QuantityNeeded = 2,
            WorldOptions = [first, second],
            RecommendedSplit =
            [
                new SplitWorldPurchase
                {
                    DataCenter = "Aether",
                    WorldName = firstWorld,
                    QuantityToBuy = 1,
                    TotalCost = 50
                },
                new SplitWorldPurchase
                {
                    DataCenter = "Aether",
                    WorldName = secondWorld,
                    QuantityToBuy = 1,
                    TotalCost = 50
                }
            ]
        };
        return new ProcurementRouteExecutionResult(
            [shopping],
            [shopping],
            [],
            [],
            [],
            new MarketRouteDecision(0, null, 100, 100, 0, 2, 2, 0, 0, true, "Aether"));
    }

    private static ProcurementRouteExecutionResult CreateCoverageRouteResult(
        string firstWorld,
        string secondWorld)
    {
        var first = CreateWorld(firstWorld, 50);
        var second = CreateWorld(secondWorld, 50);
        var coverage = new MarketCoverageOption(
            "selected-coverage",
            MarketCoverageTier.CompactSplit,
            MarketCoverageKind.SupportedListings,
            MarketCoverageQualityPolicy.NqOrHq,
            2,
            2,
            0,
            100,
            100,
            50,
            MarketCoveragePriceBand.Competitive,
            [
                new MarketCoverageWorld("Aether", firstWorld, 1, 1, 50, 50),
                new MarketCoverageWorld("Aether", secondWorld, 1, 1, 50, 50)
            ],
            [],
            new MarketCoverageFriction(2, 1, 1, 1, 0),
            MarketCoverageSavings.None,
            true,
            null);
        var shopping = new DetailedShoppingPlan
        {
            ItemId = 1,
            Name = "Item",
            QuantityNeeded = 2,
            WorldOptions = [first, second],
            CoverageSet = new MarketCoverageSet(
                1,
                "Item",
                2,
                null,
                coverage,
                null,
                null,
                [coverage])
        };
        return new ProcurementRouteExecutionResult(
            [shopping],
            [shopping],
            [],
            [],
            [],
            new MarketRouteDecision(0, null, 100, 100, 0, 2, 2, 0, 0, true, "Aether"));
    }

    private static WorldShoppingSummary CreateWorld(
        string worldName,
        long totalCost,
        DateTime? timestamp = null) =>
        new()
        {
            DataCenter = "Aether",
            WorldName = worldName,
            TotalCost = totalCost,
            TotalQuantityPurchased = 2,
            MarketUploadedAtUtc = timestamp ?? DateTime.UnixEpoch,
            MarketDataAge = TimeSpan.FromMinutes(5)
        };

    private static ProcurementRouteExecutionResult CreateRouteResult(DateTime timestamp, long selectedCost)
    {
        var world = CreateWorld("Alpha", selectedCost, timestamp);
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

    private static string ComputeAnalysisHash(MarketAnalysisExecutionResult result) =>
        EngineSemanticSnapshotHash.Analysis(new ReferenceEngineSemanticSnapshotProvider().CaptureAnalysis(result));

    private static string ComputeRouteHash(ProcurementRouteExecutionResult result) =>
        EngineSemanticSnapshotHash.Route(new ReferenceEngineSemanticSnapshotProvider().CaptureRoute(result));

    private sealed class DelegateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class BlockingProgressObserver(ManualResetEventSlim release) : IProgress<EngineProgress>
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Report(EngineProgress value)
        {
            Entered.TrySetResult();
            release.Wait();
        }
    }

    private sealed class RecordingSettlement : IEngineTransactionSettlement
    {
        private readonly Dictionary<string, EngineSettlementEvidence> _outcomes = [];

        public List<EnginePhase> Phases { get; } = [];

        public List<EngineSettlementContext> Contexts { get; } = [];

        public Action<EnginePhase>? OnPhase { get; init; }

        public EnginePhase? FailPhase { get; init; }

        public Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken)
        {
            if (_outcomes.TryGetValue(context.PhaseDeliveryId, out var priorOutcome))
            {
                return Task.FromResult(priorOutcome);
            }

            Phases.Add(phase);
            Contexts.Add(context);
            OnPhase?.Invoke(phase);
            if (phase == FailPhase)
            {
                return Task.FromResult(new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "injected failure"));
            }
            var outcome = new EngineSettlementEvidence(
                phase == EnginePhase.Persisting ? EngineSettlementOutcome.Committed : EngineSettlementOutcome.Applied,
                $"{phase}:complete");
            _outcomes[context.PhaseDeliveryId] = outcome;
            return Task.FromResult(outcome);
        }

        public Task<EngineSettlementEvidence> ObserveAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(_outcomes.GetValueOrDefault(
                context.PhaseDeliveryId,
                new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied")));
    }

    private sealed class ConcurrentSettlement : IEngineTransactionSettlement
    {
        private int _gateReleaseCount;

        public int GateReleaseCount => Volatile.Read(ref _gateReleaseCount);

        public Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken)
        {
            if (phase == EnginePhase.ReleasingGate)
            {
                Interlocked.Increment(ref _gateReleaseCount);
            }
            return Task.FromResult(new EngineSettlementEvidence(
                phase == EnginePhase.Persisting ? EngineSettlementOutcome.Committed : EngineSettlementOutcome.Applied,
                $"{phase}:complete"));
        }

        public Task<EngineSettlementEvidence> ObserveAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied"));
    }

    private sealed class BlockingCapacityCleanupSettlement : IEngineTransactionSettlement
    {
        private readonly TaskCompletionSource<EngineSettlementEvidence> _cleanupRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _concurrentCleanups;
        private int _maximumConcurrentCleanups;
        private int _cleanupEntries;

        public TaskCompletionSource TwoCleanupsEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaximumConcurrentCleanups => Volatile.Read(ref _maximumConcurrentCleanups);

        public async Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken)
        {
            if (phase != EnginePhase.ReleasingGate)
            {
                return new EngineSettlementEvidence(
                    phase == EnginePhase.Persisting ? EngineSettlementOutcome.Committed : EngineSettlementOutcome.Applied,
                    $"{phase}:complete");
            }

            var concurrent = Interlocked.Increment(ref _concurrentCleanups);
            var observedMaximum = Volatile.Read(ref _maximumConcurrentCleanups);
            while (concurrent > observedMaximum)
            {
                observedMaximum = Interlocked.CompareExchange(
                    ref _maximumConcurrentCleanups,
                    concurrent,
                    observedMaximum);
            }
            if (Interlocked.Increment(ref _cleanupEntries) == 2)
            {
                TwoCleanupsEntered.TrySetResult();
            }
            try
            {
                return await _cleanupRelease.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentCleanups);
            }
        }

        public Task<EngineSettlementEvidence> ObserveAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied"));

        public void ReleaseCleanups() => _cleanupRelease.TrySetResult(
            new EngineSettlementEvidence(EngineSettlementOutcome.Applied, "operation-gate-released"));
    }

    private sealed class InvocationFencedGateSettlement :
        IEngineTransactionSettlement,
        IEngineExecutionContextRegistrar
    {
        private string? _invocationToken;
        private string? _claimToken;
        private int _settlementCalls;
        private int _gateReleaseCount;
        private int _gateHeld = 1;

        public bool IsGateHeld => Volatile.Read(ref _gateHeld) == 1;
        public int SettlementCalls => Volatile.Read(ref _settlementCalls);
        public int GateReleaseCount => Volatile.Read(ref _gateReleaseCount);

        public EngineInvocationCleanupOwnership? TryRegisterInvocationCleanupOwnership(
            EngineInvocationCleanupRegistration registration)
        {
            if (Interlocked.CompareExchange(ref _invocationToken, registration.InvocationToken, null) is not null)
            {
                return null;
            }
            string requestHash;
            try
            {
                requestHash = EngineCanonicalHash.Compute(
                    registration.Request,
                    EngineJsonSerializerOptions.CreateWire());
            }
            catch
            {
                requestHash = EngineCanonicalHash.ComputeRequestValidationFailureIdentity(registration.Request);
            }
            return new EngineInvocationCleanupOwnership(registration.InvocationToken, requestHash);
        }

        public void RegisterExecutionContext(EngineExecutionContextRegistration registration)
        {
            _invocationToken = registration.InvocationToken;
            _claimToken = registration.ClaimToken;
        }

        public Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _settlementCalls);
            if (!string.Equals(context.InvocationToken, _invocationToken, StringComparison.Ordinal) ||
                !string.Equals(context.ClaimToken, _claimToken, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The invocation does not own this gate.");
            }
            if (phase == EnginePhase.ReleasingGate)
            {
                Interlocked.Increment(ref _gateReleaseCount);
                Interlocked.Exchange(ref _gateHeld, 0);
            }
            return Task.FromResult(new EngineSettlementEvidence(
                phase == EnginePhase.Persisting ? EngineSettlementOutcome.Committed : EngineSettlementOutcome.Applied,
                $"{phase}:complete"));
        }

        public Task<EngineSettlementEvidence> ObserveAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied"));
    }

    private sealed class CancelledCommitAcknowledgementSettlement(CancellationTokenSource cancellation)
        : IEngineTransactionSettlement
    {
        private readonly Dictionary<string, EngineSettlementEvidence> _outcomes = [];

        public int PersistenceDeliveryCount { get; private set; }

        public int PersistenceObservationCount { get; private set; }

        public async Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken)
        {
            if (_outcomes.TryGetValue(context.PhaseDeliveryId, out var existing))
            {
                return existing;
            }

            var outcome = new EngineSettlementEvidence(
                phase == EnginePhase.Persisting ? EngineSettlementOutcome.Committed : EngineSettlementOutcome.Applied,
                $"{phase}:complete");
            _outcomes.Add(context.PhaseDeliveryId, outcome);
            if (phase == EnginePhase.Persisting)
            {
                PersistenceDeliveryCount++;
                cancellation.Cancel();
                await Task.Yield();
                throw new OperationCanceledException(cancellation.Token);
            }

            return outcome;
        }

        public Task<EngineSettlementEvidence> ObserveAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken)
        {
            if (phase == EnginePhase.Persisting)
            {
                PersistenceObservationCount++;
            }
            return Task.FromResult(_outcomes.GetValueOrDefault(
                context.PhaseDeliveryId,
                new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied")));
        }
    }

    private sealed class MalformedCommitSettlement : IEngineTransactionSettlement
    {
        public List<EnginePhase> Phases { get; } = [];

        public int PersistenceAttempts { get; private set; }

        public Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken)
        {
            Phases.Add(phase);
            if (phase == EnginePhase.Persisting)
            {
                PersistenceAttempts++;
                return Task.FromResult(new EngineSettlementEvidence(EngineSettlementOutcome.Committed, " "));
            }

            return Task.FromResult(new EngineSettlementEvidence(EngineSettlementOutcome.Applied, $"{phase}:complete"));
        }

        public Task<EngineSettlementEvidence> ObserveAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied"));
    }

    private sealed class RetryGateSettlement(bool throwFirstAttempt) : IEngineTransactionSettlement
    {
        public int GateAttempts { get; private set; }

        public Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken)
        {
            if (phase == EnginePhase.ReleasingGate)
            {
                GateAttempts++;
                if (GateAttempts == 1 && throwFirstAttempt)
                {
                    throw new InvalidOperationException("injected gate delivery failure");
                }
                return Task.FromResult(GateAttempts == 1
                    ? new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied")
                    : new EngineSettlementEvidence(EngineSettlementOutcome.Applied, "ReleasingGate:complete"));
            }

            return Task.FromResult(new EngineSettlementEvidence(
                phase == EnginePhase.Persisting ? EngineSettlementOutcome.Committed : EngineSettlementOutcome.Applied,
                $"{phase}:complete"));
        }

        public Task<EngineSettlementEvidence> ObserveAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied"));
    }

    private sealed class RecoveringGateSettlement : IEngineTransactionSettlement
    {
        private readonly Dictionary<string, EngineSettlementEvidence> _outcomes = [];

        public int GateAttempts { get; private set; }

        public Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken)
        {
            if (phase == EnginePhase.ReleasingGate)
            {
                GateAttempts++;
                return Task.FromResult(GateAttempts <= 2
                    ? new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied")
                    : new EngineSettlementEvidence(EngineSettlementOutcome.Applied, "ReleasingGate:complete"));
            }
            if (_outcomes.TryGetValue(context.PhaseDeliveryId, out var existing))
            {
                return Task.FromResult(existing);
            }

            var outcome = new EngineSettlementEvidence(
                phase == EnginePhase.Persisting ? EngineSettlementOutcome.Committed : EngineSettlementOutcome.Applied,
                $"{phase}:complete");
            _outcomes.Add(context.PhaseDeliveryId, outcome);
            return Task.FromResult(outcome);
        }

        public Task<EngineSettlementEvidence> ObserveAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(_outcomes.GetValueOrDefault(
                context.PhaseDeliveryId,
                new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied")));
    }

    private sealed class HangingPostCommitSettlement : IEngineTransactionSettlement
    {
        private readonly TaskCompletionSource<EngineSettlementEvidence> _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int HangingPhaseAttempts { get; private set; }

        public Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken)
        {
            if (phase == EnginePhase.SettlingUi)
            {
                HangingPhaseAttempts++;
                return _never.Task;
            }

            return Task.FromResult(new EngineSettlementEvidence(
                phase == EnginePhase.Persisting ? EngineSettlementOutcome.Committed : EngineSettlementOutcome.Applied,
                $"{phase}:complete"));
        }

        public Task<EngineSettlementEvidence> ObserveAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied"));
    }

    private sealed class HangingGateSettlement : IEngineTransactionSettlement
    {
        private readonly TaskCompletionSource<EngineSettlementEvidence> _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReleaseAttempts { get; private set; }

        public Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken)
        {
            Assert.Equal(EnginePhase.ReleasingGate, phase);
            ReleaseAttempts++;
            return _never.Task;
        }

        public Task<EngineSettlementEvidence> ObserveAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied"));
    }

    private sealed class SynchronouslyBlockingSettlement(ManualResetEventSlim release)
        : IEngineTransactionSettlement
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int BlockingAttempts { get; private set; }

        public Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken)
        {
            if (phase == EnginePhase.Publishing)
            {
                BlockingAttempts++;
                Entered.TrySetResult();
                release.Wait();
            }

            return Task.FromResult(new EngineSettlementEvidence(
                phase == EnginePhase.Persisting ? EngineSettlementOutcome.Committed : EngineSettlementOutcome.Applied,
                $"{phase}:complete"));
        }

        public Task<EngineSettlementEvidence> ObserveAsync(
            EnginePhase phase,
            EngineSettlementContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied"));
    }

    private sealed class SynchronouslyBlockingTransport(ManualResetEventSlim release)
        : IEngineExecutionTransport
    {
        public EngineExecutionTransportCapability Capability { get; } =
            new(EngineExecutionTransportKind.InProcess, true);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecutionCount { get; private set; }

        public Task<EngineComputationResult> ExecuteAsync(
            long generation,
            Guid executionId,
            EngineRequestEnvelope request,
            IProgress<EngineProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            Entered.TrySetResult();
            release.Wait();
            return Task.FromResult(CreateCompletedComputation(request, generation, executionId));
        }
    }

    private sealed class SynchronouslyBlockingClaimLedger(ManualResetEventSlim release)
        : IEngineTransactionLedger
    {
        private readonly InMemoryEngineTransactionLedger _inner = new();

        public EngineTransactionLedgerCapability Capability => _inner.Capability;

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<EngineTransactionClaim> ClaimAsync(
            Guid transactionId,
            string canonicalRequestHash,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            release.Wait();
            return _inner.ClaimAsync(transactionId, canonicalRequestHash, cancellationToken);
        }

        public ValueTask CompleteAsync(
            Guid transactionId,
            string canonicalRequestHash,
            string claimToken,
            EngineResultEnvelope terminalResult,
            CancellationToken cancellationToken) =>
            _inner.CompleteAsync(transactionId, canonicalRequestHash, claimToken, terminalResult, cancellationToken);

        public ValueTask ReleaseAsync(
            Guid transactionId,
            string canonicalRequestHash,
            string claimToken,
            CancellationToken cancellationToken) =>
            _inner.ReleaseAsync(transactionId, canonicalRequestHash, claimToken, cancellationToken);
    }

    private sealed class DelayedWriteLedger(ManualResetEventSlim release) : IEngineTransactionLedger
    {
        private readonly InMemoryEngineTransactionLedger _inner = new();

        public EngineTransactionLedgerCapability Capability => _inner.Capability;

        public TaskCompletionSource WriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WriteCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EngineResultEnvelope? WrittenResult { get; private set; }

        public ValueTask<EngineTransactionClaim> ClaimAsync(
            Guid transactionId,
            string canonicalRequestHash,
            CancellationToken cancellationToken) =>
            _inner.ClaimAsync(transactionId, canonicalRequestHash, cancellationToken);

        public ValueTask CompleteAsync(
            Guid transactionId,
            string canonicalRequestHash,
            string claimToken,
            EngineResultEnvelope terminalResult,
            CancellationToken cancellationToken)
        {
            WrittenResult = terminalResult;
            WriteEntered.TrySetResult();
            release.Wait();
            var completion = _inner.CompleteAsync(
                transactionId,
                canonicalRequestHash,
                claimToken,
                terminalResult,
                CancellationToken.None);
            WriteCompleted.TrySetResult();
            return completion;
        }

        public ValueTask ReleaseAsync(
            Guid transactionId,
            string canonicalRequestHash,
            string claimToken,
            CancellationToken cancellationToken) =>
            _inner.ReleaseAsync(transactionId, canonicalRequestHash, claimToken, cancellationToken);
    }

    private sealed class ActiveThenTerminalLedger(EngineResultEnvelope terminalResult)
        : IEngineTransactionLedger
    {
        public EngineTransactionLedgerCapability Capability { get; } = new(true, true, true);

        public int ClaimCount { get; private set; }

        public ValueTask<EngineTransactionClaim> ClaimAsync(
            Guid transactionId,
            string canonicalRequestHash,
            CancellationToken cancellationToken)
        {
            ClaimCount++;
            return ValueTask.FromResult(ClaimCount == 1
                ? new EngineTransactionClaim(EngineTransactionClaimDisposition.ActiveReplay, canonicalRequestHash)
                : new EngineTransactionClaim(
                    EngineTransactionClaimDisposition.TerminalReplay,
                    canonicalRequestHash,
                    terminalResult));
        }

        public ValueTask CompleteAsync(
            Guid transactionId,
            string canonicalRequestHash,
            string claimToken,
            EngineResultEnvelope terminalResult,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Replay ledger writes are not expected.");

        public ValueTask ReleaseAsync(
            Guid transactionId,
            string canonicalRequestHash,
            string claimToken,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Replay ledger releases are not expected.");
    }

    private sealed class AbandonedWriteFailureLedger : IEngineTransactionLedger
    {
        public EngineTransactionLedgerCapability Capability { get; } = new(true, true, true);

        public ValueTask<EngineTransactionClaim> ClaimAsync(
            Guid transactionId,
            string canonicalRequestHash,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EngineTransactionClaim(
                EngineTransactionClaimDisposition.AbandonedReplay,
                canonicalRequestHash,
                ClaimToken: "abandoned-owner"));

        public ValueTask CompleteAsync(
            Guid transactionId,
            string canonicalRequestHash,
            string claimToken,
            EngineResultEnvelope terminalResult,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("injected abandoned ledger write failure"));

        public ValueTask ReleaseAsync(
            Guid transactionId,
            string canonicalRequestHash,
            string claimToken,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("Abandoned claims are never released."));
    }

    private sealed class ClaimResponseLedger(Func<string, EngineTransactionClaim?> claimFactory)
        : IEngineTransactionLedger
    {
        public EngineTransactionLedgerCapability Capability { get; } = new(true, true, true);

        public ValueTask<EngineTransactionClaim> ClaimAsync(
            Guid transactionId,
            string canonicalRequestHash,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(claimFactory(canonicalRequestHash)!);

        public ValueTask CompleteAsync(
            Guid transactionId,
            string canonicalRequestHash,
            string claimToken,
            EngineResultEnvelope terminalResult,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Malformed claims must not be completed.");

        public ValueTask ReleaseAsync(
            Guid transactionId,
            string canonicalRequestHash,
            string claimToken,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Malformed claims must not be released.");
    }

    private sealed class TerminalReplayLedger(EngineResultEnvelope terminalResult)
        : IEngineTransactionLedger
    {
        public EngineTransactionLedgerCapability Capability { get; } = new(true, true, true);

        public ValueTask<EngineTransactionClaim> ClaimAsync(
            Guid transactionId,
            string canonicalRequestHash,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EngineTransactionClaim(
                EngineTransactionClaimDisposition.TerminalReplay,
                canonicalRequestHash,
                terminalResult));

        public ValueTask CompleteAsync(
            Guid transactionId,
            string canonicalRequestHash,
            string claimToken,
            EngineResultEnvelope terminalResult,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Replay ledger writes are not expected.");

        public ValueTask ReleaseAsync(
            Guid transactionId,
            string canonicalRequestHash,
            string claimToken,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Replay ledger releases are not expected.");
    }

    public enum PrevalidationTransportBehavior
    {
        Unsupported,
        Throwing,
        Cancelled
    }

    private sealed class PrevalidationTransport(PrevalidationTransportBehavior behavior)
        : IEngineExecutionTransport
    {
        public EngineExecutionTransportCapability Capability { get; } = new(
            EngineExecutionTransportKind.InProcess,
            behavior != PrevalidationTransportBehavior.Unsupported,
            behavior == PrevalidationTransportBehavior.Unsupported ? "unsupported for test" : null);

        public Task<EngineComputationResult> ExecuteAsync(
            long generation,
            Guid executionId,
            EngineRequestEnvelope request,
            IProgress<EngineProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            behavior switch
            {
                PrevalidationTransportBehavior.Throwing =>
                    Task.FromException<EngineComputationResult>(new InvalidOperationException("transport failure")),
                PrevalidationTransportBehavior.Cancelled =>
                    Task.FromCanceled<EngineComputationResult>(cancellationToken),
                _ => throw new InvalidOperationException("Unsupported transport execution must not be invoked.")
            };
    }

    private sealed class StubEngineExecutionTransport : IEngineExecutionTransport
    {
        private readonly Func<long, Guid, EngineRequestEnvelope, EngineComputationResult> _execute;

        public StubEngineExecutionTransport(Func<long, Guid, EngineRequestEnvelope, EngineComputationResult> execute)
        {
            _execute = execute;
        }

        public EngineExecutionTransportCapability Capability { get; } =
            new(EngineExecutionTransportKind.InProcess, true);

        public int ExecutionCount { get; private set; }

        public Task<EngineComputationResult> ExecuteAsync(
            long generation,
            Guid executionId,
            EngineRequestEnvelope request,
            IProgress<EngineProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return Task.FromResult(_execute(generation, executionId, request));
        }
    }

    private enum HangingWorkerOperation
    {
        Startup,
        ExecuteSend,
        CancellationSend,
        Termination,
        Disposal
    }

    private sealed class ForeverHangingWorkerTransport(HangingWorkerOperation hangingOperation)
        : IEngineWorkerTransport
    {
        private readonly TaskCompletionSource _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<EngineWorkerMessage>? MessageReceived
        {
            add { }
            remove { }
        }

        public int StartCount { get; private set; }

        public int TerminateCount { get; private set; }

        public Task<EngineWorkerCapability> StartAsync(long generation, CancellationToken cancellationToken)
        {
            StartCount++;
            return hangingOperation == HangingWorkerOperation.Startup
                ? _never.Task.ContinueWith(
                    _ => CreateCapability(generation),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                : Task.FromResult(CreateCapability(generation));
        }

        public Task SendAsync(EngineWorkerMessage message, CancellationToken cancellationToken) =>
            hangingOperation == HangingWorkerOperation.ExecuteSend && message.Kind == "execute" ||
            hangingOperation == HangingWorkerOperation.CancellationSend && message.Kind == "cancel"
                ? _never.Task
                : Task.CompletedTask;

        public Task TerminateAsync(CancellationToken cancellationToken)
        {
            TerminateCount++;
            return hangingOperation == HangingWorkerOperation.Termination
                ? _never.Task
                : Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => hangingOperation == HangingWorkerOperation.Disposal
            ? new ValueTask(_never.Task)
            : ValueTask.CompletedTask;

        private static EngineWorkerCapability CreateCapability(long generation) => new(
            EngineWorkerClient.ProtocolVersion,
            generation,
            true,
            false,
            false,
            false,
            ExecutionSupported: true);
    }

    private sealed class LateStartupWorkerTransport : IEngineWorkerTransport
    {
        private readonly TaskCompletionSource _startupGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _lateWorkerTerminated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _transportDisposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _activeTerminationAttempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposalAttempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _terminationHang =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposalHang =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeWorkers;

        public event EventHandler<EngineWorkerMessage>? MessageReceived
        {
            add { }
            remove { }
        }

        public int StartCount { get; private set; }

        public int TerminateCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int ActiveWorkers => Volatile.Read(ref _activeWorkers);

        public Task LateWorkerTerminated => _lateWorkerTerminated.Task;

        public Task TransportDisposed => _transportDisposed.Task;

        public Task ActiveTerminationAttempted => _activeTerminationAttempted.Task;

        public Task DisposalAttempted => _disposalAttempted.Task;

        public bool FailActiveWorkerTermination { get; set; }

        public bool FailTransportDisposal { get; set; }

        public bool HangActiveWorkerTermination { get; set; }

        public bool HangTransportDisposal { get; set; }

        public async Task<EngineWorkerCapability> StartAsync(long generation, CancellationToken cancellationToken)
        {
            StartCount++;
            await _startupGate.Task;
            Interlocked.Increment(ref _activeWorkers);
            return new EngineWorkerCapability(
                EngineWorkerClient.ProtocolVersion,
                generation,
                true,
                false,
                false,
                false,
                ExecutionSupported: true);
        }

        public Task SendAsync(EngineWorkerMessage message, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async Task TerminateAsync(CancellationToken cancellationToken)
        {
            TerminateCount++;
            if (ActiveWorkers > 0)
            {
                _activeTerminationAttempted.TrySetResult();
                if (HangActiveWorkerTermination)
                {
                    await _terminationHang.Task;
                }
                if (FailActiveWorkerTermination)
                {
                    throw new IOException("Active worker termination failed.");
                }
            }
            if (Interlocked.Exchange(ref _activeWorkers, 0) > 0)
            {
                _lateWorkerTerminated.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            _disposalAttempted.TrySetResult();
            if (HangTransportDisposal)
            {
                await _disposalHang.Task;
            }
            if (FailTransportDisposal)
            {
                throw new IOException("Worker transport disposal failed.");
            }
            _transportDisposed.TrySetResult();
        }

        public void CompleteStartup() => _startupGate.TrySetResult();

        public void ReleaseCleanupHangs()
        {
            _terminationHang.TrySetResult();
            _disposalHang.TrySetResult();
        }
    }

    private sealed class FakeWorkerTransport : IEngineWorkerTransport
    {
        private int _activeWorkers;
        private int _maxActiveWorkers;

        public event EventHandler<EngineWorkerMessage>? MessageReceived;

        public List<EngineWorkerMessage> Sent { get; } = [];

        public int StartCount { get; private set; }

        public int TerminateCount { get; private set; }

        public TaskCompletionSource? StartGate { get; init; }

        public Queue<TaskCompletionSource>? StartGates { get; init; }

        public TaskCompletionSource? CancelSendGate { get; init; }

        public TaskCompletionSource? TerminateGate { get; init; }

        public bool ThrowOnTerminate { get; init; }

        public bool ExecutionSupported { get; init; } = true;

        public long CapabilityGenerationOffset { get; init; }

        public Action<EngineWorkerMessage>? OnSend { get; set; }

        public int MaxActiveWorkers => Volatile.Read(ref _maxActiveWorkers);

        public async Task<EngineWorkerCapability> StartAsync(long generation, CancellationToken cancellationToken)
        {
            StartCount++;
            var active = Interlocked.Increment(ref _activeWorkers);
            UpdateMaximumActiveWorkers(active);
            var gate = StartGates is { Count: > 0 } ? StartGates.Peek() : StartGate;
            if (gate is not null)
            {
                try
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }
                catch
                {
                    Interlocked.Decrement(ref _activeWorkers);
                    throw;
                }
            }
            return new EngineWorkerCapability(
                EngineWorkerClient.ProtocolVersion,
                generation + CapabilityGenerationOffset,
                true,
                false,
                false,
                false,
                ExecutionSupported);
        }

        public async Task SendAsync(EngineWorkerMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            OnSend?.Invoke(message);
            if (message.Kind == "cancel" && CancelSendGate is not null)
            {
                await CancelSendGate.Task.WaitAsync(cancellationToken);
            }
        }

        public async Task TerminateAsync(CancellationToken cancellationToken)
        {
            TerminateCount++;
            if (TerminateGate is not null)
            {
                await TerminateGate.Task.WaitAsync(cancellationToken);
            }
            if (ThrowOnTerminate)
            {
                throw new InvalidOperationException("termination failed");
            }
            if (Volatile.Read(ref _activeWorkers) > 0)
            {
                Interlocked.Decrement(ref _activeWorkers);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Emit(EngineWorkerMessage message) => MessageReceived?.Invoke(this, message);

        private void UpdateMaximumActiveWorkers(int active)
        {
            var current = Volatile.Read(ref _maxActiveWorkers);
            while (active > current)
            {
                var observed = Interlocked.CompareExchange(ref _maxActiveWorkers, active, current);
                if (observed == current)
                {
                    return;
                }
                current = observed;
            }
        }
    }
}
