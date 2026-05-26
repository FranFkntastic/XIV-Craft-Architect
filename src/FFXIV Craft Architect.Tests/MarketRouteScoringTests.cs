using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Tests;

public class MarketRouteScoringTests
{
    [Fact]
    public void CompareCandidates_TravelToleranceZero_PrefersFewerDataCentersThenWorldsOverPrice()
    {
        var config = new MarketAnalysisConfig { TravelTolerance = 0 };
        var currentRoute = new MarketRouteState(
        [
            new MarketWorldKey("Aether", "Siren")
        ]);

        var currentWorldExpensive = new MarketPurchaseCandidate(
            100_000,
            [new MarketWorldKey("Aether", "Siren")]);
        var sameDataCenterNewWorld = new MarketPurchaseCandidate(
            10,
            [new MarketWorldKey("Aether", "Gilgamesh")]);
        var newDataCenterCheap = new MarketPurchaseCandidate(
            1,
            [new MarketWorldKey("Crystal", "Balmung")]);

        Assert.True(MarketRouteScoring.CompareCandidates(currentWorldExpensive, sameDataCenterNewWorld, currentRoute, config) < 0);
        Assert.True(MarketRouteScoring.CompareCandidates(sameDataCenterNewWorld, newDataCenterCheap, currentRoute, config) < 0);
    }

    [Fact]
    public void ScoreCandidate_TravelToleranceEleven_UsesRawGilCostWithoutRoutePenalty()
    {
        var config = new MarketAnalysisConfig { TravelTolerance = 11 };
        var currentRoute = new MarketRouteState(
        [
            new MarketWorldKey("Aether", "Siren")
        ]);
        var cheapNewDataCenter = new MarketPurchaseCandidate(
            1,
            [new MarketWorldKey("Crystal", "Balmung")]);
        var expensiveCurrentWorld = new MarketPurchaseCandidate(
            100_000,
            [new MarketWorldKey("Aether", "Siren")]);

        var cheapScore = MarketRouteScoring.ScoreCandidate(cheapNewDataCenter, currentRoute, config);

        Assert.Equal(0, cheapScore.RoutePenalty);
        Assert.Equal(1, cheapScore.EffectiveScore);
        Assert.True(MarketRouteScoring.CompareCandidates(cheapNewDataCenter, expensiveCurrentWorld, currentRoute, config) < 0);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(11, 11)]
    [InlineData(12, 11)]
    public void TravelTolerance_ClampsToSupportedRoutePenaltyRange(int input, int expected)
    {
        var config = new MarketAnalysisConfig { TravelTolerance = input };

        Assert.Equal(expected, config.TravelTolerance);
    }
}
