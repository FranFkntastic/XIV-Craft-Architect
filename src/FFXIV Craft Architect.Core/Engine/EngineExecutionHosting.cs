using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FFXIV_Craft_Architect.Core.Engine;

public interface IEngineExecutionTransport
{
    EngineExecutionTransportCapability Capability { get; }

    Task<EngineComputationResult> ExecuteAsync(
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class InProcessEngineExecutionTransport : IEngineExecutionTransport
{
    private readonly IMarketProcurementEngine _engine;

    public InProcessEngineExecutionTransport(IMarketProcurementEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public EngineExecutionTransportCapability Capability { get; } =
        new(EngineExecutionTransportKind.InProcess, true);

    public Task<EngineComputationResult> ExecuteAsync(
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _engine.ComputeAsync(generation, executionId, request, progress, cancellationToken);
}

public sealed class UnsupportedBrowserWorkerEngineExecutionTransport : IEngineExecutionTransport
{
    public EngineExecutionTransportCapability Capability { get; } = new(
        EngineExecutionTransportKind.BrowserWorker,
        false,
        "The static browser worker does not host the .NET engine.");

    public Task<EngineComputationResult> ExecuteAsync(
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<EngineComputationResult>(new NotSupportedException(Capability.UnsupportedReason));
}

public sealed record EngineSettlementContext(
    EngineRequestEnvelope Request,
    EngineComputationResult? Computation,
    string RequestHash,
    string PhaseDeliveryId,
    string InvocationToken,
    string? ClaimToken);

public enum EngineSettlementOutcome
{
    NotApplied = 1,
    Applied = 2,
    Committed = 3
}

public sealed record EngineSettlementEvidence(EngineSettlementOutcome Outcome, string Evidence);

public interface IEngineTransactionSettlement
{
    // PhaseDeliveryId is stable across host recreation; implementations must apply each delivery idempotently.
    Task<EngineSettlementEvidence> SettleAsync(
        EnginePhase phase,
        EngineSettlementContext context,
        CancellationToken cancellationToken);

    Task<EngineSettlementEvidence> ObserveAsync(
        EnginePhase phase,
        EngineSettlementContext context,
        CancellationToken cancellationToken);
}

public sealed record EngineExecutionContextRegistration(
    long Generation,
    Guid ExecutionId,
    EngineRequestEnvelope Request,
    string RequestHash,
    string InvocationToken,
    string ClaimToken);

public sealed record EngineInvocationCleanupRegistration(
    EngineRequestEnvelope Request,
    string InvocationToken);

public sealed record EngineInvocationCleanupOwnership(
    string InvocationToken,
    string CanonicalRequestHash);

public interface IEngineExecutionContextRegistrar
{
    EngineInvocationCleanupOwnership? TryRegisterInvocationCleanupOwnership(
        EngineInvocationCleanupRegistration registration);

    void RegisterExecutionContext(EngineExecutionContextRegistration registration);
}

public sealed class NoOpEngineTransactionSettlement : IEngineTransactionSettlement
{
    public Task<EngineSettlementEvidence> SettleAsync(
        EnginePhase phase,
        EngineSettlementContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "unsupported"));
    }

    public Task<EngineSettlementEvidence> ObserveAsync(
        EnginePhase phase,
        EngineSettlementContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(new EngineSettlementEvidence(EngineSettlementOutcome.NotApplied, "unsupported"));
}

public sealed record EngineTransactionLedgerCapability(
    bool BindsCanonicalRequestIdentity,
    bool PreservesTerminalResult,
    bool IsDurable,
    bool PreservesTerminalIdentity = false);

public enum EngineTransactionClaimDisposition
{
    Claimed = 1,
    ActiveReplay = 2,
    TerminalReplay = 3,
    Conflict = 4,
    AbandonedReplay = 5,
    ExpiredTerminalReplay = 6
}

public sealed record EngineTransactionClaim(
    EngineTransactionClaimDisposition Disposition,
    string CanonicalRequestHash,
    EngineResultEnvelope? TerminalResult = null,
    string? ClaimToken = null);

public interface IEngineTransactionLedger
{
    EngineTransactionLedgerCapability Capability { get; }

    ValueTask<EngineTransactionClaim> ClaimAsync(
        Guid transactionId,
        string canonicalRequestHash,
        CancellationToken cancellationToken);

    ValueTask CompleteAsync(
        Guid transactionId,
        string canonicalRequestHash,
        string claimToken,
        EngineResultEnvelope terminalResult,
        CancellationToken cancellationToken);

    ValueTask ReleaseAsync(
        Guid transactionId,
        string canonicalRequestHash,
        string claimToken,
        CancellationToken cancellationToken);
}

// This implementation is suitable for tests and one-process composition only. Production registration requires a durable ledger.
public sealed class InMemoryEngineTransactionLedger : IEngineTransactionLedger
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, LedgerEntry> _entries = [];

    public EngineTransactionLedgerCapability Capability { get; } = new(true, true, false);

    public ValueTask<EngineTransactionClaim> ClaimAsync(
        Guid transactionId,
        string canonicalRequestHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_entries.TryGetValue(transactionId, out var existing))
            {
                var claimToken = Guid.NewGuid().ToString("N");
                _entries.Add(transactionId, new LedgerEntry(canonicalRequestHash, claimToken));
                return ValueTask.FromResult(new EngineTransactionClaim(
                    EngineTransactionClaimDisposition.Claimed,
                    canonicalRequestHash,
                    ClaimToken: claimToken));
            }

            if (!string.Equals(existing.CanonicalRequestHash, canonicalRequestHash, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(new EngineTransactionClaim(
                    EngineTransactionClaimDisposition.Conflict,
                    existing.CanonicalRequestHash));
            }

            return ValueTask.FromResult(existing.TerminalResult is null
                ? ClaimActiveEntry(existing, canonicalRequestHash)
                : new EngineTransactionClaim(
                    EngineTransactionClaimDisposition.TerminalReplay,
                    canonicalRequestHash,
                    existing.TerminalResult));
        }
    }

    public ValueTask CompleteAsync(
        Guid transactionId,
        string canonicalRequestHash,
        string claimToken,
        EngineResultEnvelope terminalResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminalResult);
        terminalResult = EngineEvidenceSnapshots.FreezeTerminal(terminalResult);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_entries.TryGetValue(transactionId, out var existing) ||
                !string.Equals(existing.CanonicalRequestHash, canonicalRequestHash, StringComparison.Ordinal) ||
                !string.Equals(existing.ClaimToken, claimToken, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The transaction ledger claim owner no longer matches its canonical request identity.");
            }

            if (existing.TerminalResult is not null &&
                !string.Equals(
                    existing.TerminalResult.Completion.FinalTransactionHash,
                    terminalResult.Completion.FinalTransactionHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The transaction ledger already contains a different terminal result.");
            }

            existing.TerminalResult ??= terminalResult;
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask ReleaseAsync(
        Guid transactionId,
        string canonicalRequestHash,
        string claimToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_entries.TryGetValue(transactionId, out var existing) ||
                !string.Equals(existing.CanonicalRequestHash, canonicalRequestHash, StringComparison.Ordinal) ||
                !string.Equals(existing.ClaimToken, claimToken, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The transaction ledger claim owner no longer matches its canonical request identity.");
            }
            if (existing.TerminalResult is not null)
            {
                throw new InvalidOperationException("A terminal transaction ledger entry cannot be released.");
            }

            _entries.Remove(transactionId);
            return ValueTask.CompletedTask;
        }
    }

    public void MarkClaimAbandoned(
        Guid transactionId,
        string canonicalRequestHash,
        string claimToken)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(transactionId, out var existing) ||
                !string.Equals(existing.CanonicalRequestHash, canonicalRequestHash, StringComparison.Ordinal) ||
                !string.Equals(existing.ClaimToken, claimToken, StringComparison.Ordinal) ||
                existing.TerminalResult is not null)
            {
                throw new InvalidOperationException("Only the current nonterminal claim owner can be marked abandoned.");
            }

            existing.ClaimToken = Guid.NewGuid().ToString("N");
            existing.IsAbandoned = true;
        }
    }

    private static EngineTransactionClaim ClaimActiveEntry(LedgerEntry existing, string canonicalRequestHash)
    {
        if (!existing.IsAbandoned)
        {
            return new EngineTransactionClaim(EngineTransactionClaimDisposition.ActiveReplay, canonicalRequestHash);
        }

        existing.IsAbandoned = false;
        return new EngineTransactionClaim(
            EngineTransactionClaimDisposition.AbandonedReplay,
            canonicalRequestHash,
            ClaimToken: existing.ClaimToken);
    }

    private sealed class LedgerEntry(string canonicalRequestHash, string claimToken)
    {
        public string CanonicalRequestHash { get; } = canonicalRequestHash;

        public string ClaimToken { get; set; } = claimToken;

        public EngineResultEnvelope? TerminalResult { get; set; }

        public bool IsAbandoned { get; set; }
    }
}

public sealed record EngineExecutionHostOptions(
    int CompletedExecutionCapacity,
    TimeSpan TerminalGateCleanupTimeout,
    int MaxConcurrentExecutions = 4,
    TimeSpan? ComputationTimeout = null,
    TimeSpan? SettlementPhaseTimeout = null,
    TimeSpan? LedgerWriteTimeout = null,
    TimeSpan? LedgerClaimTimeout = null,
    int MaxConcurrentCleanupOperations = 2,
    Func<CancellationToken, ValueTask>? CooperativeYield = null)
{
    public static EngineExecutionHostOptions Default { get; } = new(
        128,
        TimeSpan.FromSeconds(5),
        4,
        TimeSpan.FromMinutes(2),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(5));
}

public interface IEngineExecutionHost
{
    Task<EngineResultEnvelope> ExecuteAsync(
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class EngineExecutionHost : IEngineExecutionHost
{
    private const int MaximumGateReleaseAttempts = 2;
    private readonly IEngineExecutionTransport _transport;
    private readonly IEngineTransactionSettlement _settlement;
    private readonly IEngineTransactionLedger _ledger;
    private readonly IReferenceEngineSemanticSnapshotProvider _snapshots;
    private readonly EngineExecutionHostOptions _options;
    private readonly TimeSpan _computationTimeout;
    private readonly TimeSpan _settlementPhaseTimeout;
    private readonly TimeSpan _ledgerWriteTimeout;
    private readonly TimeSpan _ledgerClaimTimeout;
    private readonly Func<CancellationToken, ValueTask>? _cooperativeYield;
    private readonly SemaphoreSlim _executionSlots;
    private readonly SemaphoreSlim _cleanupSlots;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, HostedExecution> _executions = [];
    private long _generation;
    private long _accessSequence;

    private long _lastComputationValidationMilliseconds = -1;

    public long? LastComputationValidationMilliseconds
    {
        get
        {
            var value = Interlocked.Read(ref _lastComputationValidationMilliseconds);
            return value < 0 ? null : value;
        }
    }

    public EngineExecutionHost(
        IEngineExecutionTransport transport,
        IEngineTransactionSettlement settlement,
        IEngineTransactionLedger ledger,
        IReferenceEngineSemanticSnapshotProvider snapshots,
        EngineExecutionHostOptions? options = null)
        : this(transport, settlement, ledger, snapshots, options, allowNonDurableLedger: false)
    {
    }

    internal static EngineExecutionHost CreateForTesting(
        IEngineExecutionTransport transport,
        IEngineTransactionSettlement settlement,
        IEngineTransactionLedger ledger,
        IReferenceEngineSemanticSnapshotProvider snapshots,
        EngineExecutionHostOptions? options = null) =>
        new(transport, settlement, ledger, snapshots, options, allowNonDurableLedger: true);

    private EngineExecutionHost(
        IEngineExecutionTransport transport,
        IEngineTransactionSettlement settlement,
        IEngineTransactionLedger ledger,
        IReferenceEngineSemanticSnapshotProvider snapshots,
        EngineExecutionHostOptions? options,
        bool allowNonDurableLedger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _settlement = settlement ?? throw new ArgumentNullException(nameof(settlement));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _options = options ?? EngineExecutionHostOptions.Default;
        _computationTimeout = _options.ComputationTimeout ?? TimeSpan.FromMinutes(2);
        _settlementPhaseTimeout = _options.SettlementPhaseTimeout ?? TimeSpan.FromSeconds(30);
        _ledgerWriteTimeout = _options.LedgerWriteTimeout ?? TimeSpan.FromSeconds(5);
        _ledgerClaimTimeout = _options.LedgerClaimTimeout ?? _ledgerWriteTimeout;
        _cooperativeYield = _options.CooperativeYield;
        _executionSlots = new SemaphoreSlim(_options.MaxConcurrentExecutions, _options.MaxConcurrentExecutions);
        _cleanupSlots = new SemaphoreSlim(
            _options.MaxConcurrentCleanupOperations,
            _options.MaxConcurrentCleanupOperations);
        var ledgerCapability = _ledger.Capability ??
            throw new ArgumentException("The transaction ledger must declare its capabilities.", nameof(ledger));
        if (!ledgerCapability.BindsCanonicalRequestIdentity ||
            !ledgerCapability.PreservesTerminalResult && !ledgerCapability.PreservesTerminalIdentity)
        {
            throw new ArgumentException(
                "The transaction ledger must bind canonical requests and preserve terminal results or terminal identities.",
                nameof(ledger));
        }
        if (!ledgerCapability.IsDurable && !allowNonDurableLedger)
        {
            throw new ArgumentException("The transaction ledger must durably preserve claims and terminal results.", nameof(ledger));
        }
        if (_options.CompletedExecutionCapacity <= 0 ||
            _options.MaxConcurrentExecutions <= 0 ||
            _options.MaxConcurrentCleanupOperations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Execution capacities must be positive.");
        }
        if (_options.TerminalGateCleanupTimeout <= TimeSpan.Zero ||
            _computationTimeout <= TimeSpan.Zero ||
            _settlementPhaseTimeout <= TimeSpan.Zero ||
            _ledgerWriteTimeout <= TimeSpan.Zero ||
            _ledgerClaimTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Execution timeouts must be positive.");
        }
    }

    public Task<EngineResultEnvelope> ExecuteAsync(
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invocationToken = Guid.NewGuid().ToString("N");
        var registrar = _settlement as IEngineExecutionContextRegistrar;
        EngineInvocationCleanupOwnership? cleanupOwnership;
        try
        {
            cleanupOwnership = registrar?.TryRegisterInvocationCleanupOwnership(
                new EngineInvocationCleanupRegistration(request, invocationToken));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CreateRequestValidationFailureTerminal(
                request,
                "execution-context-registration-failed",
                ex));
        }

        string requestHash;
        try
        {
            requestHash = EngineCanonicalHash.ComputeRequestIdentity(request);
        }
        catch (Exception ex)
        {
            var fallbackHash = cleanupOwnership?.CanonicalRequestHash ??
                EngineCanonicalHash.ComputeRequestValidationFailureIdentity(request);
            var failure = CreateRequestValidationFailureTerminal(
                request,
                "canonical-request-validation-failed",
                ex);
            return CompletePreComputationOutcomeAsync(
                request,
                fallbackHash,
                failure,
                new ExecutionLease(static () => { }),
                invocationToken,
                claimToken: null,
                ownsCleanup: registrar is null || cleanupOwnership is not null,
                requiresCleanupAdmission: true,
                requestValidationFailure: true);
        }

        return BeginRegisteredExecutionAsync(
            request,
            requestHash,
            progress,
            cancellationToken,
            invocationToken,
            registrar,
            cleanupOwnership);
    }

    private Task<EngineResultEnvelope> BeginRegisteredExecutionAsync(
        EngineRequestEnvelope request,
        string requestHash,
        IProgress<EngineProgress>? progress,
        CancellationToken cancellationToken,
        string invocationToken,
        IEngineExecutionContextRegistrar? registrar,
        EngineInvocationCleanupOwnership? cleanupOwnership)
    {
        if (cleanupOwnership is not null &&
            !string.Equals(cleanupOwnership.CanonicalRequestHash, requestHash, StringComparison.Ordinal))
        {
            var conflict = CreateTerminal(
                request,
                null,
                EngineTerminalStatus.Failed,
                EnginePhase.Failed,
                EnginePhase.Accepted,
                "settlement-context-conflict",
                "The invocation cleanup context is bound to a different canonical engine request.",
                nameof(InvalidOperationException),
                new Dictionary<string, string>(StringComparer.Ordinal));
            return CompletePreComputationOutcomeAsync(
                request,
                cleanupOwnership.CanonicalRequestHash,
                conflict,
                new ExecutionLease(static () => { }),
                invocationToken,
                claimToken: null,
                ownsCleanup: true,
                requiresCleanupAdmission: true);
        }

        var ownsCleanup = registrar is null || cleanupOwnership is not null;
        lock (_sync)
        {
            if (_executions.TryGetValue(request.TransactionId, out var existing))
            {
                if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    var conflict = CreateTerminal(
                        request,
                        null,
                        EngineTerminalStatus.Failed,
                        EnginePhase.Failed,
                        EnginePhase.Accepted,
                        "transaction-id-conflict",
                        "A transaction id cannot be reused for a different canonical engine request.",
                        nameof(InvalidOperationException),
                        new Dictionary<string, string>(StringComparer.Ordinal));
                    return CompletePreComputationOutcomeAsync(
                        request,
                        requestHash,
                        conflict,
                        new ExecutionLease(static () => { }),
                        invocationToken,
                        claimToken: null,
                        ownsCleanup,
                        requiresCleanupAdmission: true);
                }

                existing.LastAccess = ++_accessSequence;
                return existing.Completion;
            }

            TrimCompletedExecutionsLocked(1);
            if (!_executionSlots.Wait(0))
            {
                var exhausted = CreateTerminal(
                    request,
                    null,
                    EngineTerminalStatus.Failed,
                    EnginePhase.Failed,
                    EnginePhase.Accepted,
                    "execution-capacity-exhausted",
                    "The engine host has reached its active execution limit.",
                    nameof(InvalidOperationException),
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    isRetryable: true);
                return CompletePreComputationOutcomeAsync(
                    request,
                    requestHash,
                    exhausted,
                    new ExecutionLease(static () => { }),
                    invocationToken,
                    claimToken: null,
                    ownsCleanup,
                    requiresCleanupAdmission: true);
            }

            var generation = ++_generation;
            var executionId = Guid.NewGuid();
            var lease = new ExecutionLease(() => _executionSlots.Release());
            var completion = BeginExecutionAsync(
                generation,
                executionId,
                requestHash,
                request,
                progress,
                cancellationToken,
                lease,
                invocationToken,
                ownsCleanup);
            _executions.Add(request.TransactionId, new HostedExecution(requestHash, completion, ++_accessSequence));
            _ = completion.ContinueWith(
                task => CompleteHostedExecution(request.TransactionId, task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return completion;
        }
    }

    private async Task<EngineResultEnvelope> BeginExecutionAsync(
        long generation,
        Guid executionId,
        string requestHash,
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress,
        CancellationToken cancellationToken,
        ExecutionLease lease,
        string invocationToken,
        bool ownsCleanup)
    {
        await Task.Yield();
        var isolatedProgress = progress is null ? null : new IsolatedProgressObserver<EngineProgress>(progress);
        try
        {
            try
            {
                var validatedRequestHash = await EngineCanonicalHash.ValidateAndComputeRequestIdentityAsync(
                    request,
                    _cooperativeYield,
                    CancellationToken.None);
                if (!string.Equals(validatedRequestHash, requestHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The validated engine request identity changed before execution.");
                }
            }
            catch (Exception ex)
            {
                var failure = CreateRequestValidationFailureTerminal(
                    request,
                    "canonical-request-validation-failed",
                    ex);
                return await CompletePreComputationOutcomeAsync(
                    request,
                    requestHash,
                    failure,
                    lease,
                    invocationToken,
                    claimToken: null,
                    ownsCleanup,
                    requestValidationFailure: true);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                var cancelled = CreateTerminal(
                    request,
                    null,
                    EngineTerminalStatus.Cancelled,
                    EnginePhase.Cancelled,
                    EnginePhase.Accepted,
                    "cancelled",
                    "The engine transaction was cancelled before its ledger claim.",
                    nameof(OperationCanceledException),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["replayStatus"] = "unclaimed-transient"
                    });
                return await CompletePreComputationOutcomeAsync(
                    request,
                    requestHash,
                    cancelled,
                    lease,
                    invocationToken,
                    claimToken: null,
                    ownsCleanup);
            }

            EngineTransactionClaim claim;
            try
            {
                claim = await InvokeBoundedAsync(
                    token => _ledger.ClaimAsync(request.TransactionId, requestHash, token).AsTask(),
                    _ledgerClaimTimeout,
                    CancellationToken.None,
                    lease,
                    "transaction-ledger-claim-indeterminate",
                    "The transaction ledger claim did not terminate before the deadline.");
                ValidateClaim(requestHash, claim);
            }
            catch (Exception ex)
            {
                var code = ex is EngineOperationIndeterminateException operation
                    ? operation.Code
                    : "transaction-ledger-claim-indeterminate";
                var indeterminate = CreateTerminal(
                    request,
                    null,
                    EngineTerminalStatus.Indeterminate,
                    EnginePhase.Indeterminate,
                    EnginePhase.Accepted,
                    code,
                    ex.Message,
                    ex.GetType().FullName ?? ex.GetType().Name,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["replayStatus"] = "ledger-claim-unknown"
                    },
                    isRetryable: true);
                return await CompletePreComputationOutcomeAsync(
                    request,
                    requestHash,
                    indeterminate,
                    lease,
                    invocationToken,
                    claimToken: null,
                    ownsCleanup);
            }

            if (claim.Disposition == EngineTransactionClaimDisposition.Conflict)
            {
                var conflict = CreateTerminal(
                    request,
                    null,
                    EngineTerminalStatus.Failed,
                    EnginePhase.Failed,
                    EnginePhase.Accepted,
                    "transaction-id-conflict",
                    "A transaction id is already bound to a different canonical engine request.",
                    nameof(InvalidOperationException),
                    new Dictionary<string, string>(StringComparer.Ordinal));
                return await CompletePreComputationOutcomeAsync(
                    request,
                    requestHash,
                    conflict,
                    lease,
                    invocationToken,
                    claimToken: null,
                    ownsCleanup);
            }
            if (claim.Disposition == EngineTransactionClaimDisposition.TerminalReplay)
            {
                var replay = ValidateLedgerReplay(request, claim.TerminalResult!);
                return await CompletePreComputationOutcomeAsync(
                    request,
                    requestHash,
                    replay,
                    lease,
                    invocationToken,
                    claimToken: null,
                    ownsCleanup);
            }
            if (claim.Disposition == EngineTransactionClaimDisposition.ExpiredTerminalReplay)
            {
                var expired = CreateTerminal(
                    request,
                    null,
                    EngineTerminalStatus.Indeterminate,
                    EnginePhase.Indeterminate,
                    EnginePhase.Accepted,
                    "transaction-replay-expired",
                    "The transaction completed previously, but its exact terminal payload has expired.",
                    nameof(InvalidOperationException),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["replayStatus"] = "terminal-payload-expired"
                    });
                return await CompletePreComputationOutcomeAsync(
                    request,
                    requestHash,
                    expired,
                    lease,
                    invocationToken,
                    claimToken: null,
                    ownsCleanup);
            }
            if (claim.Disposition == EngineTransactionClaimDisposition.ActiveReplay)
            {
                var active = CreateTerminal(
                    request,
                    null,
                    EngineTerminalStatus.Indeterminate,
                    EnginePhase.Indeterminate,
                    EnginePhase.Accepted,
                    "transaction-replay-in-progress",
                    "The exact transaction is already active, but this host cannot observe its terminal result yet.",
                    nameof(InvalidOperationException),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["replayStatus"] = "active-on-another-host"
                    },
                    isRetryable: true);
                return await CompletePreComputationOutcomeAsync(
                    request,
                    requestHash,
                    active,
                    lease,
                    invocationToken,
                    claimToken: null,
                    ownsCleanup);
            }

            var claimToken = claim.ClaimToken!;

            if (claim.Disposition == EngineTransactionClaimDisposition.AbandonedReplay)
            {
                var abandoned = CreateTerminal(
                    request,
                    null,
                    EngineTerminalStatus.Indeterminate,
                    EnginePhase.Indeterminate,
                    EnginePhase.Accepted,
                    "transaction-abandoned-after-crash",
                    "The durable ledger proved that the prior claim owner was abandoned after a crash.",
                    nameof(InvalidOperationException),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["replayStatus"] = "abandoned-after-crash"
                    });
                try
                {
                    await InvokeBoundedAsync(
                        token => _ledger.CompleteAsync(
                            request.TransactionId,
                            requestHash,
                            claimToken,
                            abandoned,
                            token).AsTask(),
                        _ledgerWriteTimeout,
                        CancellationToken.None,
                        lease,
                        "transaction-ledger-write-indeterminate",
                        "The abandoned transaction result could not be durably recorded before the deadline.");
                    return await CompletePreComputationOutcomeAsync(
                        request,
                        requestHash,
                        abandoned,
                        lease,
                        invocationToken,
                        claimToken: null,
                        ownsCleanup);
                }
                catch (Exception ex)
                {
                    return await CompletePreComputationOutcomeAsync(
                        request,
                        requestHash,
                        CreateLedgerWriteIndeterminate(request, abandoned, ex),
                        lease,
                        invocationToken,
                        claimToken: null,
                        ownsCleanup);
                }
            }

            var result = await ExecuteCoreAsync(
                generation,
                executionId,
                requestHash,
                request,
                isolatedProgress,
                cancellationToken,
                lease,
                invocationToken,
                claimToken);
            if (result.Failure?.Code == "gate-release-failed")
            {
                try
                {
                    await InvokeBoundedAsync(
                        token => _ledger.ReleaseAsync(request.TransactionId, requestHash, claimToken, token).AsTask(),
                        _ledgerWriteTimeout,
                        CancellationToken.None,
                        lease,
                        "gate-release-indeterminate",
                        "The failed gate-release claim could not be made retryable before the deadline.");
                    return result;
                }
                catch (Exception ex)
                {
                    return CreateGateReleaseClaimIndeterminate(request, result, ex);
                }
            }
            try
            {
                await InvokeBoundedAsync(
                    token => _ledger.CompleteAsync(request.TransactionId, requestHash, claimToken, result, token).AsTask(),
                    _ledgerWriteTimeout,
                    CancellationToken.None,
                    lease,
                    "transaction-ledger-write-indeterminate",
                    "The terminal transaction result could not be durably recorded before the deadline.");
                return result;
            }
            catch (Exception ex)
            {
                return CreateLedgerWriteIndeterminate(request, result, ex);
            }
        }
        finally
        {
            lease.ReleaseWhenSettled();
        }
    }

    private async Task<EngineResultEnvelope> ExecuteCoreAsync(
        long generation,
        Guid executionId,
        string requestHash,
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress,
        CancellationToken cancellationToken,
        ExecutionLease lease,
        string invocationToken,
        string claimToken)
    {
        var phase = EnginePhase.Accepted;
        var committed = false;
        var visibleEffects = false;
        var settlementStarted = false;
        var gateReleaseConfirmed = false;
        var gateReleaseIndeterminate = false;
        var gateReleaseAttempts = 0;
        var computationValidated = false;
        EngineComputationResult? computation = null;
        var evidence = new Dictionary<string, string>(StringComparer.Ordinal);

        async Task<bool> EnsureGateReleasedAsync()
        {
            if (gateReleaseConfirmed)
            {
                return true;
            }

            var context = CreateSettlementContext(
                request,
                computationValidated ? computation : null,
                requestHash,
                EnginePhase.ReleasingGate,
                invocationToken,
                claimToken);
            while (!gateReleaseConfirmed && gateReleaseAttempts < MaximumGateReleaseAttempts)
            {
                gateReleaseAttempts++;
                try
                {
                    var released = await DeliverSettlementAsync(
                        EnginePhase.ReleasingGate,
                        context,
                        CancellationToken.None,
                        _options.TerminalGateCleanupTimeout,
                        lease);
                    if (released.Outcome == EngineSettlementOutcome.Applied &&
                        !string.IsNullOrWhiteSpace(released.Evidence))
                    {
                        gateReleaseConfirmed = true;
                        gateReleaseIndeterminate = false;
                        evidence["delivery:ReleasingGate"] = context.PhaseDeliveryId;
                        evidence["cleanup:ReleasingGate"] = released.Evidence;
                        return true;
                    }

                    evidence["cleanup:ReleasingGate"] = $"failed:{released.Outcome}:{released.Evidence}";
                }
                catch (Exception ex)
                {
                    if (ex is EngineOperationIndeterminateException or EngineSettlementOutcomeUnknownException)
                    {
                        gateReleaseIndeterminate = true;
                    }
                    evidence["cleanup:ReleasingGate"] = $"failed:{ex.GetType().Name}";
                    if (gateReleaseIndeterminate)
                    {
                        break;
                    }
                }
            }

            return false;
        }

        try
        {
            if (_settlement is IEngineExecutionContextRegistrar registrar)
            {
                registrar.RegisterExecutionContext(new EngineExecutionContextRegistration(
                    generation,
                    executionId,
                    request,
                    requestHash,
                    invocationToken,
                    claimToken));
            }

            if (!_transport.Capability.IsSupported)
            {
                throw new NotSupportedException(
                    _transport.Capability.UnsupportedReason ??
                    $"Engine transport '{_transport.Capability.Kind}' is not supported.");
            }

            computation = await InvokeBoundedAsync(
                token => _transport.ExecuteAsync(generation, executionId, request, progress, token),
                _computationTimeout,
                cancellationToken,
                lease,
                "computation-timeout-indeterminate",
                "The engine computation did not terminate before its deadline.");
            await YieldCooperativelyAsync(cancellationToken);
            var validationElapsed = Stopwatch.StartNew();
            var preparedResult = EngineComputationResultValidation.PrepareCompletedResult(
                generation,
                executionId,
                request,
                computation,
                _snapshots);
            validationElapsed.Stop();
            if (preparedResult is not null)
            {
                await YieldCooperativelyAsync(cancellationToken);
            }
            validationElapsed.Start();
            computation = EngineComputationResultValidation.ValidatePrepared(
                generation,
                executionId,
                request,
                computation,
                _snapshots,
                preparedResult);
            validationElapsed.Stop();
            Interlocked.Exchange(ref _lastComputationValidationMilliseconds, validationElapsed.ElapsedMilliseconds);
            computationValidated = true;
            await YieldCooperativelyAsync(cancellationToken);
            foreach (var pair in computation.ComputationEvidence)
            {
                evidence[pair.Key] = pair.Value;
            }
            evidence["computationHash"] = computation.ComputationHash;

            if (computation.Status == EngineComputationStatus.Cancelled)
            {
                if (!await EnsureGateReleasedAsync())
                {
                    return CreateGateReleaseResult(
                        request,
                        computation,
                        computation.FinalPhase,
                        evidence,
                        gateReleaseIndeterminate);
                }
                return CreateTerminal(
                    request,
                    computation,
                    EngineTerminalStatus.Cancelled,
                    EnginePhase.Cancelled,
                    computation.FinalPhase,
                    "cancelled",
                    "The engine transaction was cancelled.",
                    nameof(OperationCanceledException),
                    evidence);
            }
            if (computation.Status == EngineComputationStatus.Failed)
            {
                var failure = computation.Failure!;
                if (!await EnsureGateReleasedAsync())
                {
                    return CreateGateReleaseResult(
                        request,
                        computation,
                        failure.FailedPhase,
                        evidence,
                        gateReleaseIndeterminate);
                }
                return CreateTerminal(
                    request,
                    computation,
                    EngineTerminalStatus.Failed,
                    EnginePhase.Failed,
                    failure.FailedPhase,
                    failure.Code,
                    failure.Message,
                    failure.FailureType,
                    evidence,
                    failure.IsRetryable);
            }

            var input = request.Input.Deserialize<ReferenceEngineInput>(EngineJsonSerializerOptions.CreateWire())
                ?? throw new InvalidOperationException("Cannot determine settlement phases for the engine request.");
            var requirements = EngineSuccessPhasePolicy.Resolve(
                request.InputKind,
                input.MarketAnalysis is not null,
                input.ProcurementRoute is not null);
            foreach (var settlementPhase in requirements.SettlementPhases)
            {
                phase = settlementPhase;
                var settlementToken = committed ? CancellationToken.None : cancellationToken;
                await YieldCooperativelyAsync(settlementToken, committed);
                settlementToken.ThrowIfCancellationRequested();
                progress?.Report(new EngineProgress(
                    request.TransactionId,
                    generation,
                    executionId,
                    phase,
                    ProgressIndex(phase),
                    12,
                    PhaseMessage(phase)));
                settlementStarted = true;
                if (phase == EnginePhase.ReleasingGate)
                {
                    if (!await EnsureGateReleasedAsync())
                    {
                        throw gateReleaseIndeterminate
                            ? new EngineOperationIndeterminateException(
                                "gate-release-indeterminate",
                                "The operation gate release outcome is indeterminate.")
                            : new EngineGateReleaseFailedException("The operation gate was not released after an idempotent retry.");
                    }
                    evidence["phase:ReleasingGate"] = evidence["cleanup:ReleasingGate"];
                    continue;
                }

                var context = CreateSettlementContext(
                    request,
                    computation,
                    requestHash,
                    phase,
                    invocationToken,
                    claimToken);
                var settled = await DeliverSettlementAsync(
                    phase,
                    context,
                    settlementToken,
                    _settlementPhaseTimeout,
                    lease);
                if (phase == EngineSuccessPhasePolicy.CommitPhase && settled.Outcome == EngineSettlementOutcome.Committed)
                {
                    committed = true;
                    evidence["commitPoint"] = EngineSuccessPhasePolicy.CommitPhase.ToString();
                    evidence["commitState"] = "committed";
                }
                if (phase is EnginePhase.Publishing or EnginePhase.SettlingRoute &&
                    settled.Outcome == EngineSettlementOutcome.Applied)
                {
                    visibleEffects = true;
                }

                var expectedOutcome = phase == EngineSuccessPhasePolicy.CommitPhase
                    ? EngineSettlementOutcome.Committed
                    : EngineSettlementOutcome.Applied;
                if (settled.Outcome != expectedOutcome || string.IsNullOrWhiteSpace(settled.Evidence))
                {
                    if (phase == EngineSuccessPhasePolicy.CommitPhase && committed)
                    {
                        throw new EngineCommittedIndeterminateException(
                            "Persistence reported a committed outcome without valid acknowledgement evidence.");
                    }
                    throw new EngineSettlementException(
                        $"Settlement phase '{phase}' did not report '{expectedOutcome}': {settled.Evidence}");
                }

                evidence[$"phase:{phase}"] = settled.Evidence;
                evidence[$"delivery:{phase}"] = context.PhaseDeliveryId;
            }

            await YieldCooperativelyAsync(
                committed ? CancellationToken.None : cancellationToken,
                committed);

            foreach (var requiredPhase in requirements.RequiredEvidencePhases)
            {
                if (!evidence.TryGetValue($"phase:{requiredPhase}", out var phaseEvidence) ||
                    string.IsNullOrWhiteSpace(phaseEvidence))
                {
                    throw new InvalidOperationException($"Successful engine result is missing required phase evidence for '{requiredPhase}'.");
                }
            }

            evidence["settlement"] = "complete";
            evidence["visibleEffects"] = visibleEffects.ToString();
            var completion = CreateCompletion(
                request,
                computation,
                EngineTerminalStatus.Succeeded,
                EnginePhase.Completed,
                evidence);
            progress?.Report(new EngineProgress(
                request.TransactionId,
                generation,
                executionId,
                EnginePhase.Completed,
                12,
                12,
                "Transaction completed."));
            return new EngineResultEnvelope(
                request.ContractVersion,
                request.TransactionId,
                EngineTerminalStatus.Succeeded,
                computation.Result,
                completion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !committed)
        {
            RecordIncompleteSettlement(evidence, committed, visibleEffects, settlementStarted);
            if (!await EnsureGateReleasedAsync())
            {
                return CreateGateReleaseResult(
                    request,
                    computation,
                    phase,
                    evidence,
                    gateReleaseIndeterminate);
            }
            return CreateTerminal(
                request,
                computation,
                EngineTerminalStatus.Cancelled,
                EnginePhase.Cancelled,
                phase,
                "cancelled",
                "The engine transaction was cancelled before persistence committed.",
                nameof(OperationCanceledException),
                evidence);
        }
        catch (Exception ex)
        {
            RecordIncompleteSettlement(evidence, committed, visibleEffects, settlementStarted);
            if (phase == EngineSuccessPhasePolicy.CommitPhase &&
                !committed &&
                ex is EngineOperationIndeterminateException or EngineSettlementOutcomeUnknownException)
            {
                evidence["commitState"] = "indeterminate";
            }
            if (!await EnsureGateReleasedAsync())
            {
                return CreateGateReleaseResult(
                    request,
                    computation,
                    phase,
                    evidence,
                    gateReleaseIndeterminate);
            }
            var indeterminate = ex is EngineOperationIndeterminateException or
                EngineSettlementOutcomeUnknownException or
                EngineCommittedIndeterminateException ||
                gateReleaseIndeterminate;
            var code = ex switch
            {
                EngineCommittedIndeterminateException => "committed-indeterminate",
                EngineOperationIndeterminateException operation => operation.Code,
                EngineSettlementOutcomeUnknownException => "settlement-outcome-unknown",
                EngineComputationContractException => "contract-version-mismatch",
                EngineCancellationPhaseException => "invalid-cancellation-phase",
                EngineGateReleaseFailedException => "gate-release-failed",
                _ when committed => "settlement-incomplete-after-commit",
                _ when visibleEffects => "pre-commit-visible-effect-failure",
                _ => "unhandled"
            };
            return CreateTerminal(
                request,
                computation,
                indeterminate ? EngineTerminalStatus.Indeterminate : EngineTerminalStatus.Failed,
                indeterminate ? EnginePhase.Indeterminate : EnginePhase.Failed,
                phase,
                code,
                ex.Message,
                ex.GetType().FullName ?? ex.GetType().Name,
                evidence,
                isRetryable: ex is EngineGateReleaseFailedException);
        }
    }

    private async Task<EngineSettlementEvidence> DeliverSettlementAsync(
        EnginePhase phase,
        EngineSettlementContext context,
        CancellationToken cancellationToken,
        TimeSpan timeout,
        ExecutionLease lease)
    {
        try
        {
            return await InvokeBoundedAsync(
                token => _settlement.SettleAsync(phase, context, token),
                timeout,
                cancellationToken,
                lease,
                "settlement-phase-timeout-indeterminate",
                $"Settlement phase '{phase}' did not terminate before its deadline.");
        }
        catch (EngineOperationIndeterminateException)
        {
            // Starting observation after a delegate has ignored its deadline would create another
            // unbounded external call. Keep the execution slot until that leftover terminates.
            throw;
        }
        catch (Exception deliveryFailure)
        {
            try
            {
                var observed = await InvokeBoundedAsync(
                    token => _settlement.ObserveAsync(phase, context, token),
                    timeout,
                    CancellationToken.None,
                    lease,
                    "settlement-observation-timeout-indeterminate",
                    $"Settlement phase '{phase}' could not be observed before its deadline.");
                if (observed.Outcome != EngineSettlementOutcome.NotApplied)
                {
                    return observed;
                }
            }
            catch (Exception observationFailure)
            {
                throw new EngineSettlementOutcomeUnknownException(
                    $"Settlement phase '{phase}' failed and its outcome could not be observed.",
                    new AggregateException(deliveryFailure, observationFailure));
            }

            throw;
        }
    }

    private async Task<EngineResultEnvelope> CompletePreComputationOutcomeAsync(
        EngineRequestEnvelope request,
        string requestHash,
        EngineResultEnvelope outcome,
        ExecutionLease lease,
        string invocationToken,
        string? claimToken,
        bool ownsCleanup,
        bool requiresCleanupAdmission = false,
        bool requestValidationFailure = false)
    {
        await Task.Yield();
        if (!ownsCleanup)
        {
            return outcome;
        }

        var evidence = new Dictionary<string, string>(outcome.Completion.TerminalEvidence, StringComparer.Ordinal);
        ExecutionLease cleanupLease = lease;
        if (requiresCleanupAdmission)
        {
            if (!_cleanupSlots.Wait(0))
            {
                evidence["cleanup:ReleasingGate"] = "failed:cleanup-capacity-exhausted";
                return requestValidationFailure
                    ? ReplaceTerminalEvidence(request, outcome, evidence, requestValidationFailure: true)
                    : CreateTerminal(
                        request,
                        null,
                        EngineTerminalStatus.Indeterminate,
                        EnginePhase.Indeterminate,
                        outcome.Failure?.FailedPhase ?? EnginePhase.Accepted,
                        "cleanup-capacity-exhausted",
                        "The bounded invocation cleanup pool has reached capacity.",
                        nameof(InvalidOperationException),
                        evidence,
                        isRetryable: true);
            }
            cleanupLease = new ExecutionLease(() => _cleanupSlots.Release());
        }

        var context = CreateSettlementContext(
            request,
            null,
            requestHash,
            EnginePhase.ReleasingGate,
            invocationToken,
            claimToken);
        var indeterminate = false;
        try
        {
            for (var attempt = 0; attempt < MaximumGateReleaseAttempts; attempt++)
            {
                try
                {
                    var released = await DeliverSettlementAsync(
                        EnginePhase.ReleasingGate,
                        context,
                        CancellationToken.None,
                        _options.TerminalGateCleanupTimeout,
                        cleanupLease);
                    if (released.Outcome == EngineSettlementOutcome.Applied &&
                        !string.IsNullOrWhiteSpace(released.Evidence))
                    {
                        evidence["delivery:ReleasingGate"] = context.PhaseDeliveryId;
                        evidence["cleanup:ReleasingGate"] = released.Evidence;
                        return requestValidationFailure
                            ? ReplaceTerminalEvidence(
                                request,
                                outcome,
                                evidence,
                                requestValidationFailure: true)
                            : outcome;
                    }

                    evidence["cleanup:ReleasingGate"] = $"failed:{released.Outcome}:{released.Evidence}";
                }
                catch (Exception ex)
                {
                    indeterminate = ex is EngineOperationIndeterminateException or EngineSettlementOutcomeUnknownException;
                    evidence["cleanup:ReleasingGate"] = $"failed:{ex.GetType().Name}";
                    if (indeterminate)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            if (requiresCleanupAdmission)
            {
                cleanupLease.ReleaseWhenSettled();
            }
        }

        if (requestValidationFailure)
        {
            return ReplaceTerminalEvidence(request, outcome, evidence, requestValidationFailure: true);
        }

        return CreateGateReleaseResult(
            request,
            null,
            outcome.Failure?.FailedPhase ?? EnginePhase.Accepted,
            evidence,
            indeterminate || outcome.Status == EngineTerminalStatus.Indeterminate);
    }

    private static void ValidateClaim(string requestHash, EngineTransactionClaim? claim)
    {
        if (claim is null ||
            !Enum.IsDefined(claim.Disposition) ||
            string.IsNullOrWhiteSpace(claim.CanonicalRequestHash) ||
            !IsCanonicalHash(claim.CanonicalRequestHash))
        {
            throw new InvalidOperationException("The transaction ledger returned an invalid claim response.");
        }

        var matchesRequest = string.Equals(claim.CanonicalRequestHash, requestHash, StringComparison.Ordinal);
        var hasClaimToken = !string.IsNullOrWhiteSpace(claim.ClaimToken);
        var valid = claim.Disposition switch
        {
            EngineTransactionClaimDisposition.Claimed =>
                matchesRequest && hasClaimToken && claim.TerminalResult is null,
            EngineTransactionClaimDisposition.ActiveReplay =>
                matchesRequest && !hasClaimToken && claim.TerminalResult is null,
            EngineTransactionClaimDisposition.AbandonedReplay =>
                matchesRequest && hasClaimToken && claim.TerminalResult is null,
            EngineTransactionClaimDisposition.TerminalReplay =>
                matchesRequest && !hasClaimToken && claim.TerminalResult is not null,
            EngineTransactionClaimDisposition.ExpiredTerminalReplay =>
                matchesRequest && !hasClaimToken && claim.TerminalResult is null,
            EngineTransactionClaimDisposition.Conflict =>
                !matchesRequest && !hasClaimToken && claim.TerminalResult is null,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidOperationException("The transaction ledger claim response is inconsistent with its disposition.");
        }
    }

    private static async Task<T> InvokeBoundedAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        ExecutionLease lease,
        string indeterminateCode,
        string indeterminateMessage)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var task = Task.Factory.StartNew(
            () => operation(linked.Token),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
        try
        {
            return await task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!task.IsCompleted)
            {
                lease.Track(task);
            }
            throw new EngineOperationIndeterminateException(indeterminateCode, indeterminateMessage);
        }
    }

    private static async Task InvokeBoundedAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        ExecutionLease lease,
        string indeterminateCode,
        string indeterminateMessage)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var task = Task.Factory.StartNew(
            () => operation(linked.Token),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
        try
        {
            await task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!task.IsCompleted)
            {
                lease.Track(task);
            }
            throw new EngineOperationIndeterminateException(indeterminateCode, indeterminateMessage);
        }
    }

    private async ValueTask YieldCooperativelyAsync(
        CancellationToken cancellationToken,
        bool committed = false)
    {
        if (_cooperativeYield is null)
        {
            return;
        }

        try
        {
            await _cooperativeYield(cancellationToken)
                .AsTask()
                .WaitAsync(_options.TerminalGateCleanupTimeout, cancellationToken);
        }
        catch (Exception ex) when (committed && ex is not EngineOperationIndeterminateException)
        {
            throw new EngineOperationIndeterminateException(
                ex is TimeoutException ? "cooperative-yield-timeout" : "cooperative-yield-failed",
                ex is TimeoutException
                    ? "Browser scheduling did not resume before the post-commit deadline."
                    : "Browser scheduling failed after persistence committed.");
        }
    }

    private EngineComputationResult ValidateComputation(
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        EngineComputationResult computation) =>
        EngineComputationResultValidation.Validate(generation, executionId, request, computation, _snapshots);

    private EngineResultEnvelope ValidateLedgerReplay(
        EngineRequestEnvelope request,
        EngineResultEnvelope replay)
    {
        try
        {
            replay = EngineEvidenceSnapshots.FreezeTerminal(replay);
            ValidateTerminalResult(request, replay);
            return replay;
        }
        catch (Exception ex)
        {
            return CreateTerminal(
                request,
                null,
                EngineTerminalStatus.Indeterminate,
                EnginePhase.Indeterminate,
                EnginePhase.Accepted,
                "transaction-ledger-corrupt",
                $"The transaction ledger returned invalid terminal evidence: {ex.Message}",
                ex.GetType().FullName ?? ex.GetType().Name,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["replayStatus"] = "invalid-terminal-evidence"
                });
        }
    }

    private void ValidateTerminalResult(EngineRequestEnvelope request, EngineResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Completion);
        ArgumentNullException.ThrowIfNull(result.Completion.TerminalEvidence);
        if (!Enum.IsDefined(result.Status) ||
            !string.Equals(result.ContractVersion, request.ContractVersion, StringComparison.Ordinal) ||
            !string.Equals(result.Completion.ContractVersion, request.ContractVersion, StringComparison.Ordinal) ||
            result.TransactionId != request.TransactionId ||
            result.Completion.TransactionId != request.TransactionId ||
            result.Status != result.Completion.Status ||
            result.Completion.TerminalPhase != ExpectedTerminalPhase(result.Status) ||
            result.Completion.Basis != request.Basis ||
            !string.Equals(result.Completion.RootIntentHash, request.RootIntentHash, StringComparison.Ordinal) ||
            !string.Equals(result.Completion.ExpandedGraphHash, request.ExpandedGraphHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Ledger completion identity, status, phase, or basis does not match the request.");
        }

        var input = request.Input.Deserialize<ReferenceEngineInput>(EngineJsonSerializerOptions.CreateWire())
            ?? throw new InvalidOperationException("Cannot validate ledger semantic evidence for the request.");
        var includesMarketAnalysis = input.MarketAnalysis is not null;
        var includesProcurementRoute = input.ProcurementRoute is not null;
        EnginePhaseValidation.ValidateOrderedPhaseEvidence(
            result.Completion.TerminalEvidence,
            request.InputKind,
            includesMarketAnalysis,
            includesProcurementRoute,
            includeSettlementPhases: true);

        var hasFailure = result.Status is EngineTerminalStatus.Failed or EngineTerminalStatus.Indeterminate;
        if (hasFailure != (result.Failure is not null))
        {
            throw new InvalidOperationException("Ledger failure envelope semantics are inconsistent.");
        }
        if (result.Status != EngineTerminalStatus.Succeeded && result.Result is not null)
        {
            throw new InvalidOperationException("A non-success ledger record cannot carry a result payload.");
        }
        var parsedCancellationPhase = default(EnginePhase);
        if (hasFailure)
        {
            var failure = result.Failure!;
            if (!EnginePhaseValidation.IsExecutionPhaseAllowed(
                    failure.FailedPhase,
                    request.InputKind,
                    includesMarketAnalysis,
                    includesProcurementRoute) ||
                string.IsNullOrWhiteSpace(failure.Code) ||
                string.IsNullOrWhiteSpace(failure.Message) ||
                string.IsNullOrWhiteSpace(failure.FailureType) ||
                !result.Completion.TerminalEvidence.TryGetValue("terminalCode", out var terminalCode) ||
                !result.Completion.TerminalEvidence.TryGetValue("failedPhase", out var failedPhase) ||
                !result.Completion.TerminalEvidence.TryGetValue("failureType", out var failureType) ||
                !result.Completion.TerminalEvidence.TryGetValue("failureMessageHash", out var failureMessageHash) ||
                !result.Completion.TerminalEvidence.TryGetValue("isRetryable", out var isRetryable) ||
                !string.Equals(terminalCode, failure.Code, StringComparison.Ordinal) ||
                !string.Equals(failedPhase, failure.FailedPhase.ToString(), StringComparison.Ordinal) ||
                !string.Equals(failureType, failure.FailureType, StringComparison.Ordinal) ||
                !string.Equals(failureMessageHash, EngineCanonicalHash.Compute(failure.Message.Trim()), StringComparison.Ordinal) ||
                !string.Equals(isRetryable, failure.IsRetryable.ToString(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Ledger failure fields are not bound to terminal evidence.");
            }
            EnginePhaseValidation.ValidatePhaseEvidenceThroughPhase(
                result.Completion.TerminalEvidence,
                request.InputKind,
                includesMarketAnalysis,
                includesProcurementRoute,
                includeSettlementPhases: true,
                failure.FailedPhase);
        }
        else if (result.Status == EngineTerminalStatus.Cancelled &&
                 (!result.Completion.TerminalEvidence.TryGetValue("terminalCode", out var cancelledCode) ||
                  !string.Equals(cancelledCode, "cancelled", StringComparison.Ordinal) ||
                  !result.Completion.TerminalEvidence.TryGetValue("failedPhase", out var cancelledPhase) ||
                  !EnginePhaseValidation.TryParseCancellationPhase(cancelledPhase, out parsedCancellationPhase) ||
                  !EnginePhaseValidation.IsExecutionPhaseAllowed(
                      parsedCancellationPhase,
                      request.InputKind,
                      includesMarketAnalysis,
                      includesProcurementRoute) ||
                  !result.Completion.TerminalEvidence.TryGetValue("failureType", out var cancellationType) ||
                  !string.Equals(cancellationType, nameof(OperationCanceledException), StringComparison.Ordinal) ||
                  !result.Completion.TerminalEvidence.TryGetValue("failureMessageHash", out var cancellationMessageHash) ||
                  !EnginePhaseValidation.IsCancellationMessageHash(cancellationMessageHash) ||
                  !result.Completion.TerminalEvidence.TryGetValue("isRetryable", out var cancellationRetryability) ||
                  !string.Equals(cancellationRetryability, bool.FalseString, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Ledger cancellation phase evidence is invalid.");
        }
        else if (result.Status == EngineTerminalStatus.Cancelled)
        {
            EnginePhaseValidation.ValidatePhaseEvidenceThroughPhase(
                result.Completion.TerminalEvidence,
                request.InputKind,
                includesMarketAnalysis,
                includesProcurementRoute,
                includeSettlementPhases: true,
                parsedCancellationPhase);
        }

        var hasAnalysisHash = !string.IsNullOrEmpty(result.Completion.AnalysisResultHash);
        var hasRouteHash = !string.IsNullOrEmpty(result.Completion.ProcurementRouteResultHash);
        if (hasAnalysisHash && input.MarketAnalysis is null ||
            hasRouteHash && input.ProcurementRoute is null ||
            hasAnalysisHash && !IsCanonicalHash(result.Completion.AnalysisResultHash) ||
            hasRouteHash && !IsCanonicalHash(result.Completion.ProcurementRouteResultHash) ||
            (hasAnalysisHash || hasRouteHash) &&
            (!result.Completion.TerminalEvidence.TryGetValue("resultPayloadHash", out var resultPayloadHash) ||
             !IsCanonicalHash(resultPayloadHash) ||
             !result.Completion.TerminalEvidence.TryGetValue("computationHash", out var computationHash) ||
             !IsCanonicalHash(computationHash)))
        {
            throw new InvalidOperationException("Ledger semantic evidence is inconsistent with the requested operations.");
        }

        if (result.Status == EngineTerminalStatus.Succeeded)
        {
            ValidateSuccessfulTerminalResult(request, result);
        }

        var expectedHash = EngineCanonicalHash.ComputeFinalTransactionHash(
            request,
            result.Status,
            result.Completion.AnalysisResultHash,
            result.Completion.ProcurementRouteResultHash,
            result.Completion.TerminalEvidence);
        if (!string.Equals(result.Completion.FinalTransactionHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Ledger final transaction hash validation failed.");
        }
    }

    private void ValidateSuccessfulTerminalResult(EngineRequestEnvelope request, EngineResultEnvelope result)
    {
        if (result.Result is not { } payload)
        {
            throw new InvalidOperationException("Successful ledger payload or settlement evidence is invalid.");
        }
        var transported = _snapshots.CaptureTransportedResult(payload);
        var input = request.Input.Deserialize<ReferenceEngineInput>(EngineJsonSerializerOptions.CreateWire())
            ?? throw new InvalidOperationException("Cannot validate successful ledger phase evidence.");
        var derivedRoute = transported.ProcurementRouteResult is null
            ? null
            : _snapshots.CaptureRoute(transported.ProcurementRouteResult);
        if (transported.ProcurementRoute is not null &&
            derivedRoute is not null &&
            !string.Equals(
                EngineSemanticSnapshotHash.Route(transported.ProcurementRoute),
                EngineSemanticSnapshotHash.Route(derivedRoute),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The ledger procurement result does not match its semantic route snapshot.");
        }
        transported = transported with { ProcurementRoute = derivedRoute ?? transported.ProcurementRoute };
        var requirements = EngineSuccessPhasePolicy.Resolve(
            request.InputKind,
            input.MarketAnalysis is not null,
            input.ProcurementRoute is not null);
        foreach (var requiredPhase in requirements.RequiredEvidencePhases)
        {
            if (!result.Completion.TerminalEvidence.TryGetValue($"phase:{requiredPhase}", out var phaseEvidence) ||
                string.IsNullOrWhiteSpace(phaseEvidence))
            {
                throw new InvalidOperationException(
                    $"Successful ledger result is missing required phase evidence for '{requiredPhase}'.");
            }
        }

        if ((transported.MarketAnalysis is not null) != (input.MarketAnalysis is not null) ||
            (transported.ProcurementRoute is not null) != (input.ProcurementRoute is not null) ||
            (transported.ProcurementRouteResult is not null) != (input.ProcurementRoute is not null))
        {
            throw new InvalidOperationException("Ledger result operations do not match the request.");
        }
        var analysisHash = transported.MarketAnalysis is null
            ? string.Empty
            : EngineSemanticSnapshotHash.Analysis(transported.MarketAnalysis);
        var routeHash = transported.ProcurementRoute is null
            ? string.Empty
            : EngineSemanticSnapshotHash.Route(transported.ProcurementRoute);
        var expectedPayloadHash = string.IsNullOrWhiteSpace(request.InputHash)
            ? EngineCanonicalHash.Compute(payload)
            : EngineCanonicalHash.ComputeAuthoritativeResultPayloadHash(analysisHash, routeHash);
        if (!result.Completion.TerminalEvidence.TryGetValue("resultPayloadHash", out var claimedPayloadHash) ||
            !string.Equals(claimedPayloadHash, expectedPayloadHash, StringComparison.Ordinal) ||
            !result.Completion.TerminalEvidence.TryGetValue("settlement", out var settlement) ||
            !string.Equals(settlement, "complete", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Successful ledger payload or settlement evidence is invalid.");
        }
        if (!string.Equals(result.Completion.AnalysisResultHash, analysisHash, StringComparison.Ordinal) ||
            !string.Equals(result.Completion.ProcurementRouteResultHash, routeHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Ledger semantic hashes do not match its result payload.");
        }
    }

    private static EnginePhase ExpectedTerminalPhase(EngineTerminalStatus status) => status switch
    {
        EngineTerminalStatus.Succeeded => EnginePhase.Completed,
        EngineTerminalStatus.Cancelled => EnginePhase.Cancelled,
        EngineTerminalStatus.Failed => EnginePhase.Failed,
        EngineTerminalStatus.Indeterminate => EnginePhase.Indeterminate,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static bool IsCanonicalHash(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static EngineResultEnvelope CreateGateReleaseResult(
        EngineRequestEnvelope request,
        EngineComputationResult? computation,
        EnginePhase failedPhase,
        IReadOnlyDictionary<string, string> evidence,
        bool indeterminate) =>
        CreateTerminal(
            request,
            computation,
            indeterminate ? EngineTerminalStatus.Indeterminate : EngineTerminalStatus.Failed,
            indeterminate ? EnginePhase.Indeterminate : EnginePhase.Failed,
            failedPhase,
            indeterminate ? "gate-release-indeterminate" : "gate-release-failed",
            indeterminate
                ? "The operation gate release outcome is indeterminate."
                : "The operation gate was not released after an idempotent retry.",
            indeterminate
                ? nameof(EngineSettlementOutcomeUnknownException)
                : nameof(EngineGateReleaseFailedException),
            evidence,
            isRetryable: true);

    private static EngineResultEnvelope CreateLedgerWriteIndeterminate(
        EngineRequestEnvelope request,
        EngineResultEnvelope result,
        Exception exception)
    {
        var reachedPhase = result.Failure?.FailedPhase ??
            (result.Completion.TerminalEvidence.TryGetValue("failedPhase", out var failedPhaseValue) &&
             EnginePhaseValidation.TryParseCancellationPhase(failedPhaseValue, out var parsedFailedPhase)
                ? parsedFailedPhase
                : EnginePhase.ReleasingGate);
        var evidence = new Dictionary<string, string>(result.Completion.TerminalEvidence, StringComparer.Ordinal)
        {
            ["replayStatus"] = "ledger-claim-retained",
            ["ledgerWrite:originalStatus"] = result.Status.ToString(),
            ["ledgerWrite:originalTerminalPhase"] = result.Completion.TerminalPhase.ToString(),
            ["ledgerWrite:originalFinalTransactionHash"] = result.Completion.FinalTransactionHash
        };
        foreach (var pair in result.Completion.TerminalEvidence)
        {
            evidence[$"ledgerWrite:originalEvidence:{pair.Key}"] = pair.Value;
        }
        if (result.Completion.TerminalEvidence.TryGetValue("terminalCode", out var terminalCode))
        {
            evidence["ledgerWrite:originalTerminalCode"] = terminalCode;
        }
        if (result.Completion.TerminalEvidence.TryGetValue("failedPhase", out var failedPhase))
        {
            evidence["ledgerWrite:originalFailedPhase"] = failedPhase;
        }
        if (result.Completion.TerminalEvidence.TryGetValue("isRetryable", out var isRetryable))
        {
            evidence["ledgerWrite:originalIsRetryable"] = isRetryable;
        }
        if (result.Failure is { } failure)
        {
            evidence["ledgerWrite:originalFailureCode"] = failure.Code;
            evidence["ledgerWrite:originalFailedPhase"] = failure.FailedPhase.ToString();
            evidence["ledgerWrite:originalFailureType"] = failure.FailureType;
            evidence["ledgerWrite:originalFailureMessageHash"] = EngineCanonicalHash.Compute(failure.Message.Trim());
            evidence["ledgerWrite:originalFailureIsRetryable"] = failure.IsRetryable.ToString();
        }

        return CreateTerminal(
            request,
            EngineTerminalStatus.Indeterminate,
            EnginePhase.Indeterminate,
            reachedPhase,
            "transaction-ledger-write-indeterminate",
            exception.Message,
            exception.GetType().FullName ?? exception.GetType().Name,
            result.Completion.AnalysisResultHash,
            result.Completion.ProcurementRouteResultHash,
            evidence,
            isRetryable: true);
    }

    private static EngineResultEnvelope CreateGateReleaseClaimIndeterminate(
        EngineRequestEnvelope request,
        EngineResultEnvelope result,
        Exception exception)
    {
        var evidence = new Dictionary<string, string>(result.Completion.TerminalEvidence, StringComparer.Ordinal)
        {
            ["replayStatus"] = "ledger-release-unknown",
            ["gateRelease:failedResultHash"] = result.Completion.FinalTransactionHash
        };
        return CreateTerminal(
            request,
            EngineTerminalStatus.Indeterminate,
            EnginePhase.Indeterminate,
            result.Failure?.FailedPhase ?? EnginePhase.ReleasingGate,
            "gate-release-indeterminate",
            exception.Message,
            exception.GetType().FullName ?? exception.GetType().Name,
            result.Completion.AnalysisResultHash,
            result.Completion.ProcurementRouteResultHash,
            evidence,
            isRetryable: true);
    }

    private static EngineResultEnvelope CreateRequestValidationFailureTerminal(
        EngineRequestEnvelope request,
        string code,
        Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "The canonical engine request could not be validated."
            : exception.Message;
        var failureType = exception.GetType().FullName ?? exception.GetType().Name;
        var evidence = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["terminalCode"] = code,
            ["failedPhase"] = EnginePhase.Accepted.ToString(),
            ["failureType"] = failureType,
            ["failureMessageHash"] = EngineCanonicalHash.Compute(message.Trim()),
            ["isRetryable"] = bool.FalseString,
            ["requestValidation"] = "canonical-hash-unavailable"
        };
        var frozenEvidence = EngineEvidenceSnapshots.Freeze(evidence);
        var completion = new EngineCompletionEvidence(
            request.ContractVersion,
            request.TransactionId,
            EngineTerminalStatus.Indeterminate,
            EnginePhase.Indeterminate,
            request.Basis,
            request.RootIntentHash,
            request.ExpandedGraphHash,
            string.Empty,
            string.Empty,
            EngineCanonicalHash.ComputeRequestValidationFailureHash(
                request,
                EngineTerminalStatus.Indeterminate,
                frozenEvidence),
            frozenEvidence);
        return new EngineResultEnvelope(
            request.ContractVersion,
            request.TransactionId,
            EngineTerminalStatus.Indeterminate,
            null,
            completion,
            new EngineFailure(code, message, false, EnginePhase.Accepted, failureType));
    }

    private static EngineResultEnvelope ReplaceTerminalEvidence(
        EngineRequestEnvelope request,
        EngineResultEnvelope result,
        IReadOnlyDictionary<string, string> evidence,
        bool requestValidationFailure)
    {
        evidence = EngineEvidenceSnapshots.Freeze(evidence);
        var finalHash = requestValidationFailure
            ? EngineCanonicalHash.ComputeRequestValidationFailureHash(request, result.Status, evidence)
            : EngineCanonicalHash.ComputeFinalTransactionHash(
                request,
                result.Status,
                result.Completion.AnalysisResultHash,
                result.Completion.ProcurementRouteResultHash,
                evidence);
        return result with
        {
            Completion = result.Completion with
            {
                FinalTransactionHash = finalHash,
                TerminalEvidence = evidence
            }
        };
    }

    private static EngineResultEnvelope CreateTerminal(
        EngineRequestEnvelope request,
        EngineComputationResult? computation,
        EngineTerminalStatus status,
        EnginePhase terminalPhase,
        EnginePhase failedPhase,
        string code,
        string message,
        string failureType,
        IReadOnlyDictionary<string, string> priorEvidence,
        bool isRetryable = false)
    {
        var evidence = new Dictionary<string, string>(priorEvidence, StringComparer.Ordinal)
        {
            ["terminalCode"] = code,
            ["failedPhase"] = failedPhase.ToString(),
            ["failureType"] = failureType,
            ["failureMessageHash"] = EngineCanonicalHash.Compute(message.Trim()),
            ["isRetryable"] = isRetryable.ToString()
        };
        var frozenEvidence = EngineEvidenceSnapshots.Freeze(evidence);
        var completion = CreateCompletion(request, computation, status, terminalPhase, frozenEvidence);
        var failure = status is EngineTerminalStatus.Failed or EngineTerminalStatus.Indeterminate
            ? new EngineFailure(code, message, isRetryable, failedPhase, failureType)
            : null;
        return new EngineResultEnvelope(
            request.ContractVersion,
            request.TransactionId,
            status,
            null,
            completion,
            failure);
    }

    private static EngineResultEnvelope CreateTerminal(
        EngineRequestEnvelope request,
        EngineTerminalStatus status,
        EnginePhase terminalPhase,
        EnginePhase failedPhase,
        string code,
        string message,
        string failureType,
        string analysisResultHash,
        string procurementRouteResultHash,
        IReadOnlyDictionary<string, string> priorEvidence,
        bool isRetryable = false)
    {
        var evidence = new Dictionary<string, string>(priorEvidence, StringComparer.Ordinal)
        {
            ["terminalCode"] = code,
            ["failedPhase"] = failedPhase.ToString(),
            ["failureType"] = failureType,
            ["failureMessageHash"] = EngineCanonicalHash.Compute(message.Trim()),
            ["isRetryable"] = isRetryable.ToString()
        };
        var frozenEvidence = EngineEvidenceSnapshots.Freeze(evidence);
        var completion = CreateCompletion(
            request,
            status,
            terminalPhase,
            analysisResultHash,
            procurementRouteResultHash,
            frozenEvidence);
        return new EngineResultEnvelope(
            request.ContractVersion,
            request.TransactionId,
            status,
            null,
            completion,
            new EngineFailure(code, message, isRetryable, failedPhase, failureType));
    }

    private static EngineCompletionEvidence CreateCompletion(
        EngineRequestEnvelope request,
        EngineComputationResult? computation,
        EngineTerminalStatus status,
        EnginePhase terminalPhase,
        IReadOnlyDictionary<string, string> evidence)
    {
        evidence = EngineEvidenceSnapshots.Freeze(evidence);
        var analysisHash = computation?.AnalysisResultHash ?? string.Empty;
        var routeHash = computation?.ProcurementRouteResultHash ?? string.Empty;
        var finalHash = EngineCanonicalHash.ComputeFinalTransactionHash(
            request,
            status,
            analysisHash,
            routeHash,
            evidence);
        return new EngineCompletionEvidence(
            request.ContractVersion,
            request.TransactionId,
            status,
            terminalPhase,
            request.Basis,
            request.RootIntentHash,
            request.ExpandedGraphHash,
            analysisHash,
            routeHash,
            finalHash,
            evidence);
    }

    private static EngineCompletionEvidence CreateCompletion(
        EngineRequestEnvelope request,
        EngineTerminalStatus status,
        EnginePhase terminalPhase,
        string analysisResultHash,
        string procurementRouteResultHash,
        IReadOnlyDictionary<string, string> evidence)
    {
        evidence = EngineEvidenceSnapshots.Freeze(evidence);
        var finalHash = EngineCanonicalHash.ComputeFinalTransactionHash(
            request,
            status,
            analysisResultHash,
            procurementRouteResultHash,
            evidence);
        return new EngineCompletionEvidence(
            request.ContractVersion,
            request.TransactionId,
            status,
            terminalPhase,
            request.Basis,
            request.RootIntentHash,
            request.ExpandedGraphHash,
            analysisResultHash,
            procurementRouteResultHash,
            finalHash,
            evidence);
    }

    private static int ProgressIndex(EnginePhase phase) => phase switch
    {
        EnginePhase.Publishing => 7,
        EnginePhase.SettlingRoute => 8,
        EnginePhase.Persisting => 9,
        EnginePhase.SettlingUi => 10,
        EnginePhase.CapturingPostActionEvidence => 11,
        EnginePhase.CapturingRestorationEvidence => 11,
        EnginePhase.ReleasingGate => 11,
        _ => 0
    };

    private static string PhaseMessage(EnginePhase phase) => phase switch
    {
        EnginePhase.Publishing => "Publishing analysis.",
        EnginePhase.SettlingRoute => "Settling procurement route.",
        EnginePhase.Persisting => "Persisting and autosaving.",
        EnginePhase.SettlingUi => "Settling UI state.",
        EnginePhase.CapturingPostActionEvidence => "Capturing post-action evidence.",
        EnginePhase.CapturingRestorationEvidence => "Capturing restoration evidence.",
        EnginePhase.ReleasingGate => "Releasing operation gate.",
        _ => phase.ToString()
    };

    private static EngineSettlementContext CreateSettlementContext(
        EngineRequestEnvelope request,
        EngineComputationResult? computation,
        string requestHash,
        EnginePhase phase,
        string invocationToken,
        string? claimToken)
    {
        var deliveryId = EngineCanonicalHash.Compute(new
        {
            Domain = "engine-settlement-delivery-v1",
            request.TransactionId,
            RequestHash = requestHash,
            Phase = phase
        });
        return new EngineSettlementContext(
            request,
            computation,
            requestHash,
            deliveryId,
            invocationToken,
            claimToken);
    }

    private static void RecordIncompleteSettlement(
        IDictionary<string, string> evidence,
        bool committed,
        bool visibleEffects,
        bool settlementStarted)
    {
        if (!settlementStarted)
        {
            return;
        }

        evidence["settlement"] = "incomplete";
        evidence["commitState"] = committed ? "committed" : "not-committed";
        evidence["visibleEffects"] = visibleEffects.ToString();
    }

    private void CompleteHostedExecution(Guid transactionId, Task<EngineResultEnvelope> completion)
    {
        lock (_sync)
        {
            if (_executions.TryGetValue(transactionId, out var hosted) &&
                ReferenceEquals(hosted.Completion, completion) &&
                completion.Status == TaskStatus.RanToCompletion &&
                IsTransientResult(completion.Result))
            {
                _executions.Remove(transactionId);
            }
            TrimCompletedExecutionsLocked(0);
        }
    }

    private static bool IsTransientResult(EngineResultEnvelope result) =>
        (result.Failure?.Code is
             "transaction-replay-in-progress" or
             "transaction-ledger-claim-indeterminate" or
             "transaction-ledger-write-indeterminate" or
             "execution-capacity-exhausted" or
             "gate-release-failed") ||
        (result.Completion.TerminalEvidence.TryGetValue("replayStatus", out var transientReplayStatus) &&
         string.Equals(transientReplayStatus, "unclaimed-transient", StringComparison.Ordinal)) ||
        (result.Failure?.Code == "gate-release-indeterminate" &&
         result.Completion.TerminalEvidence.TryGetValue("replayStatus", out var replayStatus) &&
         string.Equals(replayStatus, "ledger-release-unknown", StringComparison.Ordinal));

    private void TrimCompletedExecutionsLocked(int reservedSlots)
    {
        while (_executions.Count + reservedSlots > _options.CompletedExecutionCapacity)
        {
            var completed = _executions
                .Where(pair => pair.Value.Completion.IsCompleted)
                .OrderBy(pair => pair.Value.LastAccess)
                .FirstOrDefault();
            if (completed.Value is null)
            {
                return;
            }

            _executions.Remove(completed.Key);
        }
    }

    private sealed class HostedExecution(
        string requestHash,
        Task<EngineResultEnvelope> completion,
        long lastAccess)
    {
        public string RequestHash { get; } = requestHash;

        public Task<EngineResultEnvelope> Completion { get; } = completion;

        public long LastAccess { get; set; } = lastAccess;
    }

    private sealed class ExecutionLease(Action release)
    {
        private readonly object _sync = new();
        private readonly List<Task> _nonCooperativeTasks = [];
        private int _released;

        public void Track(Task task)
        {
            lock (_sync)
            {
                _nonCooperativeTasks.Add(task);
            }
        }

        public void ReleaseWhenSettled()
        {
            Task[] tasks;
            lock (_sync)
            {
                tasks = _nonCooperativeTasks.ToArray();
            }

            if (tasks.Length == 0)
            {
                Release();
                return;
            }

            _ = Task.WhenAll(tasks).ContinueWith(
                task =>
                {
                    _ = task.Exception;
                    Release();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                release();
            }
        }
    }

    private sealed class IsolatedProgressObserver<T>(IProgress<T> observer) : IProgress<T>
    {
        private readonly object _sync = new();
        private T _pending = default!;
        private bool _hasPending;
        private bool _dispatching;

        public void Report(T value)
        {
            lock (_sync)
            {
                _pending = value;
                _hasPending = true;
                if (_dispatching)
                {
                    return;
                }
                _dispatching = true;
            }

            ThreadPool.UnsafeQueueUserWorkItem(static state => state.Dispatch(), this, preferLocal: false);
        }

        private void Dispatch()
        {
            while (true)
            {
                T value;
                lock (_sync)
                {
                    if (!_hasPending)
                    {
                        _dispatching = false;
                        return;
                    }
                    value = _pending;
                    _hasPending = false;
                }

                try
                {
                    observer.Report(value);
                }
                catch
                {
                    // Observers are advisory and cannot participate in engine execution.
                }
            }
        }
    }

    private sealed class EngineSettlementException(string message) : InvalidOperationException(message);

    private sealed class EngineGateReleaseFailedException(string message) : InvalidOperationException(message);

    private sealed class EngineSettlementOutcomeUnknownException(string message, Exception innerException)
        : InvalidOperationException(message, innerException);

    private sealed class EngineOperationIndeterminateException(string code, string message)
        : TimeoutException(message)
    {
        public string Code { get; } = code;
    }

    private sealed class EngineCommittedIndeterminateException(string message) : InvalidOperationException(message);

}

public static class CraftArchitectEngineServiceCollectionExtensions
{
    // This intentionally registers computation foundations only, never a production execution host.
    public static IServiceCollection AddCraftArchitectEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IReferenceEngineSemanticSnapshotProvider, ReferenceEngineSemanticSnapshotProvider>();
        services.TryAddScoped<IMarketProcurementEngine, ReferenceMarketProcurementEngine>();
        services.TryAddScoped<IEngineExecutionTransport, InProcessEngineExecutionTransport>();
        return services;
    }
}
