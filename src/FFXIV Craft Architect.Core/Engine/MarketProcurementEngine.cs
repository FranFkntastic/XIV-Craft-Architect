using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Engine;

public interface IMarketProcurementEngine
{
    Task<EngineComputationResult> ComputeAsync(
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record ReferenceEngineInput(
    MarketAnalysisExecutionRequest? MarketAnalysis,
    ProcurementRouteExecutionRequest? ProcurementRoute);

public sealed class ReferenceMarketProcurementEngine : IMarketProcurementEngine
{
    private const string ContractVersion = "1";
    private readonly IMarketAnalysisExecutionService _marketAnalysis;
    private readonly IProcurementRouteExecutionService _procurementRoute;
    private readonly IReferenceEngineSemanticSnapshotProvider _snapshots;

    public ReferenceMarketProcurementEngine(
        IMarketAnalysisExecutionService marketAnalysis,
        IProcurementRouteExecutionService procurementRoute,
        IReferenceEngineSemanticSnapshotProvider snapshots)
    {
        _marketAnalysis = marketAnalysis ?? throw new ArgumentNullException(nameof(marketAnalysis));
        _procurementRoute = procurementRoute ?? throw new ArgumentNullException(nameof(procurementRoute));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    }

    public async Task<EngineComputationResult> ComputeAsync(
        long generation,
        Guid executionId,
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
        var computationEvidence = new Dictionary<string, string>(StringComparer.Ordinal);
        var analysisHash = string.Empty;
        var routeHash = string.Empty;
        try
        {
            Report(progress, generation, executionId, request, phase, 0, 12, "Transaction accepted.");
            cancellationToken.ThrowIfCancellationRequested();

            phase = EnginePhase.InterpretingInput;
            Report(progress, generation, executionId, request, phase, 1, 12, "Interpreting engine input.");
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
            phase = EnginePhase.ConstructingOrRestoringGraph;
            Report(progress, generation, executionId, request, phase, 2, 12, "Preparing graph basis.");
            cancellationToken.ThrowIfCancellationRequested();

            MarketAnalysisExecutionResult? analysis = null;
            if (input.MarketAnalysis is not null)
            {
                phase = EnginePhase.ResolvingEvidence;
                Report(progress, generation, executionId, request, phase, 3, 12, "Resolving market evidence.");
                phase = EnginePhase.Analyzing;
                analysis = await _marketAnalysis.ExecuteAsync(input.MarketAnalysis, ct: cancellationToken);
                computationEvidence["phase:Analyzing"] = "complete";
            }

            ProcurementRouteExecutionResult? route = null;
            if (input.ProcurementRoute is not null)
            {
                phase = EnginePhase.Reconciling;
                Report(progress, generation, executionId, request, phase, 5, 12, "Reconciling procurement evidence.");
                route = await _procurementRoute.AnalyzeAsync(input.ProcurementRoute, ct: cancellationToken);
                computationEvidence["phase:Reconciling"] = "complete";
            }

            var analysisSnapshot = analysis is null ? null : _snapshots.CaptureAnalysis(analysis);
            var routeSnapshot = route is null ? null : _snapshots.CaptureRoute(route);
            analysisHash = analysisSnapshot is null ? string.Empty : EngineSemanticSnapshotHash.Analysis(analysisSnapshot);
            routeHash = routeSnapshot is null ? string.Empty : EngineSemanticSnapshotHash.Route(routeSnapshot);
            if (routeSnapshot is { IsComplete: false })
            {
                throw new InvalidOperationException("The procurement route does not provide a viable acquisition for every requested item.");
            }
            var resultElement = JsonSerializer.SerializeToElement(
                new ReferenceEngineResultSnapshot(analysisSnapshot, routeSnapshot),
                EngineJsonSerializerOptions.CreateWire());
            computationEvidence["resultPayloadHash"] = EngineCanonicalHash.Compute(resultElement);
            var computationHash = EngineCanonicalHash.ComputeComputationHash(
                generation,
                executionId,
                request,
                EngineComputationStatus.Completed,
                phase,
                computationEvidence["resultPayloadHash"],
                analysisHash,
                routeHash,
                computationEvidence,
                null);
            return new EngineComputationResult(
                request.ContractVersion,
                generation,
                executionId,
                request.TransactionId,
                EngineComputationStatus.Completed,
                phase,
                resultElement,
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
                computationEvidence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CreateTerminal(generation, executionId, request, EngineComputationStatus.Cancelled, phase, "cancelled", "The computation was cancelled.", nameof(OperationCanceledException), computationEvidence);
        }
        catch (Exception ex)
        {
            return CreateTerminal(generation, executionId, request, EngineComputationStatus.Failed, phase, "unhandled", ex.Message, ex.GetType().FullName ?? ex.GetType().Name, computationEvidence);
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

    private static EngineComputationResult CreateTerminal(
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        EngineComputationStatus status,
        EnginePhase failedPhase,
        string code,
        string message,
        string failureType,
        IReadOnlyDictionary<string, string> computationEvidence)
    {
        var evidence = new Dictionary<string, string>(computationEvidence, StringComparer.Ordinal)
        {
            ["terminalCode"] = code,
            ["failedPhase"] = failedPhase.ToString(),
            ["failureType"] = failureType,
            ["failureMessageHash"] = EngineCanonicalHash.Compute(message.Trim()),
            ["isRetryable"] = bool.FalseString
        };
        var failure = status == EngineComputationStatus.Failed
            ? new EngineFailure(code, message, false, failedPhase, failureType)
            : null;
        var computationHash = EngineCanonicalHash.ComputeComputationHash(
            generation,
            executionId,
            request,
            status,
            failedPhase,
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
            status,
            failedPhase,
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

    private static void Report(
        IProgress<EngineProgress>? progress,
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        EnginePhase phase,
        int completed,
        int total,
        string message) =>
        progress?.Report(new EngineProgress(request.TransactionId, generation, executionId, phase, completed, total, message));

}
