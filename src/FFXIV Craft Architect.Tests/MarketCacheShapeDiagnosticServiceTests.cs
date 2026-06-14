using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketCacheShapeDiagnosticServiceTests
{
    [Fact]
    public void Analyze_RepeatedIdenticalListingFingerprintAboveThreshold_DoesNotReportWithoutStrongerEvidence()
    {
        var service = new MarketCacheShapeDiagnosticService();
        var data = CachedData(
            itemId: 42,
            dataCenter: "Aether",
            worlds:
            [
                World(
                    "Siren",
                    Enumerable.Range(0, 30)
                        .Select(_ => Listing(quantity: 99, price: 123, retainer: "Same Retainer", reviewedAtUnix: 1_710_000_000))
                        .ToList())
            ]);

        var report = service.Analyze(data);

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Analyze_PlausibleSmallDuplicateStacks_DoesNotReport()
    {
        var service = new MarketCacheShapeDiagnosticService();
        var data = CachedData(
            itemId: 42,
            dataCenter: "Aether",
            worlds:
            [
                World(
                    "Siren",
                    [
                        Listing(quantity: 99, price: 123, retainer: "Same Retainer", reviewedAtUnix: 1_710_000_000),
                        Listing(quantity: 99, price: 123, retainer: "Same Retainer", reviewedAtUnix: 1_710_000_000),
                        Listing(quantity: 99, price: 124, retainer: "Neighbor"),
                        Listing(quantity: 99, price: 125, retainer: "Other")
                    ])
            ]);

        var report = service.Analyze(data);

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Analyze_DifferentRetainersOrReviewTimes_AreNotTreatedAsRepeatedFingerprints()
    {
        var service = new MarketCacheShapeDiagnosticService();
        var listings = Enumerable.Range(0, 40)
            .Select(index => Listing(
                quantity: 99,
                price: 123,
                retainer: $"Retainer {index}",
                reviewedAtUnix: 1_710_000_000 + index))
            .ToList();
        var data = CachedData(
            itemId: 42,
            dataCenter: "Aether",
            worlds: [World("Siren", listings)]);

        var report = service.Analyze(data);

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Analyze_MissingListingIdentity_DoesNotReportHighConfidenceFingerprintWarning()
    {
        var service = new MarketCacheShapeDiagnosticService();
        var data = CachedData(
            itemId: 42,
            dataCenter: "Aether",
            worlds:
            [
                World(
                    "Siren",
                    Enumerable.Range(0, 30)
                        .Select(_ => Listing(quantity: 99, price: 123, retainer: "Unknown"))
                        .ToList())
            ]);

        var report = service.Analyze(data);

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Analyze_MultipleEntries_DoesNotReportRepeatedBulkSellerStacks()
    {
        var service = new MarketCacheShapeDiagnosticService();
        var entries = new Dictionary<(int itemId, string dataCenter), CachedMarketData>
        {
            [(42, "Aether")] = CachedData(
                itemId: 42,
                dataCenter: "Aether",
                worlds:
                [
                    World(
                        "Siren",
                        Enumerable.Range(0, 30)
                            .Select(_ => Listing(
                                quantity: 99,
                                price: 123,
                                retainer: "Aether Retainer",
                                reviewedAtUnix: 1_710_000_000))
                            .ToList())
                ]),
            [(84, "Crystal")] = CachedData(
                itemId: 84,
                dataCenter: "Crystal",
                worlds:
                [
                    World(
                        "Coeurl",
                        Enumerable.Range(0, 30)
                            .Select(_ => Listing(
                                quantity: 12,
                                price: 456,
                                retainer: "Crystal Retainer",
                                reviewedAtUnix: 1_710_000_000))
                            .ToList())
                ])
        };

        var report = service.Analyze(entries);

        Assert.Empty(report.Issues);
    }

    private static CachedMarketData CachedData(
        int itemId,
        string dataCenter,
        IReadOnlyList<CachedWorldData> worlds)
    {
        return new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            FetchedAt = DateTime.UtcNow,
            Worlds = worlds.ToList()
        };
    }

    private static CachedWorldData World(string worldName, IReadOnlyList<CachedListing> listings)
    {
        return new CachedWorldData
        {
            WorldName = worldName,
            Listings = listings.ToList()
        };
    }

    private static CachedListing Listing(
        int quantity,
        long price,
        string retainer,
        long? reviewedAtUnix = null)
    {
        return new CachedListing
        {
            Quantity = quantity,
            PricePerUnit = price,
            RetainerName = retainer,
            LastReviewTimeUnix = reviewedAtUnix
        };
    }
}
