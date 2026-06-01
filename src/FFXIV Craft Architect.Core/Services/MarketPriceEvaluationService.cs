using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class MarketPriceEvaluationService : IMarketPriceEvaluationService
{
    private const decimal BandTolerance = 0.10m;
    private const decimal OutlierMultiplier = 2.5m;
    private const decimal ScopeSaneMultiplier = 2.0m;
    private const decimal ScopeCompetitiveMultiplier = 1.5m;
    private const decimal LowOutlierBreakPercent = 400m;
    private const int FullStackQuantity = 99;
    private const int MinimumCredibleLowRegionQuantity = 80;
    private const int MinimumSubstantialLowListingQuantity = 40;
    private const int ElementalCommodityCredibleLowRegionQuantity = 1_000;
    private const int ElementalCommodityMinimumCredibleLowRegionQuantity = 500;
    private const int ElementalCommodityMinimumSubstantialLowListingQuantity = 100;

    public MarketPriceEvaluationContext Evaluate(
        int itemId,
        MarketFetchScope scope,
        DateTime evaluatedAtUtc,
        IReadOnlyList<CachedMarketData> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var listings = entries
            .SelectMany(entry => entry.Worlds.SelectMany(world => CreateScopeListings(entry, world, evaluatedAtUtc)))
            .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
            .OrderBy(listing => listing.PricePerUnit)
            .ToList();
        if (listings.Count == 0)
        {
            return CreateContext(
                itemId,
                scope,
                evaluatedAtUtc,
                listings,
                centralListings: [],
                qualityPolicy: DetermineQualityPolicy(listings),
                baseline: 0,
                average: 0,
                competitiveAverage: 0,
                median: 0,
                competitiveThreshold: 0,
                saneThreshold: 0,
                lowOutlierMaxUnitPrice: 0);
        }

        var lowOutlierMaxUnitPrice = CalculateLowOutlierMaxUnitPrice(itemId, listings);
        var baselineListings = lowOutlierMaxUnitPrice.HasValue
            ? listings.Where(listing => listing.PricePerUnit > lowOutlierMaxUnitPrice.Value).ToList()
            : listings;
        if (baselineListings.Count == 0)
        {
            baselineListings = listings;
        }

        baselineListings = TrimHighOutlierTail(baselineListings);

        var median = CalculateWeightedMedian(baselineListings);
        if (median <= 0)
        {
            return CreateContext(
                itemId,
                scope,
                evaluatedAtUtc,
                listings,
                centralListings: [],
                qualityPolicy: DetermineQualityPolicy(listings),
                baseline: 0,
                average: 0,
                competitiveAverage: 0,
                median: 0,
                competitiveThreshold: 0,
                saneThreshold: 0,
                lowOutlierMaxUnitPrice: lowOutlierMaxUnitPrice ?? 0);
        }

        var broadThreshold = median * OutlierMultiplier;
        var averageListings = baselineListings
            .Where(listing => listing.PricePerUnit <= broadThreshold)
            .ToList();
        var average = CalculateWeightedAveragePrice(averageListings);
        var baseline = average > 0 ? Math.Max(average, median) : median;
        var competitiveThreshold = baseline * ScopeCompetitiveMultiplier;
        var competitiveAverage = CalculateWeightedAveragePrice(averageListings
            .Where(listing => listing.PricePerUnit <= competitiveThreshold)
            .ToList());
        var qualityPolicy = DetermineQualityPolicy(averageListings);

        return CreateContext(
            itemId,
            scope,
            evaluatedAtUtc,
            listings,
            averageListings,
            qualityPolicy,
            baseline,
            average,
            competitiveAverage,
            median,
            competitiveThreshold,
            baseline * ScopeSaneMultiplier,
            lowOutlierMaxUnitPrice ?? 0);
    }

    private static MarketPriceEvaluationContext CreateContext(
        int itemId,
        MarketFetchScope scope,
        DateTime evaluatedAtUtc,
        IReadOnlyList<ScopeListing> allListings,
        IReadOnlyList<ScopeListing> centralListings,
        MarketPriceQualityPolicy qualityPolicy,
        decimal baseline,
        decimal average,
        decimal competitiveAverage,
        decimal median,
        decimal competitiveThreshold,
        decimal saneThreshold,
        long lowOutlierMaxUnitPrice)
    {
        var credibility = CalculateCredibility(centralListings);
        var priceEvaluation = new MarketPriceEvaluation
        {
            ItemId = itemId,
            Scope = scope,
            QualityPolicy = qualityPolicy,
            EvaluatedAtUtc = evaluatedAtUtc,
            CentralRegion = CreateCentralRegion(centralListings, median, average, credibility),
            Thresholds = new MarketPriceThresholds
            {
                DealCeilingUnitPrice = baseline,
                CompetitiveCeilingUnitPrice = competitiveThreshold,
                SaneCeilingUnitPrice = saneThreshold,
                InsaneFloorUnitPrice = saneThreshold
            },
            ListingClassCounts = CreateListingClassCounts(
                allListings,
                centralListings,
                competitiveThreshold,
                saneThreshold,
                lowOutlierMaxUnitPrice),
            Confidence = credibility switch
            {
                MarketPriceRegionCredibility.Strong => MarketPriceEvaluationConfidence.High,
                MarketPriceRegionCredibility.Credible => MarketPriceEvaluationConfidence.Medium,
                MarketPriceRegionCredibility.Thin => MarketPriceEvaluationConfidence.Low,
                _ => MarketPriceEvaluationConfidence.Unknown
            },
            Diagnostics = CreateDiagnostics(centralListings, allListings, credibility)
        };

        return new MarketPriceEvaluationContext
        {
            BaselineUnitPrice = baseline,
            AverageUnitPrice = average,
            CompetitiveAverageUnitPrice = competitiveAverage,
            MedianUnitPrice = median,
            CompetitiveThresholdUnitPrice = competitiveThreshold,
            SaneThresholdUnitPrice = saneThreshold,
            LowOutlierMaxUnitPrice = lowOutlierMaxUnitPrice,
            PriceEvaluation = priceEvaluation
        };
    }

    private static MarketCentralPriceRegion CreateCentralRegion(
        IReadOnlyList<ScopeListing> centralListings,
        decimal median,
        decimal average,
        MarketPriceRegionCredibility credibility)
    {
        return new MarketCentralPriceRegion
        {
            MinUnitPrice = centralListings.Count > 0 ? centralListings.Min(listing => listing.PricePerUnit) : 0,
            MaxUnitPrice = centralListings.Count > 0 ? centralListings.Max(listing => listing.PricePerUnit) : 0,
            MedianUnitPrice = median,
            WeightedAverageUnitPrice = average,
            ListingCount = centralListings.Count,
            TotalQuantity = centralListings.Sum(listing => listing.Quantity),
            DistinctRetainerCount = centralListings
                .Select(listing => listing.RetainerName)
                .Where(retainer => !string.IsNullOrWhiteSpace(retainer))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            DistinctWorldCount = centralListings
                .Select(listing => listing.WorldName)
                .Where(world => !string.IsNullOrWhiteSpace(world))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            DataQualityBucket = centralListings.Count > 0
                ? centralListings.Max(listing => listing.DataQualityBucket)
                : MarketDataQualityBucket.Missing,
            Credibility = credibility
        };
    }

    private static MarketPriceQualityPolicy DetermineQualityPolicy(IReadOnlyList<ScopeListing> listings)
    {
        if (listings.Count == 0)
        {
            return MarketPriceQualityPolicy.Unknown;
        }

        var hasHq = listings.Any(listing => listing.IsHq);
        var hasNq = listings.Any(listing => !listing.IsHq);
        return (hasHq, hasNq) switch
        {
            (true, true) => MarketPriceQualityPolicy.Combined,
            (true, false) => MarketPriceQualityPolicy.HqOnly,
            (false, true) => MarketPriceQualityPolicy.NqOnly,
            _ => MarketPriceQualityPolicy.Unknown
        };
    }

    private static MarketListingClassCounts CreateListingClassCounts(
        IReadOnlyList<ScopeListing> allListings,
        IReadOnlyList<ScopeListing> centralListings,
        decimal competitiveThreshold,
        decimal saneThreshold,
        long lowOutlierMaxUnitPrice)
    {
        var thresholds = new MarketPriceThresholds
        {
            DealCeilingUnitPrice = competitiveThreshold > 0 ? competitiveThreshold / ScopeCompetitiveMultiplier : 0,
            CompetitiveCeilingUnitPrice = competitiveThreshold,
            SaneCeilingUnitPrice = saneThreshold,
            InsaneFloorUnitPrice = saneThreshold
        };
        var classified = allListings
            .Select(listing =>
            {
                var sanity = ClassifyPriceSanity(listing.PricePerUnit, lowOutlierMaxUnitPrice, saneThreshold);
                return MarketListingClassification.ClassifyCompetitiveness(
                    listing.PricePerUnit,
                    sanity,
                    thresholds,
                    excludeOutliers: true);
            })
            .ToList();

        return new MarketListingClassCounts
        {
            DealCount = classified.Count(competitiveness => competitiveness == MarketListingCompetitiveness.Deal),
            CompetitiveCount = classified.Count(competitiveness => competitiveness == MarketListingCompetitiveness.Competitive),
            FairCount = classified.Count(competitiveness => competitiveness == MarketListingCompetitiveness.Fair),
            UncompetitiveCount = classified.Count(competitiveness => competitiveness == MarketListingCompetitiveness.Uncompetitive),
            ExcludedCount = classified.Count(competitiveness => competitiveness == MarketListingCompetitiveness.Excluded),
            LowOutlierCount = lowOutlierMaxUnitPrice > 0
                ? allListings.Count(listing => listing.PricePerUnit <= lowOutlierMaxUnitPrice)
                : 0,
            SaneCount = allListings.Count(listing => ClassifyPriceSanity(listing.PricePerUnit, lowOutlierMaxUnitPrice, saneThreshold) == MarketListingPriceSanity.Sane),
            InsaneCount = saneThreshold > 0
                ? allListings.Count(listing => listing.PricePerUnit > saneThreshold)
                : 0
        };
    }

    private static MarketListingPriceSanity ClassifyPriceSanity(
        long pricePerUnit,
        long lowOutlierMaxUnitPrice,
        decimal saneThreshold)
    {
        if (lowOutlierMaxUnitPrice > 0 && pricePerUnit <= lowOutlierMaxUnitPrice)
        {
            return MarketListingPriceSanity.LowOutlier;
        }

        return saneThreshold > 0 && pricePerUnit > saneThreshold
            ? MarketListingPriceSanity.Insane
            : MarketListingPriceSanity.Sane;
    }

    private static MarketPriceEvaluationDiagnostics CreateDiagnostics(
        IReadOnlyList<ScopeListing> centralListings,
        IReadOnlyList<ScopeListing> allListings,
        MarketPriceRegionCredibility credibility)
    {
        var diagnostics = new MarketPriceEvaluationDiagnostics
        {
            DebugDetailAvailable = false
        };
        if (centralListings.Any(listing => listing.IsHq) && centralListings.Any(listing => !listing.IsHq))
        {
            diagnostics.CompactReasonCodes.Add(MarketPriceEvaluationReasonCode.QualityChannelFallbackToCombined);
        }

        if (centralListings.Count > 0)
        {
            diagnostics.CompactRegionSummaries.Add(new MarketPriceRegionSummary
            {
                MinUnitPrice = centralListings.Min(listing => listing.PricePerUnit),
                MaxUnitPrice = centralListings.Max(listing => listing.PricePerUnit),
                ListingCount = centralListings.Count,
                TotalQuantity = centralListings.Sum(listing => listing.Quantity),
                Credibility = credibility,
                ReasonCode = MarketPriceEvaluationReasonCode.Unknown
            });
        }

        diagnostics.DetectedPriceGapSummaries.AddRange(BuildCachedPriceBands(allListings)
            .Where(band => band.NextBreakPercent.HasValue)
            .Select(band => new MarketPriceGapSummary
            {
                BeforeUnitPrice = band.MaxUnitPrice,
                AfterUnitPrice = band.NextMinimumUnitPrice,
                BreakPercent = band.NextBreakPercent!.Value
            }));

        return diagnostics;
    }

    private static MarketPriceRegionCredibility CalculateCredibility(IReadOnlyList<ScopeListing> listings)
    {
        if (listings.Count == 0)
        {
            return MarketPriceRegionCredibility.Unknown;
        }

        var totalQuantity = listings.Sum(listing => listing.Quantity);
        var distinctWorlds = listings
            .Select(listing => listing.WorldName)
            .Where(world => !string.IsNullOrWhiteSpace(world))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (listings.Count >= 3 && distinctWorlds >= 2)
        {
            return MarketPriceRegionCredibility.Strong;
        }

        return listings.Count >= 2 || totalQuantity >= FullStackQuantity
            ? MarketPriceRegionCredibility.Credible
            : MarketPriceRegionCredibility.Thin;
    }

    private static decimal CalculateWeightedMedian(IReadOnlyList<ScopeListing> listings)
    {
        var totalQuantity = listings.Sum(listing => (long)listing.Quantity);
        if (totalQuantity <= 0)
        {
            return 0;
        }

        var midpoint = (totalQuantity + 1) / 2;
        long cumulativeQuantity = 0;
        foreach (var listing in listings.OrderBy(listing => listing.PricePerUnit))
        {
            cumulativeQuantity += listing.Quantity;
            if (cumulativeQuantity >= midpoint)
            {
                return listing.PricePerUnit;
            }
        }

        return listings[^1].PricePerUnit;
    }

    private static decimal CalculateWeightedAveragePrice(IReadOnlyList<ScopeListing> listings)
    {
        var totalQuantity = listings.Sum(listing => (long)listing.Quantity);
        if (totalQuantity <= 0)
        {
            return 0;
        }

        var totalCost = listings.Sum(listing => (decimal)listing.PricePerUnit * listing.Quantity);
        return totalCost / totalQuantity;
    }

    private static long? CalculateLowOutlierMaxUnitPrice(int itemId, IReadOnlyList<ScopeListing> listings)
    {
        var bands = BuildCachedPriceBands(listings);
        var lowOutlierBand = bands
            .OrderBy(band => band.MinUnitPrice)
            .FirstOrDefault(band => band.NextBreakPercent >= LowOutlierBreakPercent);

        if (lowOutlierBand == null)
        {
            return null;
        }

        var lowRegionBands = bands
            .Where(band => band.MinUnitPrice <= lowOutlierBand.MaxUnitPrice)
            .ToList();
        if (IsCrediblePriceRegion(itemId, lowRegionBands))
        {
            return null;
        }

        return lowOutlierBand.MaxUnitPrice;
    }

    private static List<ScopeListing> TrimHighOutlierTail(IReadOnlyList<ScopeListing> listings)
    {
        var bands = BuildCachedPriceBands(listings);
        var lastBaselineBand = bands.FirstOrDefault(band => band.NextBreakPercent >= LowOutlierBreakPercent);
        if (lastBaselineBand == null)
        {
            return listings.ToList();
        }

        var trimmed = listings
            .Where(listing => listing.PricePerUnit <= lastBaselineBand.MaxUnitPrice)
            .ToList();

        return trimmed.Count > 0 ? trimmed : listings.ToList();
    }

    private static List<CachedPriceBand> BuildCachedPriceBands(IReadOnlyList<ScopeListing> listings)
    {
        var bands = new List<List<ScopeListing>>();
        var current = new List<ScopeListing>();

        foreach (var listing in listings.OrderBy(listing => listing.PricePerUnit))
        {
            if (current.Count > 0 && listing.PricePerUnit > CalculateWeightedAveragePrice(current) * (1 + BandTolerance))
            {
                bands.Add(current);
                current = [];
            }

            current.Add(listing);
        }

        if (current.Count > 0)
        {
            bands.Add(current);
        }

        return bands
            .Select((band, index) =>
            {
                var average = CalculateWeightedAveragePrice(band);
                var nextMinimum = index < bands.Count - 1
                    ? bands[index + 1].Min(listing => listing.PricePerUnit)
                    : (long?)null;

                return new CachedPriceBand(
                    band.Min(listing => listing.PricePerUnit),
                    band.Max(listing => listing.PricePerUnit),
                    band.Count,
                    band.Sum(listing => listing.Quantity),
                    band.Max(listing => listing.Quantity),
                    band
                        .Select(listing => listing.RetainerName)
                        .Where(retainer => !string.IsNullOrWhiteSpace(retainer))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    band
                        .Select(listing => listing.WorldName)
                        .Where(world => !string.IsNullOrWhiteSpace(world))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    nextMinimum ?? 0,
                    nextMinimum.HasValue ? CalculateBreakPercent(average, nextMinimum.Value) : null);
            })
            .ToList();
    }

    private static bool IsCrediblePriceRegion(int itemId, IReadOnlyList<CachedPriceBand> bands)
    {
        var quantity = bands.Sum(band => band.Quantity);
        var isElementalCommodity = IsElementalCommodity(itemId);

        // TODO: Replace this eyeballed shard/crystal/cluster rule with item-family metadata when available.
        var fullStackQuantity = isElementalCommodity ? ElementalCommodityCredibleLowRegionQuantity : FullStackQuantity;
        var minimumCredibleQuantity = isElementalCommodity
            ? ElementalCommodityMinimumCredibleLowRegionQuantity
            : MinimumCredibleLowRegionQuantity;
        var minimumSubstantialListingQuantity = isElementalCommodity
            ? ElementalCommodityMinimumSubstantialLowListingQuantity
            : MinimumSubstantialLowListingQuantity;

        return quantity >= fullStackQuantity ||
            (quantity >= minimumCredibleQuantity &&
                bands.Max(band => band.MaxListingQuantity) >= minimumSubstantialListingQuantity &&
                (bands.Sum(band => band.DistinctRetainerCount) >= 2 ||
                    bands.Sum(band => band.DistinctWorldCount) >= 2));
    }

    private static bool IsElementalCommodity(int itemId) => itemId is >= 2 and <= 19;

    private static decimal CalculateBreakPercent(decimal currentAverage, long nextMinimum)
    {
        return currentAverage <= 0
            ? 0
            : (nextMinimum - currentAverage) / currentAverage * 100m;
    }

    private static IEnumerable<ScopeListing> CreateScopeListings(
        CachedMarketData entry,
        CachedWorldData world,
        DateTime evaluatedAtUtc)
    {
        var dataQualityBucket = CalculateDataQualityBucket(entry, world, evaluatedAtUtc);
        return world.Listings.Select(listing => new ScopeListing(
            listing.Quantity,
            listing.PricePerUnit,
            listing.RetainerName,
            listing.IsHq,
            world.WorldName,
            dataQualityBucket));
    }

    private static MarketDataQualityBucket CalculateDataQualityBucket(
        CachedMarketData entry,
        CachedWorldData world,
        DateTime evaluatedAtUtc)
    {
        if (TryGetUtcFromUnixMilliseconds(world.LastUploadTimeUnixMilliseconds, out var worldUploadedAtUtc))
        {
            return CalculateDataQualityBucket(worldUploadedAtUtc, evaluatedAtUtc);
        }

        if (TryGetUtcFromUnixMilliseconds(entry.LastUploadTimeUnixMilliseconds, out var responseUploadedAtUtc))
        {
            return CalculateDataQualityBucket(responseUploadedAtUtc, evaluatedAtUtc);
        }

        var fallbackBucket = CalculateDataQualityBucket(entry.FetchedAt, evaluatedAtUtc);
        return fallbackBucket == MarketDataQualityBucket.Current
            ? MarketDataQualityBucket.Aging
            : fallbackBucket;
    }

    private static MarketDataQualityBucket CalculateDataQualityBucket(DateTime timestampUtc, DateTime evaluatedAtUtc)
    {
        var age = CacheTimeHelper.NormalizeToUtc(evaluatedAtUtc) - CacheTimeHelper.NormalizeToUtc(timestampUtc);
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age <= TimeSpan.FromMinutes(15))
        {
            return MarketDataQualityBucket.Current;
        }

        if (age <= TimeSpan.FromHours(1))
        {
            return MarketDataQualityBucket.Aging;
        }

        if (age <= TimeSpan.FromHours(6))
        {
            return MarketDataQualityBucket.Old;
        }

        return MarketDataQualityBucket.VeryOld;
    }

    private static bool TryGetUtcFromUnixMilliseconds(long? unixMilliseconds, out DateTime value)
    {
        if (unixMilliseconds is > 0)
        {
            value = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds.Value).UtcDateTime;
            return true;
        }

        value = default;
        return false;
    }

    private sealed record ScopeListing(
        int Quantity,
        long PricePerUnit,
        string RetainerName,
        bool IsHq,
        string WorldName,
        MarketDataQualityBucket DataQualityBucket);

    private sealed record CachedPriceBand(
        long MinUnitPrice,
        long MaxUnitPrice,
        int ListingCount,
        int Quantity,
        int MaxListingQuantity,
        int DistinctRetainerCount,
        int DistinctWorldCount,
        long NextMinimumUnitPrice,
        decimal? NextBreakPercent);
}
