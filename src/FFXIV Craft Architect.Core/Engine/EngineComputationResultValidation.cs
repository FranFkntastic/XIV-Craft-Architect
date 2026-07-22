using System.Text.Json;

namespace FFXIV_Craft_Architect.Core.Engine;

public static class EngineComputationResultValidation
{
    public static EngineComputationResult Validate(
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        EngineComputationResult computation,
        IReferenceEngineSemanticSnapshotProvider snapshots)
    {
        return ValidateCore(
            generation,
            executionId,
            request,
            computation,
            snapshots,
            preparedResult: null);
    }

    internal static PreparedEngineComputationResult? PrepareCompletedResult(
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        EngineComputationResult computation,
        IReferenceEngineSemanticSnapshotProvider snapshots)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(computation);
        ArgumentNullException.ThrowIfNull(snapshots);
        ValidateCorrelatedHeader(generation, executionId, request, computation);
        if (computation.Status != EngineComputationStatus.Completed)
        {
            return null;
        }
        if (computation.Result is not { } result || computation.Failure is not null)
        {
            throw new InvalidOperationException("Completed engine computation evidence is invalid.");
        }

        var transported = snapshots.CaptureTransportedResult(result);
        var derivedRoute = transported.ProcurementRouteResult is null
            ? null
            : snapshots.CaptureRoute(transported.ProcurementRouteResult);
        if (transported.ProcurementRoute is not null &&
            derivedRoute is not null &&
            !string.Equals(
                EngineSemanticSnapshotHash.Route(transported.ProcurementRoute),
                EngineSemanticSnapshotHash.Route(derivedRoute),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The transported procurement result does not match its semantic route snapshot.");
        }
        return new PreparedEngineComputationResult(
            transported with { ProcurementRoute = derivedRoute ?? transported.ProcurementRoute });
    }

    internal static EngineComputationResult ValidatePrepared(
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        EngineComputationResult computation,
        IReferenceEngineSemanticSnapshotProvider snapshots,
        PreparedEngineComputationResult? preparedResult) =>
        ValidateCore(generation, executionId, request, computation, snapshots, preparedResult);

    private static EngineComputationResult ValidateCore(
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        EngineComputationResult computation,
        IReferenceEngineSemanticSnapshotProvider snapshots,
        PreparedEngineComputationResult? preparedResult)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(computation);
        ArgumentNullException.ThrowIfNull(snapshots);
        ValidateCorrelatedHeader(generation, executionId, request, computation);
        computation = computation with
        {
            ComputationEvidence = EngineEvidenceSnapshots.Freeze(computation.ComputationEvidence)
        };
        var input = request.Input.Deserialize<ReferenceEngineInput>(EngineJsonSerializerOptions.CreateWire())
            ?? throw new InvalidOperationException("Cannot validate engine computation phases for the request.");
        var includesMarketAnalysis = input.MarketAnalysis is not null;
        var includesProcurementRoute = input.ProcurementRoute is not null;
        if (!EnginePhaseValidation.IsComputationPhaseAllowed(
                computation.FinalPhase,
                includesMarketAnalysis,
                includesProcurementRoute))
        {
            if (computation.Status == EngineComputationStatus.Cancelled)
            {
                throw new EngineCancellationPhaseException("Cancelled computation reported an impossible cancellation phase.");
            }
            throw new InvalidOperationException("Engine computation final phase is invalid.");
        }
        EnginePhaseValidation.ValidateOrderedPhaseEvidence(
            computation.ComputationEvidence,
            request.InputKind,
            includesMarketAnalysis,
            includesProcurementRoute,
            includeSettlementPhases: false);

        foreach (var key in computation.ComputationEvidence.Keys)
        {
            var allowed = key is "phase:Analyzing" or "phase:Reconciling" ||
                computation.Status == EngineComputationStatus.Completed && key == "resultPayloadHash" ||
                computation.Status != EngineComputationStatus.Completed &&
                key is ("terminalCode" or "failedPhase" or "failureType" or "failureMessageHash" or "isRetryable");
            if (!allowed)
            {
                throw new InvalidOperationException($"Engine computation evidence key '{key}' is not authoritative for computation.");
            }
        }

        var payloadHash = string.Empty;
        ReferenceEngineResultSnapshot? validatedTransportedResult = null;
        if (computation.Status == EngineComputationStatus.Completed)
        {
            if (computation.Result is not { } result || computation.Failure is not null)
            {
                throw new InvalidOperationException("Completed engine computation evidence is invalid.");
            }

            var transported = (preparedResult ?? PrepareCompletedResult(
                generation,
                executionId,
                request,
                computation,
                snapshots))!.TransportedResult;

            if ((transported.MarketAnalysis is not null) != (input.MarketAnalysis is not null) ||
                (transported.ProcurementRoute is not null) != includesProcurementRoute ||
                (transported.ProcurementRouteResult is not null) != includesProcurementRoute)
            {
                throw new InvalidOperationException("Transported engine result operations do not match the request.");
            }

            var analysisHash = transported.MarketAnalysis is null
                ? string.Empty
                : EngineSemanticSnapshotHash.Analysis(transported.MarketAnalysis);
            var routeHash = transported.ProcurementRoute is null
                ? string.Empty
                : EngineSemanticSnapshotHash.Route(transported.ProcurementRoute);
            if (!string.Equals(computation.AnalysisResultHash, analysisHash, StringComparison.Ordinal) ||
                !string.Equals(computation.ProcurementRouteResultHash, routeHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Engine semantic result hashes do not match the transported payload.");
            }
            payloadHash = string.IsNullOrWhiteSpace(request.InputHash)
                ? EngineCanonicalHash.Compute(result)
                : EngineCanonicalHash.ComputeAuthoritativeResultPayloadHash(analysisHash, routeHash);
            if (!computation.ComputationEvidence.TryGetValue("resultPayloadHash", out var claimedPayloadHash) ||
                !string.Equals(claimedPayloadHash, payloadHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Completed engine computation payload hash is invalid.");
            }
            validatedTransportedResult = transported;

            var expectedFinalPhase = input.ProcurementRoute is not null
                ? EnginePhase.Reconciling
                : EnginePhase.Analyzing;
            if (computation.FinalPhase != expectedFinalPhase)
            {
                throw new InvalidOperationException("Completed engine computation reported an impossible final phase.");
            }
            if (input.MarketAnalysis is not null)
            {
                ValidatePhaseEvidence(computation, EnginePhase.Analyzing);
            }
            if (input.ProcurementRoute is not null)
            {
                ValidatePhaseEvidence(computation, EnginePhase.Reconciling);
            }
        }
        else if (computation.Status is EngineComputationStatus.Cancelled or EngineComputationStatus.Failed)
        {
            if (computation.Result is not null ||
                (computation.Status == EngineComputationStatus.Failed) != (computation.Failure is not null) ||
                !string.IsNullOrEmpty(computation.AnalysisResultHash) ||
                !string.IsNullOrEmpty(computation.ProcurementRouteResultHash))
            {
                throw new InvalidOperationException("Terminal engine computation evidence is inconsistent.");
            }
            if (!computation.ComputationEvidence.TryGetValue("failedPhase", out var failedPhase) ||
                !string.Equals(failedPhase, computation.FinalPhase.ToString(), StringComparison.Ordinal) ||
                computation.Failure is { } failure && failure.FailedPhase != computation.FinalPhase)
            {
                throw new InvalidOperationException("Terminal engine computation did not preserve its actual phase.");
            }
            if (!computation.ComputationEvidence.TryGetValue("terminalCode", out var terminalCode) ||
                !computation.ComputationEvidence.TryGetValue("failureType", out var failureType) ||
                !computation.ComputationEvidence.TryGetValue("failureMessageHash", out var failureMessageHash) ||
                !computation.ComputationEvidence.TryGetValue("isRetryable", out var isRetryable) ||
                computation.Status == EngineComputationStatus.Failed &&
                (computation.Failure is not { } terminalFailure ||
                 !string.Equals(terminalCode, terminalFailure.Code, StringComparison.Ordinal) ||
                 !string.Equals(failureType, terminalFailure.FailureType, StringComparison.Ordinal) ||
                 !string.Equals(failureMessageHash, EngineCanonicalHash.Compute(terminalFailure.Message.Trim()), StringComparison.Ordinal) ||
                 !string.Equals(isRetryable, terminalFailure.IsRetryable.ToString(), StringComparison.Ordinal)) ||
                computation.Status == EngineComputationStatus.Cancelled &&
                (!string.Equals(terminalCode, "cancelled", StringComparison.Ordinal) ||
                 string.IsNullOrWhiteSpace(failureType) ||
                 string.IsNullOrWhiteSpace(failureMessageHash) ||
                 !bool.TryParse(isRetryable, out _)))
            {
                throw new InvalidOperationException("Terminal engine computation failure evidence is inconsistent.");
            }
            if (computation.Status == EngineComputationStatus.Cancelled &&
                (!EnginePhaseValidation.TryParseCancellationPhase(failedPhase, out var parsedPhase) ||
                  parsedPhase != computation.FinalPhase ||
                  !EnginePhaseValidation.IsComputationPhaseAllowed(
                      parsedPhase,
                      includesMarketAnalysis,
                      includesProcurementRoute)))
            {
                throw new EngineCancellationPhaseException("Cancelled computation reported an impossible cancellation phase.");
            }
        }
        else
        {
            throw new InvalidOperationException("Engine computation status is not supported.");
        }

        EnginePhaseValidation.ValidatePhaseEvidenceThroughPhase(
            computation.ComputationEvidence,
            request.InputKind,
            includesMarketAnalysis,
            includesProcurementRoute,
            includeSettlementPhases: false,
            computation.FinalPhase);

        var expectedComputationHash = EngineCanonicalHash.ComputeComputationHash(
            generation,
            executionId,
            request,
            computation.Status,
            computation.FinalPhase,
            payloadHash,
            computation.AnalysisResultHash,
            computation.ProcurementRouteResultHash,
            computation.ComputationEvidence,
            computation.Failure);
        if (!string.Equals(computation.ComputationHash, expectedComputationHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Engine computation hash validation failed.");
        }
        return computation with { ValidatedTransportedResult = validatedTransportedResult };
    }

    private static void ValidatePhaseEvidence(EngineComputationResult computation, EnginePhase phase)
    {
        if (!computation.ComputationEvidence.TryGetValue($"phase:{phase}", out var phaseEvidence) ||
            string.IsNullOrWhiteSpace(phaseEvidence))
        {
            throw new InvalidOperationException(
                $"Completed engine computation is missing required phase evidence for '{phase}'.");
        }
    }

    private static void AddMismatch(ICollection<string> mismatches, string name, bool mismatched)
    {
        if (mismatched)
        {
            mismatches.Add(name);
        }
    }

    private static void ValidateCorrelatedHeader(
        long generation,
        Guid executionId,
        EngineRequestEnvelope request,
        EngineComputationResult computation)
    {
        if (computation.Generation != generation || computation.ExecutionId != executionId)
        {
            throw new InvalidOperationException("Stale engine computation identity was rejected.");
        }
        if (!string.Equals(computation.ContractVersion, request.ContractVersion, StringComparison.Ordinal))
        {
            throw new EngineComputationContractException(
                "Engine computation contract version does not exactly match its request.");
        }
        if (!Enum.IsDefined(computation.Status))
        {
            throw new InvalidOperationException("Engine computation status is invalid.");
        }

        var inputHash = EngineCanonicalHash.ResolveEngineInputHash(request);
        var identityMismatches = new List<string>();
        AddMismatch(identityMismatches, "transaction", computation.TransactionId != request.TransactionId);
        AddMismatch(identityMismatches, "basis", computation.Basis != request.Basis);
        AddMismatch(identityMismatches, "input", !string.Equals(computation.RequestInputHash, inputHash, StringComparison.Ordinal));
        AddMismatch(identityMismatches, "budgets", computation.Budgets != request.Budgets);
        AddMismatch(identityMismatches, "root-intent", !string.Equals(computation.RootIntentHash, request.RootIntentHash, StringComparison.Ordinal));
        AddMismatch(identityMismatches, "expanded-graph", !string.Equals(computation.ExpandedGraphHash, request.ExpandedGraphHash, StringComparison.Ordinal));
        AddMismatch(identityMismatches, "analysis-basis", !string.Equals(computation.AnalysisBasisHash, request.AnalysisBasisHash, StringComparison.Ordinal));
        AddMismatch(identityMismatches, "route-basis", !string.Equals(computation.RouteBasisHash, request.RouteBasisHash, StringComparison.Ordinal));
        if (identityMismatches.Count > 0)
        {
            throw new InvalidOperationException(
                $"Engine computation evidence does not match its authoritative request: {string.Join(", ", identityMismatches)}.");
        }
    }
}

internal sealed record PreparedEngineComputationResult(
    ReferenceEngineResultSnapshot TransportedResult);

internal sealed class EngineComputationContractException(string message) : InvalidOperationException(message);

internal sealed class EngineCancellationPhaseException(string message) : InvalidOperationException(message);
