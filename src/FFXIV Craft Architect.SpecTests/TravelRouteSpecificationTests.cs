using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.SpecTests;

public sealed class TravelRouteSpecificationTests
{
    [Fact]
    public void ToleranceZeroHasUnboundedTravelPremium()
    {
        Assert.Null(MarketRouteScoring.GetMaximumPremiumRate(0));
    }

    [Fact]
    public void ToleranceElevenAllowsNoTravelPremium()
    {
        Assert.Equal(0m, MarketRouteScoring.GetMaximumPremiumRate(11));
    }

    [Fact]
    public void FiniteTravelPremiumCurveNarrowsMonotonically()
    {
        decimal[] expected = [1.00m, 0.75m, 0.50m, 0.35m, 0.25m, 0.18m, 0.12m, 0.08m, 0.05m, 0.02m, 0m];
        var actual = Enumerable.Range(1, 11)
            .Select(tolerance => MarketRouteScoring.GetMaximumPremiumRate(tolerance)!.Value)
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.All(actual.Zip(actual.Skip(1)), pair => Assert.True(pair.First >= pair.Second));
    }

    [Fact]
    public void TwoPercentPremiumIncludesExactBoundaryButExcludesNextGil()
    {
        var route = new MarketRouteState();
        var config = SpecificationFixtures.Config(10, MarketTravelPriority.WorldVisitsFirst);
        var cheapest = new MarketPurchaseCandidate(
            100,
            [new("Aether", "Faerie"), new("Aether", "Siren")]);
        var exactBoundary = new MarketPurchaseCandidate(102, [new("Aether", "Sargatanas")]);
        var oneGilOver = new MarketPurchaseCandidate(103, [new("Aether", "Sargatanas")]);

        Assert.True(MarketRouteScoring.CompareCandidates(exactBoundary, cheapest, route, config) < 0);
        Assert.True(MarketRouteScoring.CompareCandidates(oneGilOver, cheapest, route, config) > 0);
    }

    [Fact]
    public void WorldVisitsFirstPrefersOneNewWorldOverAvoidingTransfer()
    {
        var current = new MarketRouteState([new("Aether", "Siren")]);
        var oneWorldWithTransfer = new MarketPurchaseCandidate(100, [new("Crystal", "Balmung")]);
        var twoWorldsWithoutTransfer = new MarketPurchaseCandidate(
            100,
            [new("Aether", "Faerie"), new("Aether", "Gilgamesh")]);
        var config = SpecificationFixtures.Config(0, MarketTravelPriority.WorldVisitsFirst);

        Assert.True(MarketRouteScoring.CompareCandidates(oneWorldWithTransfer, twoWorldsWithoutTransfer, current, config) < 0);
    }

    [Fact]
    public void DataCenterTransfersFirstPrefersTwoLocalWorldsOverTransfer()
    {
        var current = new MarketRouteState([new("Aether", "Siren")]);
        var oneWorldWithTransfer = new MarketPurchaseCandidate(100, [new("Crystal", "Balmung")]);
        var twoWorldsWithoutTransfer = new MarketPurchaseCandidate(
            100,
            [new("Aether", "Faerie"), new("Aether", "Gilgamesh")]);
        var config = SpecificationFixtures.Config(0, MarketTravelPriority.DataCenterTransfersFirst);

        Assert.True(MarketRouteScoring.CompareCandidates(twoWorldsWithoutTransfer, oneWorldWithTransfer, current, config) < 0);
    }
}
