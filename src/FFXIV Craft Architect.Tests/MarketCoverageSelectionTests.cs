using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketCoverageSelectionTests
{
    [Fact]
    public void GetDefaultOption_PrefersDefaultEligibleExactNeededCost()
    {
        var expensiveSingle = CreateOption("single", MarketCoverageTier.SingleWorld, exact: 500, cashOut: 500, worlds: 1, defaultEligible: true);
        var cheaperCompact = CreateOption("compact", MarketCoverageTier.CompactSplit, exact: 300, cashOut: 450, worlds: 2, defaultEligible: true);
        var cheapestObserved = CreateOption("observed", MarketCoverageTier.CheapestObserved, exact: 100, cashOut: 100, worlds: 4, defaultEligible: false);

        var set = new MarketCoverageSet(1, "Test Item", 10, expensiveSingle, cheaperCompact, null, cheapestObserved, [expensiveSingle, cheaperCompact, cheapestObserved]);

        var selected = MarketCoverageSelection.GetDefaultOption(set);

        Assert.Same(cheaperCompact, selected);
    }

    [Fact]
    public void GetDefaultOption_FiltersByQualityPolicy()
    {
        var nq = CreateOption("nq", MarketCoverageTier.SingleWorld, exact: 100, cashOut: 100, worlds: 1, defaultEligible: true, MarketCoverageQualityPolicy.NqOrHq);
        var hq = CreateOption("hq", MarketCoverageTier.SingleWorld, exact: 200, cashOut: 200, worlds: 1, defaultEligible: true, MarketCoverageQualityPolicy.HqOnly);

        var set = new MarketCoverageSet(1, "Test Item", 10, nq, null, null, null, [nq, hq]);

        var selected = MarketCoverageSelection.GetDefaultOption(set, MarketCoverageQualityPolicy.HqOnly);

        Assert.Same(hq, selected);
    }

    [Fact]
    public void GetCandidates_DeduplicatesByCandidateId()
    {
        var option = CreateOption("same", MarketCoverageTier.SingleWorld, exact: 100, cashOut: 100, worlds: 1, defaultEligible: true);
        var set = new MarketCoverageSet(1, "Test Item", 10, option, null, null, null, [option]);

        var candidates = MarketCoverageSelection.GetCandidates(set).ToList();

        Assert.Single(candidates);
    }

    private static MarketCoverageOption CreateOption(
        string candidateId,
        MarketCoverageTier tier,
        decimal exact,
        decimal cashOut,
        int worlds,
        bool defaultEligible,
        MarketCoverageQualityPolicy qualityPolicy = MarketCoverageQualityPolicy.NqOrHq)
    {
        var coverageWorlds = Enumerable.Range(1, worlds)
            .Select(index => new MarketCoverageWorld("Aether", $"World {index}", 10, 10, exact / worlds, cashOut / worlds))
            .ToList();

        return new MarketCoverageOption(
            candidateId,
            tier,
            MarketCoverageKind.SupportedListings,
            qualityPolicy,
            QuantityCovered: 10,
            QuantityToPurchase: 10,
            ExcessQuantity: 0,
            ExactNeededCost: exact,
            CashOutCost: cashOut,
            AverageUnitCost: exact / 10,
            MarketCoveragePriceBand.Competitive,
            coverageWorlds,
            Listings: [],
            new MarketCoverageFriction(worlds, 1, 10, 10, 0),
            MarketCoverageSavings.None,
            defaultEligible,
            DegradedReason: null);
    }
}
