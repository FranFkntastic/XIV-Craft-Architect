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

public interface IEngineTransactionSettlement
{
    Task SettleAsync(EnginePhase phase, EngineRequestEnvelope request, CancellationToken cancellationToken);
}

public sealed class NoOpEngineTransactionSettlement : IEngineTransactionSettlement
{
    public Task SettleAsync(EnginePhase phase, EngineRequestEnvelope request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed class ReferenceMarketProcurementEngine : IMarketProcurementEngine
{
    private const string ContractVersion = "1";
    private readonly IMarketAnalysisExecutionService _marketAnalysis;
    private readonly IProcurementRouteExecutionService _procurementRoute;
    private readonly IEngineTransactionSettlement _settlement;

    public ReferenceMarketProcurementEngine(
        IMarketAnalysisExecutionService marketAnalysis,
        IProcurementRouteExecutionService procurementRoute,
        IEngineTransactionSettlement settlement)
    {
        _marketAnalysis = marketAnalysis ?? throw new ArgumentNullException(nameof(marketAnalysis));
        _procurementRoute = procurementRoute ?? throw new ArgumentNullException(nameof(procurementRoute));
        _settlement = settlement ?? throw new ArgumentNullException(nameof(settlement));
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
        try
        {
            Report(progress, request, phase, 0, 12, "Transaction accepted.");
            cancellationToken.ThrowIfCancellationRequested();

            phase = EnginePhase.InterpretingInput;
            Report(progress, request, phase, 1, 12, "Interpreting engine input.");
            var input = request.Input.Deserialize<ReferenceEngineInput>(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException("The reference engine input is empty.");
            ValidateBudgets(request.Budgets, input);

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

            var analysisHash = analysis is null ? string.Empty : EngineCanonicalHash.Compute(analysis);
            var routeHash = route is null ? string.Empty : EngineCanonicalHash.Compute(route);
            var output = new ReferenceEngineOutput(analysis, route);
            var resultElement = JsonSerializer.SerializeToElement(output);

            foreach (var settlementPhase in SettlementPhases)
            {
                phase = settlementPhase;
                Report(progress, request, phase, ProgressIndex(phase), 12, PhaseMessage(phase));
                await _settlement.SettleAsync(phase, request, cancellationToken);
                settledPhases.Add(phase);
            }

            var terminalEvidence = new Dictionary<string, string>(StringComparer.Ordinal)
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
            await RunTerminalSettlementAsync(request, settledPhases);
            return CreateTerminal(request, EngineTerminalStatus.Cancelled, EnginePhase.Cancelled, phase, "cancelled", "The transaction was cancelled.");
        }
        catch (Exception ex)
        {
            await RunTerminalSettlementAsync(request, settledPhases);
            return CreateTerminal(request, EngineTerminalStatus.Failed, EnginePhase.Failed, phase, "unhandled", ex.Message);
        }
    }

    private async Task RunTerminalSettlementAsync(
        EngineRequestEnvelope request,
        IReadOnlySet<EnginePhase> settledPhases)
    {
        foreach (var terminalPhase in TerminalSettlementPhases)
        {
            if (settledPhases.Contains(terminalPhase))
            {
                continue;
            }

            try
            {
                await _settlement.SettleAsync(terminalPhase, request, CancellationToken.None);
            }
            catch
            {
                // Preserve the original terminal outcome; settlement failures are reflected by missing evidence.
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

    private static readonly EnginePhase[] SettlementPhases =
    [
        EnginePhase.Publishing,
        EnginePhase.SettlingRoute,
        EnginePhase.Persisting,
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
        string message)
    {
        var evidence = new Dictionary<string, string>(StringComparer.Ordinal) { ["terminalCode"] = code };
        var finalHash = EngineCanonicalHash.ComputeFinalTransactionHash(request, status, string.Empty, string.Empty, evidence);
        var completion = new EngineCompletionEvidence(
            ContractVersion,
            request.TransactionId,
            status,
            terminalPhase,
            request.Basis,
            request.RootIntentHash,
            request.ExpandedGraphHash,
            string.Empty,
            string.Empty,
            finalHash,
            evidence);
        var failure = status == EngineTerminalStatus.Failed
            ? new EngineFailure(code, message, false, failedPhase)
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
