using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketIntelligenceProjectionServiceTests
{
    [Fact]
    public void Project_PreservesSummaryCostsRecommendedWorldWarningAndCoverage()
    {
        var projection = new MarketIntelligenceProjectionService();
        var publicationId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var result = projection.Project(CreateRequest(publicationId, runId));

        var item = Assert.Single(result.Publication.Summary.Items);
        var world = Assert.Single(item.Worlds);

        Assert.Equal(5338, item.ItemId);
        Assert.Equal("Mythril Ore", item.Name);
        Assert.Equal(12, item.QuantityNeeded);
        Assert.Equal(new MarketWorldKey("Aether", "Siren"), item.RecommendedWorld);
        Assert.Equal(1_200, item.RecommendedTotalCost);
        Assert.Equal(100, item.CompetitiveAverageUnitPrice);
        Assert.Equal(110, item.BaselineUnitPrice);
        Assert.Equal(120, item.AverageUnitPrice);
        Assert.Equal(105, item.MedianUnitPrice);
        Assert.Equal(115, item.CompetitiveThresholdUnitPrice);
        Assert.Equal(300, item.SaneThresholdUnitPrice);
        Assert.Equal(MarketCoverageBucket.Full, item.CoverageBucket);
        Assert.Equal(MarketDataQualityBucket.Current, item.DataQualityBucket);
        Assert.Equal(MarketPriceEvaluationConfidence.High, item.Confidence);
        Assert.Equal("watch the shelf", item.Warning);
        Assert.Equal(new MarketWorldKey("Aether", "Siren"), world.World);
        Assert.Equal(12, world.CompetitiveQuantity);
        Assert.Equal(10, world.LocalCompetitiveQuantity);
        Assert.Equal(12, world.ScopeCompetitiveQuantity);
        Assert.Equal(18, world.ScopeSaneQuantity);
        Assert.Equal(4, world.ScopeUncompetitiveQuantity);
        Assert.Equal(2, world.ScopeInsaneQuantity);
        Assert.Equal(20, world.TotalSaneQuantity);
        Assert.Equal(1m, world.ScopeCompetitiveCoverageRatio);
        Assert.Equal(1.5m, world.ScopeSaneCoverageRatio);
        Assert.Equal(1.67m, world.SaneCoverageRatio);
        Assert.Equal(100, world.ScopeCompetitiveAverageUnitPrice);
        Assert.Equal(MarketCoverageBucket.Full, world.CoverageBucket);
        Assert.True(result.Publication.Summary.ContainsLoadedListingDetails is false);
    }

    [Fact]
    public void Project_PreservesDetailKeysListingsClassificationReasonsAndSourceFacts()
    {
        var projection = new MarketIntelligenceProjectionService();
        var publicationId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var result = projection.Project(CreateRequest(publicationId, runId));

        var worldDetail = Assert.Single(result.Publication.Details, detail => detail.Key.World != null);
        var itemDetail = Assert.Single(result.Publication.Details, detail => detail.Key.World == null);
        var manifestEntry = Assert.Single(
            result.Publication.Summary.DetailManifest.Entries,
            entry => entry.Key.Equals(worldDetail.Key));
        var listing = Assert.Single(worldDetail.Listings);
        var fact = Assert.Single(result.SourceFacts);

        Assert.Equal(worldDetail.Key, manifestEntry.Key);
        Assert.Equal(MarketIntelligenceDetailAvailability.Available, manifestEntry.Availability);
        Assert.Equal(publicationId, worldDetail.Key.PublicationId);
        Assert.Equal(MarketFetchScope.SelectedDataCenter, worldDetail.Key.Scope);
        Assert.Equal(5338, worldDetail.Key.ItemId);
        Assert.Equal(new MarketWorldKey("Aether", "Siren"), worldDetail.Key.World);
        Assert.Contains("item:5338", worldDetail.Key.DemandFingerprint.Value);
        Assert.Equal(runId, worldDetail.RunId);
        Assert.Equal(fact.RetrievedAtUtc, worldDetail.RetrievedAtUtc);
        Assert.Equal(fact.MarketUploadedAtUtc, worldDetail.MarketUploadedAtUtc);
        Assert.Equal(MarketListingCompetitiveness.Competitive, listing.Competitiveness);
        Assert.Contains(MarketPriceEvaluationReasonCode.AcceptedDueToQuantityDespiteLowDiversity, itemDetail.ClassificationReasons);
        Assert.Empty(itemDetail.Listings);
        Assert.Equal(publicationId, fact.PublicationId);
        Assert.Equal(runId, fact.RunId);
        Assert.Equal(worldDetail.Key.DemandFingerprint, fact.DemandFingerprint);
        Assert.Equal("Universalis", fact.SourceProvider);
        Assert.Equal("Siren", fact.WorldName);
        Assert.Equal(100, fact.UnitPrice);
        Assert.True(fact.IsHq);
        Assert.Contains(MarketPriceEvaluationReasonCode.AcceptedDueToQuantityDespiteLowDiversity, fact.ClassificationReasons);
    }

    [Fact]
    public void Project_SourceFactsUsePerItemClassificationReasons()
    {
        var projection = new MarketIntelligenceProjectionService();
        var request = CreateRequest(Guid.NewGuid(), Guid.NewGuid());
        var firstAnalysis = request.ExecutionResult.Analyses.Single();
        var firstPlan = request.ExecutionResult.ShoppingPlans.Single();
        var secondAnalysis = new MarketItemAnalysis
        {
            ItemId = 9999,
            Name = "Second Item",
            QuantityNeeded = 4,
            Scope = MarketFetchScope.SelectedDataCenter,
            LoadedAtUtc = firstAnalysis.LoadedAtUtc,
            PriceEvaluation = new MarketPriceEvaluation
            {
                ItemId = 9999,
                Scope = MarketFetchScope.SelectedDataCenter,
                Diagnostics = new MarketPriceEvaluationDiagnostics
                {
                    CompactReasonCodes =
                    [
                        MarketPriceEvaluationReasonCode.RejectedAsThinLowRegion
                    ]
                }
            },
            Worlds =
            [
                new WorldMarketAnalysis
                {
                    DataCenter = "Aether",
                    WorldName = "Adamantoise",
                    FetchedAtUtc = firstAnalysis.LoadedAtUtc,
                    Listings =
                    [
                        new AnalyzedMarketListing
                        {
                            Quantity = 4,
                            PricePerUnit = 250,
                            Competitiveness = MarketListingCompetitiveness.Uncompetitive
                        }
                    ]
                }
            ]
        };
        var secondPlan = new DetailedShoppingPlan
        {
            ItemId = 9999,
            Name = "Second Item",
            QuantityNeeded = 4
        };
        var updatedRequest = CopyRequest(
            request,
            new MarketAnalysisExecutionResult(
                request.ExecutionResult.Evidence,
                [firstAnalysis, secondAnalysis],
                [firstPlan, secondPlan]));

        var result = projection.Project(updatedRequest);

        var firstFact = Assert.Single(result.SourceFacts, fact => fact.ItemId == 5338);
        var secondFact = Assert.Single(result.SourceFacts, fact => fact.ItemId == 9999);
        Assert.Contains(
            MarketPriceEvaluationReasonCode.AcceptedDueToQuantityDespiteLowDiversity,
            firstFact.ClassificationReasons);
        Assert.DoesNotContain(
            MarketPriceEvaluationReasonCode.RejectedAsThinLowRegion,
            firstFact.ClassificationReasons);
        Assert.Contains(
            MarketPriceEvaluationReasonCode.RejectedAsThinLowRegion,
            secondFact.ClassificationReasons);
        Assert.DoesNotContain(
            MarketPriceEvaluationReasonCode.AcceptedDueToQuantityDespiteLowDiversity,
            secondFact.ClassificationReasons);
    }

    [Fact]
    public void Project_PreservesSplitRecommendationInHotSummary()
    {
        var projection = new MarketIntelligenceProjectionService();
        var publicationId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var request = CreateRequest(publicationId, runId);
        var plan = request.ExecutionResult.ShoppingPlans.Single();
        plan.RecommendedSplit =
        [
            new SplitWorldPurchase
            {
                DataCenter = "Aether",
                WorldName = "Siren",
                QuantityToBuy = 8,
                PricePerUnit = 100,
                TotalCost = 800,
                TravelContext = TravelContextConstants.Primary
            },
            new SplitWorldPurchase
            {
                DataCenter = "Aether",
                WorldName = "Adamantoise",
                QuantityToBuy = 4,
                PricePerUnit = 120,
                TotalCost = 480,
                TravelContext = TravelContextConstants.Supplemental
            }
        ];

        var result = projection.Project(request);

        var item = Assert.Single(result.Publication.Summary.Items);
        Assert.Equal(1_280, item.RecommendedTotalCost);
        Assert.Collection(
            item.RecommendedSplit,
            split =>
            {
                Assert.Equal(new MarketWorldKey("Aether", "Siren"), split.World);
                Assert.Equal(8, split.QuantityToBuy);
                Assert.Equal(800, split.TotalCost);
                Assert.Equal(TravelContextConstants.Primary, split.TravelContext);
            },
            split =>
            {
                Assert.Equal(new MarketWorldKey("Aether", "Adamantoise"), split.World);
                Assert.Equal(4, split.QuantityToBuy);
                Assert.Equal(480, split.TotalCost);
                Assert.Equal(TravelContextConstants.Supplemental, split.TravelContext);
            });
    }

    [Fact]
    public void Project_CompactHydrationPreservesIconAndVendorRecommendation()
    {
        var projection = new MarketIntelligenceProjectionService();
        var publicationId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var request = CreateRequest(publicationId, runId);
        var plan = request.ExecutionResult.ShoppingPlans.Single();
        plan.IconId = 42;
        plan.RecommendedWorld = new WorldShoppingSummary
        {
            DataCenter = MarketShoppingConstants.VendorWorldName,
            WorldName = MarketShoppingConstants.VendorWorldName,
            TotalCost = 504,
            AveragePricePerUnit = 42,
            TotalQuantityPurchased = 12,
            VendorName = "Material Supplier (Mist)"
        };
        plan.Vendors =
        [
            new VendorInfo
            {
                Name = "Material Supplier",
                Location = "Mist",
                Price = 42,
                Currency = "gil"
            }
        ];

        var result = projection.Project(request);
        var item = Assert.Single(result.Publication.Summary.Items);
        var hydratedPlan = Assert.Single(MarketIntelligenceSummaryHydrator.HydrateShoppingPlans(result.Publication.Summary));

        Assert.Equal(42, item.IconId);
        Assert.Equal(42, item.RecommendedWorldAveragePricePerUnit);
        Assert.Equal("Material Supplier (Mist)", item.RecommendedWorldVendorName);
        Assert.Equal(42, Assert.Single(item.Vendors).Price);
        Assert.Equal(42, hydratedPlan.IconId);
        Assert.Equal(42, hydratedPlan.RecommendedWorld!.AveragePricePerUnit);
        Assert.Equal(504, hydratedPlan.RecommendedWorld.TotalCost);
        Assert.Equal("Material Supplier (Mist)", hydratedPlan.RecommendedWorld.VendorName);
        Assert.Equal(42, Assert.Single(hydratedPlan.Vendors).Price);
    }

    [Fact]
    public void Project_CompactHydrationPreservesBlockedRecommendationError()
    {
        var projection = new MarketIntelligenceProjectionService();
        var request = CreateRequest(Guid.NewGuid(), Guid.NewGuid());
        var plan = request.ExecutionResult.ShoppingPlans.Single();
        plan.Error = "Suspicious cached market evidence could not be refreshed. Recommendations are blocked.";
        plan.MarketDataWarning = "Existing warning.";
        plan.RecommendedWorld = null;
        plan.RecommendedSplit = null;

        var result = projection.Project(request);
        var hydratedPlan = Assert.Single(MarketIntelligenceSummaryHydrator.HydrateShoppingPlans(result.Publication.Summary));

        Assert.Contains("blocked", hydratedPlan.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("watch the shelf", hydratedPlan.MarketDataWarning);
    }

    [Fact]
    public void Project_PreservesUnavailableItemsWhenAllRequestsAreMissing()
    {
        var projection = new MarketIntelligenceProjectionService();
        var publicationId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var request = CreateRequest(publicationId, runId);
        var missingOnlyRequest = new MarketIntelligenceProjectionRequest
        {
            PublicationId = request.PublicationId,
            RunId = request.RunId,
            ExecutionResult = new MarketAnalysisExecutionResult(
                CreateEvidenceSet(request.CompletedAtUtc),
                [],
                []),
            PublicationContext = request.PublicationContext,
            AnalyzerVersion = request.AnalyzerVersion,
            StartedAtUtc = request.StartedAtUtc,
            CompletedAtUtc = request.CompletedAtUtc,
            CacheMode = request.CacheMode
        };

        var result = projection.Project(missingOnlyRequest);

        Assert.Equal(5338, Assert.Single(result.Publication.Summary.UnavailableMarketItems).ItemId);
    }

    [Fact]
    public void Project_CreatesRunRecordForBenchmarkAndPayloadAccounting()
    {
        var projection = new MarketIntelligenceProjectionService();
        var publicationId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var result = projection.Project(CreateRequest(publicationId, runId));

        var run = Assert.Single(result.Publication.RunRecords);

        Assert.Equal(runId, run.RunId);
        Assert.Equal(publicationId, run.PublicationId);
        Assert.Equal("market-price-ladder:v1", run.AnalyzerVersion);
        Assert.Equal(MarketFetchScope.SelectedDataCenter, run.Scope);
        Assert.Equal("Aether", run.SelectedDataCenter);
        Assert.Equal("North America", run.SelectedRegion);
        Assert.True(run.ProjectionDuration > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, run.PublicationDuration);
        Assert.True(run.MarketIntelligencePayloadBytes > 0);
        Assert.True(run.RetainedDetailBytes > 0);
        Assert.True(run.LegacyPayloadBytes > run.MarketIntelligencePayloadBytes);
    }

    [Fact]
    public void Project_WhenEvidenceContainsCachedEntries_DoesNotSerializeTupleKeyDictionaryForPayloadAccounting()
    {
        var projection = new MarketIntelligenceProjectionService();
        var request = CreateRequest(Guid.NewGuid(), Guid.NewGuid());
        var loadedAt = request.CompletedAtUtc;
        request = new MarketIntelligenceProjectionRequest
        {
            PublicationId = request.PublicationId,
            RunId = request.RunId,
            ExecutionResult = new MarketAnalysisExecutionResult(
                new MarketEvidenceSet(
                    new Dictionary<(int itemId, string dataCenter), CachedMarketData>
                    {
                        [(5338, "Aether")] = new()
                        {
                            ItemId = 5338,
                            DataCenter = "Aether",
                            FetchedAt = loadedAt
                        }
                    },
                    [(5338, "Aether")],
                    MarketFetchScope.SelectedDataCenter,
                    ["Aether"],
                    "Aether",
                    "North America",
                    TimeSpan.FromHours(24),
                    fetchedCount: 1,
                    loadedAt),
                request.ExecutionResult.Analyses,
                request.ExecutionResult.ShoppingPlans),
            PublicationContext = request.PublicationContext,
            AnalyzerVersion = request.AnalyzerVersion,
            StartedAtUtc = request.StartedAtUtc,
            CompletedAtUtc = request.CompletedAtUtc,
            CacheMode = request.CacheMode,
            NetworkRequestCount = request.NetworkRequestCount,
            FreshCacheHitCount = request.FreshCacheHitCount
        };

        var result = projection.Project(request);

        Assert.True(Assert.Single(result.Publication.RunRecords).LegacyPayloadBytes > 0);
    }

    private static MarketIntelligenceProjectionRequest CreateRequest(Guid publicationId, Guid runId)
    {
        var loadedAt = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);
        var uploadAt = loadedAt.AddMinutes(-20);
        var lastReviewAt = loadedAt.AddMinutes(-15);
        var analysis = new MarketItemAnalysis
        {
            ItemId = 5338,
            Name = "Mythril Ore",
            QuantityNeeded = 12,
            Scope = MarketFetchScope.SelectedDataCenter,
            LoadedAtUtc = loadedAt,
            AnalysisScopeBaselineUnitPrice = 110,
            AnalysisScopeAverageUnitPrice = 120,
            AnalysisScopeCompetitiveAverageUnitPrice = 100,
            AnalysisScopeMedianUnitPrice = 105,
            CompetitiveThresholdUnitPrice = 115,
            SaneThresholdUnitPrice = 300,
            WorstDataQualityBucket = MarketDataQualityBucket.Current,
            Warning = "watch the shelf",
            PriceEvaluation = new MarketPriceEvaluation
            {
                ItemId = 5338,
                Scope = MarketFetchScope.SelectedDataCenter,
                Confidence = MarketPriceEvaluationConfidence.High,
                Diagnostics = new MarketPriceEvaluationDiagnostics
                {
                    CompactReasonCodes =
                    [
                        MarketPriceEvaluationReasonCode.AcceptedDueToQuantityDespiteLowDiversity
                    ]
                }
            },
            Worlds =
            [
                new WorldMarketAnalysis
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    QuantityNeeded = 12,
                    CompetitiveQuantity = 12,
                    LocalCompetitiveQuantity = 10,
                    ScopeCompetitiveQuantity = 12,
                    ScopeSaneQuantity = 18,
                    ScopeUncompetitiveQuantity = 4,
                    ScopeInsaneQuantity = 2,
                    TotalSaneQuantity = 20,
                    TotalListingQuantity = 24,
                    CompetitiveCoverageRatio = 1m,
                    ScopeCompetitiveCoverageRatio = 1m,
                    ScopeSaneCoverageRatio = 1.5m,
                    SaneCoverageRatio = 1.67m,
                    ScopeCompetitiveAverageUnitPrice = 100,
                    CoverageBucket = MarketCoverageBucket.Full,
                    FetchedAtUtc = loadedAt,
                    MarketUploadedAtUtc = uploadAt,
                    DataAge = loadedAt - uploadAt,
                    DataAgeSource = MarketDataAgeSource.UniversalisWorldUpload,
                    DataQualityBucket = MarketDataQualityBucket.Current,
                    PriceBands =
                    [
                        new MarketPriceBand
                        {
                            MinUnitPrice = 100,
                            MaxUnitPrice = 100,
                            ListingCount = 1,
                            Quantity = 12,
                            IsCompetitiveShelf = true
                        }
                    ],
                    Listings =
                    [
                        new AnalyzedMarketListing
                        {
                            SortIndex = 0,
                            Quantity = 12,
                            PricePerUnit = 100,
                            RetainerName = "Test Retainer",
                            IsHq = true,
                            Competitiveness = MarketListingCompetitiveness.Competitive,
                            PriceSanity = MarketListingPriceSanity.Sane,
                            LastReviewTimeUtc = lastReviewAt
                        }
                    ],
                    Scores =
                    [
                        new WorldLensScore
                        {
                            Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                            Rank = 1,
                            Score = 1_200,
                            ScoreBucket = MarketScoreBucket.Optimal,
                            Summary = "Best cost"
                        }
                    ]
                }
            ]
        };
        var shoppingPlan = new DetailedShoppingPlan
        {
            ItemId = 5338,
            Name = "Mythril Ore",
            QuantityNeeded = 12,
            MarketDataWarning = "watch the shelf",
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = "Siren",
                TotalCost = 1_200,
                AveragePricePerUnit = 100,
                TotalQuantityPurchased = 12,
                MarketDataQualityBucket = MarketDataQualityBucket.Current,
                MarketDataAge = TimeSpan.FromMinutes(20),
                MarketUploadedAtUtc = uploadAt
            }
        };
        shoppingPlan.WorldOptions.Add(shoppingPlan.RecommendedWorld);

        return new MarketIntelligenceProjectionRequest
        {
            PublicationId = publicationId,
            RunId = runId,
            ExecutionResult = new MarketAnalysisExecutionResult(
                CreateEvidenceSet(loadedAt),
                [analysis],
                [shoppingPlan]),
            PublicationContext = CreatePublicationContext(loadedAt),
            AnalyzerVersion = "market-price-ladder:v1",
            StartedAtUtc = loadedAt.AddSeconds(-2),
            CompletedAtUtc = loadedAt,
            CacheMode = "warm",
            NetworkRequestCount = 1,
            FreshCacheHitCount = 1
        };
    }

    private static MarketEvidenceSet CreateEvidenceSet(DateTime loadedAt) =>
        new(
            new Dictionary<(int itemId, string dataCenter), CachedMarketData>(),
            [(5338, "Aether")],
            MarketFetchScope.SelectedDataCenter,
            ["Aether"],
            "Aether",
            "North America",
            TimeSpan.FromHours(24),
            fetchedCount: 1,
            loadedAt);

    private static MarketIntelligencePublicationContext CreatePublicationContext(DateTime publishedAt) =>
        new(
            MarketIntelligencePublicationContextKind.Known,
            MarketFetchScope.SelectedDataCenter,
            "Aether",
            "North America",
            ["Aether"],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Aether"] = ["Siren"]
            },
            TimeSpan.FromHours(24),
            ForceRefreshData: false,
            RecommendationMode.MinimizeTotalCost,
            MarketAcquisitionLens.MinimumUpfrontCost,
            null,
            WebPlanSessionVersion: null,
            WebMarketAnalysisVersion: null,
            publishedAt);

    private static MarketIntelligenceProjectionRequest CopyRequest(
        MarketIntelligenceProjectionRequest request,
        MarketAnalysisExecutionResult executionResult) =>
        new()
        {
            PublicationId = request.PublicationId,
            RunId = request.RunId,
            ExecutionResult = executionResult,
            PublicationContext = request.PublicationContext,
            AnalyzerVersion = request.AnalyzerVersion,
            StartedAtUtc = request.StartedAtUtc,
            CompletedAtUtc = request.CompletedAtUtc,
            PlanBuildDuration = request.PlanBuildDuration,
            MarketFetchDuration = request.MarketFetchDuration,
            LadderAnalysisDuration = request.LadderAnalysisDuration,
            ShoppingPlanProjectionDuration = request.ShoppingPlanProjectionDuration,
            AnalysisDuration = request.AnalysisDuration,
            ProjectionDuration = request.ProjectionDuration,
            PublicationDuration = request.PublicationDuration,
            DetailPersistenceDuration = request.DetailPersistenceDuration,
            SourceFactPersistenceDuration = request.SourceFactPersistenceDuration,
            HotStatePublicationDuration = request.HotStatePublicationDuration,
            PlanPersistenceDuration = request.PlanPersistenceDuration,
            AutosaveDuration = request.AutosaveDuration,
            CacheMode = request.CacheMode,
            NetworkRequestCount = request.NetworkRequestCount,
            FreshCacheHitCount = request.FreshCacheHitCount,
            StaleCacheRefreshCount = request.StaleCacheRefreshCount
        };
}
