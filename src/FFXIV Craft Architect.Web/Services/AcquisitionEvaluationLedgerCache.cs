namespace FFXIV_Craft_Architect.Web.Services;

public sealed record AcquisitionEvaluationLedgerKey(
    long PlanStructureVersion,
    long PlanDecisionVersion,
    long PlanPriceVersion,
    long MarketAnalysisVersion);

public sealed class AcquisitionEvaluationLedgerCache
{
    private const AppStateChangeScope RelevantScopes =
        AppStateChangeScope.PlanStructure |
        AppStateChangeScope.PlanDecision |
        AppStateChangeScope.PlanPrice |
        AppStateChangeScope.MarketAnalysis;

    private AcquisitionEvaluationSnapshot? _snapshot;
    private AcquisitionEvaluationLedgerKey? _key;
    private AcquisitionFilter? _filter;

    public int BuildCount { get; private set; }

    public static bool IsRelevantStateChange(AppStateChangeScope scopes)
    {
        return (scopes & RelevantScopes) != AppStateChangeScope.None;
    }

    public AcquisitionEvaluationSnapshot GetOrBuild(
        AcquisitionEvaluationLedgerKey key,
        AcquisitionFilter filter,
        Func<AcquisitionEvaluationSnapshot> build)
    {
        if (_snapshot == null || _key != key)
        {
            _snapshot = build();
            _key = key;
            _filter = filter;
            BuildCount++;
            return _snapshot;
        }

        if (_filter != filter)
        {
            _snapshot = _snapshot with
            {
                VisibleRows = AcquisitionEvaluationSnapshotBuilder.ApplyFilter(_snapshot.Rows, filter).ToList()
            };
            _filter = filter;
        }

        return _snapshot;
    }

    public void Invalidate()
    {
        _snapshot = null;
        _key = null;
        _filter = null;
    }
}
