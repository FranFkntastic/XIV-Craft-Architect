namespace FFXIV_Craft_Architect.Web.Services;

public sealed record AcquisitionEvaluationLedgerKey(
    long PlanStructureVersion,
    long PlanDecisionVersion,
    long PlanPriceVersion,
    long MarketAnalysisVersion,
    long ProcurementOverlayVersion);

public sealed class AcquisitionEvaluationLedgerCache
{
    private const AppStateChangeScope RelevantScopes =
        AppStateChangeScope.PlanStructure |
        AppStateChangeScope.PlanDecision |
        AppStateChangeScope.PlanPrice |
        AppStateChangeScope.MarketAnalysis |
        AppStateChangeScope.ProcurementOverlay;

    private AcquisitionEvaluationSnapshot? _snapshot;
    private AcquisitionEvaluationLedgerKey? _key;
    private AcquisitionFilter? _filter;

    public int BuildCount { get; private set; }

    public static bool IsRelevantStateChange(AppStateChangeScope scopes)
    {
        return (scopes & RelevantScopes) != AppStateChangeScope.None;
    }

    public bool TryGet(
        AcquisitionEvaluationLedgerKey key,
        AcquisitionFilter filter,
        out AcquisitionEvaluationSnapshot snapshot)
    {
        if (_snapshot == null || _key != key)
        {
            snapshot = null!;
            return false;
        }

        if (_filter != filter)
        {
            _snapshot = _snapshot with
            {
                VisibleRows = AcquisitionEvaluationSnapshotBuilder.ApplyFilter(_snapshot.Rows, filter).ToList()
            };
            _filter = filter;
        }

        snapshot = _snapshot;
        return true;
    }

    public AcquisitionEvaluationSnapshot Store(
        AcquisitionEvaluationLedgerKey key,
        AcquisitionFilter filter,
        AcquisitionEvaluationSnapshot snapshot)
    {
        _snapshot = snapshot;
        _key = key;
        _filter = filter;
        BuildCount++;
        return snapshot;
    }

    public AcquisitionEvaluationSnapshot GetOrBuild(
        AcquisitionEvaluationLedgerKey key,
        AcquisitionFilter filter,
        Func<AcquisitionEvaluationSnapshot> build)
    {
        return TryGet(key, filter, out var snapshot)
            ? snapshot
            : Store(key, filter, build());
    }

    public void Invalidate()
    {
        _snapshot = null;
        _key = null;
        _filter = null;
    }
}
