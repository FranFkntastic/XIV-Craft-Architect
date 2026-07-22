using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.SpecTests;

public sealed class MarketEvidenceSpecificationTests
{
    private static readonly DateTime NowUtc = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OneHourFreshnessBoundaryIsAging()
    {
        var result = MarketEvidenceFreshness.Evaluate(NowUtc.AddHours(-1), NowUtc);

        Assert.Equal(MarketDataQualityBucket.Aging, result.Bucket);
        Assert.Equal(TimeSpan.FromHours(1), result.Age);
    }

    [Fact]
    public void TwelveHourRecommendationBoundaryIsExclusive()
    {
        Assert.True(MarketEvidenceFreshness.IsRecommendationEligible(TimeSpan.FromHours(12) - TimeSpan.FromTicks(1)));
        Assert.False(MarketEvidenceFreshness.IsRecommendationEligible(TimeSpan.FromHours(12)));
    }

    [Fact]
    public void FutureMarketTimestampClampsToZeroAge()
    {
        var result = MarketEvidenceFreshness.Evaluate(NowUtc.AddMinutes(5), NowUtc);

        Assert.Equal(TimeSpan.Zero, result.Age);
        Assert.Equal(MarketDataQualityBucket.Current, result.Bucket);
        Assert.Equal(100m, result.Score);
    }

    [Fact]
    public void NewerWorldObservationSurvivesLaterCacheFetch()
    {
        var retained = Cache(
            NowUtc,
            World("Siren", price: 90, observedAt: 2_000));
        var incoming = Cache(
            NowUtc.AddMinutes(5),
            World("Siren", price: 110, observedAt: 1_000));

        var merged = MarketEvidenceCacheMerger.PreferNewestWorldEvidence(retained, incoming);

        Assert.Equal(90, Assert.Single(Assert.Single(merged.Worlds).Listings).PricePerUnit);
    }

    [Fact]
    public void EqualWorldEvidenceTimestampPrefersIncomingPayload()
    {
        var retained = Cache(NowUtc, World("Siren", price: 90, observedAt: 2_000));
        var incoming = Cache(NowUtc, World("Siren", price: 110, observedAt: 2_000));

        var merged = MarketEvidenceCacheMerger.PreferNewestWorldEvidence(retained, incoming);

        Assert.Equal(110, Assert.Single(Assert.Single(merged.Worlds).Listings).PricePerUnit);
    }

    [Fact]
    public void DistinctWorldIdsPreventSameNameEvidenceCollision()
    {
        var retained = Cache(NowUtc, World("Siren", price: 90, observedAt: 2_000, worldId: 1));
        var incoming = Cache(NowUtc, World("Siren", price: 110, observedAt: 1_000, worldId: 2));

        var merged = MarketEvidenceCacheMerger.PreferNewestWorldEvidence(retained, incoming);

        Assert.Equal(2, Assert.Single(merged.Worlds).WorldId);
        Assert.Equal(110, Assert.Single(Assert.Single(merged.Worlds).Listings).PricePerUnit);
    }

    [Fact]
    public void CollectionMergeReplacesMatchingItemsWithoutReorderingExistingItems()
    {
        var first = new DetailedShoppingPlan { ItemId = 1, Name = "First" };
        var oldSecond = new DetailedShoppingPlan { ItemId = 2, Name = "Old second" };
        var newSecond = new DetailedShoppingPlan { ItemId = 2, Name = "New second" };
        var third = new DetailedShoppingPlan { ItemId = 3, Name = "Third" };

        var merged = MarketEvidenceCollectionMerger.MergeShoppingPlans(
            [first, oldSecond],
            [newSecond, third]);

        Assert.Equal([1, 2, 3], merged.Select(plan => plan.ItemId).ToArray());
        Assert.Equal("New second", merged[1].Name);
    }

    private static CachedMarketData Cache(DateTime fetchedAt, params CachedWorldData[] worlds) => new()
    {
        ItemId = 1,
        DataCenter = "Aether",
        FetchedAt = fetchedAt,
        Worlds = [.. worlds]
    };

    private static CachedWorldData World(
        string name,
        long price,
        long observedAt,
        int? worldId = null) => new()
        {
            WorldId = worldId,
            WorldName = name,
            ObservedAtUnixMilliseconds = observedAt,
            Listings = [new CachedListing { Quantity = 1, PricePerUnit = price }]
        };
}
