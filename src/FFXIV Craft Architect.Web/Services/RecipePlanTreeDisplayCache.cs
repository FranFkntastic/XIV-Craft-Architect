namespace FFXIV_Craft_Architect.Web.Services;

public sealed record RecipePlanTreeDisplayKey(
    long PlanStructureVersion,
    long PlanDecisionVersion,
    long PlanPriceVersion,
    long MarketAnalysisVersion);

public sealed class RecipePlanTreeDisplayCache
{
    private const AppStateChangeScope RelevantScopes =
        AppStateChangeScope.PlanStructure |
        AppStateChangeScope.PlanDecision |
        AppStateChangeScope.PlanPrice |
        AppStateChangeScope.MarketAnalysis;

    private IReadOnlyDictionary<string, RecipeNodeDisplayState>? _states;
    private RecipePlanTreeDisplayKey? _key;

    public int BuildCount { get; private set; }

    public static bool IsRelevantStateChange(AppStateChangeScope scopes)
    {
        return (scopes & RelevantScopes) != AppStateChangeScope.None;
    }

    public IReadOnlyDictionary<string, RecipeNodeDisplayState> GetOrBuild(
        RecipePlanTreeDisplayKey key,
        Func<IReadOnlyDictionary<string, RecipeNodeDisplayState>> build)
    {
        if (_states != null && _key == key)
        {
            return _states;
        }

        _states = build();
        _key = key;
        BuildCount++;
        return _states;
    }

    public void Invalidate()
    {
        _states = null;
        _key = null;
    }
}
