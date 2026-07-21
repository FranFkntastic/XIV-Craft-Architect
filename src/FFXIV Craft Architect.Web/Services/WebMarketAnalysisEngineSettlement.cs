using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record WebEngineSettlementCapability(
    bool IsReady,
    bool HasDurableLedger,
    string UnsupportedReason);

public sealed record WebMarketAnalysisExpectedSemanticHashes(
    string EngineAnalysisResultHash,
    string? PublishedAppStateAnalysisHash = null);

public sealed class OperationGateLeaseId
{
    internal OperationGateLeaseId()
    {
    }
}

public sealed class OperationGateLease
{
    private readonly Func<bool> _isHeld;
    private readonly Func<bool> _release;

    private OperationGateLease(
        OperationGateLeaseId leaseId,
        Func<bool> isHeld,
        Func<bool> release)
    {
        LeaseId = leaseId;
        _isHeld = isHeld;
        _release = release;
    }

    public OperationGateLeaseId LeaseId { get; }

    public static OperationGateLease Create(Func<bool> isHeld, Func<bool> release)
    {
        ArgumentNullException.ThrowIfNull(isHeld);
        ArgumentNullException.ThrowIfNull(release);
        return new OperationGateLease(new OperationGateLeaseId(), isHeld, release);
    }

    public OperationGateLease Wrap(Func<bool> isHeld, Func<bool> release)
    {
        ArgumentNullException.ThrowIfNull(isHeld);
        ArgumentNullException.ThrowIfNull(release);
        return new OperationGateLease(LeaseId, isHeld, release);
    }

    internal bool IsHeld() => _isHeld();

    internal bool Release() => _release();
}

public sealed record WebEngineRejectedGateCleanupEvidence(
    Guid CleanupId,
    Guid TransactionId,
    int Attempts,
    bool Released,
    bool GateMayBeHeld,
    bool AdmittedOwnerObserved,
    IReadOnlyList<string> Failures);

public sealed class WebEngineRegistryAdmissionException : InvalidOperationException
{
    public WebEngineRegistryAdmissionException(
        Exception admissionFailure,
        WebEngineRejectedGateCleanupEvidence cleanupEvidence)
        : base("Web engine registry admission failed and the rejected gate lease remains retained for cleanup retry.", admissionFailure)
    {
        CleanupEvidence = cleanupEvidence;
    }

    public WebEngineRejectedGateCleanupEvidence CleanupEvidence { get; }
}

public sealed record WebMarketAnalysisSettlementRegistration(
    EngineRequestEnvelope EngineRequest,
    MarketAnalysisPublicationRequest PublicationRequest,
    MarketAnalysisPersistenceSnapshot PersistenceSnapshot,
    MarketAnalysisExecutionResult AnalysisResult,
    WebMarketAnalysisExpectedSemanticHashes ExpectedSemanticHashes,
    WebMarketAnalysisAppStateBinding AppStateBinding,
    OperationGateLease OperationGateLease);

public sealed record WebMarketAnalysisAppStateBinding(
    long PlanSessionVersion,
    long PlanStructureVersion,
    long PlanDecisionVersion,
    long PlanPriceVersion,
    long PlanCoreVersion,
    long MarketAnalysisVersion,
    long SettingsVersion,
    string? PlanId,
    string? PlanName,
    RecommendationMode RecommendationMode,
    MarketAcquisitionLens MarketAnalysisLens,
    string PlanSemanticHash,
    string SessionSemanticHash,
    string RootIntentHash);

public sealed class WebEngineTransactionContextRegistry
{
    private const int RejectedGateCleanupAttempts = 3;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, RegisteredTransaction> _transactions = [];
    private readonly Dictionary<Guid, RejectedGateCleanup> _rejectedGateCleanups = [];
    private readonly int _capacity;
    private long _accessSequence;

    public WebEngineTransactionContextRegistry(int capacity = 128)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public void Register(WebMarketAnalysisSettlementRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var incomingLease = registration.OperationGateLease;
        try
        {
            ArgumentNullException.ThrowIfNull(registration.EngineRequest);
            ArgumentNullException.ThrowIfNull(registration.ExpectedSemanticHashes);
            ArgumentNullException.ThrowIfNull(incomingLease);
            if (registration.EngineRequest.TransactionId == Guid.Empty)
            {
                throw new ArgumentException("The transaction id is required.", nameof(registration));
            }
            if (string.IsNullOrWhiteSpace(registration.ExpectedSemanticHashes.EngineAnalysisResultHash))
            {
                throw new ArgumentException("The expected analysis semantic hash is required.", nameof(registration));
            }

            var persistence = MarketAnalysisPublicationService.SnapshotPersistence(
                registration.PersistenceSnapshot);
            var publication = persistence.ToPublicationRequest(registration.PublicationRequest.Plan);
            var snapshot = registration with
            {
                PublicationRequest = publication,
                PersistenceSnapshot = persistence,
                AnalysisResult = registration.AnalysisResult with
                {
                    Analyses = publication.Analyses.ToList(),
                    ShoppingPlans = publication.ShoppingPlans
                }
            };
            var requestHash = EngineCanonicalHash.Compute(
                snapshot.EngineRequest,
                EngineJsonSerializerOptions.CreateWire());
            var transaction = new RegisteredTransaction(snapshot, requestHash);
            lock (_sync)
            {
                if (_transactions.TryGetValue(snapshot.EngineRequest.TransactionId, out var existing))
                {
                    if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal) ||
                        !SamePublicationContext(existing.Registration, snapshot))
                    {
                        if (string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal) &&
                            existing.CanReplaceRegistration)
                        {
                            _transactions[snapshot.EngineRequest.TransactionId] = transaction;
                            transaction.LastAccess = ++_accessSequence;
                            return;
                        }
                        throw new InvalidOperationException("The transaction id is already bound to a different settlement context.");
                    }

                    existing.LastAccess = ++_accessSequence;
                    return;
                }

                if (_transactions.Count >= _capacity)
                {
                    var evictable = _transactions
                        .Where(pair => pair.Value.IsTerminal)
                        .OrderBy(pair => pair.Value.LastAccess)
                        .FirstOrDefault();
                    if (evictable.Value is null)
                    {
                        throw new InvalidOperationException("The Web engine settlement registry has reached capacity.");
                    }
                    _transactions.Remove(evictable.Key);
                }

                transaction.LastAccess = ++_accessSequence;
                _transactions.Add(snapshot.EngineRequest.TransactionId, transaction);
            }
        }
        catch (Exception admissionFailure)
        {
            if (incomingLease is not null)
            {
                var cleanup = RetainAndTryCleanupRejectedRegistrationGate(
                    registration.EngineRequest?.TransactionId ?? Guid.Empty,
                    incomingLease);
                if (!cleanup.Released && !cleanup.AdmittedOwnerObserved)
                {
                    throw new WebEngineRegistryAdmissionException(admissionFailure, cleanup);
                }
            }
            throw;
        }
    }

    public WebEngineRejectedGateCleanupEvidence RetryRejectedRegistrationGateCleanup(Guid cleanupId)
    {
        RejectedGateCleanup cleanup;
        lock (_sync)
        {
            if (!_rejectedGateCleanups.TryGetValue(cleanupId, out cleanup!))
            {
                throw new InvalidOperationException("The rejected gate cleanup is no longer pending.");
            }
        }

        return TryCleanupRejectedRegistrationGate(cleanup);
    }

    internal EngineInvocationCleanupOwnership? TryRegisterInvocationCleanupOwnership(
        EngineInvocationCleanupRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (string.IsNullOrWhiteSpace(registration.InvocationToken))
        {
            throw new ArgumentException("The invocation cleanup token is required.", nameof(registration));
        }
        lock (_sync)
        {
            if (!_transactions.TryGetValue(registration.Request.TransactionId, out var transaction))
            {
                throw new InvalidOperationException("The engine transaction has no registered Web settlement context.");
            }
            if (transaction.IsTerminal || transaction.CleanupInvocationToken is not null)
            {
                return null;
            }

            transaction.CleanupInvocationToken = registration.InvocationToken;
            transaction.CleanupClaimToken = null;
            transaction.LastAccess = ++_accessSequence;
            return new EngineInvocationCleanupOwnership(registration.InvocationToken, transaction.RequestHash);
        }
    }

    internal void RegisterExecutionContext(
        EngineExecutionContextRegistration registration,
        WebMarketAnalysisAppStateBinding liveBinding)
    {
        BindExecutionOwnership(registration);
        lock (_sync)
        {
            var transaction = _transactions[registration.Request.TransactionId];
            transaction.ExpectedLiveBinding = liveBinding;
            transaction.LastAccess = ++_accessSequence;
        }
    }

    internal void BindExecutionOwnership(EngineExecutionContextRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_sync)
        {
            if (!_transactions.TryGetValue(registration.Request.TransactionId, out var transaction))
            {
                throw new InvalidOperationException("The engine transaction has no registered Web settlement context.");
            }
            if (!string.Equals(transaction.RequestHash, registration.RequestHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The host execution context does not match its registered Web transaction.");
            }
            if (string.IsNullOrWhiteSpace(registration.InvocationToken) ||
                string.IsNullOrWhiteSpace(registration.ClaimToken))
            {
                throw new InvalidOperationException("The host execution context is missing its invocation or claim fence.");
            }
            if (transaction.ExecutionContext is { } existing &&
                (existing.Generation != registration.Generation || existing.ExecutionId != registration.ExecutionId) &&
                transaction.IsTerminal)
            {
                throw new InvalidOperationException("A terminal Web settlement context cannot be rebound to another execution.");
            }

            transaction.ExecutionContext = registration;
            transaction.CleanupInvocationToken = registration.InvocationToken;
            transaction.CleanupClaimToken = registration.ClaimToken;
            transaction.LastAccess = ++_accessSequence;
        }
    }

    internal RegisteredTransaction GetRequired(Guid transactionId)
    {
        lock (_sync)
        {
            if (!_transactions.TryGetValue(transactionId, out var transaction))
            {
                throw new InvalidOperationException("The engine transaction has no registered Web settlement context.");
            }
            transaction.LastAccess = ++_accessSequence;
            return transaction;
        }
    }

    private static bool SamePublicationContext(
        WebMarketAnalysisSettlementRegistration left,
        WebMarketAnalysisSettlementRegistration right) =>
        ReferenceEquals(left.PublicationRequest.Plan, right.PublicationRequest.Plan) &&
        left.PublicationRequest.PlanSessionVersion == right.PublicationRequest.PlanSessionVersion &&
        left.PublicationRequest.PlanDecisionVersion == right.PublicationRequest.PlanDecisionVersion &&
        string.Equals(left.PublicationRequest.PlanId, right.PublicationRequest.PlanId, StringComparison.Ordinal) &&
        string.Equals(
            WebMarketAnalysisEngineTransactionSettlement.ComputePublicationPayloadHash(left.PersistenceSnapshot),
            WebMarketAnalysisEngineTransactionSettlement.ComputePublicationPayloadHash(right.PersistenceSnapshot),
            StringComparison.Ordinal) &&
        Equals(left.ExpectedSemanticHashes, right.ExpectedSemanticHashes) &&
        Equals(left.AppStateBinding, right.AppStateBinding) &&
        ReferenceEquals(left.OperationGateLease.LeaseId, right.OperationGateLease.LeaseId);

    private WebEngineRejectedGateCleanupEvidence RetainAndTryCleanupRejectedRegistrationGate(
        Guid transactionId,
        OperationGateLease lease)
    {
        RejectedGateCleanup cleanup;
        lock (_sync)
        {
            cleanup = new RejectedGateCleanup(
                Guid.NewGuid(),
                transactionId,
                lease);
            _rejectedGateCleanups.Add(cleanup.CleanupId, cleanup);
        }

        return TryCleanupRejectedRegistrationGate(cleanup);
    }

    private WebEngineRejectedGateCleanupEvidence TryCleanupRejectedRegistrationGate(RejectedGateCleanup cleanup)
    {
        lock (cleanup)
        {
            var failures = new List<string>();
            for (var attempt = 0; attempt < RejectedGateCleanupAttempts; attempt++)
            {
                lock (_sync)
                {
                    if (!_rejectedGateCleanups.TryGetValue(cleanup.CleanupId, out var pending) ||
                        !ReferenceEquals(pending, cleanup))
                    {
                        return CreateRejectedGateCleanupEvidence(cleanup);
                    }
                    cleanup.Attempts++;
                    if (IsLeaseAdmittedLocked(cleanup.Lease.LeaseId))
                    {
                        cleanup.Failures.AddRange(failures);
                        cleanup.Failures.Add("lease-owned-by-admitted-registration");
                        cleanup.AdmittedOwnerObserved = true;
                        _rejectedGateCleanups.Remove(cleanup.CleanupId);
                        return CreateRejectedGateCleanupEvidence(cleanup);
                    }

                    try
                    {
                        if (!cleanup.Lease.IsHeld())
                        {
                            cleanup.Failures.AddRange(failures);
                            return CompleteRejectedGateCleanupLocked(cleanup);
                        }

                        if (!cleanup.Lease.Release())
                        {
                            failures.Add("release-not-acknowledged");
                        }
                        if (!cleanup.Lease.IsHeld())
                        {
                            cleanup.Failures.AddRange(failures);
                            return CompleteRejectedGateCleanupLocked(cleanup);
                        }
                        failures.Add("gate-still-held");
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{ex.GetType().Name}:{ex.Message}");
                    }
                }
            }

            cleanup.Failures.AddRange(failures);
            return CreateRejectedGateCleanupEvidence(cleanup);
        }
    }

    private bool IsLeaseAdmittedLocked(OperationGateLeaseId leaseId) =>
        _transactions.Values.Any(transaction =>
            ReferenceEquals(transaction.Registration.OperationGateLease.LeaseId, leaseId));

    private WebEngineRejectedGateCleanupEvidence CompleteRejectedGateCleanupLocked(
        RejectedGateCleanup cleanup)
    {
        cleanup.Released = true;
        _rejectedGateCleanups.Remove(cleanup.CleanupId);
        return CreateRejectedGateCleanupEvidence(cleanup);
    }

    private static WebEngineRejectedGateCleanupEvidence CreateRejectedGateCleanupEvidence(
        RejectedGateCleanup cleanup) =>
        new(
            cleanup.CleanupId,
            cleanup.TransactionId,
            cleanup.Attempts,
            cleanup.Released,
            !cleanup.Released,
            cleanup.AdmittedOwnerObserved,
            cleanup.Failures.ToArray());

    internal sealed class RegisteredTransaction
    {
        public RegisteredTransaction(WebMarketAnalysisSettlementRegistration registration, string requestHash)
        {
            Registration = registration;
            RequestHash = requestHash;
            PersistenceSnapshotHash = WebMarketAnalysisEngineTransactionSettlement.ComputePublicationPayloadHash(
                registration.PersistenceSnapshot);
        }

        public WebMarketAnalysisSettlementRegistration Registration { get; }
        public string RequestHash { get; }
        public string PersistenceSnapshotHash { get; }
        public EngineExecutionContextRegistration? ExecutionContext { get; set; }
        public string? CleanupInvocationToken { get; set; }
        public string? CleanupClaimToken { get; set; }
        public WebMarketAnalysisAppStateBinding? ExpectedLiveBinding { get; set; }
        public long LastAccess { get; set; }
        public SemaphoreSlim Mutex { get; } = new(1, 1);
        public Dictionary<EnginePhase, DeliveredPhase> DeliveredPhases { get; } = [];
        public MarketAnalysisPublication? Publication { get; set; }
        public string? PublishedAnalysisHash { get; set; }
        public string? PublicationPayloadHash { get; set; }
        public string? PublishedAppStatePayloadHash { get; set; }
        public bool NamedPlanPersisted { get; set; }
        public bool AutoSavePersisted { get; set; }
        public bool PersistenceIndeterminate { get; set; }
        public bool PublicationSemanticMismatch { get; set; }
        public PublicationAttempt? PendingPublication { get; set; }
        public bool IsTerminal => DeliveredPhases.ContainsKey(EnginePhase.ReleasingGate);
        public bool CanReplaceRegistration =>
            ExecutionContext is null &&
            (DeliveredPhases.Count == 0 || DeliveredPhases.Keys.All(phase => phase == EnginePhase.ReleasingGate)) &&
            Publication is null &&
            !NamedPlanPersisted &&
            !AutoSavePersisted &&
            !PersistenceIndeterminate;
    }

    internal sealed record DeliveredPhase(string DeliveryId, EngineSettlementEvidence Evidence);
    internal sealed record PublicationAttempt(
        WebMarketAnalysisAppStateBinding Before,
        PreparedMarketAnalysisPublication Prepared,
        string PublicationPayloadHash);

    private sealed class RejectedGateCleanup(
        Guid cleanupId,
        Guid transactionId,
        OperationGateLease lease)
    {
        public Guid CleanupId { get; } = cleanupId;
        public Guid TransactionId { get; } = transactionId;
        public OperationGateLease Lease { get; } = lease;
        public int Attempts { get; set; }
        public bool Released { get; set; }
        public bool AdmittedOwnerObserved { get; set; }
        public List<string> Failures { get; } = [];
    }
}

public sealed class WebMarketAnalysisEngineTransactionSettlement :
    IEngineTransactionSettlement,
    IEngineExecutionContextRegistrar
{
    private static readonly EnginePhase[] OrderedPhases =
    [
        EnginePhase.Publishing,
        EnginePhase.Persisting,
        EnginePhase.SettlingUi,
        EnginePhase.CapturingPostActionEvidence,
        EnginePhase.ReleasingGate
    ];

    private readonly AppState _appState;
    private readonly WebEngineTransactionContextRegistry _registry;
    private readonly MarketAnalysisPublicationService _publicationService;
    private readonly IReferenceEngineSemanticSnapshotProvider _snapshots;
    private readonly bool _allowTestExecution;

    public WebMarketAnalysisEngineTransactionSettlement(
        AppState appState,
        WebEngineTransactionContextRegistry registry,
        MarketAnalysisPublicationService publicationService,
        IReferenceEngineSemanticSnapshotProvider snapshots)
        : this(appState, registry, publicationService, snapshots, allowTestExecution: false)
    {
    }

    private WebMarketAnalysisEngineTransactionSettlement(
        AppState appState,
        WebEngineTransactionContextRegistry registry,
        MarketAnalysisPublicationService publicationService,
        IReferenceEngineSemanticSnapshotProvider snapshots,
        bool allowTestExecution)
    {
        _appState = appState;
        _registry = registry;
        _publicationService = publicationService;
        _snapshots = snapshots;
        _allowTestExecution = allowTestExecution;
        Capability = new WebEngineSettlementCapability(
            false,
            false,
            "A durable Web settlement ledger is required before this adapter can be enabled.");
    }

    public WebEngineSettlementCapability Capability { get; }

    internal static WebMarketAnalysisEngineTransactionSettlement CreateForTesting(
        AppState appState,
        WebEngineTransactionContextRegistry registry,
        MarketAnalysisPublicationService publicationService,
        IReferenceEngineSemanticSnapshotProvider snapshots) =>
        new(appState, registry, publicationService, snapshots, allowTestExecution: true);

    public EngineInvocationCleanupOwnership? TryRegisterInvocationCleanupOwnership(
        EngineInvocationCleanupRegistration registration)
    {
        EnsureCapabilityReady();
        return _registry.TryRegisterInvocationCleanupOwnership(registration);
    }

    public void RegisterExecutionContext(EngineExecutionContextRegistration registration)
    {
        EnsureCapabilityReady();
        ArgumentNullException.ThrowIfNull(registration);
        var transaction = _registry.GetRequired(registration.Request.TransactionId);
        var isInitialContext = transaction.ExecutionContext is null;
        _registry.BindExecutionOwnership(registration);
        var liveBinding = CaptureLiveBinding(registration.Request);
        if (isInitialContext)
        {
            ValidateInitialLiveBinding(transaction, liveBinding);
        }
        else
        {
            ValidateLiveBinding(transaction);
        }
        _registry.RegisterExecutionContext(registration, liveBinding);
    }

    public async Task<EngineSettlementEvidence> SettleAsync(
        EnginePhase phase,
        EngineSettlementContext context,
        CancellationToken cancellationToken)
    {
        EnsureCapabilityReady();
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = _registry.GetRequired(context.Request.TransactionId);
        await transaction.Mutex.WaitAsync(cancellationToken);
        try
        {
            ValidateContext(
                transaction,
                context,
                requireComputation: phase != EnginePhase.ReleasingGate,
                validatePublicationState: phase != EnginePhase.ReleasingGate);
            if (transaction.DeliveredPhases.TryGetValue(phase, out var delivered))
            {
                EnsureSameDelivery(delivered, context);
                return delivered.Evidence;
            }

            ValidatePhaseOrder(transaction, phase);
            var evidence = phase switch
            {
                EnginePhase.Publishing => Publish(transaction, context, cancellationToken),
                EnginePhase.Persisting => await PersistAsync(transaction, context, cancellationToken),
                EnginePhase.SettlingUi => Recapture(transaction, "ui-settled"),
                EnginePhase.CapturingPostActionEvidence => Recapture(transaction, "post-action-captured"),
                EnginePhase.ReleasingGate => ReleaseGate(transaction),
                _ => throw new NotSupportedException(
                    $"Web market-analysis settlement does not handle phase '{phase}'.")
            };
            if (evidence.Outcome != EngineSettlementOutcome.NotApplied)
            {
                transaction.DeliveredPhases.Add(
                    phase,
                    new WebEngineTransactionContextRegistry.DeliveredPhase(context.PhaseDeliveryId, evidence));
            }
            return evidence;
        }
        finally
        {
            transaction.Mutex.Release();
        }
    }

    public async Task<EngineSettlementEvidence> ObserveAsync(
        EnginePhase phase,
        EngineSettlementContext context,
        CancellationToken cancellationToken)
    {
        EnsureCapabilityReady();
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = _registry.GetRequired(context.Request.TransactionId);
        await transaction.Mutex.WaitAsync(cancellationToken);
        try
        {
            ValidateContext(
                transaction,
                context,
                requireComputation: phase != EnginePhase.ReleasingGate,
                validatePublicationState: phase is not (EnginePhase.Publishing or EnginePhase.ReleasingGate));
            if (transaction.DeliveredPhases.TryGetValue(phase, out var delivered))
            {
                EnsureSameDelivery(delivered, context);
                return delivered.Evidence;
            }
            if (phase == EnginePhase.Publishing)
            {
                var observed = ObservePublication(transaction);
                RecordObservedPhase(transaction, phase, context, observed);
                return observed;
            }
            if (phase == EnginePhase.Persisting &&
                (transaction.PersistenceIndeterminate ||
                 transaction.NamedPlanPersisted && !transaction.AutoSavePersisted))
            {
                throw new InvalidOperationException(
                    "Persistence produced a durable or potentially durable effect without a complete commit acknowledgement.");
            }
            if (phase == EnginePhase.ReleasingGate)
            {
                var observed = ObserveGate(transaction);
                RecordObservedPhase(transaction, phase, context, observed);
                return observed;
            }

            return new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "not-applied");
        }
        finally
        {
            transaction.Mutex.Release();
        }
    }

    private EngineSettlementEvidence Publish(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction,
        EngineSettlementContext context,
        CancellationToken cancellationToken)
    {
        var registration = transaction.Registration;
        var sourceHash = CaptureAnalysisHash(registration.AnalysisResult);
        if (!string.Equals(sourceHash, registration.ExpectedSemanticHashes.EngineAnalysisResultHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The registered market-analysis candidate has a stale semantic hash.");
        }

        if (transaction.Publication != null)
        {
            throw new InvalidOperationException("A failed publication delivery cannot apply AppState a second time.");
        }

        var prepared = _publicationService.Prepare(
            registration.PublicationRequest,
            registration.PersistenceSnapshot,
            cancellationToken)
            ?? throw new InvalidOperationException("The market-analysis publication context became stale.");
        var publicationPayloadHash = ComputePublicationPayloadHash(prepared.PersistenceSnapshot);
        transaction.PublicationPayloadHash = publicationPayloadHash;
        RejectUnversionedPreparedPlanMutation(transaction, context);
        transaction.PendingPublication = new WebEngineTransactionContextRegistry.PublicationAttempt(
            transaction.ExpectedLiveBinding!,
            prepared,
            publicationPayloadHash);
        EnsurePublicationPayloadUnchanged(transaction);
        var publication = _publicationService.PublishPrepared(prepared, cancellationToken);
        transaction.Publication = publication;

        var appStateHash = CaptureAppStateAnalysisHash(registration.AnalysisResult);
        var publicationHash = CaptureAnalysisHash(registration.AnalysisResult with
        {
            Analyses = prepared.Request.Analyses.ToList(),
            ShoppingPlans = prepared.Request.ShoppingPlans
        });
        transaction.PublishedAnalysisHash = appStateHash;
        if (!string.Equals(appStateHash, publicationHash, StringComparison.Ordinal) ||
            registration.ExpectedSemanticHashes.PublishedAppStateAnalysisHash is { } expected &&
            !string.Equals(appStateHash, expected, StringComparison.Ordinal))
        {
            transaction.PublicationSemanticMismatch = true;
            throw new InvalidOperationException("AppState does not match the authoritative published analysis semantic hash.");
        }

        ValidateAndAdvancePublishedBinding(transaction, transaction.PendingPublication);
        var appStatePayloadHash = CaptureAppStatePublicationPayloadHash(prepared.PersistenceSnapshot);
        transaction.PublishedAppStatePayloadHash = appStatePayloadHash;
        if (!string.Equals(appStatePayloadHash, publicationPayloadHash, StringComparison.Ordinal))
        {
            transaction.PublicationSemanticMismatch = true;
            throw new InvalidOperationException("AppState does not match the exact registered publication payload.");
        }

        ValidateContext(transaction, context);
        return new EngineSettlementEvidence(EngineSettlementOutcome.Applied, $"app-state:{appStatePayloadHash}");
    }

    private async Task<EngineSettlementEvidence> PersistAsync(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction,
        EngineSettlementContext context,
        CancellationToken cancellationToken)
    {
        var publication = transaction.Publication
            ?? throw new InvalidOperationException("Market analysis must be published before persistence.");
        if (_publicationService.ShouldPersistNamedPlan(publication) && !transaction.NamedPlanPersisted)
        {
            ValidateContext(transaction, context);
            EnsurePublicationPayloadUnchanged(transaction);
            bool saved;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                saved = await _publicationService.PersistNamedPlanAsync(publication);
                ValidateContext(transaction, context);
                EnsurePublicationPayloadUnchanged(transaction);
            }
            catch
            {
                transaction.PersistenceIndeterminate = true;
                throw;
            }
            if (!saved)
            {
                transaction.PersistenceIndeterminate = true;
                throw new InvalidOperationException("Named-plan market-analysis persistence was not acknowledged.");
            }

            transaction.NamedPlanPersisted = true;
            transaction.PersistenceIndeterminate = true;
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (!transaction.AutoSavePersisted)
        {
            ValidateContext(transaction, context);
            EnsurePublicationPayloadUnchanged(transaction);
            bool saved;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                saved = await _publicationService.AutoSaveAsync(publication);
                ValidateContext(transaction, context);
                EnsurePublicationPayloadUnchanged(transaction);
            }
            catch
            {
                transaction.PersistenceIndeterminate = true;
                throw;
            }
            if (!saved)
            {
                transaction.PersistenceIndeterminate = true;
                throw new InvalidOperationException("Market-analysis autosave was not acknowledged.");
            }

            transaction.AutoSavePersisted = true;
            transaction.PersistenceIndeterminate = true;
        }

        transaction.PersistenceIndeterminate = false;
        ValidateContext(transaction, context);
        return new EngineSettlementEvidence(
            EngineSettlementOutcome.Committed,
            transaction.NamedPlanPersisted ? "named-plan-and-autosave-committed" : "autosave-committed");
    }

    private EngineSettlementEvidence Recapture(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction,
        string evidence)
    {
        var expected = transaction.PublishedAnalysisHash
            ?? throw new InvalidOperationException("Published AppState semantics have not been captured.");
        var actual = CaptureAppStateAnalysisHash(transaction.Registration.AnalysisResult);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AppState market-analysis semantics changed during transaction settlement.");
        }

        EnsurePublicationPayloadUnchanged(transaction);

        return new EngineSettlementEvidence(
            EngineSettlementOutcome.Applied,
            $"{evidence}:{transaction.PublishedAppStatePayloadHash}");
    }

    private EngineSettlementEvidence ReleaseGate(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction)
    {
        var gate = transaction.Registration.OperationGateLease;
        if (!gate.IsHeld())
        {
            return new EngineSettlementEvidence(EngineSettlementOutcome.Applied, "operation-gate-already-released");
        }

        var released = gate.Release();
        return released || !gate.IsHeld()
            ? new EngineSettlementEvidence(EngineSettlementOutcome.Applied, "operation-gate-released")
            : new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "operation-gate-still-held");
    }

    private static EngineSettlementEvidence ObserveGate(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction) =>
        transaction.Registration.OperationGateLease.IsHeld()
            ? new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "operation-gate-still-held")
            : new EngineSettlementEvidence(EngineSettlementOutcome.Applied, "operation-gate-release-observed");

    private static void RecordObservedPhase(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction,
        EnginePhase phase,
        EngineSettlementContext context,
        EngineSettlementEvidence observed)
    {
        if (observed.Outcome != EngineSettlementOutcome.NotApplied)
        {
            transaction.DeliveredPhases[phase] =
                new WebEngineTransactionContextRegistry.DeliveredPhase(context.PhaseDeliveryId, observed);
        }
    }

    private EngineSettlementEvidence ObservePublication(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction)
    {
        if (transaction.PublicationSemanticMismatch)
        {
            throw new InvalidOperationException(
                "The applied AppState publication did not match its registered semantic expectation.");
        }
        if (transaction.Publication != null)
        {
            return new EngineSettlementEvidence(EngineSettlementOutcome.Applied, "publication-observed");
        }
        if (transaction.PendingPublication is not { } before)
        {
            return new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "publication-not-applied");
        }

        var registration = transaction.Registration;
        var versions = _appState.CurrentVersions;
        if (versions.MarketAnalysisVersion <= before.Before.MarketAnalysisVersion)
        {
            transaction.PendingPublication = null;
            return new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "publication-not-applied");
        }

        var expectedHash = CaptureAnalysisHash(registration.AnalysisResult with
        {
            Analyses = before.Prepared.Request.Analyses.ToList(),
            ShoppingPlans = before.Prepared.Request.ShoppingPlans
        });
        var actualHash = CaptureAppStateAnalysisHash(registration.AnalysisResult);
        if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal) ||
            registration.ExpectedSemanticHashes.PublishedAppStateAnalysisHash is { } publishedExpected &&
            !string.Equals(publishedExpected, actualHash, StringComparison.Ordinal))
        {
            transaction.PublicationSemanticMismatch = true;
            throw new InvalidOperationException(
                "AppState changed during publication but does not authoritatively match the registered publication.");
        }

        ValidateAndAdvancePublishedBinding(transaction, before);
        var appStatePayloadHash = CaptureAppStatePublicationPayloadHash(before.Prepared.PersistenceSnapshot);
        if (!string.Equals(appStatePayloadHash, before.PublicationPayloadHash, StringComparison.Ordinal))
        {
            transaction.PublicationSemanticMismatch = true;
            throw new InvalidOperationException(
                "AppState changed during publication but does not match the exact registered payload.");
        }

        transaction.Publication = new MarketAnalysisPublication(
            before.Prepared.Request,
            before.Prepared.ChangedDecisionCount,
            versions.PlanDecisionVersion,
            before.Prepared.PersistenceSnapshot);
        transaction.PublishedAnalysisHash = actualHash;
        transaction.PublishedAppStatePayloadHash = appStatePayloadHash;
        return new EngineSettlementEvidence(EngineSettlementOutcome.Applied, $"publication-observed:{appStatePayloadHash}");
    }

    private void ValidateContext(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction,
        EngineSettlementContext context,
        bool requireComputation = true,
        bool validatePublicationState = true)
    {
        var registration = transaction.Registration;
        var publication = registration.PublicationRequest;
        if (string.IsNullOrWhiteSpace(context.InvocationToken) ||
            !string.Equals(context.InvocationToken, transaction.CleanupInvocationToken, StringComparison.Ordinal) ||
            !string.Equals(context.ClaimToken, transaction.CleanupClaimToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The settlement invocation does not own the registered operation gate.");
        }
        if (context.Request.TransactionId != registration.EngineRequest.TransactionId ||
            !string.Equals(context.RequestHash, transaction.RequestHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The settlement request does not match its registered transaction context.");
        }
        if (!requireComputation)
        {
            if (context.Computation is { } gateComputation &&
                (transaction.ExecutionContext is not { } gateExecution ||
                 gateComputation.TransactionId != context.Request.TransactionId ||
                 gateComputation.Generation != gateExecution.Generation ||
                 gateComputation.ExecutionId != gateExecution.ExecutionId))
            {
                throw new InvalidOperationException("The settlement computation generation is stale.");
            }
            return;
        }
        if (transaction.ExecutionContext is not { } execution ||
            context.Computation is not { Status: EngineComputationStatus.Completed } computation ||
            computation.TransactionId != context.Request.TransactionId ||
            computation.Generation != execution.Generation ||
            computation.ExecutionId != execution.ExecutionId)
        {
            throw new InvalidOperationException("The settlement computation generation is stale or incomplete.");
        }
        if (!string.Equals(
                computation.AnalysisResultHash,
                registration.ExpectedSemanticHashes.EngineAnalysisResultHash,
                StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(computation.ProcurementRouteResultHash))
        {
            throw new InvalidOperationException("The computation semantic hashes do not match the registered analysis-only request.");
        }
        if (computation.Result is not { } result ||
            _snapshots.CaptureTransportedResult(result) is not { MarketAnalysis: { } transported, ProcurementRoute: null } ||
            !string.Equals(
                EngineSemanticSnapshotHash.Analysis(transported),
                registration.ExpectedSemanticHashes.EngineAnalysisResultHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The transported market-analysis payload does not match its semantic hash.");
        }
        if (!validatePublicationState)
        {
            return;
        }
        ValidateLiveBinding(transaction);
        if (!ScopeMatches(publication.PublishedScope, _appState.CreateCurrentMarketAnalysisScopeSnapshot(
                publication.PublishedScope.PublishedAtUtc)))
        {
            throw new InvalidOperationException("The registered market scope is stale.");
        }
        if (transaction.PublishedAnalysisHash is { } expected &&
            !string.Equals(
                CaptureAppStateAnalysisHash(registration.AnalysisResult),
                expected,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AppState market-analysis semantics no longer match the registered transaction.");
        }
        if (transaction.PublishedAppStatePayloadHash is not null)
        {
            EnsurePublicationPayloadUnchanged(transaction);
        }
    }

    private string CaptureAppStateAnalysisHash(MarketAnalysisExecutionResult basis) =>
        CaptureAnalysisHash(basis with
        {
            Analyses = _appState.MarketItemAnalyses.ToList(),
            ShoppingPlans = _appState.ShoppingPlans.ToList()
        });

    private string CaptureAnalysisHash(MarketAnalysisExecutionResult result) =>
        EngineSemanticSnapshotHash.Analysis(_snapshots.CaptureAnalysis(result));

    internal static string ComputePublicationPayloadHash(MarketAnalysisPersistenceSnapshot snapshot) =>
        EngineCanonicalHash.Compute(snapshot, EngineJsonSerializerOptions.CreateWire());

    private string CaptureAppStatePublicationPayloadHash(MarketAnalysisPersistenceSnapshot snapshot)
    {
        var versions = _appState.CurrentVersions;
        return ComputePublicationPayloadHash(snapshot with
        {
            SettingsVersion = versions.SettingsVersion,
            PlanSessionVersion = _appState.PlanSessionVersion,
            PlanDecisionVersion = versions.PlanDecisionVersion,
            PlanId = _appState.CurrentPlanId,
            PlanName = _appState.CurrentPlanName,
            DataCenter = _appState.SelectedDataCenter,
            ProjectItems = _appState.ProjectItems.Select(item => new StoredProjectItem
            {
                Id = item.Id,
                Name = item.Name,
                IconId = item.IconId,
                Quantity = item.Quantity,
                MustBeHq = item.MustBeHq
            }).ToArray(),
            Plan = _appState.CurrentPlan
                ?? throw new InvalidOperationException("AppState has no current plan."),
            Analyses = _appState.MarketItemAnalyses.ToArray(),
            ShoppingPlans = _appState.ShoppingPlans.ToArray(),
            RecipeBasis = _appState.MarketAnalysisRecipeBasis,
            PublishedScope = _appState.PublishedMarketAnalysisScope
                ?? throw new InvalidOperationException("AppState has no published market-analysis scope."),
            RecommendationMode = _appState.RecommendationMode,
            MarketAnalysisLens = _appState.MarketAnalysisLens,
            MarketIntelligence = _appState.MarketIntelligence
        });
    }

    private void EnsurePublicationPayloadUnchanged(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction)
    {
        var expected = transaction.PublicationPayloadHash
            ?? throw new InvalidOperationException("The exact publication payload has not been bound.");
        var snapshot = transaction.Publication?.PersistenceSnapshot
            ?? transaction.PendingPublication?.Prepared.PersistenceSnapshot
            ?? transaction.Registration.PersistenceSnapshot;
        if (!string.Equals(ComputePublicationPayloadHash(snapshot), expected, StringComparison.Ordinal) ||
            !string.Equals(expected, transaction.PersistenceSnapshotHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The prepared market-analysis publication payload was mutated.");
        }
        if (transaction.PublishedAppStatePayloadHash is { } published &&
            (!string.Equals(published, expected, StringComparison.Ordinal) ||
             !string.Equals(CaptureAppStatePublicationPayloadHash(snapshot), expected, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("AppState no longer matches the exact publication payload.");
        }
    }

    private WebMarketAnalysisAppStateBinding CaptureLiveBinding(EngineRequestEnvelope request)
    {
        var plan = _appState.CurrentPlan
            ?? throw new InvalidOperationException("A current plan is required for Web engine settlement.");
        var prepared = _snapshots.PrepareInput(request);
        var rootIntentHash = EngineSemanticSnapshotHash.RootIntent(prepared.RootIntent);
        var planSemanticHash = ComputePlanSemanticHash(plan);
        var versions = _appState.CurrentVersions;
        var sessionSemanticHash = ComputeSessionSemanticHash(_appState, planSemanticHash, rootIntentHash);
        return new WebMarketAnalysisAppStateBinding(
            _appState.PlanSessionVersion,
            versions.PlanStructureVersion,
            versions.PlanDecisionVersion,
            versions.PlanPriceVersion,
            versions.PlanCoreVersion,
            versions.MarketAnalysisVersion,
            versions.SettingsVersion,
            _appState.CurrentPlanId,
            _appState.CurrentPlanName,
            _appState.RecommendationMode,
            _appState.MarketAnalysisLens,
            planSemanticHash,
            sessionSemanticHash,
            rootIntentHash);
    }

    internal static string ComputePlanSemanticHash(CraftingPlan plan) =>
        EngineCanonicalHash.Compute(plan, EngineJsonSerializerOptions.CreateWire());

    internal static string ComputeSessionSemanticHash(
        AppState appState,
        string planSemanticHash,
        string rootIntentHash)
    {
        var plan = appState.CurrentPlan
            ?? throw new InvalidOperationException("A current plan is required for Web engine settlement.");
        var versions = appState.CurrentVersions;
        return EngineCanonicalHash.Compute(new
        {
            Domain = "web-app-state-session-v1",
            PlanObjectId = plan.Id,
            appState.CurrentPlanId,
            appState.CurrentPlanName,
            appState.PlanSessionVersion,
            versions.PlanStructureVersion,
            versions.PlanDecisionVersion,
            versions.PlanPriceVersion,
            versions.PlanCoreVersion,
            versions.MarketAnalysisVersion,
            versions.SettingsVersion,
            appState.RecommendationMode,
            appState.MarketAnalysisLens,
            PlanSemanticHash = planSemanticHash,
            RootIntentHash = rootIntentHash
        });
    }

    private void ValidateInitialLiveBinding(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction,
        WebMarketAnalysisAppStateBinding actual)
    {
        var registration = transaction.Registration;
        var publication = registration.PublicationRequest;
        if (registration.AppStateBinding != actual ||
            !_appState.IsCurrentPlanSession(publication.Plan, publication.PlanSessionVersion) ||
            publication.PlanSessionVersion != actual.PlanSessionVersion ||
            publication.PlanDecisionVersion != actual.PlanDecisionVersion ||
            !string.Equals(publication.PlanId, actual.PlanId, StringComparison.Ordinal) ||
            !string.Equals(publication.PlanName, actual.PlanName, StringComparison.Ordinal) ||
            !string.Equals(registration.EngineRequest.RootIntentHash, actual.RootIntentHash, StringComparison.Ordinal) ||
            !string.Equals(registration.EngineRequest.Basis.Plan.Hash, actual.PlanSemanticHash, StringComparison.Ordinal) ||
            !string.Equals(registration.EngineRequest.Basis.Session.Hash, actual.SessionSemanticHash, StringComparison.Ordinal) ||
             !ScopeMatches(publication.PublishedScope, _appState.CreateCurrentMarketAnalysisScopeSnapshot(
                 publication.PublishedScope.PublishedAtUtc)))
        {
            throw new InvalidOperationException(
                "The engine request is not bound to the current plan, session, root intent, and market scope.");
        }
        EnsurePersistenceSourceUnchanged(registration.PersistenceSnapshot);
    }

    private void ValidateLiveBinding(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction)
    {
        var expected = transaction.ExpectedLiveBinding
            ?? throw new InvalidOperationException("The Web settlement has no bound AppState generation.");
        var actual = CaptureLiveBinding(transaction.Registration.EngineRequest);
        if (actual != expected ||
            !_appState.IsCurrentPlanSession(
                transaction.Registration.PublicationRequest.Plan,
                expected.PlanSessionVersion))
        {
            throw new InvalidOperationException("The complete AppState plan generation or semantic identity is stale.");
        }
    }

    private void RejectUnversionedPreparedPlanMutation(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction,
        EngineSettlementContext context)
    {
        var before = transaction.ExpectedLiveBinding
            ?? throw new InvalidOperationException("The Web settlement has no bound AppState generation.");
        var actual = CaptureLiveBinding(transaction.Registration.EngineRequest);
        if (context.Request.TransactionId != transaction.Registration.EngineRequest.TransactionId ||
            actual.PlanSessionVersion != before.PlanSessionVersion ||
            actual.PlanStructureVersion != before.PlanStructureVersion ||
            actual.PlanDecisionVersion != before.PlanDecisionVersion ||
            actual.PlanPriceVersion != before.PlanPriceVersion ||
            actual.PlanCoreVersion != before.PlanCoreVersion ||
            actual.MarketAnalysisVersion != before.MarketAnalysisVersion ||
            actual.SettingsVersion != before.SettingsVersion ||
            !string.Equals(actual.PlanId, before.PlanId, StringComparison.Ordinal) ||
            !string.Equals(actual.PlanName, before.PlanName, StringComparison.Ordinal) ||
            actual.RecommendationMode != before.RecommendationMode ||
            actual.MarketAnalysisLens != before.MarketAnalysisLens ||
            !string.Equals(actual.PlanSemanticHash, before.PlanSemanticHash, StringComparison.Ordinal) ||
            !string.Equals(actual.SessionSemanticHash, before.SessionSemanticHash, StringComparison.Ordinal) ||
            !string.Equals(actual.RootIntentHash, before.RootIntentHash, StringComparison.Ordinal) ||
            !_appState.IsCurrentPlanSession(
                transaction.Registration.PublicationRequest.Plan,
                before.PlanSessionVersion))
        {
            throw new InvalidOperationException("AppState changed while preparing the exact publication payload.");
        }

        EnsurePublicationPayloadUnchanged(transaction);
        EnsurePersistenceSourceUnchanged(transaction.Registration.PersistenceSnapshot);
    }

    private void ValidateAndAdvancePublishedBinding(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction,
        WebEngineTransactionContextRegistry.PublicationAttempt? attempt)
    {
        if (attempt is null)
        {
            throw new InvalidOperationException("The publication attempt has no bound pre-publication generation.");
        }
        var before = attempt.Before;
        var actual = CaptureLiveBinding(transaction.Registration.EngineRequest);
        if (actual.PlanSessionVersion != before.PlanSessionVersion ||
            actual.PlanStructureVersion != before.PlanStructureVersion ||
            actual.PlanDecisionVersion != before.PlanDecisionVersion ||
            actual.PlanPriceVersion != before.PlanPriceVersion ||
            actual.PlanCoreVersion != before.PlanCoreVersion ||
            actual.MarketAnalysisVersion != before.MarketAnalysisVersion + 1 ||
            actual.SettingsVersion != before.SettingsVersion ||
            !string.Equals(actual.PlanId, before.PlanId, StringComparison.Ordinal) ||
            !string.Equals(actual.PlanName, before.PlanName, StringComparison.Ordinal) ||
            actual.RecommendationMode != before.RecommendationMode ||
            actual.MarketAnalysisLens != before.MarketAnalysisLens ||
            !string.Equals(actual.PlanSemanticHash, before.PlanSemanticHash, StringComparison.Ordinal) ||
            !string.Equals(actual.RootIntentHash, before.RootIntentHash, StringComparison.Ordinal) ||
            !_appState.IsCurrentPlanSession(
                transaction.Registration.PublicationRequest.Plan,
                before.PlanSessionVersion))
        {
            transaction.PublicationSemanticMismatch = true;
            throw new InvalidOperationException("AppState advanced by an unexpected generation during publication.");
        }

        transaction.ExpectedLiveBinding = actual;
    }

    private void EnsurePersistenceSourceUnchanged(MarketAnalysisPersistenceSnapshot snapshot)
    {
        var expected = EngineCanonicalHash.Compute(new
        {
            snapshot.SettingsVersion,
            snapshot.PlanSessionVersion,
            snapshot.PlanDecisionVersion,
            snapshot.PlanId,
            snapshot.PlanName,
            snapshot.DataCenter,
            snapshot.ProjectItems,
            snapshot.Plan,
            snapshot.RecommendationMode,
            snapshot.MarketAnalysisLens,
            snapshot.MarketIntelligence.UnavailableMarketItems
        }, EngineJsonSerializerOptions.CreateWire());
        var versions = _appState.CurrentVersions;
        var actual = EngineCanonicalHash.Compute(new
        {
            SettingsVersion = versions.SettingsVersion,
            PlanSessionVersion = _appState.PlanSessionVersion,
            PlanDecisionVersion = versions.PlanDecisionVersion,
            PlanId = _appState.CurrentPlanId,
            PlanName = _appState.CurrentPlanName,
            DataCenter = _appState.SelectedDataCenter,
            ProjectItems = _appState.ProjectItems.Select(item => new StoredProjectItem
            {
                Id = item.Id,
                Name = item.Name,
                IconId = item.IconId,
                Quantity = item.Quantity,
                MustBeHq = item.MustBeHq
            }).ToArray(),
            Plan = _appState.CurrentPlan,
            RecommendationMode = _appState.RecommendationMode,
            MarketAnalysisLens = _appState.MarketAnalysisLens,
            UnavailableMarketItems = _appState.UnavailableMarketItems
        }, EngineJsonSerializerOptions.CreateWire());
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable AppState source no longer matches the registered persistence snapshot.");
        }
    }

    private void EnsureCapabilityReady()
    {
        if (!Capability.IsReady && !_allowTestExecution)
        {
            throw new NotSupportedException(Capability.UnsupportedReason);
        }
    }

    private static void ValidatePhaseOrder(
        WebEngineTransactionContextRegistry.RegisteredTransaction transaction,
        EnginePhase phase)
    {
        var phaseIndex = Array.IndexOf(OrderedPhases, phase);
        if (phaseIndex < 0)
        {
            throw new NotSupportedException($"Phase '{phase}' is outside analysis-only Web settlement.");
        }
        if (phase == EnginePhase.ReleasingGate)
        {
            return;
        }
        for (var index = 0; index < phaseIndex; index++)
        {
            if (!transaction.DeliveredPhases.ContainsKey(OrderedPhases[index]))
            {
                throw new InvalidOperationException(
                    $"Settlement phase '{phase}' arrived before '{OrderedPhases[index]}'.");
            }
        }
    }

    private static void EnsureSameDelivery(
        WebEngineTransactionContextRegistry.DeliveredPhase delivered,
        EngineSettlementContext context)
    {
        if (!string.Equals(delivered.DeliveryId, context.PhaseDeliveryId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A settled phase cannot be replayed with a different delivery identity.");
        }
    }

    private static bool ScopeMatches(
        PublishedMarketAnalysisScopeSnapshot expected,
        PublishedMarketAnalysisScopeSnapshot actual) =>
        expected.Scope == actual.Scope &&
        expected.Lens == actual.Lens &&
        expected.PlanSessionVersion == actual.PlanSessionVersion &&
        expected.PublishedAtUtc == actual.PublishedAtUtc &&
        string.Equals(expected.SelectedDataCenter, actual.SelectedDataCenter, StringComparison.Ordinal) &&
        string.Equals(expected.SelectedRegion, actual.SelectedRegion, StringComparison.Ordinal) &&
        expected.RequestedDataCenters.SequenceEqual(actual.RequestedDataCenters, StringComparer.Ordinal);
}
