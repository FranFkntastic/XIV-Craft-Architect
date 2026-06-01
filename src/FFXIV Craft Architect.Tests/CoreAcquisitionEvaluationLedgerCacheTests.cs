using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class CoreAcquisitionEvaluationLedgerCacheTests
{
    [Fact]
    public void GetOrBuild_SameKey_ReusesSnapshot()
    {
        var cache = new CoreAcquisitionEvaluationLedgerCache();
        var key = new CoreAcquisitionEvaluationLedgerKey(1, 1, 1, 1);
        var buildCalls = 0;

        var first = cache.GetOrBuild(key, CoreAcquisitionFilter.All, () => CreateSnapshot(++buildCalls));
        var second = cache.GetOrBuild(key, CoreAcquisitionFilter.All, () => CreateSnapshot(++buildCalls));

        Assert.Same(first, second);
        Assert.Equal(1, buildCalls);
        Assert.Equal(1, cache.BuildCount);
    }

    [Fact]
    public void GetOrBuild_FilterOnlyChange_ReusesRowsAndUpdatesVisibleRows()
    {
        var cache = new CoreAcquisitionEvaluationLedgerCache();
        var key = new CoreAcquisitionEvaluationLedgerKey(1, 1, 1, 1);
        var buildCalls = 0;

        var all = cache.GetOrBuild(key, CoreAcquisitionFilter.All, () => CreateSnapshot(++buildCalls));
        var active = cache.GetOrBuild(key, CoreAcquisitionFilter.Active, () => CreateSnapshot(++buildCalls));

        Assert.Equal(1, buildCalls);
        Assert.Same(all.Rows, active.Rows);
        Assert.Single(active.VisibleRows);
        Assert.All(active.VisibleRows, row => Assert.True(row.IsActiveProcurement));
    }

    [Theory]
    [InlineData(CraftSessionChangeScope.PlanCore, true)]
    [InlineData(CraftSessionChangeScope.PlanDecision, true)]
    [InlineData(CraftSessionChangeScope.MarketAnalysis, true)]
    [InlineData(CraftSessionChangeScope.ProcurementOverlay, false)]
    [InlineData(CraftSessionChangeScope.SettingsContext, false)]
    [InlineData(CraftSessionChangeScope.ViewState, false)]
    public void IsRelevantStateChange_OnlyMatchesLedgerScopes(CraftSessionChangeScope scope, bool expected)
    {
        Assert.Equal(expected, CoreAcquisitionEvaluationLedgerCache.IsRelevantStateChange(scope));
    }

    private static CoreAcquisitionEvaluationSnapshot CreateSnapshot(int buildNumber)
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
        var rows = new List<CoreDecisionRow>
        {
            CreateRow(active, 1, 1, "Project item x1", false, false, [], true, true, true, "Not analyzed", "1g"),
            CreateRow(suppressed, 1, 0, "Parent x1", true, true, ["Parent"], false, false, false, "Not analyzed", "-")
        };

        return new CoreAcquisitionEvaluationSnapshot(
            rows,
            rows.ToList(),
            [],
            [],
            AcquisitionPlanningService.CreateCostContext([]));
    }

    private static CoreDecisionRow CreateRow(
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
        return new CoreDecisionRow(
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
            [],
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
