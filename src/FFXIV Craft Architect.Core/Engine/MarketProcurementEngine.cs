using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Engine;

public interface IMarketProcurementEngine
{
    Task<EngineResultEnvelope> ExecuteAsync(
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record ReferenceEngineInput(
    MarketAnalysisExecutionRequest? MarketAnalysis,
    ProcurementRouteExecutionRequest? ProcurementRoute);

public sealed record ReferenceEngineOutput(
    MarketAnalysisExecutionResult? MarketAnalysis,
    ProcurementRouteExecutionResult? ProcurementRoute);

public sealed record EngineSettlementContext(
    EngineRequestEnvelope Request,
    ReferenceEngineOutput Output,
    string AnalysisResultHash,
    string ProcurementRouteResultHash);

public sealed record EngineSettlementEvidence(bool Completed, string Evidence);

public interface IEngineTransactionSettlement
{
    Task<EngineSettlementEvidence> SettleAsync(
        EnginePhase phase,
        EngineSettlementContext context,
        CancellationToken cancellationToken);
}

public sealed class NoOpEngineTransactionSettlement : IEngineTransactionSettlement
{
    public Task<EngineSettlementEvidence> SettleAsync(
        EnginePhase phase,
        EngineSettlementContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new EngineSettlementEvidence(false, "unsupported"));
    }
}

public sealed class ReferenceMarketProcurementEngine : IMarketProcurementEngine
{
    private const string ContractVersion = "1";
    private readonly IMarketAnalysisExecutionService _marketAnalysis;
    private readonly IProcurementRouteExecutionService _procurementRoute;
    private readonly IEngineTransactionSettlement _settlement;
    private readonly IReferenceEngineSemanticSnapshotProvider _snapshots;

    public ReferenceMarketProcurementEngine(
        IMarketAnalysisExecutionService marketAnalysis,
        IProcurementRouteExecutionService procurementRoute,
        IEngineTransactionSettlement settlement,
        IReferenceEngineSemanticSnapshotProvider snapshots)
    {
        _marketAnalysis = marketAnalysis ?? throw new ArgumentNullException(nameof(marketAnalysis));
        _procurementRoute = procurementRoute ?? throw new ArgumentNullException(nameof(procurementRoute));
        _settlement = settlement ?? throw new ArgumentNullException(nameof(settlement));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    }

    public async Task<EngineResultEnvelope> ExecuteAsync(
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ContractVersion, ContractVersion, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Engine contract version '{request.ContractVersion}' is not supported.");
        }

        var phase = EnginePhase.Accepted;
        var settledPhases = new HashSet<EnginePhase>();
        var settlementEvidence = new Dictionary<string, string>(StringComparer.Ordinal);
        var output = new ReferenceEngineOutput(null, null);
        var analysisHash = string.Empty;
        var routeHash = string.Empty;
        try
        {
            Report(progress, request, phase, 0, 12, "Transaction accepted.");
            cancellationToken.ThrowIfCancellationRequested();

            phase = EnginePhase.InterpretingInput;
            Report(progress, request, phase, 1, 12, "Interpreting engine input.");
            if (request.InputKind != EngineInputKind.RootIntent)
            {
                throw new NotSupportedException($"Input kind '{request.InputKind}' is not supported by the reference executor yet.");
            }

            var prepared = _snapshots.PrepareInput(request);
            var actualRootIntentHash = EngineSemanticSnapshotHash.RootIntent(prepared.RootIntent);
            if (!string.Equals(request.RootIntentHash, actualRootIntentHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The supplied root-intent basis does not match the authoritative root-intent snapshot.");
            }

            var input = prepared.Input;
            var actualGraphHash = EngineSemanticSnapshotHash.ExpandedGraph(prepared.ExpandedGraph);
            if (!string.Equals(request.ExpandedGraphHash, actualGraphHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The supplied expanded-graph basis does not match the authoritative reference graph.");
            }
            if (input.MarketAnalysis is null && input.ProcurementRoute is null)
            {
                throw new InvalidOperationException("The reference engine requires at least one analysis or procurement operation.");
            }
            ValidateBudgets(request.Budgets, input);
            var successPhases = EngineSuccessPhasePolicy.Resolve(
                request.InputKind,
                input.MarketAnalysis is not null,
                input.ProcurementRoute is not null);

            phase = EnginePhase.ConstructingOrRestoringGraph;
            Report(progress, request, phase, 2, 12, "Preparing graph basis.");
            cancellationToken.ThrowIfCancellationRequested();

            MarketAnalysisExecutionResult? analysis = null;
            if (input.MarketAnalysis is not null)
            {
                phase = EnginePhase.ResolvingEvidence;
                Report(progress, request, phase, 3, 12, "Resolving market evidence.");
                phase = EnginePhase.Analyzing;
                analysis = await _marketAnalysis.ExecuteAsync(input.MarketAnalysis, ct: cancellationToken);
            }

            ProcurementRouteExecutionResult? route = null;
            if (input.ProcurementRoute is not null)
            {
                phase = EnginePhase.Reconciling;
                Report(progress, request, phase, 5, 12, "Reconciling procurement evidence.");
                route = await _procurementRoute.AnalyzeAsync(input.ProcurementRoute, ct: cancellationToken);
            }

            var analysisSnapshot = analysis is null ? null : _snapshots.CaptureAnalysis(analysis);
            var routeSnapshot = route is null ? null : _snapshots.CaptureRoute(route);
            analysisHash = analysisSnapshot is null ? string.Empty : EngineSemanticSnapshotHash.Analysis(analysisSnapshot);
            routeHash = routeSnapshot is null ? string.Empty : EngineSemanticSnapshotHash.Route(routeSnapshot);
            output = new ReferenceEngineOutput(analysis, route);
            if (routeSnapshot is { IsComplete: false })
            {
                throw new InvalidOperationException("The procurement route does not provide a viable acquisition for every requested item.");
            }
            var resultElement = JsonSerializer.SerializeToElement(new
            {
                MarketAnalysis = analysisSnapshot,
                ProcurementRoute = routeSnapshot
            });
            if (analysis is not null)
            {
                settlementEvidence["phase:Analyzing"] = "complete";
            }
            if (route is not null)
            {
                settlementEvidence["phase:Reconciling"] = "complete";
            }
            var settlementContext = new EngineSettlementContext(request, output, analysisHash, routeHash);

            foreach (var settlementPhase in successPhases.SettlementPhases)
            {
                phase = settlementPhase;
                Report(progress, request, phase, ProgressIndex(phase), 12, PhaseMessage(phase));
                var evidence = await _settlement.SettleAsync(phase, settlementContext, cancellationToken);
                if (!evidence.Completed)
                {
                    throw new InvalidOperationException($"Settlement phase '{phase}' did not complete: {evidence.Evidence}");
                }
                settlementEvidence[$"phase:{phase}"] = evidence.Evidence;
                settledPhases.Add(phase);
            }

            foreach (var requiredPhase in successPhases.RequiredEvidencePhases)
            {
                if (!settlementEvidence.TryGetValue($"phase:{requiredPhase}", out var evidence) ||
                    string.IsNullOrWhiteSpace(evidence))
                {
                    throw new InvalidOperationException($"Successful engine result is missing required phase evidence for '{requiredPhase}'.");
                }
            }

            var terminalEvidence = new Dictionary<string, string>(settlementEvidence, StringComparer.Ordinal)
            {
                ["resultPayloadHash"] = EngineCanonicalHash.Compute(resultElement),
                ["settlement"] = "complete"
            };
            var finalHash = EngineCanonicalHash.ComputeFinalTransactionHash(
                request,
                EngineTerminalStatus.Succeeded,
                analysisHash,
                routeHash,
                terminalEvidence);
            var completion = new EngineCompletionEvidence(
                ContractVersion,
                request.TransactionId,
                EngineTerminalStatus.Succeeded,
                EnginePhase.Completed,
                request.Basis,
                request.RootIntentHash,
                request.ExpandedGraphHash,
                analysisHash,
                routeHash,
                finalHash,
                terminalEvidence);
            Report(progress, request, EnginePhase.Completed, 12, 12, "Transaction completed.");
            return new EngineResultEnvelope(ContractVersion, request.TransactionId, EngineTerminalStatus.Succeeded, resultElement, completion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RunTerminalSettlementAsync(new EngineSettlementContext(request, output, analysisHash, routeHash), settledPhases, settlementEvidence);
            return CreateTerminal(request, EngineTerminalStatus.Cancelled, EnginePhase.Cancelled, phase, "cancelled", "The transaction was cancelled.", nameof(OperationCanceledException), analysisHash, routeHash, settlementEvidence);
        }
        catch (Exception ex)
        {
            await RunTerminalSettlementAsync(new EngineSettlementContext(request, output, analysisHash, routeHash), settledPhases, settlementEvidence);
            return CreateTerminal(request, EngineTerminalStatus.Failed, EnginePhase.Failed, phase, "unhandled", ex.Message, ex.GetType().FullName ?? ex.GetType().Name, analysisHash, routeHash, settlementEvidence);
        }
    }

    private async Task RunTerminalSettlementAsync(
        EngineSettlementContext context,
        IReadOnlySet<EnginePhase> settledPhases,
        IDictionary<string, string> settlementEvidence)
    {
        foreach (var terminalPhase in TerminalSettlementPhases)
        {
            if (settledPhases.Contains(terminalPhase))
            {
                continue;
            }

            try
            {
                var evidence = await _settlement.SettleAsync(terminalPhase, context, CancellationToken.None);
                settlementEvidence[$"cleanup:{terminalPhase}"] = evidence.Completed
                    ? evidence.Evidence
                    : $"failed:{evidence.Evidence}";
            }
            catch (Exception ex)
            {
                settlementEvidence[$"cleanup:{terminalPhase}"] = $"failed:{ex.GetType().Name}";
            }
        }
    }

    private static void ValidateBudgets(EngineExecutionBudgets budgets, ReferenceEngineInput input)
    {
        if (budgets.MaxWorkUnits <= 0 || budgets.MaxEvidenceRequests < 0 ||
            budgets.MaxCandidateEvaluations <= 0 || budgets.CooperativeCancellationInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budgets), "Engine budgets must be positive (evidence requests may be zero).");
        }

        if (budgets.MaxCandidateEvaluations != EngineExecutionBudgets.Default.MaxCandidateEvaluations ||
            budgets.CooperativeCancellationInterval != EngineExecutionBudgets.Default.CooperativeCancellationInterval)
        {
            throw new NotSupportedException("Candidate-evaluation and cooperative-cancellation budgets are not supported by the reference executor yet; use their default values.");
        }

        var operationCount = (input.MarketAnalysis is null ? 0 : 1) + (input.ProcurementRoute is null ? 0 : 1);
        if (operationCount > budgets.MaxWorkUnits)
        {
            throw new InvalidOperationException("The request exceeds its engine work-unit budget.");
        }

        var evidenceItems = (input.MarketAnalysis?.Items.Count ?? 0) +
            (input.ProcurementRoute?.ActiveProcurementItems.Count ?? 0);
        if (evidenceItems > budgets.MaxEvidenceRequests)
        {
            throw new InvalidOperationException("The request exceeds its market-evidence request budget.");
        }
    }

    private static readonly EnginePhase[] TerminalSettlementPhases =
    [
        EnginePhase.ReleasingGate,
        EnginePhase.SettlingUi,
        EnginePhase.CapturingPostActionEvidence,
        EnginePhase.CapturingRestorationEvidence
    ];

    private static EngineResultEnvelope CreateTerminal(
        EngineRequestEnvelope request,
        EngineTerminalStatus status,
        EnginePhase terminalPhase,
        EnginePhase failedPhase,
        string code,
        string message,
        string failureType,
        string analysisResultHash,
        string routeResultHash,
        IReadOnlyDictionary<string, string> settlementEvidence)
    {
        var evidence = new Dictionary<string, string>(settlementEvidence, StringComparer.Ordinal)
        {
            ["terminalCode"] = code,
            ["failedPhase"] = failedPhase.ToString(),
            ["failureType"] = failureType,
            ["failureMessageHash"] = EngineCanonicalHash.Compute(message.Trim()),
            ["isRetryable"] = bool.FalseString
        };
        var finalHash = EngineCanonicalHash.ComputeFinalTransactionHash(request, status, analysisResultHash, routeResultHash, evidence);
        var completion = new EngineCompletionEvidence(
            ContractVersion,
            request.TransactionId,
            status,
            terminalPhase,
            request.Basis,
            request.RootIntentHash,
            request.ExpandedGraphHash,
            analysisResultHash,
            routeResultHash,
            finalHash,
            evidence);
        var failure = status == EngineTerminalStatus.Failed
            ? new EngineFailure(code, message, false, failedPhase, failureType)
            : null;
        return new EngineResultEnvelope(ContractVersion, request.TransactionId, status, null, completion, failure);
    }

    private static void Report(IProgress<EngineProgress>? progress, EngineRequestEnvelope request, EnginePhase phase, int completed, int total, string message) =>
        progress?.Report(new EngineProgress(request.TransactionId, phase, completed, total, message));

    private static int ProgressIndex(EnginePhase phase) => phase switch
    {
        EnginePhase.Publishing => 6,
        EnginePhase.SettlingRoute => 7,
        EnginePhase.Persisting => 8,
        EnginePhase.ReleasingGate => 9,
        EnginePhase.SettlingUi => 10,
        _ => 11
    };

    private static string PhaseMessage(EnginePhase phase) => phase switch
    {
        EnginePhase.Publishing => "Publishing analysis.",
        EnginePhase.SettlingRoute => "Settling procurement route.",
        EnginePhase.Persisting => "Persisting and autosaving.",
        EnginePhase.ReleasingGate => "Releasing operation gate.",
        EnginePhase.SettlingUi => "Settling UI state.",
        EnginePhase.CapturingPostActionEvidence => "Capturing post-action evidence.",
        EnginePhase.CapturingRestorationEvidence => "Capturing restoration evidence.",
        _ => phase.ToString()
    };
}
