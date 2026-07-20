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
    Failed = 16
}

public enum EngineTerminalStatus
{
    Succeeded = 1,
    Cancelled = 2,
    Failed = 3
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

public sealed record EngineDeterministicSettings(
    string AlgorithmVersion,
    IReadOnlyDictionary<string, string> Values)
{
    public static EngineDeterministicSettings Default { get; } =
        new("1", new Dictionary<string, string>(StringComparer.Ordinal));
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
    EnginePhase Phase,
    int CompletedWorkUnits,
    int TotalWorkUnits,
    string Message);

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
    EnginePhase FailedPhase);

public sealed record EngineResultEnvelope(
    string ContractVersion,
    Guid TransactionId,
    EngineTerminalStatus Status,
    JsonElement? Result,
    EngineCompletionEvidence Completion,
    EngineFailure? Failure = null);

public sealed record EngineCancelRequest(
    string ContractVersion,
    Guid TransactionId,
    string Reason);
