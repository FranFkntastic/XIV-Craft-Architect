using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed record CoreAcquisitionEvaluationLedgerKey(
    long PlanCoreVersion,
    long PlanDecisionVersion,
    long PlanPriceVersion,
    long MarketAnalysisVersion)
{
    public static CoreAcquisitionEvaluationLedgerKey FromStamp(CraftSessionVersionStamp stamp) =>
        new(stamp.PlanCore, stamp.PlanDecision, stamp.PlanPrice, stamp.MarketAnalysis);
}

public sealed class CoreAcquisitionEvaluationLedgerCache
{
    private const CraftSessionChangeScope RelevantScopes =
        CraftSessionChangeScope.PlanCore |
        CraftSessionChangeScope.PlanDecision |
        CraftSessionChangeScope.MarketAnalysis;

    private CoreAcquisitionEvaluationSnapshot? _snapshot;
    private CoreAcquisitionEvaluationLedgerKey? _key;
    private CoreAcquisitionFilter? _filter;

    public int BuildCount { get; private set; }

    public static bool IsRelevantStateChange(CraftSessionChangeScope scopes)
    {
        return (scopes & RelevantScopes) != CraftSessionChangeScope.None;
    }

    public bool TryGet(
        CoreAcquisitionEvaluationLedgerKey key,
        CoreAcquisitionFilter filter,
        out CoreAcquisitionEvaluationSnapshot snapshot)
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
                VisibleRows = CoreAcquisitionEvaluationSnapshotBuilder.ApplyFilter(
                    _snapshot.Rows,
                    filter).ToList()
            };
            _filter = filter;
        }

        snapshot = _snapshot;
        return true;
    }

    public CoreAcquisitionEvaluationSnapshot Store(
        CoreAcquisitionEvaluationLedgerKey key,
        CoreAcquisitionFilter filter,
        CoreAcquisitionEvaluationSnapshot snapshot)
    {
        _snapshot = snapshot;
        _key = key;
        _filter = filter;
        BuildCount++;
        return snapshot;
    }

    public CoreAcquisitionEvaluationSnapshot GetOrBuild(
        CoreAcquisitionEvaluationLedgerKey key,
        CoreAcquisitionFilter filter,
        Func<CoreAcquisitionEvaluationSnapshot> build)
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
