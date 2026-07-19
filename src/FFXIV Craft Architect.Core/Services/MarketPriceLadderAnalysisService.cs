using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class MarketPriceLadderAnalysisService : IMarketPriceLadderAnalysisService
{
    private const decimal BandTolerance = 0.10m;
    private const decimal OutlierMultiplier = 2.5m;

    private readonly IMarketPriceEvaluationService _priceEvaluationService;

    public MarketPriceLadderAnalysisService()
        : this(new MarketPriceEvaluationService())
    {
    }

    public MarketPriceLadderAnalysisService(IMarketPriceEvaluationService priceEvaluationService)
    {
        ArgumentNullException.ThrowIfNull(priceEvaluationService);

        _priceEvaluationService = priceEvaluationService;
    }

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
            var scopePriceContext = _priceEvaluationService.Evaluate(
                item.ItemId,
                request.Evidence.Scope,
                request.Evidence.LoadedAtUtc,
                entries);
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
            var scopePriceBands = BuildScopePriceBands(worlds, item.TotalQuantity, scopePriceContext);
            var procurementSignalWorld = SelectBestActionableWorld(worlds);

            analyses.Add(new MarketItemAnalysis
            {
                ItemId = item.ItemId,
                Name = item.Name,
                QuantityNeeded = item.TotalQuantity,
                Scope = request.Evidence.Scope,
                LoadedAtUtc = request.Evidence.LoadedAtUtc,
                AnalysisScopeBaselineUnitPrice = scopePriceContext.BaselineUnitPrice,
                AnalysisScopeAverageUnitPrice = scopePriceContext.AverageUnitPrice,
                AnalysisCompetitiveAverageUnitPrice = scopePriceContext.CompetitiveAverageUnitPrice,
                ProcurementSignalQuantity = procurementSignalWorld?.ActionableQuantity ?? 0,
                PrimaryProcurementShelfAverageUnitPrice = procurementSignalWorld?.ActionableAverageUnitPrice ?? 0,
                CostToCoverTotalGil = procurementSignalWorld?.ActionableCostToCoverTotalGil ?? 0,
                CostToCoverUnitPrice = procurementSignalWorld?.ActionableCostToCoverUnitPrice ?? 0,
                CostToCoverMaxUnitPrice = procurementSignalWorld?.ActionableCostToCoverMaxUnitPrice ?? 0,
                AnalysisScopeMedianUnitPrice = scopePriceContext.MedianUnitPrice,
                CompetitiveThresholdUnitPrice = scopePriceContext.CompetitiveThresholdUnitPrice,
                SaneThresholdUnitPrice = scopePriceContext.SaneThresholdUnitPrice,
                PriceEvaluation = scopePriceContext.PriceEvaluation,
                RequestedDataCenters = requestedDataCenters,
                PresentDataCenters = presentDataCenters,
                MissingDataCenters = missingDataCenters,
                WorstDataQualityBucket = GetWorstDataQuality(worlds, missingDataCenters),
                ScopePriceBands = scopePriceBands,
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
            DCAveragePrice = GetProjectionUnitPrice(analysis),
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

        if (plan.RecommendedWorld == null)
        {
            plan.RecommendedSplit = BuildRecommendedSplit(plan);
        }

        if (plan.WorldOptions.Count == 0 && !string.IsNullOrWhiteSpace(analysis.Warning))
        {
            plan.Error = analysis.Warning;
        }

        plan.CoverageSet = MarketCoverageBuilder.Build(plan);

        return plan;
    }

    private static decimal GetProjectionUnitPrice(MarketItemAnalysis analysis)
    {
        if (analysis.CostToCoverUnitPrice > 0)
        {
            return analysis.CostToCoverUnitPrice;
        }

        if (analysis.PrimaryProcurementShelfAverageUnitPrice > 0)
        {
            return analysis.PrimaryProcurementShelfAverageUnitPrice;
        }

        if (analysis.AnalysisCompetitiveAverageUnitPrice > 0)
        {
            return analysis.AnalysisCompetitiveAverageUnitPrice;
        }

        if (analysis.AnalysisScopeAverageUnitPrice > 0)
        {
            return analysis.AnalysisScopeAverageUnitPrice;
        }

        if (analysis.AnalysisScopeBaselineUnitPrice > 0)
        {
            return analysis.AnalysisScopeBaselineUnitPrice;
        }

        return 0;
    }

    private static List<SplitWorldPurchase>? BuildRecommendedSplit(DetailedShoppingPlan plan)
    {
        if (plan.WorldOptions.Sum(world => world.TotalQuantityPurchased) < plan.QuantityNeeded)
        {
            return null;
        }

        var candidateOrders = new List<IEnumerable<WorldShoppingSummary>>
        {
            plan.WorldOptions.Where(world => world.TotalQuantityPurchased > 0),
            plan.WorldOptions
                .Where(world => world.TotalQuantityPurchased > 0)
                .OrderBy(world => world.ProcurementPriorityScore)
                .ThenBy(world => world.TotalCost)
                .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
                .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase),
            plan.WorldOptions
                .Where(world => world.TotalQuantityPurchased > 0)
                .OrderBy(world => world.TotalCost)
                .ThenBy(world => world.ProcurementPriorityScore)
                .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
                .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase),
            plan.WorldOptions
                .Where(world => world.TotalQuantityPurchased > 0)
                .OrderByDescending(world => world.TotalQuantityPurchased)
                .ThenBy(world => world.TotalCost)
                .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
                .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase),
            plan.WorldOptions
                .Where(world => world.TotalQuantityPurchased > 0)
                .OrderBy(world => world.AveragePricePerUnit <= 0 ? decimal.MaxValue : world.AveragePricePerUnit)
                .ThenByDescending(world => world.TotalQuantityPurchased)
                .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
                .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase)
        };

        return candidateOrders
            .Select(order => MarketShoppingService.BuildSplitPurchase(plan.QuantityNeeded, order))
            .Where(split => split.Count > 1 && split.Sum(part => part.QuantityToBuy) >= plan.QuantityNeeded)
            .OrderBy(split => split.Sum(part => part.TotalCost))
            .ThenBy(split => split.Count)
            .ThenBy(split => string.Join("|", split.Select(part => $"{part.DataCenter}:{part.WorldName}")), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IEnumerable<WorldMarketAnalysis> AnalyzeEntryWorlds(
        CachedMarketData entry,
        int quantityNeeded,
        DateTime nowUtc,
        MarketPriceEvaluationContext scopePriceContext)
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
        MarketPriceEvaluationContext scopePriceContext)
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

        var bands = BuildPriceBands(analyzedListings, quantityNeeded, scopePriceContext);
        var primaryBandIndex = SelectPrimaryUsableBand(bands);
        var primaryUsableBand = primaryBandIndex.HasValue
            ? bands[primaryBandIndex.Value]
            : null;
        var primaryUsableQuantity = primaryUsableBand?.Quantity ?? 0;
        var primaryUsableAverageUnitPrice = primaryUsableBand?.WeightedAverageUnitPrice ?? 0;
        var priceSignalBandIndexes = SelectPriceSignalBands(primaryBandIndex);
        var priceSignalBands = priceSignalBandIndexes
            .Select(index => bands[index])
            .ToList();
        var priceSignalQuantity = priceSignalBands.Sum(band => band.Quantity);
        var priceSignalAverageUnitPrice = CalculateWeightedAverage(priceSignalBands);
        var priceSignalDepth = ClassifyPriceBandDepth(priceSignalQuantity, quantityNeeded);
        var saneThreshold = scopePriceContext.SaneThresholdUnitPrice;
        var scopeCompetitiveThreshold = scopePriceContext.CompetitiveThresholdUnitPrice;

        // One fused pass for every quantity aggregate, computed against the initial
        // (outlier-based) sanity classification before the scope-sane override below.
        var totalListingQuantity = 0;
        var totalSaneQuantity = 0;
        var scopeSaneQuantity = 0;
        var scopeInsaneQuantity = 0;
        var scopeUncompetitiveQuantity = 0;
        foreach (var listing in analyzedListings)
        {
            totalListingQuantity += listing.Quantity;
            var isSane = listing.PriceSanity is MarketListingPriceSanity.Sane or MarketListingPriceSanity.LowOutlier;
            if (isSane)
            {
                totalSaneQuantity += listing.Quantity;
            }

            var isScopeSane = saneThreshold > 0
                ? listing.PricePerUnit <= saneThreshold
                : isSane;
            if (isScopeSane)
            {
                scopeSaneQuantity += listing.Quantity;
                if (scopeCompetitiveThreshold > 0 && listing.PricePerUnit > scopeCompetitiveThreshold)
                {
                    scopeUncompetitiveQuantity += listing.Quantity;
                }
            }
            else if (saneThreshold > 0)
            {
                scopeInsaneQuantity += listing.Quantity;
            }
        }
        var primaryUsableSortIndexes = primaryBandIndex.HasValue
            ? Enumerable.Range(
                    bands[primaryBandIndex.Value].FirstListingIndex,
                    bands[primaryBandIndex.Value].LastListingIndex - bands[primaryBandIndex.Value].FirstListingIndex + 1)
                .ToHashSet()
            : [];
        var priceSignalSortIndexes = priceSignalBandIndexes
            .SelectMany(index => Enumerable.Range(
                bands[index].FirstListingIndex,
                bands[index].LastListingIndex - bands[index].FirstListingIndex + 1))
            .ToHashSet();
        var thresholds = scopePriceContext.PriceEvaluation.Thresholds;
        foreach (var listing in analyzedListings)
        {
            if (saneThreshold > 0 && listing.PricePerUnit > saneThreshold)
            {
                listing.PriceSanity = MarketListingPriceSanity.Insane;
            }

            listing.Competitiveness = MarketListingClassification.ClassifyCompetitiveness(
                listing.PricePerUnit,
                listing.PriceSanity,
                thresholds,
                excludeOutliers: false);
            listing.IsInPriceSignalBand = priceSignalSortIndexes.Contains(listing.SortIndex);
            listing.IsInPrimaryUsableBand = primaryUsableSortIndexes.Contains(listing.SortIndex);
        }

        var listings = analyzedListings;
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
                Competitiveness = band.Competitiveness,
                Depth = band.Depth,
                IsPriceSignalBand = priceSignalBandIndexes.Contains(index),
                IsPrimaryUsableBand = primaryBandIndex == index
            })
            .ToList();

        // `listings` is already in price order from the initial sort, so filtering
        // preserves the actionable ordering without a redundant re-sort.
        var actionableListings = listings
            .Where(listing => listing.Quantity > 0 && listing.PricePerUnit > 0)
            .ToList();
        var actionableQuantity = actionableListings.Sum(listing => listing.Quantity);
        var actionableCostToCover = CalculateCostToCover(actionableListings, quantityNeeded);
        var worldAverageUnitPrice = CalculateWeightedAverage(actionableListings);
        var actionableAverageUnitPrice = actionableCostToCover?.QuotedAverageUnitPrice ?? worldAverageUnitPrice;
        var hasCredibleReference = scopePriceContext.PriceEvaluation.CentralRegion.Credibility is
            MarketPriceRegionCredibility.Credible or MarketPriceRegionCredibility.Strong;
        var comparableListings = hasCredibleReference
            ? listings.Where(MarketProcurementEvidencePolicy.IsComparableListing).ToList()
            : actionableListings;
        var comparableQuantity = comparableListings.Sum(listing => listing.Quantity);
        var comparableAverageUnitPrice = CalculateWeightedAverage(comparableListings);
        var coverageBucket = GetCoverageBucket(actionableQuantity, quantityNeeded);
        var primaryUsableListings = listings
            .Where(listing => listing.IsInPrimaryUsableBand)
            .ToList();
        var costToCover = CalculateCostToCover(primaryUsableListings, quantityNeeded);
        var scores = CreateScores(
            quantityNeeded,
            actionableQuantity,
            actionableCostToCover,
            coverageBucket,
            comparableAverageUnitPrice,
            dataQualityScore);

        return new WorldMarketAnalysis
        {
            DataCenter = dataCenter,
            WorldName = worldName,
            QuantityNeeded = quantityNeeded,
            PrimaryUsableQuantity = primaryUsableQuantity,
            PriceSignalQuantity = priceSignalQuantity,
            ScopeSaneQuantity = scopeSaneQuantity,
            ScopeUncompetitiveQuantity = scopeUncompetitiveQuantity,
            ScopeInsaneQuantity = scopeInsaneQuantity,
            TotalSaneQuantity = totalSaneQuantity,
            TotalListingQuantity = totalListingQuantity,
            ActionableQuantity = actionableQuantity,
            ActionableAverageUnitPrice = actionableAverageUnitPrice,
            ComparableQuantity = comparableQuantity,
            ComparableAverageUnitPrice = comparableAverageUnitPrice,
            ActionableCostToCoverTotalGil = actionableCostToCover?.TotalCost ?? 0,
            ActionableCostToCoverUnitPrice = actionableCostToCover?.UnitPrice ?? 0,
            ActionableCostToCoverMaxUnitPrice = actionableCostToCover?.MaxUnitPrice ?? 0,
            WorldAverageUnitPrice = worldAverageUnitPrice,
            ReferenceSupportScore = scopePriceContext.PriceEvaluation.CentralRegion.SupportScore,
            ReferencePriceCredibility = scopePriceContext.PriceEvaluation.CentralRegion.Credibility,
            CostToCoverTotalGil = costToCover?.TotalCost ?? 0,
            CostToCoverUnitPrice = costToCover?.UnitPrice ?? 0,
            CostToCoverMaxUnitPrice = costToCover?.MaxUnitPrice ?? 0,
            PrimaryUsableCoverageRatio = CalculateCoverageRatio(primaryUsableQuantity, quantityNeeded),
            PriceSignalCoverageRatio = CalculateCoverageRatio(priceSignalQuantity, quantityNeeded),
            ScopeSaneCoverageRatio = CalculateCoverageRatio(scopeSaneQuantity, quantityNeeded),
            SaneCoverageRatio = CalculateCoverageRatio(totalSaneQuantity, quantityNeeded),
            CoverageBucket = coverageBucket,
            AnalysisScopeBaselineUnitPrice = scopePriceContext.BaselineUnitPrice,
            AnalysisScopeAverageUnitPrice = scopePriceContext.AverageUnitPrice,
            AnalysisCompetitiveAverageUnitPrice = scopePriceContext.CompetitiveAverageUnitPrice,
            PrimaryUsableAverageUnitPrice = primaryUsableAverageUnitPrice,
            PriceSignalAverageUnitPrice = priceSignalAverageUnitPrice,
            AnalysisScopeMedianUnitPrice = scopePriceContext.MedianUnitPrice,
            CompetitiveThresholdUnitPrice = scopePriceContext.CompetitiveThresholdUnitPrice,
            SaneThresholdUnitPrice = scopePriceContext.SaneThresholdUnitPrice,
            FetchedAtUtc = fetchedAtUtc,
            PriceSignalDepth = priceSignalDepth,
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
            PrimaryUsableQuantity = 0,
            TotalSaneQuantity = 0,
            TotalListingQuantity = 0,
            PrimaryUsableCoverageRatio = 0,
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

    private static List<MarketPriceBand> BuildPriceBands(
        IReadOnlyList<AnalyzedMarketListing> listings,
        int quantityNeeded,
        MarketPriceEvaluationContext scopePriceContext)
    {
        var bands = new List<MarketPriceBand>();
        var current = new List<AnalyzedMarketListing>();

        // Running totals mirror CalculateWeightedAverage(current) exactly (same
        // accumulation order), but keep band construction O(n) instead of O(n²).
        long currentQuantity = 0;
        long currentValue = 0;

        foreach (var listing in listings)
        {
            if (current.Count > 0 && listing.PricePerUnit > currentValue / (decimal)currentQuantity * (1 + BandTolerance))
            {
                bands.Add(CreateBand(current));
                current.Clear();
                currentQuantity = 0;
                currentValue = 0;
            }

            current.Add(listing);
            currentQuantity += listing.Quantity;
            currentValue += listing.PricePerUnit * listing.Quantity;
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
                Competitiveness = ClassifyPriceBandCompetitiveness(band, scopePriceContext),
                Depth = ClassifyPriceBandDepth(band.Quantity, quantityNeeded)
            })
            .ToList();
    }

    private static MarketPriceBand CreateBand(IReadOnlyList<AnalyzedMarketListing> listings)
    {
        var firstIndex = int.MaxValue;
        var lastIndex = int.MinValue;
        long minPrice = long.MaxValue;
        long maxPrice = long.MinValue;
        var totalQuantity = 0;
        long totalValue = 0;

        foreach (var listing in listings)
        {
            firstIndex = Math.Min(firstIndex, listing.SortIndex);
            lastIndex = Math.Max(lastIndex, listing.SortIndex);
            minPrice = Math.Min(minPrice, listing.PricePerUnit);
            maxPrice = Math.Max(maxPrice, listing.PricePerUnit);
            totalQuantity += listing.Quantity;
            totalValue += listing.PricePerUnit * listing.Quantity;
        }

        return new MarketPriceBand
        {
            FirstListingIndex = firstIndex,
            LastListingIndex = lastIndex,
            MinUnitPrice = minPrice,
            MaxUnitPrice = maxPrice,
            WeightedAverageUnitPrice = totalQuantity > 0 ? totalValue / (decimal)totalQuantity : 0,
            ListingCount = listings.Count,
            Quantity = totalQuantity
        };
    }

    private static List<MarketScopePriceBand> BuildScopePriceBands(
        IReadOnlyList<WorldMarketAnalysis> worlds,
        int quantityNeeded,
        MarketPriceEvaluationContext scopePriceContext)
    {
        var listings = worlds
            .SelectMany(world => world.Listings.Select(listing => new ScopeBandListing(world.WorldName, listing)))
            .Where(entry => entry.Listing.Quantity > 0 && entry.Listing.PricePerUnit > 0)
            .OrderBy(entry => entry.Listing.PricePerUnit)
            .ThenByDescending(entry => entry.Listing.Quantity)
            .ThenBy(entry => entry.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Listing.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var grouped = new List<List<ScopeBandListing>>();
        var current = new List<ScopeBandListing>();

        // Running totals mirror CalculateWeightedAverage(current) exactly (same
        // accumulation order), but keep band construction O(n) instead of O(n²).
        long currentQuantity = 0;
        long currentValue = 0;

        foreach (var listing in listings)
        {
            if (current.Count > 0 && listing.Listing.PricePerUnit > currentValue / (decimal)currentQuantity * (1 + BandTolerance))
            {
                grouped.Add(current);
                current = [];
                currentQuantity = 0;
                currentValue = 0;
            }

            current.Add(listing);
            currentQuantity += listing.Listing.Quantity;
            currentValue += listing.Listing.PricePerUnit * listing.Listing.Quantity;
        }

        if (current.Count > 0)
        {
            grouped.Add(current);
        }

        return grouped
            .Select((band, index) =>
            {
                var nextBand = index < grouped.Count - 1 ? grouped[index + 1] : null;
                var scopeBand = CreateScopePriceBand(band, quantityNeeded, scopePriceContext);
                return new MarketScopePriceBand
                {
                    MinUnitPrice = scopeBand.MinUnitPrice,
                    MaxUnitPrice = scopeBand.MaxUnitPrice,
                    WeightedAverageUnitPrice = scopeBand.WeightedAverageUnitPrice,
                    TotalQuantity = scopeBand.TotalQuantity,
                    ListingCount = scopeBand.ListingCount,
                    DistinctWorldCount = scopeBand.DistinctWorldCount,
                    DistinctRetainerCount = scopeBand.DistinctRetainerCount,
                    Competitiveness = scopeBand.Competitiveness,
                    Depth = scopeBand.Depth,
                    BreakPercentToNextBand = nextBand is null
                        ? null
                        : CalculateBreakPercent(scopeBand.WeightedAverageUnitPrice, nextBand.Min(entry => entry.Listing.PricePerUnit))
                };
            })
            .ToList();
    }

    private static MarketScopePriceBand CreateScopePriceBand(
        IReadOnlyList<ScopeBandListing> band,
        int quantityNeeded,
        MarketPriceEvaluationContext scopePriceContext)
    {
        var minUnitPrice = band.Min(entry => entry.Listing.PricePerUnit);
        var maxUnitPrice = band.Max(entry => entry.Listing.PricePerUnit);
        var listingCount = band.Count;
        var totalQuantity = band.Sum(entry => entry.Listing.Quantity);
        var overlapsCentralRegion = OverlapsCentralRegion(minUnitPrice, maxUnitPrice, scopePriceContext.PriceEvaluation.CentralRegion);
        var depth = ClassifyPriceBandDepth(totalQuantity, quantityNeeded);
        var isUsableCentralBand = overlapsCentralRegion && depth != PriceBandDepth.Thin;
        var competitiveness = ClassifyScopePriceBandCompetitiveness(band, scopePriceContext, isUsableCentralBand);

        return new MarketScopePriceBand
        {
            MinUnitPrice = minUnitPrice,
            MaxUnitPrice = maxUnitPrice,
            WeightedAverageUnitPrice = CalculateWeightedAverage(band),
            TotalQuantity = totalQuantity,
            ListingCount = listingCount,
            DistinctWorldCount = band
                .Select(entry => entry.WorldName)
                .Where(world => !string.IsNullOrWhiteSpace(world))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            DistinctRetainerCount = band
                .Select(entry => entry.Listing.RetainerName)
                .Where(retainer => !string.IsNullOrWhiteSpace(retainer))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            Competitiveness = competitiveness,
            Depth = depth
        };
    }

    private static bool OverlapsCentralRegion(
        long minUnitPrice,
        long maxUnitPrice,
        MarketCentralPriceRegion centralRegion)
    {
        return centralRegion.MinUnitPrice > 0 &&
            centralRegion.MaxUnitPrice > 0 &&
            minUnitPrice <= centralRegion.MaxUnitPrice &&
            maxUnitPrice >= centralRegion.MinUnitPrice;
    }

    private static PriceBandCompetitiveness ClassifyScopePriceBandCompetitiveness(
        IReadOnlyList<ScopeBandListing> band,
        MarketPriceEvaluationContext scopePriceContext,
        bool isUsableCentralBand)
    {
        if (band.All(entry => entry.Listing.PriceSanity == MarketListingPriceSanity.LowOutlier))
        {
            return PriceBandCompetitiveness.LowOutlier;
        }

        if (band.All(entry =>
            entry.Listing.PriceSanity is MarketListingPriceSanity.Outlier or MarketListingPriceSanity.Insane ||
            entry.Listing.Competitiveness == MarketListingCompetitiveness.Excluded ||
            entry.Listing.PricePerUnit > scopePriceContext.PriceEvaluation.CentralRegion.MaxUnitPrice))
        {
            return PriceBandCompetitiveness.Insane;
        }

        if (band.Any(entry => entry.Listing.Competitiveness is MarketListingCompetitiveness.Deal or MarketListingCompetitiveness.Competitive))
        {
            return PriceBandCompetitiveness.Competitive;
        }

        if (isUsableCentralBand)
        {
            return PriceBandCompetitiveness.Uncompetitive;
        }

        return PriceBandCompetitiveness.Unknown;
    }

    private static int? SelectPrimaryUsableBand(IReadOnlyList<MarketPriceBand> bands)
    {
        for (var index = 0; index < bands.Count; index++)
        {
            var band = bands[index];
            if (band.Competitiveness == PriceBandCompetitiveness.Competitive &&
                band.Depth is PriceBandDepth.Usable or PriceBandDepth.Deep)
            {
                return index;
            }
        }

        return null;
    }

    private static IReadOnlyList<int> SelectPriceSignalBands(int? primaryBandIndex)
    {
        return primaryBandIndex.HasValue
            ? [primaryBandIndex.Value]
            : [];
    }

    private static PriceBandCompetitiveness ClassifyPriceBandCompetitiveness(
        MarketPriceBand band,
        MarketPriceEvaluationContext scopePriceContext)
    {
        if (band.MaxUnitPrice <= scopePriceContext.LowOutlierMaxUnitPrice)
        {
            return PriceBandCompetitiveness.LowOutlier;
        }

        if (scopePriceContext.SaneThresholdUnitPrice > 0 &&
            band.MinUnitPrice > scopePriceContext.SaneThresholdUnitPrice)
        {
            return PriceBandCompetitiveness.Insane;
        }

        if (scopePriceContext.CompetitiveThresholdUnitPrice > 0 &&
            band.MinUnitPrice <= scopePriceContext.CompetitiveThresholdUnitPrice)
        {
            return PriceBandCompetitiveness.Competitive;
        }

        return PriceBandCompetitiveness.Uncompetitive;
    }

    private static PriceBandDepth ClassifyPriceBandDepth(int quantity, int quantityNeeded)
    {
        if (quantity <= 0 || quantityNeeded <= 0)
        {
            return PriceBandDepth.None;
        }

        if (quantity >= Math.Max(quantityNeeded / 2, 1))
        {
            return PriceBandDepth.Deep;
        }

        return quantity >= Math.Max(quantityNeeded / 4, 1)
            ? PriceBandDepth.Usable
            : PriceBandDepth.Thin;
    }

    private static MarketCoverageBucket GetCoverageBucket(int primaryUsableQuantity, int neededQuantity)
    {
        if (neededQuantity <= 0 || primaryUsableQuantity <= 0)
        {
            return MarketCoverageBucket.None;
        }

        if (primaryUsableQuantity >= neededQuantity)
        {
            return MarketCoverageBucket.Full;
        }

        return primaryUsableQuantity >= Math.Max(neededQuantity / 2, 1)
            ? MarketCoverageBucket.PartialDeep
            : MarketCoverageBucket.PartialThin;
    }

    private static List<WorldLensScore> CreateScores(
        int quantityNeeded,
        int actionableQuantity,
        CostToCoverResult? costToCover,
        MarketCoverageBucket coverageBucket,
        decimal comparableAverageUnitPrice,
        decimal dataQualityScore)
    {
        var coverageRatio = CalculateCoverageRatio(actionableQuantity, quantityNeeded);
        var actionableRatio = CalculateCoverageRatio(actionableQuantity, quantityNeeded);
        var minimumScore = costToCover.HasValue
            ? 1_000_000_000m / Math.Max(costToCover.Value.TotalCost, 1)
            : coverageRatio * 100;
        minimumScore += coverageBucket == MarketCoverageBucket.Full ? 10_000 : coverageRatio * 100;
        minimumScore *= dataQualityScore / 100m;

        var bulkScore = actionableRatio * 10_000m
            + Math.Min(actionableQuantity, quantityNeeded) * 10m;
        if (comparableAverageUnitPrice > 0)
        {
            bulkScore += 1_000m / comparableAverageUnitPrice;
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
                    ? $"{costToCover.Value.TotalCost:N0}g to cover need"
                    : $"{actionableQuantity:N0}/{quantityNeeded:N0} listed"
            },
            new WorldLensScore
            {
                Lens = MarketAcquisitionLens.BulkValue,
                Score = bulkScore,
                ScoreBucket = ScoreBucketForBulkValue(actionableQuantity, quantityNeeded, dataQualityScore),
                Summary = comparableAverageUnitPrice > 0
                    ? $"{actionableQuantity:N0} listed at ~{comparableAverageUnitPrice:N0}g comparable average"
                    : $"{actionableQuantity:N0} listed"
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
        return MarketProcurementEvidencePolicy.GetEligibleListings(world);
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
                IsUnderAverage = listing.IsInPrimaryUsableBand,
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
            ValueScore = hasSufficientStock ? totalCost : decimal.MaxValue,
            MarketDataQualityScore = world.DataQualityScore,
            MarketDataQualityBucket = world.DataQualityBucket,
            MarketDataAgeSource = world.DataAgeSource,
            MarketDataAge = world.DataAge,
            MarketUploadedAtUtc = world.MarketUploadedAtUtc,
            LensRank = lensScore?.Rank ?? int.MaxValue,
            LensScoreBucket = lensScore?.ScoreBucket ?? MarketScoreBucket.Unavailable,
            ModePricePerUnit = world.PriceBands.FirstOrDefault(band => band.IsPrimaryUsableBand)?.MinUnitPrice ?? 0
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
        if (TryGetUtcFromUnixMilliseconds(world.ObservedAtUnixMilliseconds, out var observedAtUtc) &&
            world.EvidenceOrigin is MarketEvidenceOrigin.MarketMafioso or MarketEvidenceOrigin.ManualObservation)
        {
            var (score, bucket, age) = MarketEvidenceFreshness.Evaluate(observedAtUtc, nowUtc);
            var source = world.EvidenceOrigin == MarketEvidenceOrigin.MarketMafioso
                ? MarketDataAgeSource.MarketMafiosoObservation
                : MarketDataAgeSource.ManualObservation;
            return new DataQualityEvaluation(score, bucket, age, observedAtUtc, source);
        }

        if (TryGetUtcFromUnixMilliseconds(world.LastUploadTimeUnixMilliseconds, out var worldUploadedAtUtc))
        {
            var (score, bucket, age) = MarketEvidenceFreshness.Evaluate(worldUploadedAtUtc, nowUtc);
            return new DataQualityEvaluation(score, bucket, age, worldUploadedAtUtc, MarketDataAgeSource.UniversalisWorldUpload);
        }

        if (TryGetUtcFromUnixMilliseconds(world.ObservedAtUnixMilliseconds, out observedAtUtc))
        {
            var observedFallback = MarketEvidenceFreshness.Evaluate(observedAtUtc, nowUtc, capCurrentToAging: true);
            return new DataQualityEvaluation(
                observedFallback.Score,
                observedFallback.Bucket,
                observedFallback.Age,
                null,
                MarketDataAgeSource.LocalFetchFallback);
        }

        if (TryGetUtcFromUnixMilliseconds(entry.LastUploadTimeUnixMilliseconds, out var responseUploadedAtUtc))
        {
            var (score, bucket, age) = MarketEvidenceFreshness.Evaluate(responseUploadedAtUtc, nowUtc);
            return new DataQualityEvaluation(score, bucket, age, responseUploadedAtUtc, MarketDataAgeSource.UniversalisResponseUpload);
        }

        var fallback = MarketEvidenceFreshness.Evaluate(entry.FetchedAt, nowUtc, capCurrentToAging: true);
        return new DataQualityEvaluation(
            fallback.Score,
            fallback.Bucket,
            fallback.Age,
            null,
            MarketDataAgeSource.LocalFetchFallback);
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

    private static decimal CalculateWeightedAverage(IReadOnlyList<MarketPriceBand> bands)
    {
        var quantity = bands.Sum(band => band.Quantity);
        return quantity > 0
            ? bands.Sum(band => band.WeightedAverageUnitPrice * band.Quantity) / quantity
            : 0;
    }

    private static decimal CalculateWeightedAverage(IReadOnlyList<ScopeBandListing> listings)
    {
        var quantity = listings.Sum(entry => entry.Listing.Quantity);
        return quantity > 0
            ? listings.Sum(entry => entry.Listing.PricePerUnit * entry.Listing.Quantity) / (decimal)quantity
            : 0;
    }

    private static decimal CalculateBreakPercent(decimal currentAverage, long nextMinimum)
    {
        return currentAverage <= 0
            ? 0
            : (nextMinimum - currentAverage) / currentAverage * 100m;
    }

    private static CostToCoverResult? CalculateCostToCover(IReadOnlyList<AnalyzedMarketListing> listings, int quantityNeeded)
    {
        var remaining = quantityNeeded;
        long total = 0;
        long maxUnitPrice = 0;
        var quotedQuantity = 0;

        foreach (var listing in listings)
        {
            total += listing.Quantity * listing.PricePerUnit;
            quotedQuantity += listing.Quantity;
            maxUnitPrice = Math.Max(maxUnitPrice, listing.PricePerUnit);
            remaining -= listing.Quantity;
            if (remaining <= 0)
            {
                return new CostToCoverResult(
                    total,
                    quantityNeeded > 0 ? total / (decimal)quantityNeeded : 0,
                    quotedQuantity > 0 ? total / (decimal)quotedQuantity : 0,
                    maxUnitPrice);
            }
        }

        return null;
    }

    private static WorldMarketAnalysis? SelectBestActionableWorld(IEnumerable<WorldMarketAnalysis> worlds)
    {
        return worlds
            .Where(world => world.ActionableCostToCoverTotalGil > 0 && world.ActionableCostToCoverUnitPrice > 0)
            .OrderBy(world => world.ActionableCostToCoverTotalGil)
            .ThenBy(world => world.ActionableCostToCoverMaxUnitPrice)
            .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private readonly record struct ScopeBandListing(string WorldName, AnalyzedMarketListing Listing);

    private readonly record struct CostToCoverResult(
        long TotalCost,
        decimal UnitPrice,
        decimal QuotedAverageUnitPrice,
        long MaxUnitPrice);
}
