using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class RecipePlanTreeDisplayCacheTests
{
    [Fact]
    public void GetOrBuild_SameKey_ReusesDisplayStates()
    {
        var cache = new RecipePlanTreeDisplayCache();
        var key = new RecipePlanTreeDisplayKey(1, 1, 1, 1);
        var buildCalls = 0;

        var first = cache.GetOrBuild(key, () => CreateStates(++buildCalls));
        var second = cache.GetOrBuild(key, () => CreateStates(++buildCalls));

        Assert.Same(first, second);
        Assert.Equal(1, buildCalls);
        Assert.Equal(1, cache.BuildCount);
    }

    [Fact]
    public void GetOrBuild_NewRelevantKey_RebuildsDisplayStates()
    {
        var cache = new RecipePlanTreeDisplayCache();
        var buildCalls = 0;

        cache.GetOrBuild(new RecipePlanTreeDisplayKey(1, 1, 1, 1), () => CreateStates(++buildCalls));
        cache.GetOrBuild(new RecipePlanTreeDisplayKey(1, 2, 1, 1), () => CreateStates(++buildCalls));

        Assert.Equal(2, buildCalls);
        Assert.Equal(2, cache.BuildCount);
    }

    [Theory]
    [InlineData(AppStateChangeScope.PlanStructure, true)]
    [InlineData(AppStateChangeScope.PlanDecision, true)]
    [InlineData(AppStateChangeScope.PlanPrice, true)]
    [InlineData(AppStateChangeScope.MarketAnalysis, true)]
    [InlineData(AppStateChangeScope.ProcurementOverlay, false)]
    [InlineData(AppStateChangeScope.ShoppingItems, false)]
    [InlineData(AppStateChangeScope.Status, false)]
    [InlineData(AppStateChangeScope.Settings, false)]
    public void IsRelevantStateChange_OnlyMatchesTreeDisplayScopes(AppStateChangeScope scope, bool expected)
    {
        Assert.Equal(expected, RecipePlanTreeDisplayCache.IsRelevantStateChange(scope));
    }

    private static IReadOnlyDictionary<string, RecipeNodeDisplayState> CreateStates(int buildNumber)
    {
        return new Dictionary<string, RecipeNodeDisplayState>
        {
            [$"node-{buildNumber}"] = new(
                $"node-{buildNumber}",
                "#ffffff",
                $"{buildNumber}g",
                string.Empty,
                null)
        };
    }
}
