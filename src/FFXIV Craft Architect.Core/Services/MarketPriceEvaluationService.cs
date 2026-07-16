using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class MarketPriceEvaluationService : IMarketPriceEvaluationService
{
    private const decimal BandTolerance = 0.10m;
    private const double TukeyFenceMultiplier = 1.5d;
    private const double MadScale = 1.4826d;
    private const double MadFenceSigma = 3d;

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

        var distribution = BuildReferenceDistribution(listings);
        var baselineListings = distribution.CentralListings;
        var median = distribution.MedianUnitPrice;
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
                lowOutlierMaxUnitPrice: 0,
                support: MarketRegionSupport.Empty);
        }

        var averageListings = baselineListings;
        var average = CalculateSupportWeightedAveragePrice(distribution.CentralWeightedListings);
        var baseline = average > 0 ? Math.Max(average, median) : median;
        var competitiveThreshold = Math.Max(baseline, distribution.UpperQuartileUnitPrice);
        var saneThreshold = Math.Max(competitiveThreshold, distribution.UpperFenceUnitPrice);
        var competitiveWeightedListings = distribution.CentralWeightedListings
            .Where(entry => entry.Listing.PricePerUnit <= competitiveThreshold)
            .ToList();
        var competitiveAverage = CalculateSupportWeightedAveragePrice(competitiveWeightedListings);
        var qualityPolicy = DetermineQualityPolicy(listings);
        var lowOutlierMaxUnitPrice = listings
            .Where(listing => listing.PricePerUnit < distribution.LowerFenceUnitPrice)
            .Select(listing => listing.PricePerUnit)
            .DefaultIfEmpty(0)
            .Max();
        var support = CalculateSupport(listings, averageListings);

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
            saneThreshold,
            lowOutlierMaxUnitPrice,
            support);
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
        long lowOutlierMaxUnitPrice,
        MarketRegionSupport? support = null)
    {
        support ??= CalculateSupport(allListings, centralListings);
        var credibility = support.Credibility;
        var priceEvaluation = new MarketPriceEvaluation
        {
            ItemId = itemId,
            Scope = scope,
            QualityPolicy = qualityPolicy,
            EvaluatedAtUtc = evaluatedAtUtc,
            CentralRegion = CreateCentralRegion(centralListings, median, average, support),
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
                baseline,
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
        MarketRegionSupport support)
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
            SupportScore = support.Score,
            ListingShare = support.ListingShare,
            SourceShare = support.SourceShare,
            WorldShare = support.WorldShare,
            DataQualityBucket = centralListings.Count > 0
                ? centralListings.Max(listing => listing.DataQualityBucket)
                : MarketDataQualityBucket.Missing,
            Credibility = support.Credibility
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
        decimal dealThreshold,
        decimal competitiveThreshold,
        decimal saneThreshold,
        long lowOutlierMaxUnitPrice)
    {
        var thresholds = new MarketPriceThresholds
        {
            DealCeilingUnitPrice = dealThreshold,
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
        if (allListings.Any(listing => listing.IsHq) && allListings.Any(listing => !listing.IsHq))
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

    private static ReferenceDistribution BuildReferenceDistribution(IReadOnlyList<ScopeListing> listings)
    {
        var weightedListings = CreateSourceWeightedListings(listings);
        var dominantListings = SelectDominantPriceRegion(weightedListings);
        var lowerQuartileLog = WeightedQuantileLog(dominantListings, 0.25m);
        var medianLog = WeightedQuantileLog(dominantListings, 0.50m);
        var upperQuartileLog = WeightedQuantileLog(dominantListings, 0.75m);
        var interquartileRange = Math.Max(0d, upperQuartileLog - lowerQuartileLog);

        double lowerFenceLog;
        double upperFenceLog;
        if (interquartileRange > double.Epsilon)
        {
            lowerFenceLog = lowerQuartileLog - TukeyFenceMultiplier * interquartileRange;
            upperFenceLog = upperQuartileLog + TukeyFenceMultiplier * interquartileRange;
        }
        else
        {
            var deviations = dominantListings
                .Select(entry => new WeightedScalar(Math.Abs(Math.Log(entry.Listing.PricePerUnit) - medianLog), entry.Weight))
                .ToList();
            var medianAbsoluteDeviation = WeightedQuantile(deviations, 0.50m);
            var fence = MadFenceSigma * MadScale * medianAbsoluteDeviation;
            lowerFenceLog = medianLog - fence;
            upperFenceLog = medianLog + fence;
        }

        var centralWeightedListings = dominantListings
            .Where(entry =>
            {
                var logPrice = Math.Log(entry.Listing.PricePerUnit);
                return logPrice >= lowerFenceLog && logPrice <= upperFenceLog;
            })
            .ToList();
        if (centralWeightedListings.Count == 0)
        {
            centralWeightedListings =
            [
                dominantListings
                    .OrderBy(entry => Math.Abs(Math.Log(entry.Listing.PricePerUnit) - medianLog))
                    .First()
            ];
        }

        return new ReferenceDistribution(
            centralWeightedListings.Select(entry => entry.Listing).ToList(),
            centralWeightedListings,
            ToUnitPrice(medianLog),
            ToUnitPrice(upperQuartileLog),
            ToUnitPrice(lowerFenceLog),
            ToUnitPrice(upperFenceLog));
    }

    private static List<WeightedScopeListing> CreateSourceWeightedListings(IReadOnlyList<ScopeListing> listings)
    {
        return listings
            .Select((listing, index) => new IndexedScopeListing(listing, index, CreateSourceKey(listing, index)))
            .GroupBy(entry => entry.SourceKey, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group =>
            {
                var entries = group.ToList();
                var sourceFreshnessWeight = entries.Average(entry => GetFreshnessWeight(entry.Listing.DataQualityBucket));
                var listingWeight = sourceFreshnessWeight / entries.Count;
                return entries.Select(entry => new WeightedScopeListing(entry.Listing, listingWeight));
            })
            .OrderBy(entry => entry.Listing.PricePerUnit)
            .ToList();
    }

    private static List<WeightedScopeListing> SelectDominantPriceRegion(IReadOnlyList<WeightedScopeListing> listings)
    {
        if (listings.Count <= 1)
        {
            return listings.ToList();
        }

        var ordered = listings.OrderBy(entry => entry.Listing.PricePerUnit).ToList();
        var gaps = new List<PriceGap>();
        for (var index = 0; index < ordered.Count - 1; index++)
        {
            var current = ordered[index].Listing.PricePerUnit;
            var next = ordered[index + 1].Listing.PricePerUnit;
            if (next <= current)
            {
                continue;
            }

            gaps.Add(new PriceGap(index, Math.Log((double)next / current)));
        }

        if (gaps.Count == 0)
        {
            return ordered;
        }

        var largestGap = gaps.OrderByDescending(gap => gap.LogDistance).First();
        var shouldSplit = gaps.Count == 1 || IsDominantGap(largestGap, gaps);
        if (!shouldSplit)
        {
            return ordered;
        }

        var left = ordered.Take(largestGap.Index + 1).ToList();
        var right = ordered.Skip(largestGap.Index + 1).ToList();
        var leftSupport = left.Sum(entry => entry.Weight);
        var rightSupport = right.Sum(entry => entry.Weight);

        // Equal support is genuinely ambiguous. Prefer the lower executable price
        // as the reference while preserving every region in the price-band view.
        return leftSupport >= rightSupport ? left : right;
    }

    private static bool IsDominantGap(PriceGap candidate, IReadOnlyList<PriceGap> gaps)
    {
        var scalars = gaps
            .Select(gap => new WeightedScalar(gap.LogDistance, 1m))
            .ToList();
        var median = WeightedQuantile(scalars, 0.50m);
        var deviations = scalars
            .Select(value => new WeightedScalar(Math.Abs(value.Value - median), value.Weight))
            .ToList();
        var mad = WeightedQuantile(deviations, 0.50m);
        var robustUpperFence = median + MadFenceSigma * MadScale * mad;
        return candidate.LogDistance > robustUpperFence;
    }

    private static decimal CalculateSupportWeightedAveragePrice(IReadOnlyList<WeightedScopeListing> listings)
    {
        var totalWeight = listings.Sum(entry => entry.Weight);
        return totalWeight <= 0
            ? 0
            : listings.Sum(entry => entry.Listing.PricePerUnit * entry.Weight) / totalWeight;
    }

    private static decimal CalculateWeightedAveragePrice(IReadOnlyList<ScopeListing> listings)
    {
        var totalQuantity = listings.Sum(listing => (long)listing.Quantity);
        return totalQuantity <= 0
            ? 0
            : listings.Sum(listing => (decimal)listing.PricePerUnit * listing.Quantity) / totalQuantity;
    }

    private static MarketRegionSupport CalculateSupport(
        IReadOnlyList<ScopeListing> allListings,
        IReadOnlyList<ScopeListing> centralListings)
    {
        if (centralListings.Count == 0 || allListings.Count == 0)
        {
            return MarketRegionSupport.Empty;
        }

        var allSourceCount = CountSources(allListings);
        var centralSourceCount = CountSources(centralListings);
        var allWorldCount = CountWorlds(allListings);
        var centralWorldCount = CountWorlds(centralListings);
        var listingShare = centralListings.Count / (decimal)allListings.Count;
        var sourceShare = allSourceCount > 0 ? centralSourceCount / (decimal)allSourceCount : 0;
        var worldShare = allWorldCount > 0 ? centralWorldCount / (decimal)allWorldCount : 0;
        var sourceStrength = SmoothIndependentStrength(centralSourceCount);
        var worldStrength = SmoothIndependentStrength(centralWorldCount);
        var independentStrength = sourceStrength * 0.60m + worldStrength * 0.40m;
        var relativeSupport = listingShare * 0.30m + sourceShare * 0.45m + worldShare * 0.25m;
        var freshness = centralListings.Average(listing => GetFreshnessWeight(listing.DataQualityBucket));
        var score = Math.Clamp(independentStrength * 0.65m + relativeSupport * 0.25m + freshness * 0.10m, 0, 1);
        var credibility = score switch
        {
            >= 0.70m => MarketPriceRegionCredibility.Strong,
            >= 0.40m => MarketPriceRegionCredibility.Credible,
            _ => MarketPriceRegionCredibility.Thin
        };

        return new MarketRegionSupport(score, listingShare, sourceShare, worldShare, credibility);
    }

    private static decimal SmoothIndependentStrength(int independentCount)
    {
        return independentCount <= 0
            ? 0
            : 1m - (decimal)(1d / Math.Sqrt(independentCount));
    }

    private static int CountSources(IEnumerable<ScopeListing> listings)
    {
        return listings
            .Select((listing, index) => CreateSourceKey(listing, index))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static int CountWorlds(IEnumerable<ScopeListing> listings)
    {
        return listings
            .Select(listing => listing.WorldName)
            .Where(world => !string.IsNullOrWhiteSpace(world))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static string CreateSourceKey(ScopeListing listing, int index)
    {
        var retainer = string.IsNullOrWhiteSpace(listing.RetainerName)
            ? $"missing-{index}"
            : listing.RetainerName.Trim();
        return $"{listing.WorldName.Trim()}\u001f{retainer}\u001f{listing.IsHq}";
    }

    private static decimal GetFreshnessWeight(MarketDataQualityBucket bucket)
    {
        return 1m / (1m + (int)bucket);
    }

    private static double WeightedQuantileLog(IReadOnlyList<WeightedScopeListing> listings, decimal quantile)
    {
        return WeightedQuantile(
            listings.Select(entry => new WeightedScalar(Math.Log(entry.Listing.PricePerUnit), entry.Weight)).ToList(),
            quantile);
    }

    private static double WeightedQuantile(IReadOnlyList<WeightedScalar> values, decimal quantile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var ordered = values.OrderBy(value => value.Value).ToList();
        var totalWeight = ordered.Sum(value => value.Weight);
        var target = totalWeight * Math.Clamp(quantile, 0, 1);
        decimal cumulative = 0;
        foreach (var value in ordered)
        {
            cumulative += value.Weight;
            if (cumulative >= target)
            {
                return value.Value;
            }
        }

        return ordered[^1].Value;
    }

    private static decimal ToUnitPrice(double logPrice)
    {
        var value = Math.Exp(logPrice);
        return !double.IsFinite(value) || value >= (double)decimal.MaxValue
            ? decimal.MaxValue
            : Math.Max(0, (decimal)value);
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
            return MarketEvidenceFreshness.Evaluate(worldUploadedAtUtc, evaluatedAtUtc).Bucket;
        }

        if (TryGetUtcFromUnixMilliseconds(entry.LastUploadTimeUnixMilliseconds, out var responseUploadedAtUtc))
        {
            return MarketEvidenceFreshness.Evaluate(responseUploadedAtUtc, evaluatedAtUtc).Bucket;
        }

        return MarketEvidenceFreshness.Evaluate(
            entry.FetchedAt,
            evaluatedAtUtc,
            capCurrentToAging: true).Bucket;
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

    private sealed record IndexedScopeListing(ScopeListing Listing, int Index, string SourceKey);

    private sealed record WeightedScopeListing(ScopeListing Listing, decimal Weight);

    private readonly record struct WeightedScalar(double Value, decimal Weight);

    private readonly record struct PriceGap(int Index, double LogDistance);

    private sealed record ReferenceDistribution(
        IReadOnlyList<ScopeListing> CentralListings,
        IReadOnlyList<WeightedScopeListing> CentralWeightedListings,
        decimal MedianUnitPrice,
        decimal UpperQuartileUnitPrice,
        decimal LowerFenceUnitPrice,
        decimal UpperFenceUnitPrice);

    private sealed record MarketRegionSupport(
        decimal Score,
        decimal ListingShare,
        decimal SourceShare,
        decimal WorldShare,
        MarketPriceRegionCredibility Credibility)
    {
        public static MarketRegionSupport Empty { get; } = new(
            0,
            0,
            0,
            0,
            MarketPriceRegionCredibility.Unknown);
    }

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
