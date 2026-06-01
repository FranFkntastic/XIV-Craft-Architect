using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class MarketPriceLadderAnalysisService : IMarketPriceLadderAnalysisService
{
    private const decimal BandTolerance = 0.10m;
    private const decimal OutlierMultiplier = 2.5m;
    private const decimal ScopeSaneMultiplier = 2.0m;
    private const decimal ScopeCompetitiveMultiplier = 1.5m;
    private const decimal LowOutlierBreakPercent = 400m;

    public async Task<List<MarketItemAnalysis>> AnalyzeAsync(
        MarketAnalysisRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Evidence);

        var execution = executionOptions ?? MarketAnalysisExecutionOptions.Synchronous;
        var nowUtc = DateTime.UtcNow;
        var analyses = new List<MarketItemAnalysis>();

        for (var itemIndex = 0; itemIndex < request.Items.Count; itemIndex++)
        {
            var item = request.Items[itemIndex];
            var completedItems = itemIndex + 1;
            if (execution.ShouldReportProgress(completedItems))
            {
                progress?.Report($"Analyzing market ladders {completedItems}/{request.Items.Count}: {item.Name}...");
            }

            ct.ThrowIfCancellationRequested();

            var entries = request.Evidence.GetEntriesForItem(item.ItemId);
            var scopePriceContext = CalculateScopePriceContext(entries, item.TotalQuantity);
            var missingDataCenters = request.Evidence.MissingRequests
                .Where(pair => pair.itemId == item.ItemId)
                .Select(pair => pair.dataCenter)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(dataCenter => dataCenter, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var worlds = entries
                .SelectMany(entry => AnalyzeEntryWorlds(entry, item.TotalQuantity, nowUtc, scopePriceContext))
                .OrderBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var presentDataCenters = entries
                .Select(entry => entry.DataCenter)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(dataCenter => dataCenter, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var requestedDataCenters = request.Evidence.RequestedPairs
                .Where(pair => pair.itemId == item.ItemId)
                .Select(pair => pair.dataCenter)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(dataCenter => dataCenter, StringComparer.OrdinalIgnoreCase)
                .ToList();
            worlds.AddRange(CreateMissingWorldRows(
                request.ExpectedWorldsByDataCenter,
                worlds,
                missingDataCenters,
                item.TotalQuantity));

            ApplyLensRanks(worlds, MarketAcquisitionLens.MinimumUpfrontCost);
            ApplyLensRanks(worlds, MarketAcquisitionLens.BulkValue);

            analyses.Add(new MarketItemAnalysis
            {
                ItemId = item.ItemId,
                Name = item.Name,
                QuantityNeeded = item.TotalQuantity,
                Scope = request.Evidence.Scope,
                LoadedAtUtc = request.Evidence.LoadedAtUtc,
                AnalysisScopeBaselineUnitPrice = scopePriceContext.BaselineUnitPrice,
                AnalysisScopeAverageUnitPrice = scopePriceContext.AverageUnitPrice,
                AnalysisScopeCompetitiveAverageUnitPrice = scopePriceContext.CompetitiveAverageUnitPrice,
                AnalysisScopeMedianUnitPrice = scopePriceContext.MedianUnitPrice,
                CompetitiveThresholdUnitPrice = scopePriceContext.CompetitiveThresholdUnitPrice,
                SaneThresholdUnitPrice = scopePriceContext.SaneThresholdUnitPrice,
                RequestedDataCenters = requestedDataCenters,
                PresentDataCenters = presentDataCenters,
                MissingDataCenters = missingDataCenters,
                WorstDataQualityBucket = GetWorstDataQuality(worlds, missingDataCenters),
                Worlds = worlds,
                Warning = CreateWarning(entries.Count, missingDataCenters)
            });

            if (execution.ShouldYieldAfterItem(completedItems))
            {
                await Task.Yield();
            }
        }

        return analyses;
    }

    public DetailedShoppingPlan ProjectToShoppingPlan(
        MarketItemAnalysis analysis,
        MarketAcquisitionLens lens,
        MarketAnalysisConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var plan = new DetailedShoppingPlan
        {
            ItemId = analysis.ItemId,
            Name = analysis.Name,
            QuantityNeeded = analysis.QuantityNeeded,
            MarketDataWarning = analysis.Warning
        };

        foreach (var world in GetSortedWorlds(analysis.Worlds, lens))
        {
            var listings = GetProcurementListings(world).ToList();
            var summary = CreateWorldSummary(world, listings, analysis.QuantityNeeded, lens);
            plan.WorldOptions.Add(summary);
        }

        plan.RecommendedWorld = plan.WorldOptions
            .Where(world => world.HasSufficientStock)
            .OrderBy(world => world.ProcurementPriorityScore)
            .ThenBy(world => world.TotalCost)
            .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (plan.RecommendedWorld == null && plan.WorldOptions.Sum(world => world.TotalQuantityPurchased) >= analysis.QuantityNeeded)
        {
            plan.RecommendedSplit = MarketShoppingService.BuildSplitPurchase(
                analysis.QuantityNeeded,
                plan.WorldOptions
                    .Where(world => world.TotalQuantityPurchased > 0));
        }

        if (plan.WorldOptions.Count == 0 && !string.IsNullOrWhiteSpace(analysis.Warning))
        {
            plan.Error = analysis.Warning;
        }

        return plan;
    }

    private static ScopePriceContext CalculateScopePriceContext(IReadOnlyList<CachedMarketData> entries, int quantityNeeded)
    {
        var listings = entries
            .SelectMany(entry => entry.Worlds)
            .SelectMany(world => world.Listings)
            .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
            .OrderBy(listing => listing.PricePerUnit)
            .ToList();
        if (listings.Count == 0)
        {
            return ScopePriceContext.Empty;
        }

        var lowOutlierMaxUnitPrice = CalculateLowOutlierMaxUnitPrice(listings, quantityNeeded);
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
            return ScopePriceContext.Empty;
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

        return new ScopePriceContext(
            baseline,
            average,
            competitiveAverage,
            median,
            competitiveThreshold,
            baseline * ScopeSaneMultiplier,
            lowOutlierMaxUnitPrice ?? 0);
    }

    private static decimal CalculateWeightedMedian(IReadOnlyList<CachedListing> listings)
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

    private static decimal CalculateWeightedAveragePrice(IReadOnlyList<CachedListing> listings)
    {
        var totalQuantity = listings.Sum(listing => (long)listing.Quantity);
        if (totalQuantity <= 0)
        {
            return 0;
        }

        var totalCost = listings.Sum(listing => (decimal)listing.PricePerUnit * listing.Quantity);
        return totalCost / totalQuantity;
    }

    private static long? CalculateLowOutlierMaxUnitPrice(IReadOnlyList<CachedListing> listings, int quantityNeeded)
    {
        var bands = BuildCachedPriceBands(listings);
        var lowOutlierBand = bands
            .OrderBy(band => band.MinUnitPrice)
            .FirstOrDefault(band => band.NextBreakPercent >= LowOutlierBreakPercent);

        if (lowOutlierBand != null &&
            bands
                .Where(band => band.MinUnitPrice <= lowOutlierBand.MaxUnitPrice)
                .Sum(band => band.Quantity) >= Math.Max(quantityNeeded, 1))
        {
            return null;
        }

        return lowOutlierBand?.MaxUnitPrice;
    }

    private static List<CachedListing> TrimHighOutlierTail(IReadOnlyList<CachedListing> listings)
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

    private static List<CachedPriceBand> BuildCachedPriceBands(IReadOnlyList<CachedListing> listings)
    {
        var bands = new List<List<CachedListing>>();
        var current = new List<CachedListing>();

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
                    band.Sum(listing => listing.Quantity),
                    nextMinimum.HasValue ? CalculateBreakPercent(average, nextMinimum.Value) : null);
            })
            .ToList();
    }

    private static IEnumerable<WorldMarketAnalysis> AnalyzeEntryWorlds(
        CachedMarketData entry,
        int quantityNeeded,
        DateTime nowUtc,
        ScopePriceContext scopePriceContext)
    {
        var dataCenter = entry.DataCenter;

        foreach (var world in entry.Worlds)
        {
            var dataAge = CalculateDataQuality(entry, world, nowUtc);
            yield return AnalyzeWorld(
                dataCenter,
                world.WorldName,
                world.Listings,
                quantityNeeded,
                entry.FetchedAt,
                dataAge.UploadedAtUtc,
                dataAge.Age,
                dataAge.Source,
                dataAge.Score,
                dataAge.Bucket,
                scopePriceContext);
        }
    }

    private static WorldMarketAnalysis AnalyzeWorld(
        string dataCenter,
        string worldName,
        IReadOnlyList<CachedListing> rawListings,
        int quantityNeeded,
        DateTime fetchedAtUtc,
        DateTime? uploadedAtUtc,
        TimeSpan age,
        MarketDataAgeSource dataAgeSource,
        decimal dataQualityScore,
        MarketDataQualityBucket dataQualityBucket,
        ScopePriceContext scopePriceContext)
    {
        var sorted = rawListings
            .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
            .OrderBy(listing => listing.PricePerUnit)
            .ThenByDescending(listing => listing.Quantity)
            .ThenBy(listing => listing.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var outlierThreshold = CalculateOutlierThreshold(sorted);
        var analyzedListings = sorted
            .Select((listing, index) => new AnalyzedMarketListing
            {
                SortIndex = index,
                Quantity = listing.Quantity,
                PricePerUnit = listing.PricePerUnit,
                RetainerName = listing.RetainerName,
                IsHq = listing.IsHq,
                LastReviewTimeUtc = listing.LastReviewTimeUnix.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(listing.LastReviewTimeUnix.Value).UtcDateTime
                    : null,
                PriceSanity = ClassifyInitialPriceSanity(
                    listing.PricePerUnit,
                    scopePriceContext.LowOutlierMaxUnitPrice,
                    outlierThreshold)
            })
            .ToList();

        var saneListings = analyzedListings
            .Where(listing => listing.PriceSanity is MarketListingPriceSanity.Sane or MarketListingPriceSanity.LowOutlier)
            .ToList();
        var bands = BuildPriceBands(saneListings);
        var competitiveBandIndexes = SelectCompetitiveShelf(bands, quantityNeeded);
        var competitiveQuantity = bands
            .Where((_, index) => competitiveBandIndexes.Contains(index))
            .Sum(band => band.Quantity);
        var localCompetitiveQuantity = competitiveQuantity;
        var saneThreshold = scopePriceContext.SaneThresholdUnitPrice;
        var scopeSaneListings = saneThreshold > 0
            ? analyzedListings.Where(listing => listing.PricePerUnit <= saneThreshold).ToList()
            : saneListings;
        var scopeSaneQuantity = scopeSaneListings.Sum(listing => listing.Quantity);
        var scopeInsaneQuantity = saneThreshold > 0
            ? analyzedListings.Where(listing => listing.PricePerUnit > saneThreshold).Sum(listing => listing.Quantity)
            : 0;
        var scopeCompetitiveThreshold = scopePriceContext.CompetitiveThresholdUnitPrice;
        var scopeCompetitiveQuantity = scopeCompetitiveThreshold > 0
            ? scopeSaneListings
                .Where(listing => listing.PricePerUnit <= scopeCompetitiveThreshold)
                .Sum(listing => listing.Quantity)
            : competitiveQuantity;
        var scopeCompetitiveAverageUnitPrice = scopeCompetitiveThreshold > 0
            ? CalculateWeightedAverage(scopeSaneListings
                .Where(listing => listing.PricePerUnit <= scopeCompetitiveThreshold)
                .ToList())
            : 0;
        var scopeUncompetitiveQuantity = scopeCompetitiveThreshold > 0
            ? scopeSaneListings
                .Where(listing => listing.PricePerUnit > scopeCompetitiveThreshold)
                .Sum(listing => listing.Quantity)
            : 0;
        var competitiveSortIndexes = competitiveBandIndexes
            .SelectMany(index => Enumerable.Range(bands[index].FirstListingIndex, bands[index].LastListingIndex - bands[index].FirstListingIndex + 1))
            .ToHashSet();
        var listings = analyzedListings
            .Select(listing => new AnalyzedMarketListing
            {
                SortIndex = listing.SortIndex,
                Quantity = listing.Quantity,
                PricePerUnit = listing.PricePerUnit,
                RetainerName = listing.RetainerName,
                IsHq = listing.IsHq,
                PriceSanity = saneThreshold > 0 && listing.PricePerUnit > saneThreshold
                    ? MarketListingPriceSanity.Insane
                    : listing.PriceSanity,
                LastReviewTimeUtc = listing.LastReviewTimeUtc,
                IsInCompetitiveShelf = competitiveSortIndexes.Contains(listing.SortIndex),
                IsScopeCompetitive = scopeCompetitiveThreshold > 0 && listing.PricePerUnit <= scopeCompetitiveThreshold,
                IsScopeUncompetitive = scopeCompetitiveThreshold > 0 &&
                    saneThreshold > 0 &&
                    listing.PricePerUnit > scopeCompetitiveThreshold &&
                    listing.PricePerUnit <= saneThreshold
            })
            .ToList();
        bands = bands
            .Select((band, index) => new MarketPriceBand
            {
                FirstListingIndex = band.FirstListingIndex,
                LastListingIndex = band.LastListingIndex,
                MinUnitPrice = band.MinUnitPrice,
                MaxUnitPrice = band.MaxUnitPrice,
                WeightedAverageUnitPrice = band.WeightedAverageUnitPrice,
                ListingCount = band.ListingCount,
                Quantity = band.Quantity,
                NextBreakPercent = band.NextBreakPercent,
                IsCompetitiveShelf = competitiveBandIndexes.Contains(index)
            })
            .ToList();

        var totalSaneQuantity = saneListings.Sum(listing => listing.Quantity);
        var totalListingQuantity = analyzedListings.Sum(listing => listing.Quantity);
        var coverageBucket = GetCoverageBucket(competitiveQuantity, totalSaneQuantity, quantityNeeded);
        var scores = CreateScores(
            quantityNeeded,
            competitiveQuantity,
            totalSaneQuantity,
            saneListings,
            bands,
            coverageBucket,
            dataQualityScore);

        return new WorldMarketAnalysis
        {
            DataCenter = dataCenter,
            WorldName = worldName,
            QuantityNeeded = quantityNeeded,
            CompetitiveQuantity = competitiveQuantity,
            LocalCompetitiveQuantity = localCompetitiveQuantity,
            ScopeCompetitiveQuantity = scopeCompetitiveQuantity,
            ScopeSaneQuantity = scopeSaneQuantity,
            ScopeUncompetitiveQuantity = scopeUncompetitiveQuantity,
            ScopeInsaneQuantity = scopeInsaneQuantity,
            TotalSaneQuantity = totalSaneQuantity,
            TotalListingQuantity = totalListingQuantity,
            CompetitiveCoverageRatio = CalculateCoverageRatio(competitiveQuantity, quantityNeeded),
            ScopeCompetitiveCoverageRatio = CalculateCoverageRatio(scopeCompetitiveQuantity, quantityNeeded),
            ScopeSaneCoverageRatio = CalculateCoverageRatio(scopeSaneQuantity, quantityNeeded),
            SaneCoverageRatio = CalculateCoverageRatio(totalSaneQuantity, quantityNeeded),
            CoverageBucket = coverageBucket,
            AnalysisScopeBaselineUnitPrice = scopePriceContext.BaselineUnitPrice,
            AnalysisScopeAverageUnitPrice = scopePriceContext.AverageUnitPrice,
            AnalysisScopeCompetitiveAverageUnitPrice = scopePriceContext.CompetitiveAverageUnitPrice,
            ScopeCompetitiveAverageUnitPrice = scopeCompetitiveAverageUnitPrice,
            AnalysisScopeMedianUnitPrice = scopePriceContext.MedianUnitPrice,
            CompetitiveThresholdUnitPrice = scopePriceContext.CompetitiveThresholdUnitPrice,
            SaneThresholdUnitPrice = scopePriceContext.SaneThresholdUnitPrice,
            FetchedAtUtc = fetchedAtUtc,
            MarketUploadedAtUtc = uploadedAtUtc,
            DataAgeSource = dataAgeSource,
            DataAge = age,
            DataQualityScore = dataQualityScore,
            DataQualityBucket = dataQualityBucket,
            PriceBands = bands,
            Listings = listings,
            Scores = scores
        };
    }

    private static IEnumerable<WorldMarketAnalysis> CreateMissingWorldRows(
        IReadOnlyDictionary<string, IReadOnlyList<string>> expectedWorldsByDataCenter,
        IReadOnlyList<WorldMarketAnalysis> presentWorlds,
        IReadOnlyList<string> missingDataCenters,
        int quantityNeeded)
    {
        foreach (var dataCenter in missingDataCenters)
        {
            var expectedWorlds = expectedWorldsByDataCenter.TryGetValue(dataCenter, out var worlds) && worlds.Count > 0
                ? worlds
                : [$"{dataCenter} data unavailable"];
            foreach (var worldName in expectedWorlds)
            {
                if (presentWorlds.Any(world =>
                    string.Equals(world.DataCenter, dataCenter, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(world.WorldName, worldName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return CreateMissingWorld(dataCenter, worldName, quantityNeeded);
            }
        }
    }

    private static WorldMarketAnalysis CreateMissingWorld(string dataCenter, string worldName, int quantityNeeded)
    {
        return new WorldMarketAnalysis
        {
            DataCenter = dataCenter,
            WorldName = worldName,
            QuantityNeeded = quantityNeeded,
            CompetitiveQuantity = 0,
            TotalSaneQuantity = 0,
            TotalListingQuantity = 0,
            CompetitiveCoverageRatio = 0,
            SaneCoverageRatio = 0,
            CoverageBucket = MarketCoverageBucket.None,
            FetchedAtUtc = null,
            MarketUploadedAtUtc = null,
            DataAgeSource = MarketDataAgeSource.Missing,
            DataAge = null,
            DataQualityScore = 0,
            DataQualityBucket = MarketDataQualityBucket.Missing,
            Scores =
            [
                new WorldLensScore
                {
                    Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                    Score = 0,
                    ScoreBucket = MarketScoreBucket.Unavailable,
                    Summary = "Missing market data"
                },
                new WorldLensScore
                {
                    Lens = MarketAcquisitionLens.BulkValue,
                    Score = 0,
                    ScoreBucket = MarketScoreBucket.Unavailable,
                    Summary = "Missing market data"
                }
            ]
        };
    }

    private static decimal? CalculateOutlierThreshold(IReadOnlyList<CachedListing> listings)
    {
        if (listings.Count < 3)
        {
            return null;
        }

        var baselineCount = Math.Max(1, listings.Count / 2);
        var baselinePrices = listings
            .Take(baselineCount)
            .Select(listing => listing.PricePerUnit)
            .Order()
            .ToList();
        var median = baselinePrices[baselinePrices.Count / 2];
        return median * OutlierMultiplier;
    }

    private static MarketListingPriceSanity ClassifyInitialPriceSanity(
        long pricePerUnit,
        long lowOutlierMaxUnitPrice,
        decimal? highOutlierThreshold)
    {
        if (lowOutlierMaxUnitPrice > 0 && pricePerUnit <= lowOutlierMaxUnitPrice)
        {
            return MarketListingPriceSanity.LowOutlier;
        }

        return highOutlierThreshold.HasValue && pricePerUnit > highOutlierThreshold.Value
            ? MarketListingPriceSanity.Outlier
            : MarketListingPriceSanity.Sane;
    }

    private sealed record CachedPriceBand(
        long MinUnitPrice,
        long MaxUnitPrice,
        int Quantity,
        decimal? NextBreakPercent);

    private static List<MarketPriceBand> BuildPriceBands(IReadOnlyList<AnalyzedMarketListing> listings)
    {
        var bands = new List<MarketPriceBand>();
        var current = new List<AnalyzedMarketListing>();

        foreach (var listing in listings)
        {
            if (current.Count > 0 && listing.PricePerUnit > CalculateWeightedAverage(current) * (1 + BandTolerance))
            {
                bands.Add(CreateBand(current));
                current.Clear();
            }

            current.Add(listing);
        }

        if (current.Count > 0)
        {
            bands.Add(CreateBand(current));
        }

        return bands
            .Select((band, index) => new MarketPriceBand
            {
                FirstListingIndex = band.FirstListingIndex,
                LastListingIndex = band.LastListingIndex,
                MinUnitPrice = band.MinUnitPrice,
                MaxUnitPrice = band.MaxUnitPrice,
                WeightedAverageUnitPrice = band.WeightedAverageUnitPrice,
                ListingCount = band.ListingCount,
                Quantity = band.Quantity,
                NextBreakPercent = index < bands.Count - 1
                    ? CalculateBreakPercent(band.WeightedAverageUnitPrice, bands[index + 1].MinUnitPrice)
                    : null,
                IsCompetitiveShelf = false
            })
            .ToList();
    }

    private static MarketPriceBand CreateBand(IReadOnlyList<AnalyzedMarketListing> listings)
    {
        return new MarketPriceBand
        {
            FirstListingIndex = listings.Min(listing => listing.SortIndex),
            LastListingIndex = listings.Max(listing => listing.SortIndex),
            MinUnitPrice = listings.Min(listing => listing.PricePerUnit),
            MaxUnitPrice = listings.Max(listing => listing.PricePerUnit),
            WeightedAverageUnitPrice = CalculateWeightedAverage(listings),
            ListingCount = listings.Count,
            Quantity = listings.Sum(listing => listing.Quantity)
        };
    }

    private static HashSet<int> SelectCompetitiveShelf(IReadOnlyList<MarketPriceBand> bands, int quantityNeeded)
    {
        var selected = new HashSet<int>();
        if (bands.Count == 0 || quantityNeeded <= 0)
        {
            return selected;
        }

        if (IsCredibleCompetitiveBand(bands[0], quantityNeeded))
        {
            selected.Add(0);
            return selected;
        }

        if (bands.Count > 1 && bands[0].Quantity < Math.Max(quantityNeeded / 10, 1) && bands[0].NextBreakPercent <= 10)
        {
            selected.Add(0);
            selected.Add(1);
            return selected;
        }

        return selected;
    }

    private static bool IsCredibleCompetitiveBand(MarketPriceBand band, int quantityNeeded)
    {
        return band.ListingCount >= 2 || band.Quantity >= Math.Max(quantityNeeded / 4, 1);
    }

    private static MarketCoverageBucket GetCoverageBucket(int competitiveQuantity, int saneQuantity, int neededQuantity)
    {
        if (neededQuantity <= 0 || saneQuantity <= 0)
        {
            return MarketCoverageBucket.None;
        }

        if (saneQuantity >= neededQuantity)
        {
            return MarketCoverageBucket.Full;
        }

        return competitiveQuantity >= Math.Max(neededQuantity / 2, 1)
            ? MarketCoverageBucket.PartialDeep
            : MarketCoverageBucket.PartialThin;
    }

    private static List<WorldLensScore> CreateScores(
        int quantityNeeded,
        int competitiveQuantity,
        int totalSaneQuantity,
        IReadOnlyList<AnalyzedMarketListing> saneListings,
        IReadOnlyList<MarketPriceBand> bands,
        MarketCoverageBucket coverageBucket,
        decimal dataQualityScore)
    {
        var costToCover = CalculateCostToCover(saneListings, quantityNeeded);
        var coverageRatio = CalculateCoverageRatio(totalSaneQuantity, quantityNeeded);
        var competitiveRatio = CalculateCoverageRatio(competitiveQuantity, quantityNeeded);
        var competitivePrice = bands.FirstOrDefault(band => band.IsCompetitiveShelf)?.WeightedAverageUnitPrice
            ?? saneListings.FirstOrDefault()?.PricePerUnit
            ?? 0;
        var minimumScore = costToCover.HasValue
            ? 1_000_000_000m / Math.Max(costToCover.Value, 1)
            : coverageRatio * 100;
        minimumScore += coverageBucket == MarketCoverageBucket.Full ? 10_000 : coverageRatio * 100;
        minimumScore *= dataQualityScore / 100m;

        var bulkScore = competitiveRatio * 10_000m
            + Math.Min(competitiveQuantity, quantityNeeded) * 10m;
        if (competitivePrice > 0)
        {
            bulkScore += 1_000m / competitivePrice;
        }

        bulkScore *= dataQualityScore / 100m;

        return
        [
            new WorldLensScore
            {
                Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                Score = minimumScore,
                ScoreBucket = ScoreBucketForMinimumUpfront(coverageBucket, dataQualityScore),
                Summary = costToCover.HasValue
                    ? $"{costToCover.Value:N0}g to cover need"
                    : $"{totalSaneQuantity:N0}/{quantityNeeded:N0} available"
            },
            new WorldLensScore
            {
                Lens = MarketAcquisitionLens.BulkValue,
                Score = bulkScore,
                ScoreBucket = ScoreBucketForBulkValue(competitiveQuantity, quantityNeeded, dataQualityScore),
                Summary = competitivePrice > 0
                    ? $"{competitiveQuantity:N0} competitive at ~{competitivePrice:N0}g"
                    : $"{competitiveQuantity:N0} competitive"
            }
        ];
    }

    private static MarketScoreBucket ScoreBucketForMinimumUpfront(
        MarketCoverageBucket coverageBucket,
        decimal dataQualityScore)
    {
        if (dataQualityScore <= 0 || coverageBucket == MarketCoverageBucket.None)
        {
            return MarketScoreBucket.Unavailable;
        }

        return coverageBucket switch
        {
            MarketCoverageBucket.Full => MarketScoreBucket.Optimal,
            MarketCoverageBucket.PartialDeep => MarketScoreBucket.PoorFit,
            MarketCoverageBucket.PartialThin => MarketScoreBucket.PoorFit,
            _ => MarketScoreBucket.Unavailable
        };
    }

    private static MarketScoreBucket ScoreBucketForBulkValue(
        int competitiveQuantity,
        int quantityNeeded,
        decimal dataQualityScore)
    {
        if (dataQualityScore <= 0 || competitiveQuantity <= 0 || quantityNeeded <= 0)
        {
            return MarketScoreBucket.Unavailable;
        }

        if (competitiveQuantity >= quantityNeeded)
        {
            return MarketScoreBucket.Optimal;
        }

        return competitiveQuantity >= Math.Max(quantityNeeded / 2, 1)
            ? MarketScoreBucket.Competitive
            : MarketScoreBucket.PoorFit;
    }

    private static void ApplyLensRanks(List<WorldMarketAnalysis> worlds, MarketAcquisitionLens lens)
    {
        var ranked = worlds
            .OrderByDescending(world => world.Scores.Single(score => score.Lens == lens).Score)
            .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var rank = 0; rank < ranked.Count; rank++)
        {
            var world = ranked[rank];
            world.Scores[world.Scores.FindIndex(score => score.Lens == lens)] = new WorldLensScore
            {
                Lens = lens,
                Score = world.Scores.Single(score => score.Lens == lens).Score,
                Rank = rank + 1,
                ScoreBucket = world.Scores.Single(score => score.Lens == lens).ScoreBucket,
                Summary = world.Scores.Single(score => score.Lens == lens).Summary
            };
        }
    }

    private static IReadOnlyList<WorldMarketAnalysis> GetSortedWorlds(
        IEnumerable<WorldMarketAnalysis> worlds,
        MarketAcquisitionLens lens)
    {
        return worlds
            .OrderBy(world => world.DataQualityBucket == MarketDataQualityBucket.Missing)
            .ThenBy(world => world.Scores.Single(score => score.Lens == lens).Rank)
            .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<AnalyzedMarketListing> GetProcurementListings(WorldMarketAnalysis world)
    {
        var listings = HasScopePriceContext(world)
            ? world.Listings.Where(listing => listing.IsScopeCompetitive)
            : world.Listings.Where(listing => listing.PriceSanity is MarketListingPriceSanity.Sane or MarketListingPriceSanity.LowOutlier);

        return listings.OrderBy(listing => listing.SortIndex);
    }

    private static bool HasScopePriceContext(WorldMarketAnalysis world)
    {
        return world.CompetitiveThresholdUnitPrice > 0 && world.SaneThresholdUnitPrice > 0;
    }

    private static WorldShoppingSummary CreateWorldSummary(
        WorldMarketAnalysis world,
        IReadOnlyList<AnalyzedMarketListing> listings,
        int quantityNeeded,
        MarketAcquisitionLens lens)
    {
        var remaining = quantityNeeded;
        var entries = new List<ShoppingListingEntry>();
        long totalCost = 0;
        var purchasedQuantity = 0;

        foreach (var listing in listings)
        {
            var neededFromStack = Math.Min(remaining, listing.Quantity);
            remaining -= neededFromStack;
            purchasedQuantity += listing.Quantity;
            totalCost += listing.Quantity * listing.PricePerUnit;
            entries.Add(new ShoppingListingEntry
            {
                Quantity = listing.Quantity,
                PricePerUnit = listing.PricePerUnit,
                RetainerName = listing.RetainerName,
                IsHq = listing.IsHq,
                IsUnderAverage = listing.IsInCompetitiveShelf,
                NeededFromStack = neededFromStack,
                ExcessQuantity = Math.Max(0, listing.Quantity - neededFromStack)
            });

            if (remaining <= 0)
            {
                break;
            }
        }

        var hasSufficientStock = remaining <= 0;
        var lensScore = world.Scores.FirstOrDefault(score => score.Lens == lens);
        var summary = new WorldShoppingSummary
        {
            DataCenter = world.DataCenter,
            WorldName = world.WorldName,
            TotalCost = totalCost,
            AveragePricePerUnit = purchasedQuantity > 0 ? totalCost / (decimal)purchasedQuantity : 0,
            ListingsUsed = entries.Count,
            Listings = entries,
            TotalQuantityPurchased = purchasedQuantity,
            ExcessQuantity = Math.Max(0, purchasedQuantity - quantityNeeded),
            HasSufficientStock = hasSufficientStock,
            ShortfallQuantity = Math.Max(0, remaining),
            BestSingleListing = entries.OrderBy(entry => entry.PricePerUnit).FirstOrDefault(),
            ModePricePerUnit = world.PriceBands.FirstOrDefault(band => band.IsCompetitiveShelf)?.MinUnitPrice ?? 0,
            ValueScore = hasSufficientStock ? totalCost : decimal.MaxValue,
            MarketDataQualityScore = world.DataQualityScore,
            MarketDataQualityBucket = world.DataQualityBucket,
            MarketDataAgeSource = world.DataAgeSource,
            LensRank = lensScore?.Rank ?? int.MaxValue,
            LensScoreBucket = lensScore?.ScoreBucket ?? MarketScoreBucket.Unavailable
        };
        summary.ProcurementPriorityScore = purchasedQuantity > 0
            ? MarketWorldRecommendationScoring.CalculatePriorityScore(summary.TotalCost, summary)
            : decimal.MaxValue;

        return summary;
    }

    private static DataQualityEvaluation CalculateDataQuality(
        CachedMarketData entry,
        CachedWorldData world,
        DateTime nowUtc)
    {
        if (TryGetUtcFromUnixMilliseconds(world.LastUploadTimeUnixMilliseconds, out var worldUploadedAtUtc))
        {
            var (score, bucket, age) = CalculateDataQuality(worldUploadedAtUtc, nowUtc);
            return new DataQualityEvaluation(score, bucket, age, worldUploadedAtUtc, MarketDataAgeSource.UniversalisWorldUpload);
        }

        if (TryGetUtcFromUnixMilliseconds(entry.LastUploadTimeUnixMilliseconds, out var responseUploadedAtUtc))
        {
            var (score, bucket, age) = CalculateDataQuality(responseUploadedAtUtc, nowUtc);
            return new DataQualityEvaluation(score, bucket, age, responseUploadedAtUtc, MarketDataAgeSource.UniversalisResponseUpload);
        }

        var fallback = CalculateDataQuality(entry.FetchedAt, nowUtc);
        var cappedScore = Math.Min(fallback.Score, 70);
        var cappedBucket = fallback.Bucket == MarketDataQualityBucket.Current
            ? MarketDataQualityBucket.Aging
            : fallback.Bucket;
        return new DataQualityEvaluation(
            cappedScore,
            cappedBucket,
            fallback.Age,
            null,
            MarketDataAgeSource.LocalFetchFallback);
    }

    private static (decimal Score, MarketDataQualityBucket Bucket, TimeSpan Age) CalculateDataQuality(
        DateTime timestampUtc,
        DateTime nowUtc)
    {
        var age = nowUtc - CacheTimeHelper.NormalizeToUtc(timestampUtc);
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        var minutes = (decimal)age.TotalMinutes;
        if (age <= TimeSpan.FromMinutes(15))
        {
            return (100 - minutes / 15m * 20m, MarketDataQualityBucket.Current, age);
        }

        if (age <= TimeSpan.FromHours(1))
        {
            return (80 - (minutes - 15m) / 45m * 30m, MarketDataQualityBucket.Aging, age);
        }

        if (age <= TimeSpan.FromHours(6))
        {
            return (50 - (minutes - 60m) / 300m * 30m, MarketDataQualityBucket.Old, age);
        }

        var veryOldScore = Math.Max(1, 20 - (minutes - 360m) / 1080m * 19m);
        return (veryOldScore, MarketDataQualityBucket.VeryOld, age);
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

    private readonly record struct DataQualityEvaluation(
        decimal Score,
        MarketDataQualityBucket Bucket,
        TimeSpan Age,
        DateTime? UploadedAtUtc,
        MarketDataAgeSource Source);

    private sealed record ScopePriceContext(
        decimal BaselineUnitPrice,
        decimal AverageUnitPrice,
        decimal CompetitiveAverageUnitPrice,
        decimal MedianUnitPrice,
        decimal CompetitiveThresholdUnitPrice,
        decimal SaneThresholdUnitPrice,
        long LowOutlierMaxUnitPrice)
    {
        public static ScopePriceContext Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
    }

    private static MarketDataQualityBucket GetWorstDataQuality(
        IReadOnlyList<WorldMarketAnalysis> worlds,
        IReadOnlyList<string> missingDataCenters)
    {
        if (missingDataCenters.Count > 0 || worlds.Count == 0)
        {
            return MarketDataQualityBucket.Missing;
        }

        return worlds
            .Select(world => world.DataQualityBucket)
            .DefaultIfEmpty(MarketDataQualityBucket.Missing)
            .Max();
    }

    private static string? CreateWarning(int entryCount, IReadOnlyList<string> missingDataCenters)
    {
        if (entryCount == 0)
        {
            return missingDataCenters.Count > 0
                ? $"Missing market data for {string.Join(", ", missingDataCenters)}."
                : "Missing market data.";
        }

        return missingDataCenters.Count > 0
            ? $"Missing market data for {string.Join(", ", missingDataCenters)}; analysis uses available cache data only."
            : null;
    }

    private static decimal CalculateCoverageRatio(int quantity, int needed)
    {
        return needed <= 0 ? 0 : quantity / (decimal)needed;
    }

    private static decimal CalculateWeightedAverage(IReadOnlyList<AnalyzedMarketListing> listings)
    {
        var quantity = listings.Sum(listing => listing.Quantity);
        return quantity > 0
            ? listings.Sum(listing => listing.PricePerUnit * listing.Quantity) / (decimal)quantity
            : 0;
    }

    private static decimal CalculateBreakPercent(decimal currentAverage, long nextMinimum)
    {
        return currentAverage <= 0
            ? 0
            : (nextMinimum - currentAverage) / currentAverage * 100m;
    }

    private static long? CalculateCostToCover(IReadOnlyList<AnalyzedMarketListing> listings, int quantityNeeded)
    {
        var remaining = quantityNeeded;
        long total = 0;

        foreach (var listing in listings)
        {
            total += listing.Quantity * listing.PricePerUnit;
            remaining -= listing.Quantity;
            if (remaining <= 0)
            {
                return total;
            }
        }

        return null;
    }
}
