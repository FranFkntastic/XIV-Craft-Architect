using System.Collections.Frozen;
using System.Text.Json;

namespace FFXIV_Craft_Architect.Core.Engine;

public enum EngineInputKind
{
    RootIntent = 1,
    StructuredGraph = 2,
    RestoredSession = 3
}

public enum EnginePhase
{
    Accepted = 1,
    InterpretingInput = 2,
    ConstructingOrRestoringGraph = 3,
    ResolvingEvidence = 4,
    Analyzing = 5,
    Publishing = 6,
    Reconciling = 7,
    SettlingRoute = 8,
    Persisting = 9,
    ReleasingGate = 10,
    SettlingUi = 11,
    CapturingPostActionEvidence = 12,
    CapturingRestorationEvidence = 13,
    Completed = 14,
    Cancelled = 15,
    Failed = 16,
    Indeterminate = 17
}

public enum EngineTerminalStatus
{
    Succeeded = 1,
    Cancelled = 2,
    Failed = 3,
    Indeterminate = 4
}

public enum EngineComputationStatus
{
    Completed = 1,
    Cancelled = 2,
    Failed = 3
}

public enum EngineExecutionTransportKind
{
    InProcess = 1,
    BrowserWorker = 2
}

public sealed record EngineBasisIdentity(string Kind, string Version, string Hash)
{
    public static EngineBasisIdentity Empty(string kind, string version = "1") => new(kind, version, string.Empty);
}

public sealed record EngineBasisSet(
    EngineBasisIdentity Plan,
    EngineBasisIdentity Session,
    EngineBasisIdentity Publication,
    EngineBasisIdentity Route);

public sealed record EngineExecutionBudgets(
    int MaxWorkUnits,
    int MaxEvidenceRequests,
    long MaxCandidateEvaluations,
    int CooperativeCancellationInterval = 64)
{
    public static EngineExecutionBudgets Default { get; } = new(10_000, 10_000, 5_000_000, 64);
}

public sealed record EngineDeterministicSettings
{
    public EngineDeterministicSettings(string algorithmVersion, IReadOnlyDictionary<string, string> values)
    {
        AlgorithmVersion = string.IsNullOrWhiteSpace(algorithmVersion)
            ? throw new ArgumentException("An algorithm version is required.", nameof(algorithmVersion))
            : algorithmVersion;
        Values = values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToFrozenDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    public string AlgorithmVersion { get; }

    public IReadOnlyDictionary<string, string> Values { get; }

    public static EngineDeterministicSettings Default { get; } = new("1", new Dictionary<string, string>(StringComparer.Ordinal));
}

public sealed record EngineRequestEnvelope(
    string ContractVersion,
    Guid TransactionId,
    EngineInputKind InputKind,
    JsonElement Input,
    EngineBasisSet Basis,
    EngineDeterministicSettings Settings,
    EngineExecutionBudgets Budgets,
    string RootIntentHash,
    string ExpandedGraphHash,
    string AnalysisBasisHash,
    string RouteBasisHash);

public sealed record EngineProgress(
    Guid TransactionId,
    long Generation,
    Guid ExecutionId,
    EnginePhase Phase,
    int CompletedWorkUnits,
    int TotalWorkUnits,
    string Message);

public sealed record EngineExecutionTransportCapability(
    EngineExecutionTransportKind Kind,
    bool IsSupported,
    string? UnsupportedReason = null);

public sealed record EngineComputationResult(
    string ContractVersion,
    long Generation,
    Guid ExecutionId,
    Guid TransactionId,
    EngineComputationStatus Status,
    EnginePhase FinalPhase,
    JsonElement? Result,
    EngineBasisSet Basis,
    string RequestInputHash,
    EngineExecutionBudgets Budgets,
    string RootIntentHash,
    string ExpandedGraphHash,
    string AnalysisBasisHash,
    string RouteBasisHash,
    string AnalysisResultHash,
    string ProcurementRouteResultHash,
    string ComputationHash,
    IReadOnlyDictionary<string, string> ComputationEvidence,
    EngineFailure? Failure = null);

public sealed record EngineCompletionEvidence(
    string ContractVersion,
    Guid TransactionId,
    EngineTerminalStatus Status,
    EnginePhase TerminalPhase,
    EngineBasisSet Basis,
    string RootIntentHash,
    string ExpandedGraphHash,
    string AnalysisResultHash,
    string ProcurementRouteResultHash,
    string FinalTransactionHash,
    IReadOnlyDictionary<string, string> TerminalEvidence);

public sealed record EngineFailure(
    string Code,
    string Message,
    bool IsRetryable,
    EnginePhase FailedPhase,
    string FailureType);

public sealed record EngineResultEnvelope(
    string ContractVersion,
    Guid TransactionId,
    EngineTerminalStatus Status,
    JsonElement? Result,
    EngineCompletionEvidence Completion,
    EngineFailure? Failure = null);

public sealed record EngineCancelRequest(
    string ContractVersion,
    long Generation,
    Guid ExecutionId,
    Guid TransactionId,
    string Reason);

public static class EnginePhaseValidation
{
    public static bool IsComputationPhase(EnginePhase phase) => phase is
        EnginePhase.Accepted or
        EnginePhase.InterpretingInput or
        EnginePhase.ConstructingOrRestoringGraph or
        EnginePhase.ResolvingEvidence or
        EnginePhase.Analyzing or
        EnginePhase.Reconciling;

    public static bool IsComputationCancellationPhase(EnginePhase phase) => IsComputationPhase(phase);

    public static bool IsComputationPhaseAllowed(
        EnginePhase phase,
        bool includesMarketAnalysis,
        bool includesProcurementRoute) => phase switch
        {
            EnginePhase.Accepted or
            EnginePhase.InterpretingInput or
            EnginePhase.ConstructingOrRestoringGraph => true,
            EnginePhase.ResolvingEvidence or EnginePhase.Analyzing => includesMarketAnalysis,
            EnginePhase.Reconciling => includesProcurementRoute,
            _ => false
        };

    public static bool IsExecutionPhaseAllowed(
        EnginePhase phase,
        EngineInputKind inputKind,
        bool includesMarketAnalysis,
        bool includesProcurementRoute) =>
        IsComputationPhaseAllowed(phase, includesMarketAnalysis, includesProcurementRoute) ||
        EngineSuccessPhasePolicy.Resolve(inputKind, includesMarketAnalysis, includesProcurementRoute)
            .SettlementPhases.Contains(phase);

    public static void ValidateOrderedPhaseEvidence(
        IReadOnlyDictionary<string, string> evidence,
        EngineInputKind inputKind,
        bool includesMarketAnalysis,
        bool includesProcurementRoute,
        bool includeSettlementPhases)
    {
        var allowedPhases = new List<EnginePhase>();
        if (includesMarketAnalysis)
        {
            allowedPhases.Add(EnginePhase.Analyzing);
        }
        if (includesProcurementRoute)
        {
            allowedPhases.Add(EnginePhase.Reconciling);
        }
        if (includeSettlementPhases)
        {
            allowedPhases.AddRange(EngineSuccessPhasePolicy.Resolve(
                inputKind,
                includesMarketAnalysis,
                includesProcurementRoute).SettlementPhases);
        }

        var claimedPhases = new HashSet<EnginePhase>();
        foreach (var pair in evidence.Where(pair => pair.Key.StartsWith("phase:", StringComparison.Ordinal)))
        {
            var phaseName = pair.Key["phase:".Length..];
            if (string.IsNullOrWhiteSpace(pair.Value) ||
                !Enum.TryParse(phaseName, ignoreCase: false, out EnginePhase phase) ||
                !Enum.IsDefined(phase) ||
                !string.Equals(phaseName, phase.ToString(), StringComparison.Ordinal) ||
                !allowedPhases.Contains(phase))
            {
                throw new InvalidOperationException(
                    $"Engine phase evidence '{pair.Key}' is not allowed by the requested operation shape.");
            }
            claimedPhases.Add(phase);
        }

        var missingPriorPhase = false;
        foreach (var phase in allowedPhases)
        {
            if (!claimedPhases.Contains(phase))
            {
                missingPriorPhase = true;
                continue;
            }
            if (missingPriorPhase)
            {
                throw new InvalidOperationException(
                    $"Engine result is missing required phase evidence before '{phase}', so its phase evidence is out of order.");
            }
        }
    }

    public static void ValidatePhaseEvidenceThroughPhase(
        IReadOnlyDictionary<string, string> evidence,
        EngineInputKind inputKind,
        bool includesMarketAnalysis,
        bool includesProcurementRoute,
        bool includeSettlementPhases,
        EnginePhase reachedPhase)
    {
        var orderedPhases = new List<EnginePhase>();
        if (includesMarketAnalysis)
        {
            orderedPhases.Add(EnginePhase.Analyzing);
        }
        if (includesProcurementRoute)
        {
            orderedPhases.Add(EnginePhase.Reconciling);
        }
        if (includeSettlementPhases)
        {
            orderedPhases.AddRange(EngineSuccessPhasePolicy.Resolve(
                inputKind,
                includesMarketAnalysis,
                includesProcurementRoute).SettlementPhases);
        }

        var reachedIndex = orderedPhases.IndexOf(reachedPhase);
        var claimedPhases = evidence.Keys
            .Where(key => key.StartsWith("phase:", StringComparison.Ordinal))
            .Select(key => Enum.Parse<EnginePhase>(key["phase:".Length..], ignoreCase: false))
            .ToHashSet();
        for (var index = 0; index < orderedPhases.Count; index++)
        {
            var claimed = claimedPhases.Contains(orderedPhases[index]);
            if (index < reachedIndex && !claimed || reachedIndex < index && claimed)
            {
                throw new InvalidOperationException(
                    $"Engine phase evidence is inconsistent with the reached phase '{reachedPhase}'.");
            }
        }
    }

    public static bool IsNonTerminalPhase(EnginePhase phase) =>
        Enum.IsDefined(phase) &&
        phase is not (EnginePhase.Completed or EnginePhase.Cancelled or EnginePhase.Failed or EnginePhase.Indeterminate);

    public static bool TryParseCancellationPhase(string? value, out EnginePhase phase)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Enum.TryParse(value, ignoreCase: false, out phase) ||
            !Enum.IsDefined(phase) ||
            !string.Equals(value, phase.ToString(), StringComparison.Ordinal) ||
            phase is EnginePhase.Completed or EnginePhase.Cancelled or EnginePhase.Failed or EnginePhase.Indeterminate)
        {
            phase = default;
            return false;
        }

        return true;
    }

    public static bool IsCancellationMessageHash(string value) =>
        string.Equals(
            value,
            EngineCanonicalHash.Compute("The engine transaction was cancelled before its ledger claim."),
            StringComparison.Ordinal) ||
        string.Equals(
            value,
            EngineCanonicalHash.Compute("The engine transaction was cancelled."),
            StringComparison.Ordinal) ||
        string.Equals(
            value,
            EngineCanonicalHash.Compute("The engine transaction was cancelled before persistence committed."),
            StringComparison.Ordinal);
}
