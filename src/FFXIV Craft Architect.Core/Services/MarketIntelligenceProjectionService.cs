using System.Diagnostics;
using System.Text.Json;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class MarketIntelligenceProjectionService : IMarketIntelligenceProjectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public MarketIntelligenceProjectionResult Project(MarketIntelligenceProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ExecutionResult);

        var projectionStopwatch = Stopwatch.StartNew();
        var publicationId = request.PublicationId == Guid.Empty
            ? Guid.NewGuid()
            : request.PublicationId;
        var runId = request.RunId == Guid.Empty
            ? Guid.NewGuid()
            : request.RunId;
        var details = new List<MarketListingDetail>();
        var manifestEntries = new List<MarketIntelligenceDetailManifestEntry>();
        var sourceFacts = new List<CanonicalMarketListingFact>();
        var retainedDetailBytes = 0L;
        var analysisByItemId = request.ExecutionResult.Analyses.ToDictionary(analysis => analysis.ItemId);
        var classificationReasonsByItemId = analysisByItemId.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.PriceEvaluation?.Diagnostics.CompactReasonCodes.ToList()
                ?? new List<MarketPriceEvaluationReasonCode>());

        foreach (var analysis in request.ExecutionResult.Analyses)
        {
            var demandFingerprint = CreateDemandFingerprint(request, analysis);
            if (analysis.PriceEvaluation != null)
            {
                var itemDetailKey = new MarketIntelligenceDetailKey(
                    publicationId,
                    analysis.Scope,
                    analysis.ItemId,
                    null,
                    demandFingerprint);
                var itemDetail = new MarketListingDetail
                {
                    Key = itemDetailKey,
                    RunId = runId,
                    CreatedAtUtc = request.CompletedAtUtc,
                    RetrievedAtUtc = request.CompletedAtUtc,
                    PriceEvaluation = analysis.PriceEvaluation,
                    ClassificationReasons = analysis.PriceEvaluation.Diagnostics.CompactReasonCodes.ToList()
                };
                details.Add(itemDetail);
                var detailBytes = EstimatePayloadBytes(itemDetail);
                retainedDetailBytes += detailBytes;
                manifestEntries.Add(new MarketIntelligenceDetailManifestEntry
                {
                    Key = itemDetailKey,
                    Availability = MarketIntelligenceDetailAvailability.Available,
                    ListingCount = 0,
                    DetailBytes = detailBytes
                });
            }

            foreach (var world in analysis.Worlds)
            {
                var key = CreateDetailKey(publicationId, analysis.Scope, analysis.ItemId, world, demandFingerprint);
                var detail = new MarketListingDetail
                {
                    Key = key,
                    RunId = runId,
                    CreatedAtUtc = request.CompletedAtUtc,
                    RetrievedAtUtc = world.FetchedAtUtc ?? request.CompletedAtUtc,
                    MarketUploadedAtUtc = world.MarketUploadedAtUtc,
                    PriceBands = world.PriceBands.ToList(),
                    Listings = world.Listings.ToList(),
                    ClassificationReasons = analysis.PriceEvaluation?.Diagnostics.CompactReasonCodes.ToList() ?? []
                };

                details.Add(detail);
                var detailBytes = EstimatePayloadBytes(detail);
                retainedDetailBytes += detailBytes;
                manifestEntries.Add(new MarketIntelligenceDetailManifestEntry
                {
                    Key = key,
                    Availability = MarketIntelligenceDetailAvailability.Available,
                    ListingCount = world.Listings.Count,
                    DetailBytes = detailBytes
                });
                AppendFacts(
                    sourceFacts,
                    request,
                    runId,
                    key,
                    world,
                    classificationReasonsByItemId.TryGetValue(analysis.ItemId, out var classificationReasons)
                        ? classificationReasons
                        : []);
            }
        }

        var manifest = new MarketIntelligenceDetailManifest
        {
            PublicationId = publicationId,
            Entries = manifestEntries
        };
        var summary = new MarketIntelligencePublicationSummary
        {
            PublicationId = publicationId,
            ActiveRunId = runId,
            PublicationContext = request.PublicationContext,
            Items = request.ExecutionResult.ShoppingPlans
                .Select(plan => ProjectItemSummary(
                    publicationId,
                    request,
                    plan,
                    analysisByItemId.TryGetValue(plan.ItemId, out var analysis) ? analysis : null))
                .ToList(),
            UnavailableMarketItems = request.ExecutionResult.Evidence.MissingRequests
                .Select(pair => new CoreMarketDataUnavailableItem(pair.itemId, string.Empty))
                .Distinct()
                .ToList(),
            DetailManifest = manifest
        };
        var marketIntelligencePayloadBytes = EstimatePayloadBytes(summary);
        var legacyPayloadBytes = EstimateLegacyPayloadBytes(
            request.ExecutionResult,
            marketIntelligencePayloadBytes,
            retainedDetailBytes);
        var projectionDuration = request.ProjectionDuration > TimeSpan.Zero
            ? request.ProjectionDuration
            : projectionStopwatch.Elapsed;
        var runRecord = new MarketAnalysisRunRecord
        {
            RunId = runId,
            PublicationId = publicationId,
            DemandFingerprint = summary.Items.Count == 1
                ? summary.Items[0].DetailKey?.DemandFingerprint ?? default
                : new MarketDemandFingerprint($"publication:{publicationId:N}:items:{summary.Items.Count}"),
            AnalyzerVersion = request.AnalyzerVersion,
            Scope = request.PublicationContext.Scope,
            SelectedDataCenter = request.PublicationContext.SelectedDataCenter,
            SelectedRegion = request.PublicationContext.SelectedRegion,
            StartedAtUtc = request.StartedAtUtc,
            CompletedAtUtc = request.CompletedAtUtc,
            PlanBuildDuration = request.PlanBuildDuration,
            MarketFetchDuration = request.MarketFetchDuration,
            LadderAnalysisDuration = request.LadderAnalysisDuration,
            ShoppingPlanProjectionDuration = request.ShoppingPlanProjectionDuration,
            AnalysisDuration = request.AnalysisDuration,
            ProjectionDuration = projectionDuration,
            PublicationDuration = request.PublicationDuration,
            DetailPersistenceDuration = request.DetailPersistenceDuration,
            SourceFactPersistenceDuration = request.SourceFactPersistenceDuration,
            HotStatePublicationDuration = request.HotStatePublicationDuration,
            PlanPersistenceDuration = request.PlanPersistenceDuration,
            AutosaveDuration = request.AutosaveDuration,
            CacheMode = request.CacheMode,
            MarketIntelligencePayloadBytes = marketIntelligencePayloadBytes,
            LegacyPayloadBytes = legacyPayloadBytes,
            RetainedDetailBytes = retainedDetailBytes,
            NetworkRequestCount = request.NetworkRequestCount,
            FreshCacheHitCount = request.FreshCacheHitCount,
            StaleCacheRefreshCount = request.StaleCacheRefreshCount
        };

        return new MarketIntelligenceProjectionResult(
            new MarketIntelligencePublicationWrite(summary, details, [runRecord]),
            sourceFacts);
    }

    private static MarketItemSummary ProjectItemSummary(
        Guid publicationId,
        MarketIntelligenceProjectionRequest request,
        DetailedShoppingPlan plan,
        MarketItemAnalysis? analysis)
    {
        var demandFingerprint = CreateDemandFingerprint(request, plan, analysis);
        MarketWorldKey? recommendedWorld = plan.RecommendedWorld == null
            ? null
            : new MarketWorldKey(plan.RecommendedWorld.DataCenter, plan.RecommendedWorld.WorldName);
        MarketIntelligenceDetailKey? recommendedDetailKey = recommendedWorld is null
            ? null
            : new MarketIntelligenceDetailKey(
                publicationId,
                analysis?.Scope ?? request.PublicationContext.Scope,
                plan.ItemId,
                recommendedWorld,
                demandFingerprint);
        var worldSummaries = analysis?.Worlds
            .Select(world => ProjectWorldSummary(publicationId, request, analysis, world, demandFingerprint))
            .ToList() ?? [];

        return new MarketItemSummary
        {
            ItemId = plan.ItemId,
            Name = plan.Name,
            IconId = plan.IconId,
            QuantityNeeded = plan.QuantityNeeded,
            Scope = analysis?.Scope ?? request.PublicationContext.Scope,
            RecommendedWorld = recommendedWorld,
            RecommendedTotalCost = plan.SplitTotalCost ?? plan.RecommendedWorld?.TotalCost ?? 0,
            RecommendedWorldAveragePricePerUnit = plan.RecommendedWorld?.AveragePricePerUnit ?? 0,
            RecommendedWorldVendorName = plan.RecommendedWorld?.VendorName,
            Vendors = plan.Vendors.Select(CloneVendor).ToList(),
            BaselineUnitPrice = analysis?.AnalysisScopeBaselineUnitPrice ?? 0,
            AverageUnitPrice = analysis?.AnalysisScopeAverageUnitPrice ?? plan.DCAveragePrice,
            CompetitiveAverageUnitPrice = analysis?.AnalysisScopeCompetitiveAverageUnitPrice
                ?? plan.RecommendedWorld?.AveragePricePerUnit
                ?? 0,
            MedianUnitPrice = analysis?.AnalysisScopeMedianUnitPrice ?? 0,
            CompetitiveThresholdUnitPrice = analysis?.CompetitiveThresholdUnitPrice ?? 0,
            SaneThresholdUnitPrice = analysis?.SaneThresholdUnitPrice ?? 0,
            CoverageBucket = ResolveCoverageBucket(plan, analysis),
            DataQualityBucket = ResolveDataQualityBucket(plan, analysis),
            Confidence = analysis?.PriceEvaluation?.Confidence ?? MarketPriceEvaluationConfidence.Unknown,
            Warning = analysis?.Warning ?? plan.MarketDataWarning ?? plan.Error,
            Error = plan.Error,
            DetailKey = recommendedDetailKey,
            Worlds = worldSummaries,
            RecommendedSplit = plan.RecommendedSplit?
                .Select(split => ProjectSplitSummary(publicationId, request, plan.ItemId, split, analysis, demandFingerprint))
                .ToList() ?? []
        };
    }

    private static VendorInfo CloneVendor(VendorInfo vendor)
    {
        return new VendorInfo
        {
            Name = vendor.Name,
            Location = vendor.Location,
            Price = vendor.Price,
            Currency = vendor.Currency,
            AlternateLocations = vendor.AlternateLocations.ToList(),
            Coordinates = vendor.Coordinates?.ToList()
        };
    }

    private static MarketSplitPurchaseSummary ProjectSplitSummary(
        Guid publicationId,
        MarketIntelligenceProjectionRequest request,
        int itemId,
        SplitWorldPurchase split,
        MarketItemAnalysis? analysis,
        MarketDemandFingerprint demandFingerprint)
    {
        var world = new MarketWorldKey(split.DataCenter, split.WorldName);
        return new MarketSplitPurchaseSummary
        {
            World = world,
            QuantityToBuy = split.QuantityToBuy,
            PricePerUnit = split.PricePerUnit,
            EffectivePricePerNeededUnit = split.EffectivePricePerNeededUnit,
            TotalCost = split.TotalCost,
            IsPartial = split.IsPartial,
            TravelContext = split.TravelContext,
            ExcessAvailable = split.ExcessAvailable,
            DetailKey = new MarketIntelligenceDetailKey(
                publicationId,
                analysis?.Scope ?? request.PublicationContext.Scope,
                itemId,
                world,
                demandFingerprint)
        };
    }

    private static WorldMarketSummary ProjectWorldSummary(
        Guid publicationId,
        MarketIntelligenceProjectionRequest request,
        MarketItemAnalysis analysis,
        WorldMarketAnalysis world,
        MarketDemandFingerprint demandFingerprint)
    {
        var key = CreateDetailKey(publicationId, analysis.Scope, analysis.ItemId, world, demandFingerprint);
        return new WorldMarketSummary
        {
            World = key.World ?? new MarketWorldKey(world.DataCenter, world.WorldName),
            QuantityNeeded = world.QuantityNeeded,
            CompetitiveQuantity = world.CompetitiveQuantity,
            LocalCompetitiveQuantity = world.LocalCompetitiveQuantity,
            ScopeCompetitiveQuantity = world.ScopeCompetitiveQuantity,
            ScopeSaneQuantity = world.ScopeSaneQuantity,
            ScopeUncompetitiveQuantity = world.ScopeUncompetitiveQuantity,
            ScopeInsaneQuantity = world.ScopeInsaneQuantity,
            TotalSaneQuantity = world.TotalSaneQuantity,
            TotalListingQuantity = world.TotalListingQuantity,
            CompetitiveCoverageRatio = world.CompetitiveCoverageRatio,
            ScopeCompetitiveCoverageRatio = world.ScopeCompetitiveCoverageRatio,
            ScopeSaneCoverageRatio = world.ScopeSaneCoverageRatio,
            SaneCoverageRatio = world.SaneCoverageRatio,
            CompetitiveAverageUnitPrice = world.ScopeCompetitiveAverageUnitPrice,
            ScopeCompetitiveAverageUnitPrice = world.ScopeCompetitiveAverageUnitPrice,
            CoverageBucket = world.CoverageBucket,
            FetchedAtUtc = world.FetchedAtUtc,
            MarketUploadedAtUtc = world.MarketUploadedAtUtc,
            DataAge = world.DataAge,
            DataAgeSource = world.DataAgeSource,
            DataQualityBucket = world.DataQualityBucket,
            Scores = world.Scores.ToList(),
            DetailKey = key
        };
    }

    private static void AppendFacts(
        List<CanonicalMarketListingFact> facts,
        MarketIntelligenceProjectionRequest request,
        Guid runId,
        MarketIntelligenceDetailKey key,
        WorldMarketAnalysis world,
        IReadOnlyList<MarketPriceEvaluationReasonCode> classificationReasons)
    {
        foreach (var listing in world.Listings)
        {
            facts.Add(new CanonicalMarketListingFact
            {
                PublicationId = key.PublicationId,
                RunId = runId,
                DemandFingerprint = key.DemandFingerprint,
                ItemId = key.ItemId,
                Scope = key.Scope,
                DataCenter = world.DataCenter,
                WorldName = world.WorldName,
                RetrievedAtUtc = world.FetchedAtUtc ?? request.CompletedAtUtc,
                MarketUploadedAtUtc = world.MarketUploadedAtUtc,
                LastReviewTimeUtc = listing.LastReviewTimeUtc,
                Quantity = listing.Quantity,
                UnitPrice = listing.PricePerUnit,
                IsHq = listing.IsHq,
                RetainerName = listing.RetainerName,
                PriceSanity = listing.PriceSanity,
                Competitiveness = listing.Competitiveness,
                ClassificationReasons = classificationReasons,
                SourceProvider = "Universalis",
                SourceScopeKey = $"{key.Scope}:{world.DataCenter}:{world.WorldName}"
            });
        }
    }

    private static MarketIntelligenceDetailKey CreateDetailKey(
        Guid publicationId,
        MarketFetchScope scope,
        int itemId,
        WorldMarketAnalysis world,
        MarketDemandFingerprint demandFingerprint) =>
        new(
            publicationId,
            scope,
            itemId,
            new MarketWorldKey(world.DataCenter, world.WorldName),
            demandFingerprint);

    private static MarketDemandFingerprint CreateDemandFingerprint(
        MarketIntelligenceProjectionRequest request,
        MarketItemAnalysis analysis) =>
        new(
            $"scope:{analysis.Scope};dc:{request.PublicationContext.SelectedDataCenter};region:{request.PublicationContext.SelectedRegion};item:{analysis.ItemId};qty:{analysis.QuantityNeeded}");

    private static MarketDemandFingerprint CreateDemandFingerprint(
        MarketIntelligenceProjectionRequest request,
        DetailedShoppingPlan plan,
        MarketItemAnalysis? analysis) =>
        new(
            $"scope:{analysis?.Scope ?? request.PublicationContext.Scope};dc:{request.PublicationContext.SelectedDataCenter};region:{request.PublicationContext.SelectedRegion};item:{plan.ItemId};qty:{plan.QuantityNeeded}");

    private static MarketCoverageBucket ResolveCoverageBucket(DetailedShoppingPlan plan, MarketItemAnalysis? analysis)
    {
        if (plan.RecommendedWorld != null)
        {
            var recommendedWorld = analysis?.Worlds.FirstOrDefault(world =>
                string.Equals(world.DataCenter, plan.RecommendedWorld.DataCenter, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(world.WorldName, plan.RecommendedWorld.WorldName, StringComparison.OrdinalIgnoreCase));
            if (recommendedWorld != null)
            {
                return recommendedWorld.CoverageBucket;
            }
        }

        return analysis?.Worlds
            .OrderBy(world => world.CoverageBucket)
            .FirstOrDefault()?.CoverageBucket ?? MarketCoverageBucket.None;
    }

    private static MarketDataQualityBucket ResolveDataQualityBucket(DetailedShoppingPlan plan, MarketItemAnalysis? analysis)
    {
        if (plan.RecommendedWorld != null)
        {
            var recommendedWorld = analysis?.Worlds.FirstOrDefault(world =>
                string.Equals(world.DataCenter, plan.RecommendedWorld.DataCenter, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(world.WorldName, plan.RecommendedWorld.WorldName, StringComparison.OrdinalIgnoreCase));
            if (recommendedWorld != null)
            {
                return recommendedWorld.DataQualityBucket;
            }
        }

        return analysis?.WorstDataQualityBucket
            ?? plan.RecommendedWorld?.MarketDataQualityBucket
            ?? MarketDataQualityBucket.Missing;
    }

    private static long EstimatePayloadBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).LongLength;

    private static long EstimateLegacyPayloadBytes(
        MarketAnalysisExecutionResult executionResult,
        long marketIntelligencePayloadBytes,
        long retainedDetailBytes)
    {
        var itemOverheadBytes = executionResult.Analyses.Count * 512L;
        var shoppingPlanOverheadBytes = executionResult.ShoppingPlans.Count * 256L;
        return Math.Max(
            marketIntelligencePayloadBytes + retainedDetailBytes,
            retainedDetailBytes + itemOverheadBytes + shoppingPlanOverheadBytes);
    }
}

public sealed class MarketIntelligenceProjectionRequest
{
    public Guid PublicationId { get; init; }

    public Guid RunId { get; init; }

    public MarketAnalysisExecutionResult ExecutionResult { get; init; } = null!;

    public MarketIntelligencePublicationContext PublicationContext { get; init; } =
        MarketIntelligencePublicationContext.None;

    public string AnalyzerVersion { get; init; } = string.Empty;

    public DateTime StartedAtUtc { get; init; }

    public DateTime CompletedAtUtc { get; init; }

    public TimeSpan PlanBuildDuration { get; init; }

    public TimeSpan MarketFetchDuration { get; init; }

    public TimeSpan LadderAnalysisDuration { get; init; }

    public TimeSpan ShoppingPlanProjectionDuration { get; init; }

    public TimeSpan AnalysisDuration { get; init; }

    public TimeSpan ProjectionDuration { get; init; }

    public TimeSpan PublicationDuration { get; init; }

    public TimeSpan DetailPersistenceDuration { get; init; }

    public TimeSpan SourceFactPersistenceDuration { get; init; }

    public TimeSpan HotStatePublicationDuration { get; init; }

    public TimeSpan PlanPersistenceDuration { get; init; }

    public TimeSpan AutosaveDuration { get; init; }

    public string CacheMode { get; init; } = string.Empty;

    public int NetworkRequestCount { get; init; }

    public int FreshCacheHitCount { get; init; }

    public int StaleCacheRefreshCount { get; init; }
}

public sealed record MarketIntelligenceProjectionResult(
    MarketIntelligencePublicationWrite Publication,
    IReadOnlyList<CanonicalMarketListingFact> SourceFacts);
