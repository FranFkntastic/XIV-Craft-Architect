namespace FFXIV_Craft_Architect.Core.Engine;

public sealed record EngineSuccessPhaseRequirements(
    IReadOnlyList<EnginePhase> RequiredEvidencePhases,
    IReadOnlyList<EnginePhase> SettlementPhases);

public static class EngineSuccessPhasePolicy
{
    public const EnginePhase CommitPhase = EnginePhase.Persisting;

    public static EngineSuccessPhaseRequirements Resolve(
        EngineInputKind inputKind,
        bool includesMarketAnalysis,
        bool includesProcurementRoute)
    {
        var requiredEvidencePhases = new List<EnginePhase>();
        if (includesMarketAnalysis)
        {
            requiredEvidencePhases.Add(EnginePhase.Analyzing);
        }
        if (includesProcurementRoute)
        {
            requiredEvidencePhases.Add(EnginePhase.Reconciling);
        }

        var settlementPhases = new List<EnginePhase>
        {
            EnginePhase.Publishing
        };
        if (includesProcurementRoute)
        {
            settlementPhases.Add(EnginePhase.SettlingRoute);
        }
        settlementPhases.Add(EnginePhase.Persisting);
        settlementPhases.Add(EnginePhase.SettlingUi);
        settlementPhases.Add(EnginePhase.CapturingPostActionEvidence);
        if (inputKind == EngineInputKind.RestoredSession)
        {
            settlementPhases.Add(EnginePhase.CapturingRestorationEvidence);
        }
        settlementPhases.Add(EnginePhase.ReleasingGate);

        requiredEvidencePhases.AddRange(settlementPhases);
        return new EngineSuccessPhaseRequirements(
            requiredEvidencePhases.ToArray(),
            settlementPhases.ToArray());
    }
}
