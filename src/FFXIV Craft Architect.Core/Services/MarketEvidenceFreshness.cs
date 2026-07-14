using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketEvidenceFreshness
{
    public static bool IsRouteEligible(MarketDataQualityBucket bucket) =>
        bucket is MarketDataQualityBucket.Current or
            MarketDataQualityBucket.Aging or
            MarketDataQualityBucket.Old;

    public static bool IsRecommendationEligible(TimeSpan? age, TimeSpan? maximumAge = null) =>
        age.HasValue && age.Value < (maximumAge ?? MarketEvidencePolicyDefaults.MaximumRecommendationAge);

    public static MarketEvidenceFreshnessResult Evaluate(
        DateTime timestampUtc,
        DateTime evaluatedAtUtc,
        bool capCurrentToAging = false)
    {
        var age = CacheTimeHelper.NormalizeToUtc(evaluatedAtUtc) - CacheTimeHelper.NormalizeToUtc(timestampUtc);
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        var bucket = GetBucket(age);
        var score = GetScore(age, bucket);
        if (capCurrentToAging && bucket == MarketDataQualityBucket.Current)
        {
            bucket = MarketDataQualityBucket.Aging;
            score = Math.Min(score, 70m);
        }

        return new MarketEvidenceFreshnessResult(score, bucket, age);
    }

    private static MarketDataQualityBucket GetBucket(TimeSpan age)
    {
        if (age < MarketEvidencePolicyDefaults.ReusableCacheMaxAge)
        {
            return MarketDataQualityBucket.Current;
        }

        if (age < MarketEvidencePolicyDefaults.AgingThreshold)
        {
            return MarketDataQualityBucket.Aging;
        }

        if (age < MarketEvidencePolicyDefaults.MaximumRecommendationAge)
        {
            return MarketDataQualityBucket.Old;
        }

        if (age < MarketEvidencePolicyDefaults.VeryOldThreshold)
        {
            return MarketDataQualityBucket.VeryOld;
        }

        return MarketDataQualityBucket.Ancient;
    }

    private static decimal GetScore(TimeSpan age, MarketDataQualityBucket bucket)
    {
        var minutes = (decimal)age.TotalMinutes;
        return bucket switch
        {
            MarketDataQualityBucket.Current => 100m - minutes / 60m * 20m,
            MarketDataQualityBucket.Aging => 80m - (minutes - 60m) / 300m * 30m,
            MarketDataQualityBucket.Old => 50m - (minutes - 360m) / 360m * 25m,
            MarketDataQualityBucket.VeryOld => 25m - (minutes - 720m) / 720m * 20m,
            MarketDataQualityBucket.Ancient => Math.Max(1m, 5m - (minutes - 1440m) / 1440m * 4m),
            _ => 0m
        };
    }
}

public readonly record struct MarketEvidenceFreshnessResult(
    decimal Score,
    MarketDataQualityBucket Bucket,
    TimeSpan Age);
