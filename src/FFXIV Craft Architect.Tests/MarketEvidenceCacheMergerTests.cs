using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class MarketEvidenceCacheMergerTests
{
    [Fact]
    public void PreferNewestWorldEvidence_PreservesNewerLiveObservationDuringUniversalisRefresh()
    {
        var retained = Cache(
            fetchedAt: new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc),
            World("Gilgamesh", MarketEvidenceOrigin.MarketMafioso, observedAt: 2000, price: 90));
        var incoming = Cache(
            fetchedAt: new DateTime(2026, 7, 14, 12, 5, 0, DateTimeKind.Utc),
            World("Gilgamesh", MarketEvidenceOrigin.Universalis, observedAt: 1000, price: 110),
            World("Sargatanas", MarketEvidenceOrigin.Universalis, observedAt: 2500, price: 120));

        var merged = MarketEvidenceCacheMerger.PreferNewestWorldEvidence(retained, incoming);

        Assert.Equal(90, merged.Worlds.Single(world => world.WorldName == "Gilgamesh").Listings.Single().PricePerUnit);
        Assert.Equal(120, merged.Worlds.Single(world => world.WorldName == "Sargatanas").Listings.Single().PricePerUnit);
    }

    [Fact]
    public void PreferNewestWorldEvidence_AllowsNewerUniversalisEvidenceToReplaceLiveObservation()
    {
        var retained = Cache(DateTime.UtcNow, World("Gilgamesh", MarketEvidenceOrigin.MarketMafioso, 1000, 90));
        var incoming = Cache(DateTime.UtcNow, World("Gilgamesh", MarketEvidenceOrigin.Universalis, 2000, 110));

        var merged = MarketEvidenceCacheMerger.PreferNewestWorldEvidence(retained, incoming);

        Assert.Equal(110, merged.Worlds.Single().Listings.Single().PricePerUnit);
        Assert.Equal(MarketEvidenceOrigin.Universalis, merged.Worlds.Single().EvidenceOrigin);
    }

    private static CachedMarketData Cache(DateTime fetchedAt, params CachedWorldData[] worlds) =>
        new() { ItemId = 1, DataCenter = "Aether", FetchedAt = fetchedAt, Worlds = [.. worlds] };

    private static CachedWorldData World(string name, MarketEvidenceOrigin origin, long observedAt, long price) =>
        new()
        {
            WorldName = name,
            EvidenceOrigin = origin,
            ObservedAtUnixMilliseconds = observedAt,
            Listings = [new CachedListing { Quantity = 1, PricePerUnit = price }]
        };
}
