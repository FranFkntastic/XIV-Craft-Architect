using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AcquisitionEvaluationLedgerCacheTests
{
    [Fact]
    public void GetOrBuild_SameKey_ReusesSnapshot()
    {
        var cache = new AcquisitionEvaluationLedgerCache();
        var key = new AcquisitionEvaluationLedgerKey(1, 1, 1, 1);
        var buildCalls = 0;

        var first = cache.GetOrBuild(key, AcquisitionFilter.All, () => CreateSnapshot(++buildCalls));
        var second = cache.GetOrBuild(key, AcquisitionFilter.All, () => CreateSnapshot(++buildCalls));

        Assert.Same(first, second);
        Assert.Equal(1, buildCalls);
        Assert.Equal(1, cache.BuildCount);
    }

    [Fact]
    public void GetOrBuild_NewKey_RebuildsSnapshot()
    {
        var cache = new AcquisitionEvaluationLedgerCache();
        var buildCalls = 0;

        cache.GetOrBuild(new AcquisitionEvaluationLedgerKey(1, 1, 1, 1), AcquisitionFilter.All, () => CreateSnapshot(++buildCalls));
        cache.GetOrBuild(new AcquisitionEvaluationLedgerKey(1, 2, 1, 1), AcquisitionFilter.All, () => CreateSnapshot(++buildCalls));

        Assert.Equal(2, buildCalls);
        Assert.Equal(2, cache.BuildCount);
    }

    [Fact]
    public void GetOrBuild_FilterOnlyChange_ReusesRowsAndUpdatesVisibleRows()
    {
        var cache = new AcquisitionEvaluationLedgerCache();
        var key = new AcquisitionEvaluationLedgerKey(1, 1, 1, 1);
        var buildCalls = 0;

        var all = cache.GetOrBuild(key, AcquisitionFilter.All, () => CreateSnapshot(++buildCalls));
        var active = cache.GetOrBuild(key, AcquisitionFilter.Active, () => CreateSnapshot(++buildCalls));

        Assert.Equal(1, buildCalls);
        Assert.Same(all.Rows, active.Rows);
        Assert.Single(active.VisibleRows);
        Assert.All(active.VisibleRows, row => Assert.True(row.IsActiveProcurement));
    }

    [Fact]
    public void Invalidate_ForcesNextRebuild()
    {
        var cache = new AcquisitionEvaluationLedgerCache();
        var key = new AcquisitionEvaluationLedgerKey(1, 1, 1, 1);
        var buildCalls = 0;

        cache.GetOrBuild(key, AcquisitionFilter.All, () => CreateSnapshot(++buildCalls));
        cache.Invalidate();
        cache.GetOrBuild(key, AcquisitionFilter.All, () => CreateSnapshot(++buildCalls));

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
    public void IsRelevantStateChange_OnlyMatchesLedgerScopes(AppStateChangeScope scope, bool expected)
    {
        Assert.Equal(expected, AcquisitionEvaluationLedgerCache.IsRelevantStateChange(scope));
    }

    private static AcquisitionEvaluationSnapshot CreateSnapshot(int buildNumber)
    {
        var active = new PlanNode
        {
            ItemId = buildNumber,
            Name = $"Active {buildNumber}",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true
        };
        var suppressed = new PlanNode
        {
            ItemId = 10_000 + buildNumber,
            Name = $"Suppressed {buildNumber}",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true
        };
        var rows = new List<DecisionRow>
        {
            CreateRow(active, 1, 1, "Project item x1", false, false, Array.Empty<string>(), true, true, true, "Not analyzed", "1g"),
            CreateRow(suppressed, 1, 0, "Parent x1", true, true, ["Parent"], false, false, false, "Not analyzed", "-")
        };

        return new AcquisitionEvaluationSnapshot(
            rows,
            rows.ToList(),
            new List<MaterialAggregate>(),
            new List<MaterialAggregate>(),
            AcquisitionPlanningService.CreateCostContext(Array.Empty<DetailedShoppingPlan>()));
    }

    private static DecisionRow CreateRow(
        PlanNode node,
        int totalQuantity,
        int activeQuantity,
        string usedIn,
        bool hasSuppressedOccurrences,
        bool isFullySuppressed,
        IReadOnlyList<string> suppressedBy,
        bool isActiveProcurement,
        bool hasEditableOccurrences,
        bool isMarketCandidate,
        string marketEvidence,
        string estimatedCost)
    {
        return new DecisionRow(
            node,
            node.NodeId,
            node.ItemId,
            node.Name,
            node.IconId,
            node.Source,
            node.SourceReason,
            node.MustBeHq,
            node.Children.Count > 0,
            node.CanCraft,
            node.CanBeHq,
            node.Yield,
            node.CanBuyFromMarket,
            node.CanBuyFromVendor,
            node.MarketPrice,
            node.HqMarketPrice,
            node.VendorPrice,
            Array.Empty<RecipeDemandVendorOption>(),
            totalQuantity,
            activeQuantity,
            usedIn,
            hasSuppressedOccurrences,
            isFullySuppressed,
            suppressedBy,
            isActiveProcurement,
            hasEditableOccurrences,
            isMarketCandidate,
            marketEvidence,
            estimatedCost);
    }
}
