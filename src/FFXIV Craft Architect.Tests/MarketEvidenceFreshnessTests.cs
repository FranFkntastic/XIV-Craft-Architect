using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketEvidenceFreshnessTests
{
    [Theory]
    [InlineData(59, MarketDataQualityBucket.Current)]
    [InlineData(60, MarketDataQualityBucket.Aging)]
    [InlineData(360, MarketDataQualityBucket.Old)]
    [InlineData(720, MarketDataQualityBucket.VeryOld)]
    [InlineData(1440, MarketDataQualityBucket.Ancient)]
    [InlineData(1500, MarketDataQualityBucket.Ancient)]
    public void Evaluate_MapsAgeToGenerousFreshnessBuckets(int ageMinutes, MarketDataQualityBucket expected)
    {
        var evaluatedAtUtc = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);
        var timestampUtc = evaluatedAtUtc - TimeSpan.FromMinutes(ageMinutes);

        var result = MarketEvidenceFreshness.Evaluate(timestampUtc, evaluatedAtUtc);

        Assert.Equal(expected, result.Bucket);
        Assert.Equal(TimeSpan.FromMinutes(ageMinutes), result.Age);
    }

    [Fact]
    public void Evaluate_ClampsFutureTimestampsToCurrent()
    {
        var evaluatedAtUtc = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);
        var timestampUtc = evaluatedAtUtc + TimeSpan.FromMinutes(5);

        var result = MarketEvidenceFreshness.Evaluate(timestampUtc, evaluatedAtUtc);

        Assert.Equal(MarketDataQualityBucket.Current, result.Bucket);
        Assert.Equal(TimeSpan.Zero, result.Age);
    }

    [Fact]
    public void Evaluate_WhenFallbackCapIsRequested_DoesNotTreatLocalFetchAsCurrent()
    {
        var evaluatedAtUtc = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);
        var timestampUtc = evaluatedAtUtc - TimeSpan.FromMinutes(10);

        var result = MarketEvidenceFreshness.Evaluate(
            timestampUtc,
            evaluatedAtUtc,
            capCurrentToAging: true);

        Assert.Equal(MarketDataQualityBucket.Aging, result.Bucket);
    }

    [Fact]
    public void MarketDataQualityBucket_OrdersMissingAfterAncientForWorstQuality()
    {
        var worst = new[]
        {
            MarketDataQualityBucket.Current,
            MarketDataQualityBucket.Ancient,
            MarketDataQualityBucket.Missing
        }.Max();

        Assert.Equal(MarketDataQualityBucket.Missing, worst);
    }
}
