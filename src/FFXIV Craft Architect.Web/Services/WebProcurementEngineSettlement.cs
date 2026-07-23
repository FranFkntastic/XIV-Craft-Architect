using System.Diagnostics;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record WebProcurementSettlementRegistration(
    EngineRequestEnvelope EngineRequest,
    CraftingPlan OriginalPlan,
    long PlanSessionVersion,
    long PlanDecisionVersion,
    long MarketAnalysisVersion,
    ProcurementRoutePublicationBasis RouteBasis,
    OperationGateLease OperationGateLease);

public sealed class WebProcurementEngineSettlement :
    IEngineTransactionSettlement,
    IEngineExecutionContextRegistrar
{
    private static readonly EnginePhase[] OrderedPhases =
    [
        EnginePhase.Publishing,
        EnginePhase.SettlingRoute,
        EnginePhase.Persisting,
        EnginePhase.SettlingUi,
        EnginePhase.CapturingPostActionEvidence,
        EnginePhase.ReleasingGate
    ];
    private readonly AppState _appState;
    private readonly IndexedDbService _indexedDb;
    private readonly IReferenceEngineSemanticSnapshotProvider _snapshots;
    private readonly WebProcurementSettlementRegistration _registration;
    private readonly ILogger<WebProcurementEngineSettlement>? _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly object _contextSync = new();
    private readonly object _timingSync = new();
    private readonly Dictionary<EnginePhase, DeliveredPhase> _delivered = [];
    private readonly Dictionary<EnginePhase, long> _phaseElapsedMilliseconds = [];
    private readonly string _requestHash;
    private string? _cleanupInvocationToken;
    private EngineExecutionContextRegistration? _executionContext;
    private bool _routeWasApplied;
    private AppStateVersionSnapshot? _appliedVersions;
    private string? _pendingPersistenceFailure;

    public WebProcurementEngineSettlement(
        AppState appState,
        IndexedDbService indexedDb,
        IReferenceEngineSemanticSnapshotProvider snapshots,
        WebProcurementSettlementRegistration registration,
        ILogger<WebProcurementEngineSettlement>? logger = null)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _indexedDb = indexedDb ?? throw new ArgumentNullException(nameof(indexedDb));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _logger = logger;
        if (_registration.EngineRequest.TransactionId == Guid.Empty ||
            !_registration.OperationGateLease.IsHeld())
        {
            throw new ArgumentException("A live procurement settlement registration is required.", nameof(registration));
        }
        _requestHash = EngineCanonicalHash.ComputeRequestIdentity(_registration.EngineRequest);
    }

    public EngineInvocationCleanupOwnership? TryRegisterInvocationCleanupOwnership(
        EngineInvocationCleanupRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.Request.TransactionId != _registration.EngineRequest.TransactionId ||
            string.IsNullOrWhiteSpace(registration.InvocationToken))
        {
            throw new InvalidOperationException("The procurement cleanup registration is invalid.");
        }
        lock (_contextSync)
        {
            if (!_registration.OperationGateLease.IsHeld())
            {
                return null;
            }
            if (_cleanupInvocationToken is null)
            {
                _cleanupInvocationToken = registration.InvocationToken;
            }
            else if (!string.Equals(
                         _cleanupInvocationToken,
                         registration.InvocationToken,
                         StringComparison.Ordinal))
            {
                return null;
            }
            return new EngineInvocationCleanupOwnership(registration.InvocationToken, _requestHash);
        }
    }

    public IReadOnlyDictionary<EnginePhase, long> SettlementPhaseElapsedMilliseconds
    {
        get
        {
            lock (_timingSync)
            {
                return _phaseElapsedMilliseconds.ToDictionary();
            }
        }
    }

    public AutoSavePerformanceTiming? AutoSavePerformanceTiming =>
        _indexedDb.LastAutoSavePerformanceTiming;

    public void RegisterExecutionContext(EngineExecutionContextRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ValidateRegisteredRequest(registration.Request, registration.RequestHash);
        if (string.IsNullOrWhiteSpace(registration.InvocationToken) ||
            string.IsNullOrWhiteSpace(registration.ClaimToken))
        {
            throw new InvalidOperationException("The procurement execution context has no ownership identity.");
        }
        lock (_contextSync)
        {
            if (!string.Equals(
                    _cleanupInvocationToken,
                    registration.InvocationToken,
                    StringComparison.Ordinal) ||
                !_registration.OperationGateLease.IsHeld())
            {
                throw new InvalidOperationException("The procurement execution does not own the operation gate.");
            }
            if (_executionContext is { } existing)
            {
                if (existing.Generation == registration.Generation &&
                    existing.ExecutionId == registration.ExecutionId &&
                    string.Equals(existing.RequestHash, registration.RequestHash, StringComparison.Ordinal) &&
                    string.Equals(existing.InvocationToken, registration.InvocationToken, StringComparison.Ordinal) &&
                    string.Equals(existing.ClaimToken, registration.ClaimToken, StringComparison.Ordinal))
                {
                    return;
                }
                throw new InvalidOperationException("The procurement execution context is already bound.");
            }
            _executionContext = registration;
        }
    }

    public async Task<EngineSettlementEvidence> SettleAsync(
        EnginePhase phase,
        EngineSettlementContext context,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            ValidateContext(phase, context, requireComputation: phase != EnginePhase.ReleasingGate);
            if (_delivered.TryGetValue(phase, out var delivered))
            {
                EnsureSameDelivery(delivered, context);
                return delivered.Evidence;
            }
            var expectedIndex = _delivered.Count;
            if (phase != EnginePhase.ReleasingGate &&
                (expectedIndex >= OrderedPhases.Length || OrderedPhases[expectedIndex] != phase))
            {
                throw new InvalidOperationException($"Procurement settlement phase '{phase}' is out of order.");
            }

            var elapsed = Stopwatch.StartNew();
            try
            {
                _logger?.LogInformation("[stage] procurement settlement {Phase} starting", phase);
                var evidence = phase switch
                {
                    EnginePhase.Publishing => ValidateComputation(context),
                    EnginePhase.SettlingRoute => ApplyRoute(context),
                    EnginePhase.Persisting => await PersistAsync(cancellationToken),
                    EnginePhase.SettlingUi => ValidateVisibleRoute(context),
                    EnginePhase.CapturingPostActionEvidence => CapturePostActionEvidence(context),
                    EnginePhase.ReleasingGate => ReleaseGate(),
                    _ => throw new NotSupportedException($"Unsupported procurement settlement phase '{phase}'.")
                };
                _delivered.Add(phase, new DeliveredPhase(context.PhaseDeliveryId, evidence));
                _logger?.LogInformation(
                    "[stage] procurement settlement {Phase} complete ({ElapsedMilliseconds}ms)",
                    phase,
                    elapsed.ElapsedMilliseconds);
                return evidence;
            }
            finally
            {
                elapsed.Stop();
                lock (_timingSync)
                {
                    _phaseElapsedMilliseconds[phase] = elapsed.ElapsedMilliseconds;
                }
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<EngineSettlementEvidence> ObserveAsync(
        EnginePhase phase,
        EngineSettlementContext context,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            ValidateContext(phase, context, requireComputation: phase != EnginePhase.ReleasingGate);
            if (_delivered.TryGetValue(phase, out var delivered))
            {
                EnsureSameDelivery(delivered, context);
                return delivered.Evidence;
            }
            if (phase == EnginePhase.ReleasingGate && !_registration.OperationGateLease.IsHeld())
            {
                var evidence = new EngineSettlementEvidence(
                    EngineSettlementOutcome.Applied,
                    RollbackUncommittedRoute() ? "gate-released;route-rolled-back" : "gate-released");
                _delivered[phase] = new DeliveredPhase(context.PhaseDeliveryId, evidence);
                return evidence;
            }
            return new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied");
        }
        finally
        {
            _mutex.Release();
        }
    }

    private EngineSettlementEvidence ValidateComputation(EngineSettlementContext context)
    {
        _ = GetRouteResult(context);
        return new EngineSettlementEvidence(
            EngineSettlementOutcome.Applied,
            context.Computation!.ProcurementRouteResultHash);
    }

    private EngineSettlementEvidence ApplyRoute(EngineSettlementContext context)
    {
        EnsureLiveBasis();
        var route = GetRouteResult(context);
        var activeItems = route.ActiveProcurementItems
            ?? AcquisitionPlanningService.GetActiveProcurementItems(_registration.OriginalPlan);
        if (!_appState.ApplyProcurementRoute(
                _registration.OriginalPlan,
                activeItems,
                route.ShoppingPlans,
                route.RouteDecision,
                route.EvidenceAnalyses,
                route.EvidencePlans,
                _appState.CreateMarketAnalysisScopeSnapshot(
                    _appState.ProcurementSearchEntireRegion
                        ? MarketFetchScope.EntireRegion
                        : MarketFetchScope.SelectedDataCenter)))
        {
            throw new InvalidOperationException("The procurement route became stale before publication.");
        }
        _routeWasApplied = true;
        _appliedVersions = _appState.CurrentVersions;
        return new EngineSettlementEvidence(
            EngineSettlementOutcome.Applied,
            context.Computation!.ProcurementRouteResultHash);
    }

    private async Task<EngineSettlementEvidence> PersistAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outcome = await _indexedDb.AutoSaveStateWithOutcomeAsync(
            _appState,
            skipIfInFlight: false,
            allowDuringEngineMemoryPressure: true);
        if (outcome is not (AutoSaveStateOutcome.Saved or AutoSaveStateOutcome.AlreadyPersisted))
        {
            _pendingPersistenceFailure = "The computed procurement route could not be saved and was discarded.";
            throw new InvalidOperationException("The settled procurement route could not be durably autosaved.");
        }
        return new EngineSettlementEvidence(
            EngineSettlementOutcome.Committed,
            outcome == AutoSaveStateOutcome.Saved
                ? "autosave-committed"
                : "autosave-already-committed");
    }

    private EngineSettlementEvidence ValidateVisibleRoute(EngineSettlementContext context)
    {
        if (_appState.ProcurementRouteDecision is null ||
            _appState.ProcurementRouteValidity != ProcurementRoutePublicationValidity.Current)
        {
            throw new InvalidOperationException("The settled procurement route is not current in AppState.");
        }
        return new EngineSettlementEvidence(
            EngineSettlementOutcome.Applied,
            context.Computation!.ProcurementRouteResultHash);
    }

    private EngineSettlementEvidence CapturePostActionEvidence(EngineSettlementContext context)
    {
        var expected = GetRouteResult(context).RouteDecision
            ?? throw new InvalidOperationException("The settled procurement route has no decision evidence.");
        var actual = _appState.ProcurementRouteDecision
            ?? throw new InvalidOperationException("AppState lost the settled procurement route decision.");
        if (expected.SelectedGilCost != actual.SelectedGilCost ||
            expected.RouteSearchWasTruncated != actual.RouteSearchWasTruncated)
        {
            throw new InvalidOperationException("Visible procurement evidence differs from the Worker result.");
        }
        return new EngineSettlementEvidence(
            EngineSettlementOutcome.Applied,
            context.Computation!.ProcurementRouteResultHash);
    }

    private EngineSettlementEvidence ReleaseGate()
    {
        var rolledBack = RollbackUncommittedRoute();
        if (rolledBack && !string.IsNullOrWhiteSpace(_pendingPersistenceFailure))
        {
            _appState.MarkProcurementRouteFailed(_pendingPersistenceFailure);
        }
        if (_registration.OperationGateLease.IsHeld() && !_registration.OperationGateLease.Release())
        {
            throw new InvalidOperationException("The procurement operation gate could not be released.");
        }
        return new EngineSettlementEvidence(
            EngineSettlementOutcome.Applied,
            rolledBack ? "gate-released;route-rolled-back" : "gate-released");
    }

    private ProcurementRouteExecutionResult GetRouteResult(EngineSettlementContext context)
    {
        var transported = context.Computation?.ValidatedTransportedResult;
        if (transported is null && context.Computation?.Result is { } result)
        {
            transported = _snapshots.CaptureTransportedResult(result);
        }
        if (transported?.ProcurementRouteResult is not { IsComplete: true } route)
        {
            throw new InvalidOperationException("The Worker did not return a complete publishable procurement result.");
        }
        if (route.EvidencePlans.Count > 0 || route.EvidenceAnalyses is { Count: > 0 })
        {
            throw new InvalidOperationException(
                "Worker procurement settlement requires an already-published compact evidence result.");
        }
        return route;
    }

    private bool RollbackUncommittedRoute()
    {
        if (!_delivered.ContainsKey(EnginePhase.SettlingRoute) ||
            _delivered.TryGetValue(EnginePhase.Persisting, out var persistence) &&
            persistence.Evidence.Outcome == EngineSettlementOutcome.Committed ||
            !_routeWasApplied ||
            _appliedVersions is null)
        {
            return false;
        }
        return _appState.RollbackProcurementRoute(
            _registration.OriginalPlan,
            _registration.PlanSessionVersion,
            _appliedVersions);
    }

    private void ValidateContext(
        EnginePhase phase,
        EngineSettlementContext context,
        bool requireComputation)
    {
        if (context.Request.TransactionId != _registration.EngineRequest.TransactionId ||
            !string.Equals(context.RequestHash, _requestHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The procurement settlement request identity is invalid.");
        }
        var expectedDeliveryId = EngineCanonicalHash.Compute(new
        {
            Domain = "engine-settlement-delivery-v1",
            context.Request.TransactionId,
            RequestHash = _requestHash,
            Phase = phase
        });
        if (!string.Equals(context.PhaseDeliveryId, expectedDeliveryId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The procurement settlement delivery identity is invalid.");
        }

        EngineExecutionContextRegistration? execution;
        string? cleanupInvocationToken;
        lock (_contextSync)
        {
            execution = _executionContext;
            cleanupInvocationToken = _cleanupInvocationToken;
        }
        if (string.IsNullOrWhiteSpace(context.InvocationToken) ||
            !string.Equals(context.InvocationToken, cleanupInvocationToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The procurement settlement invocation does not own the operation gate.");
        }
        if (!requireComputation)
        {
            if (execution is null)
            {
                if (context.Computation is not null || context.ClaimToken is not null)
                {
                    throw new InvalidOperationException("The procurement cleanup context has stale execution identity.");
                }
                return;
            }
            if (!string.Equals(context.ClaimToken, execution.ClaimToken, StringComparison.Ordinal) ||
                context.Computation is { } cleanupComputation &&
                (cleanupComputation.Generation != execution.Generation ||
                 cleanupComputation.ExecutionId != execution.ExecutionId ||
                 cleanupComputation.TransactionId != context.Request.TransactionId))
            {
                throw new InvalidOperationException("The procurement cleanup context has stale execution identity.");
            }
            return;
        }
        if (execution is null ||
            !ReferenceEquals(context.Request, execution.Request) ||
            !string.Equals(context.ClaimToken, execution.ClaimToken, StringComparison.Ordinal) ||
            context.Computation is not { Status: EngineComputationStatus.Completed } computation ||
            computation.TransactionId != context.Request.TransactionId ||
            computation.Generation != execution.Generation ||
            computation.ExecutionId != execution.ExecutionId)
        {
            throw new InvalidOperationException("The procurement settlement context has no validated execution ownership.");
        }
    }

    private void ValidateRegisteredRequest(EngineRequestEnvelope request, string? claimedHash = null)
    {
        var actualHash = EngineCanonicalHash.ComputeRequestIdentity(request);
        if (request.TransactionId != _registration.EngineRequest.TransactionId ||
            !string.Equals(actualHash, _requestHash, StringComparison.Ordinal) ||
            claimedHash is not null && !string.Equals(claimedHash, _requestHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The procurement settlement request does not match its registration.");
        }
    }

    private static void EnsureSameDelivery(DeliveredPhase delivered, EngineSettlementContext context)
    {
        if (!string.Equals(delivered.DeliveryId, context.PhaseDeliveryId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A procurement phase cannot be replayed with a different delivery identity.");
        }
    }

    private void EnsureLiveBasis()
    {
        if (!_appState.IsCurrentPlanSession(
                _registration.OriginalPlan,
                _registration.PlanSessionVersion) ||
            _appState.CurrentVersions.PlanDecisionVersion != _registration.PlanDecisionVersion ||
            _appState.CurrentVersions.MarketAnalysisVersion != _registration.MarketAnalysisVersion ||
            !_registration.RouteBasis.Matches(_appState.CreateCurrentProcurementRouteBasis()))
        {
            throw new InvalidOperationException("The procurement settlement basis is stale.");
        }
    }

    private sealed record DeliveredPhase(string DeliveryId, EngineSettlementEvidence Evidence);
}
