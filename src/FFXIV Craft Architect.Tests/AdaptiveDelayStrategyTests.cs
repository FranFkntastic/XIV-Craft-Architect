using System.Net;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AdaptiveDelayStrategyTests
{
    [Fact]
    public void ReportSuccess_AfterFailure_ReducesDelayTowardMinimum()
    {
        var strategy = new AdaptiveDelayStrategy(
            initialDelayMs: 100,
            minDelayMs: 50,
            maxDelayMs: 1000,
            backoffMultiplier: 2.0,
            rateLimitMultiplier: 3.0);

        strategy.ReportFailure(HttpStatusCode.TooManyRequests);
        var backedOffDelay = strategy.GetDelay();

        strategy.ReportSuccess();

        Assert.True(strategy.GetDelay() < backedOffDelay);
        Assert.True(strategy.GetDelay() >= 50);
    }
}
