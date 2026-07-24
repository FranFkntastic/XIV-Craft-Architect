using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class PersistenceContractTests
{
    [Fact]
    public void CompressedMarketIntelligence_RoundTripsThroughSharedCodec()
    {
        var original = JsonSerializer.Deserialize<StoredMarketIntelligence>(
            CurrentMarketIntelligenceJson);
        Assert.NotNull(original);

        var compressed = MarketIntelligencePayloadCodec.Serialize(
            original,
            compress: true);
        var restored = MarketIntelligencePayloadCodec.Deserialize(compressed);

        Assert.True(MarketIntelligencePayloadCodec.IsCompressed(compressed));
        Assert.NotNull(restored);
        Assert.Equal(original.MarketIntelligenceId, restored.MarketIntelligenceId);
        Assert.Equal(original.ItemAnalyses.Count, restored.ItemAnalyses.Count);
        Assert.Equal(original.Recommendations.Count, restored.Recommendations.Count);
        Assert.Equal(
            original.RecipeBasis?.Metadata.RecipeDataIdentity,
            restored.RecipeBasis?.Metadata.RecipeDataIdentity);
    }

    [Fact]
    public void CurrentRecipeBasis_RestoresIdentityOperationsAndDemand()
    {
        using var document = JsonDocument.Parse(CurrentMarketIntelligenceJson);
        var json = document.RootElement.GetProperty("RecipeBasis").GetRawText();

        var parsed = StoredRecipeBasisMapper.TryDeserialize(json, out var warning);
        var hydrated = StoredRecipeBasisMapper.Hydrate(parsed!);

        Assert.Null(warning);
        Assert.Equal(2, parsed!.SchemaVersion);
        Assert.Equal(11, parsed.Metadata.PlanSessionVersion);
        Assert.Equal(12, parsed.Metadata.PlanStructureVersion);
        Assert.Equal(13, parsed.Metadata.PlanDecisionVersion);
        Assert.Equal(14, parsed.Metadata.PlanPriceVersion);
        Assert.Equal(15, parsed.Metadata.SettingsVersion);
        Assert.Equal(new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc), parsed.Metadata.CompletedAtUtc);
        Assert.Equal(1, parsed.Metadata.NodeCount);
        Assert.Equal(2, parsed.Metadata.UniqueItemIdCount);
        Assert.Equal(3, parsed.Metadata.DiagnosticCount);
        Assert.Equal("garland-contract-1", hydrated.Metadata.Identity.RecipeDataIdentity);
        var operation = Assert.Single(parsed.Operations);
        Assert.Equal("root", operation.NodeId);
        Assert.Null(operation.ParentNodeId);
        Assert.Empty(operation.AncestorNodeIds);
        Assert.Equal(0, operation.Depth);
        Assert.Equal(100, operation.ResultItemId);
        Assert.Equal("Varnish", operation.ResultItemName);
        Assert.Equal(2, operation.RequestedQuantity);
        Assert.Equal(AcquisitionSource.MarketBuyNq, operation.Source);
        Assert.Equal(AcquisitionSourceReason.Restored, operation.SourceReason);
        Assert.True(operation.MustBeHq);
        Assert.True(operation.CanCraft);
        Assert.Equal(RecipeOperationState.Active, operation.State);
        Assert.Null(operation.SuppressedByNodeId);
        Assert.Null(operation.SuppressedByItemName);
        Assert.Equal(RecipeOperationKind.StandardCraft, operation.Kind);
        Assert.Equal((uint)1234, operation.RecipeId);
        Assert.Equal(8, operation.JobId);
        Assert.Equal("Carpenter", operation.JobName);
        Assert.Equal(90, operation.RecipeLevel);
        Assert.Equal(100, operation.RecipeDisplayLevel);
        Assert.Equal(777, operation.RecipeUnlockItemId);
        Assert.Equal(2, operation.Yield);
        Assert.Equal(1, operation.CraftCount);
        Assert.Equal(RecipeResolutionConfidence.Exact, operation.ResolutionConfidence);
        Assert.Equal(RecipeDataSourceKind.GarlandStandardCraft, operation.RecipeDataSource);
        Assert.True(operation.HasStructuralDiagnostics);
        var ingredient = Assert.Single(operation.Ingredients);
        Assert.Equal(101, ingredient.ItemId);
        Assert.Equal("Beeswax", ingredient.Name);
        Assert.Equal(3, ingredient.AmountPerCraft);
        Assert.Equal(3, ingredient.TotalQuantity);
        Assert.Equal("wax", ingredient.ChildNodeId);
        Assert.Equal(AcquisitionSource.VendorBuy, ingredient.ChildSource);
        Assert.False(ingredient.ChildCanCraft);
        Assert.Equal(RecipeIngredientLinkStatus.Matched, ingredient.LinkStatus);
        Assert.Equal(3, ingredient.ExpectedTotalQuantity);
        Assert.Equal(3, ingredient.PlanChildQuantity);
        var demand = Assert.Single(parsed.MarketAnalysisDemandItems);
        Assert.Equal(100, demand.ItemId);
        Assert.Equal("Varnish", demand.Name);
        Assert.Equal(9876, demand.IconId);
        Assert.Equal(2, demand.TotalQuantity);
        Assert.True(demand.RequiresHq);
        Assert.Contains(404, parsed.UnavailableMarketItemIds);
        var hydratedOperation = Assert.Single(hydrated.Operations);
        Assert.Equal(100, hydratedOperation.RecipeDisplayLevel);
        Assert.Equal(777, hydratedOperation.RecipeUnlockItemId);
        Assert.Equal(3, Assert.Single(hydratedOperation.Ingredients).ExpectedTotalQuantity);
    }

    [Fact]
    public void NewerRecipeBasisSchema_IsRejectedWithoutHydration()
    {
        var stored = RecipeBasis();
        stored.SchemaVersion = StoredRecipeOperationSnapshot.CurrentSchemaVersion + 1;

        var parsed = StoredRecipeBasisMapper.TryDeserialize(JsonSerializer.Serialize(stored), out var warning);

        Assert.Null(parsed);
        Assert.Contains("newer", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateRecipeNodeIdentity_IsRejectedAsCorrupt()
    {
        var stored = RecipeBasis();
        stored.Operations.Add(new StoredRecipeOperation
        {
            NodeId = "root",
            ResultItemId = 200,
            ResultItemName = "Conflicting Root",
        });

        var parsed = StoredRecipeBasisMapper.TryDeserialize(JsonSerializer.Serialize(stored), out var warning);

        Assert.Null(parsed);
        Assert.Contains("duplicate node id", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompatibleCanonicalMarketIntelligence_RestoresAuthoritativeEvidence()
    {
        var result = Restore(CurrentMarketIntelligenceJson);

        Assert.Null(result.Warning);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), result.MarketIntelligence?.MarketIntelligenceId);
        var analysis = Assert.Single(result.MarketItemAnalyses);
        Assert.Equal(100, analysis.ItemId);
        Assert.Equal("Varnish", analysis.Name);
        Assert.Equal(2, analysis.QuantityNeeded);
        Assert.Equal(MarketFetchScope.EntireRegion, analysis.Scope);
        Assert.Equal(new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc), analysis.LoadedAtUtc);
        Assert.Equal(new DateTime(2026, 7, 20, 9, 30, 0, DateTimeKind.Utc), analysis.LastReconciledAtUtc);
        Assert.Equal(101.25m, analysis.AnalysisScopeBaselineUnitPrice);
        Assert.Equal(102.5m, analysis.AnalysisScopeAverageUnitPrice);
        Assert.Equal(99.75m, analysis.AnalysisCompetitiveAverageUnitPrice);
        Assert.Equal(12, analysis.ProcurementSignalQuantity);
        Assert.Equal(100m, analysis.PrimaryProcurementShelfAverageUnitPrice);
        Assert.Equal(200, analysis.CostToCoverTotalGil);
        Assert.Equal(100m, analysis.CostToCoverUnitPrice);
        Assert.Equal(100, analysis.CostToCoverMaxUnitPrice);
        Assert.Equal(101m, analysis.AnalysisScopeMedianUnitPrice);
        Assert.Equal(110m, analysis.CompetitiveThresholdUnitPrice);
        Assert.Equal(150m, analysis.SaneThresholdUnitPrice);
        Assert.Equal(new[] { "Aether", "Primal" }, analysis.RequestedDataCenters);
        Assert.Equal(new[] { "Aether", "Primal" }, analysis.PresentDataCenters);
        Assert.Empty(analysis.MissingDataCenters);
        Assert.True(analysis.HasCompleteScopeData);
        Assert.Equal(MarketDataQualityBucket.Aging, analysis.WorstDataQualityBucket);
        Assert.Equal("frozen warning", analysis.Warning);

        var evaluation = Assert.IsType<MarketPriceEvaluation>(analysis.PriceEvaluation);
        Assert.Equal(MarketPriceQualityPolicy.DualChannel, evaluation.QualityPolicy);
        Assert.Equal(90, evaluation.CentralRegion.MinUnitPrice);
        Assert.Equal(110, evaluation.CentralRegion.MaxUnitPrice);
        Assert.Equal(100m, evaluation.CentralRegion.MedianUnitPrice);
        Assert.Equal(99.5m, evaluation.CentralRegion.WeightedAverageUnitPrice);
        Assert.Equal(8, evaluation.CentralRegion.ListingCount);
        Assert.Equal(40, evaluation.CentralRegion.TotalQuantity);
        Assert.Equal(3, evaluation.CentralRegion.DistinctRetainerCount);
        Assert.Equal(2, evaluation.CentralRegion.DistinctWorldCount);
        Assert.Equal(0.91m, evaluation.CentralRegion.SupportScore);
        Assert.Equal(0.8m, evaluation.CentralRegion.ListingShare);
        Assert.Equal(0.75m, evaluation.CentralRegion.SourceShare);
        Assert.Equal(1m, evaluation.CentralRegion.WorldShare);
        Assert.Equal(MarketPriceRegionCredibility.Strong, evaluation.CentralRegion.Credibility);
        Assert.Equal(92m, evaluation.Thresholds.DealCeilingUnitPrice);
        Assert.Equal(110m, evaluation.Thresholds.CompetitiveCeilingUnitPrice);
        Assert.Equal(150m, evaluation.Thresholds.SaneCeilingUnitPrice);
        Assert.Equal(300m, evaluation.Thresholds.InsaneFloorUnitPrice);
        Assert.Equal(1, evaluation.ListingClassCounts.DealCount);
        Assert.Equal(2, evaluation.ListingClassCounts.CompetitiveCount);
        Assert.Equal(3, evaluation.ListingClassCounts.FairCount);
        Assert.Equal(4, evaluation.ListingClassCounts.UncompetitiveCount);
        Assert.Equal(5, evaluation.ListingClassCounts.ExcludedCount);
        Assert.Equal(6, evaluation.ListingClassCounts.LowOutlierCount);
        Assert.Equal(7, evaluation.ListingClassCounts.SaneCount);
        Assert.Equal(8, evaluation.ListingClassCounts.OutlierCount);
        Assert.Equal(9, evaluation.ListingClassCounts.InsaneCount);
        Assert.Equal(MarketPriceEvaluationConfidence.High, evaluation.Confidence);
        Assert.Equal(
            MarketPriceEvaluationReasonCode.AcceptedDueToQuantityDespiteLowDiversity,
            Assert.Single(evaluation.Diagnostics.CompactReasonCodes));
        Assert.Equal(0.5m, Assert.Single(evaluation.Diagnostics.DetectedPriceGapSummaries).BreakPercent);
        Assert.True(evaluation.Diagnostics.DebugDetailAvailable);

        var scopeBand = Assert.Single(analysis.ScopePriceBands);
        Assert.Equal(90, scopeBand.MinUnitPrice);
        Assert.Equal(110, scopeBand.MaxUnitPrice);
        Assert.Equal(99.5m, scopeBand.WeightedAverageUnitPrice);
        Assert.Equal(40, scopeBand.TotalQuantity);
        Assert.Equal(8, scopeBand.ListingCount);
        Assert.Equal(2, scopeBand.DistinctWorldCount);
        Assert.Equal(3, scopeBand.DistinctRetainerCount);
        Assert.Equal(PriceBandCompetitiveness.Competitive, scopeBand.Competitiveness);
        Assert.Equal(PriceBandDepth.Deep, scopeBand.Depth);
        Assert.Equal(0.5m, scopeBand.BreakPercentToNextBand);

        var worldAnalysis = Assert.Single(analysis.Worlds);
        Assert.Equal(30, worldAnalysis.PrimaryUsableQuantity);
        Assert.Equal(25, worldAnalysis.PriceSignalQuantity);
        Assert.Equal(20, worldAnalysis.ActionableQuantity);
        Assert.Equal(100m, worldAnalysis.ActionableAverageUnitPrice);
        Assert.Equal(200, worldAnalysis.ActionableCostToCoverTotalGil);
        Assert.Equal(100m, worldAnalysis.ActionableCostToCoverUnitPrice);
        Assert.Equal(100, worldAnalysis.ActionableCostToCoverMaxUnitPrice);
        Assert.Equal(0.75m, worldAnalysis.PrimaryUsableCoverageRatio);
        Assert.Equal(0.625m, worldAnalysis.PriceSignalCoverageRatio);
        Assert.Equal(0.5m, worldAnalysis.ScopeSaneCoverageRatio);
        Assert.Equal(0.65m, worldAnalysis.SaneCoverageRatio);
        Assert.Equal(87.5m, worldAnalysis.DataQualityScore);
        var priceBand = Assert.Single(worldAnalysis.PriceBands);
        Assert.Equal(90, priceBand.MinUnitPrice);
        Assert.Equal(110, priceBand.MaxUnitPrice);
        Assert.Equal(99.5m, priceBand.WeightedAverageUnitPrice);
        Assert.Equal(40, priceBand.Quantity);
        var analyzedListing = Assert.Single(worldAnalysis.Listings);
        Assert.Equal(3, analyzedListing.Quantity);
        Assert.Equal(100, analyzedListing.PricePerUnit);
        Assert.Equal(MarketListingPriceSanity.Sane, analyzedListing.PriceSanity);
        Assert.Equal(MarketListingCompetitiveness.Competitive, analyzedListing.Competitiveness);
        Assert.Equal(98.75m, Assert.Single(worldAnalysis.Scores).Score);

        var recommendation = Assert.Single(result.Recommendations);
        Assert.Equal(100, recommendation.ItemId);
        Assert.Equal(9876, recommendation.IconId);
        Assert.Equal(2, recommendation.QuantityNeeded);
        Assert.Equal(2, recommendation.HqQuantityNeeded);
        Assert.Equal(102.5m, recommendation.DCAveragePrice);
        Assert.Equal(120.25m, recommendation.HQAveragePrice);
        Assert.Equal("frozen market warning", recommendation.MarketDataWarning);
        Assert.Null(recommendation.Error);
        var world = Assert.Single(recommendation.WorldOptions);
        Assert.Equal("Siren", recommendation.RecommendedWorld?.WorldName);
        Assert.Equal(300, recommendation.RecommendedWorld?.TotalCost);
        Assert.Equal(300, world.TotalCost);
        Assert.Equal(100m, world.AveragePricePerUnit);
        Assert.Equal(3, world.TotalQuantityPurchased);
        Assert.Equal(1, world.ExcessQuantity);
        Assert.Equal(100, world.ModePricePerUnit);
        Assert.Equal(150m, world.ValueScore);
        Assert.Equal(87.5m, world.MarketDataQualityScore);
        Assert.Equal(TimeSpan.FromMinutes(5), world.MarketDataAge);
        Assert.Equal(315m, world.ProcurementPriorityScore);
        var listing = Assert.Single(world.Listings);
        Assert.Equal(3, listing.Quantity);
        Assert.Equal(100, listing.PricePerUnit);
        Assert.Equal(2, listing.NeededFromStack);
        Assert.Equal(1, listing.ExcessQuantity);
        Assert.Equal(999, Assert.Single(world.ExcludedListings).PricePerUnit);
        Assert.Equal(100, world.BestSingleListing?.PricePerUnit);
        Assert.Equal(250m, Assert.Single(recommendation.Vendors).Price);
        var split = Assert.Single(recommendation.RecommendedSplit!);
        Assert.Equal(2, split.QuantityToBuy);
        Assert.Equal(100m, split.PricePerUnit);
        Assert.Equal(150m, split.EffectivePricePerNeededUnit);
        Assert.Equal(300, split.TotalCost);
        Assert.Equal(1, split.ExcessAvailable);

        var coverage = Assert.IsType<MarketCoverageSet>(recommendation.CoverageSet);
        var option = Assert.IsType<MarketCoverageOption>(coverage.SingleWorld);
        Assert.Equal("single-aether-siren", option.CandidateId);
        Assert.Equal(MarketCoverageKind.SupportedListings, option.Kind);
        Assert.Equal(MarketCoverageQualityPolicy.HqOnly, option.QualityPolicy);
        Assert.Equal(2, option.QuantityCovered);
        Assert.Equal(3, option.QuantityToPurchase);
        Assert.Equal(1, option.ExcessQuantity);
        Assert.Equal(200m, option.ExactNeededCost);
        Assert.Equal(300m, option.CashOutCost);
        Assert.Equal(100m, option.AverageUnitCost);
        Assert.Equal(MarketCoveragePriceBand.Competitive, option.PriceBand);
        Assert.Equal(1, option.Friction.WorldCount);
        Assert.Equal(25m, option.Savings.VersusSingleWorld);
        Assert.Equal(7.5m, option.Savings.VersusSingleWorldPercent);
        Assert.True(option.IsDefaultEligible);
        Assert.Null(option.DegradedReason);
        Assert.Equal(300m, Assert.Single(option.Worlds).CashOutCost);
        Assert.Equal(3, Assert.Single(option.Listings).QuantityPurchased);
        Assert.Equal("single-aether-siren", Assert.Single(coverage.AllCandidates).CandidateId);
        Assert.Equal(404, Assert.Single(result.UnavailableMarketItemIds));
        Assert.Equal("Missing Dye", Assert.Single(result.MarketIntelligence!.UnavailableMarketItems).Name);
        Assert.Equal(MarketIntelligencePublicationContextKind.Known, result.MarketIntelligence.PublicationContext.Kind);
        Assert.Equal(MarketFetchScope.EntireRegion, result.MarketIntelligence.PublicationContext.Scope);
        Assert.Equal(TimeSpan.FromMinutes(15), result.MarketIntelligence.PublicationContext.MaxAge);
        Assert.True(result.MarketIntelligence.PublicationContext.ForceRefreshData);
        Assert.Equal(RecommendationMode.BestUnitPrice, result.MarketIntelligence.RecommendationMode);
        Assert.Equal(MarketAcquisitionLens.BulkValue, result.MarketIntelligence.Lens);
        Assert.Equal(21, result.MarketIntelligence.PublicationContext.CoreVersionStamp?.PlanSession);
        Assert.Equal(31, result.MarketIntelligence.PublicationContext.WebPlanSessionVersion);
        Assert.Equal(32, result.MarketIntelligence.PublicationContext.WebMarketAnalysisVersion);
        Assert.NotNull(result.RecipeBasis);
    }

    [Fact]
    public void HistoricalLegacyEvidence_RestoresOmittedDefaultsAndDegradedCoverage()
    {
        const string analysesJson = """
            [{"ItemId":100,"Name":"Varnish","QuantityNeeded":2}]
            """;
        const string recommendationsJson = """
            [{
              "ItemId":100,
              "Name":"Varnish",
              "QuantityNeeded":2,
              "DCAveragePrice":123,
              "RecommendedWorld":{
                "DataCenter":"Aether",
                "WorldName":"Siren",
                "TotalCost":246,
                "AveragePricePerUnit":123,
                "TotalQuantityPurchased":2
              }
            }]
            """;
        const string recipeBasisJson = """
            {
              "SchemaVersion":1,
              "Metadata":{"RecipeDataIdentity":"garland-legacy-2024"},
              "Operations":[{
                "NodeId":"root",
                "ResultItemId":100,
                "ResultItemName":"Varnish",
                "RequestedQuantity":2
              }],
              "MarketAnalysisDemandItems":[{
                "ItemId":100,
                "Name":"Varnish",
                "TotalQuantity":2
              }],
              "UnavailableMarketItemIds":[404]
            }
            """;

        var result = Restore(
            marketIntelligenceJson: null,
            legacyMarketItemAnalysesJson: analysesJson,
            legacyMarketPlansJson: recommendationsJson,
            legacyMarketAnalysisRecipeBasisJson: recipeBasisJson,
            legacyUnavailableMarketItemIds: new HashSet<int> { 404 },
            legacyRecommendationMode: RecommendationMode.MaximizeValue,
            legacyLens: MarketAcquisitionLens.BulkValue);

        Assert.Null(result.Warning);
        var analysis = Assert.Single(result.MarketItemAnalyses);
        Assert.Equal(MarketFetchScope.SelectedDataCenter, analysis.Scope);
        Assert.Equal(default(DateTime), analysis.LoadedAtUtc);
        Assert.Null(analysis.LastReconciledAtUtc);
        Assert.Equal(0, analysis.CostToCoverTotalGil);
        Assert.Null(analysis.PriceEvaluation);
        Assert.Empty(analysis.ScopePriceBands);
        Assert.Empty(analysis.Worlds);
        var recommendation = Assert.Single(result.Recommendations);
        Assert.Equal(123m, recommendation.DCAveragePrice);
        Assert.Null(recommendation.HQAveragePrice);
        Assert.Empty(recommendation.WorldOptions);
        Assert.Null(recommendation.RecommendedSplit);
        var coverage = Assert.IsType<MarketCoverageSet>(recommendation.CoverageSet);
        var degraded = Assert.IsType<MarketCoverageOption>(coverage.CheapestObserved);
        Assert.Equal(MarketCoverageKind.ProjectedAverage, degraded.Kind);
        Assert.Equal(246m, degraded.ExactNeededCost);
        Assert.Equal(246m, degraded.CashOutCost);
        Assert.Equal(123m, degraded.AverageUnitCost);
        Assert.False(degraded.IsDefaultEligible);
        Assert.Equal("Legacy market intelligence did not include coverage candidates.", degraded.DegradedReason);
        Assert.Empty(degraded.Worlds);
        Assert.Empty(degraded.Listings);
        var basis = Assert.IsType<StoredRecipeOperationSnapshot>(result.RecipeBasis);
        Assert.Equal(1, basis.SchemaVersion);
        Assert.Equal("garland-legacy-2024", basis.Metadata.RecipeDataIdentity);
        Assert.Equal(0, basis.Metadata.PlanSessionVersion);
        var operation = Assert.Single(basis.Operations);
        Assert.Equal(AcquisitionSource.Craft, operation.Source);
        Assert.Equal(AcquisitionSourceReason.SystemDefault, operation.SourceReason);
        Assert.Equal(0, operation.RecipeDisplayLevel);
        Assert.Null(operation.RecipeUnlockItemId);
        Assert.Empty(operation.Ingredients);
        Assert.Equal(404, Assert.Single(result.UnavailableMarketItemIds));
        var intelligence = Assert.IsType<MarketIntelligence>(result.MarketIntelligence);
        Assert.Single(intelligence.ItemAnalyses);
        Assert.Single(intelligence.Recommendations);
        Assert.Equal(404, Assert.Single(intelligence.UnavailableMarketItems).ItemId);
        Assert.Equal(MarketIntelligencePublicationContextKind.UnknownLegacy, intelligence.PublicationContext.Kind);
        Assert.Equal(RecommendationMode.MaximizeValue, intelligence.RecommendationMode);
        Assert.Equal(MarketAcquisitionLens.BulkValue, intelligence.Lens);
    }

    [Fact]
    public void RecipeBasisDemandMismatch_ClearsCanonicalMarketEvidence()
    {
        var basis = RecipeBasis();
        basis.MarketAnalysisDemandItems[0].TotalQuantity = 3;

        var result = Restore(JsonSerializer.Serialize(StoredIntelligence(basis)));

        AssertMarketEvidenceCleared(result);
    }

    [Fact]
    public void CorruptCanonicalMarketJson_FailsClosedWithWarning()
    {
        var result = Restore("{not-json");

        AssertMarketEvidenceCleared(result);
        Assert.Contains("could not be deserialized", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewerCanonicalMarketSchema_FailsClosedWithWarning()
    {
        var stored = StoredIntelligence(RecipeBasis());
        stored.SchemaVersion = StoredMarketIntelligence.CurrentSchemaVersion + 1;

        var result = Restore(JsonSerializer.Serialize(stored));

        AssertMarketEvidenceCleared(result);
        Assert.Contains("newer schema", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewerStoredPlanSchema_DoesNotReplaceActiveCoreSession()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.ActivatePlan(
            Plan(100, "Current Item"),
            [new ProjectItem { Id = 100, Name = "Current Item", Quantity = 2 }],
            new CraftSessionActiveContext("North America", "Aether", "Siren", MarketFetchScope.SelectedDataCenter),
            "current fixture");
        var future = new CoreStoredPlanSnapshot
        {
            SchemaVersion = CoreStoredPlanSnapshot.CurrentSchemaVersion + 1,
            Id = "future-plan",
            Name = "Future Plan",
            ProjectItems = [new CoreStoredProjectItem { Id = 999, Name = "Future Item", Quantity = 1 }],
            PlanJson = JsonSerializer.Serialize(Plan(999, "Future Item")),
        };

        var result = new CorePlanSessionLoadService(session).Load(future);

        Assert.False(result.CanLoad);
        Assert.Equal(100, Assert.Single(session.ActivePlan!.RootItems).ItemId);
        Assert.Equal(100, Assert.Single(session.ProjectItems).Id);
    }

    [Fact]
    public void OlderStoredPlanSchema_LoadsWithCompatibilityWarning()
    {
        var stored = new CoreStoredPlanSnapshot
        {
            SchemaVersion = 0,
            Id = "legacy-plan",
            Name = "Legacy Plan",
            ProjectItems = [new CoreStoredProjectItem { Id = 100, Name = "Varnish", Quantity = 2 }],
            PlanJson = JsonSerializer.Serialize(Plan(100, "Varnish")),
        };

        var result = CorePlanSessionLoadService.Prepare(stored);

        Assert.True(result.CanLoad);
        Assert.Equal(100, Assert.Single(result.Plan!.RootItems).ItemId);
        Assert.Contains("older session schema", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FileStore_ReloadsCanonicalEconomicEvidenceAfterAdapterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ca-persistence-contract-{Guid.NewGuid():N}");
        try
        {
            var snapshot = new CoreStoredPlanSnapshot
            {
                Id = "durable-plan",
                Name = "Durable Plan",
                DataCenter = "Aether",
                SavedAt = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
                ProjectItems = [new CoreStoredProjectItem { Id = 100, Name = "Varnish", Quantity = 2 }],
                MarketIntelligenceJson = CurrentMarketIntelligenceJson,
            };
            var writer = new FileCoreStoredPlanStore(new CoreStoredPlanStoreOptions(root));

            Assert.True(await writer.SavePlanSnapshotAsync(snapshot));

            var restartedReader = new FileCoreStoredPlanStore(new CoreStoredPlanStoreOptions(root));
            var reloaded = Assert.IsType<CoreStoredPlanSnapshot>(
                await restartedReader.LoadPlanSnapshotAsync("durable-plan"));
            var restored = CorePlanSessionLoadService.Prepare(reloaded);

            Assert.NotSame(snapshot, reloaded);
            Assert.Equal(CurrentMarketIntelligenceJson, reloaded.MarketIntelligenceJson);
            Assert.True(restored.CanLoad);
            Assert.Null(restored.Warning);
            Assert.Equal(200, Assert.Single(restored.MarketItemAnalyses).CostToCoverTotalGil);
            Assert.Equal(300m, Assert.Single(restored.ShoppingPlans).CoverageSet?.SingleWorld?.CashOutCost);
            Assert.Equal(404, Assert.Single(restored.UnavailableMarketItemIds));
            Assert.Equal("garland-contract-1", restored.MarketAnalysisRecipeBasis?.Metadata.RecipeDataIdentity);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void AutosaveSnapshot_RecordsNamedSourceIdentityOnlyWhenRequested()
    {
        var state = new AppState();
        state.ApplyBuiltRecipePlan(Plan(100, "Varnish"), []);
        state.TrackCurrentPlanIdentity("named-plan", "Workshop Restock");

        var autosave = StoredPlanSnapshotBuilder.Build(
            state,
            "autosave",
            "Autosave",
            new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
            includeSourcePlanIdentity: true);
        var ordinary = StoredPlanSnapshotBuilder.Build(
            state,
            "copy",
            "Copy",
            includeSourcePlanIdentity: false);

        Assert.Equal("named-plan", autosave.SourcePlanId);
        Assert.Equal("Workshop Restock", autosave.SourcePlanName);
        Assert.Null(ordinary.SourcePlanId);
        Assert.Null(ordinary.SourcePlanName);
    }

    [Fact]
    public void AutosaveRestoration_PreservesNamedSourceIdentity()
    {
        var stored = WebStoredPlan(
            "autosave",
            "Autosave",
            sourcePlanId: "named-plan",
            sourcePlanName: "Workshop Restock");
        var state = new AppState();

        new PlanSessionLoadService(state).Load(stored, trackStoredPlanIdentity: false);

        Assert.Equal("named-plan", state.CurrentPlanId);
        Assert.Equal("Workshop Restock", state.CurrentPlanName);
    }

    [Fact]
    public void AutosaveRestoration_CanStartFromItsAlreadyPersistedSnapshot()
    {
        var stored = WebStoredPlan("autosave", "Autosave");
        var state = new AppState();

        new PlanSessionLoadService(state).Load(
            stored,
            trackStoredPlanIdentity: false,
            markRestoredStatePersisted: true);

        Assert.Equal(PersistedStateBucket.None, state.GetDirtyPersistedBuckets());
    }

    [Fact]
    public void NamedPlanRestoration_TracksStoredIdentityAndStartsClean()
    {
        var state = new AppState();

        new PlanSessionLoadService(state).Load(WebStoredPlan("saved-plan", "Saved Plan"));

        Assert.Equal("saved-plan", state.CurrentPlanId);
        Assert.Equal("Saved Plan", state.CurrentPlanName);
        Assert.Equal(PersistedStateBucket.None, state.GetDirtyPersistedBuckets());
    }

    private static StoredRecipeOperationSnapshot RecipeBasis() => new()
    {
        Metadata = new StoredRecipeOperationMetadata
        {
            RecipeDataIdentity = "garland-contract-1",
            CompletedAtUtc = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc),
            NodeCount = 1,
            UniqueItemIdCount = 1,
        },
        Operations =
        [
            new StoredRecipeOperation
            {
                NodeId = "root",
                ResultItemId = 100,
                ResultItemName = "Varnish",
                RequestedQuantity = 2,
                Source = AcquisitionSource.MarketBuyNq,
                State = RecipeOperationState.Active,
            },
        ],
        MarketAnalysisDemandItems =
        [
            new StoredMarketAnalysisDemandItem
            {
                ItemId = 100,
                Name = "Varnish",
                TotalQuantity = 2,
            },
        ],
        UnavailableMarketItemIds = [404],
    };

    private static StoredMarketIntelligence StoredIntelligence(StoredRecipeOperationSnapshot basis) =>
        StoredMarketIntelligence.FromMarketIntelligence(new MarketIntelligence(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            [new MarketItemAnalysis { ItemId = 100, Name = "Varnish", QuantityNeeded = 2 }],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Varnish", QuantityNeeded = 2 }],
            [],
            MarketIntelligencePublicationContext.UnknownLegacy(
                RecommendationMode.MinimizeTotalCost,
                MarketAcquisitionLens.MinimumUpfrontCost,
                new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc)),
            basis));

    private static StoredMarketIntelligenceRestoreResult Restore(
        string? marketIntelligenceJson,
        string? legacyMarketItemAnalysesJson = null,
        string? legacyMarketPlansJson = null,
        string? legacyMarketAnalysisRecipeBasisJson = null,
        IReadOnlySet<int>? legacyUnavailableMarketItemIds = null,
        RecommendationMode legacyRecommendationMode = RecommendationMode.MinimizeTotalCost,
        MarketAcquisitionLens legacyLens = MarketAcquisitionLens.MinimumUpfrontCost) =>
        StoredMarketIntelligenceRestorer.Restore(new StoredMarketIntelligenceRestoreInput(
            MarketIntelligenceJson: marketIntelligenceJson,
            LegacyMarketItemAnalysesJson: legacyMarketItemAnalysesJson,
            LegacyMarketPlansJson: legacyMarketPlansJson,
            LegacyMarketAnalysisRecipeBasisJson: legacyMarketAnalysisRecipeBasisJson,
            LegacyUnavailableMarketItemIds: legacyUnavailableMarketItemIds ?? new HashSet<int>(),
            LegacyRecommendationMode: legacyRecommendationMode,
            LegacyLens: legacyLens,
            Plan: null,
            ProjectItems: [new ProjectItem { Id = 100, Name = "Varnish", Quantity = 2 }],
            BuildMarketAnalysisCandidates: _ => []));

    private static CraftingPlan Plan(int itemId, string itemName) => new()
    {
        Name = "Plan",
        DataCenter = "Aether",
        World = "Siren",
        RootItems =
        [
            new PlanNode
            {
                NodeId = "root",
                ItemId = itemId,
                Name = itemName,
                Quantity = 2,
                Source = AcquisitionSource.MarketBuyNq,
                CanBuyFromMarket = true,
            },
        ],
    };

    private static StoredPlan WebStoredPlan(
        string id,
        string name,
        string? sourcePlanId = null,
        string? sourcePlanName = null) => new()
        {
            Id = id,
            Name = name,
            DataCenter = "Aether",
            PlanJson = JsonSerializer.Serialize(Plan(100, "Varnish")),
            ProjectItems = [new StoredProjectItem { Id = 100, Name = "Varnish", Quantity = 2 }],
            SourcePlanId = sourcePlanId,
            SourcePlanName = sourcePlanName,
        };

    [Fact]
    public async Task ZeroCommissionPayrollDraftSurvivesPersistenceRoundTrip()
    {
        var companyProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var store = new JsonRoundTripPayrollStore();
        var service = new TradePayrollPersistenceService(store);

        await service.GetOrCreateDraftAsync(
            companyProfileId,
            orderId: null,
            planSessionVersion: 12,
            marketAnalysisVersion: 34,
            sourcePlanName: "Zero commission plan",
            assignedCrafterId: null,
            assignedCrafterDisplayName: null,
            paymentPolicy: new TradePaymentPolicy(TradePaymentContractMode.LegacyCommission, 0m, null));
        var reloaded = Assert.Single(await new TradePayrollPersistenceService(store).LoadDraftsAsync(companyProfileId));

        Assert.Equal(0m, reloaded.CommissionPercent);
        Assert.Equal(TradePaymentContractMode.LegacyCommission, reloaded.ActivePaymentContract);
    }

    private const string CurrentMarketIntelligenceJson = """
        {
          "SchemaVersion": 3,
          "CoverageCostSemanticsVersion": 1,
          "MarketIntelligenceId": "11111111-1111-1111-1111-111111111111",
          "ItemAnalyses": [
            {
              "ItemId": 100,
              "Name": "Varnish",
              "QuantityNeeded": 2,
              "Scope": 1,
              "LoadedAtUtc": "2026-07-20T09:00:00Z",
              "LastReconciledAtUtc": "2026-07-20T09:30:00Z",
              "AnalysisScopeBaselineUnitPrice": 101.25,
              "AnalysisScopeAverageUnitPrice": 102.5,
              "AnalysisCompetitiveAverageUnitPrice": 99.75,
              "ProcurementSignalQuantity": 12,
              "PrimaryProcurementShelfAverageUnitPrice": 100,
              "CostToCoverTotalGil": 200,
              "CostToCoverUnitPrice": 100,
              "CostToCoverMaxUnitPrice": 100,
              "AnalysisScopeMedianUnitPrice": 101,
              "CompetitiveThresholdUnitPrice": 110,
              "SaneThresholdUnitPrice": 150,
              "RequestedDataCenters": ["Aether", "Primal"],
              "PresentDataCenters": ["Aether", "Primal"],
              "MissingDataCenters": [],
              "WorstDataQualityBucket": 1,
              "PriceEvaluation": {
                "ItemId": 100,
                "Scope": 1,
                "QualityPolicy": 4,
                "EvaluatedAtUtc": "2026-07-20T09:01:00Z",
                "CentralRegion": {
                  "MinUnitPrice": 90,
                  "MaxUnitPrice": 110,
                  "MedianUnitPrice": 100,
                  "WeightedAverageUnitPrice": 99.5,
                  "ListingCount": 8,
                  "TotalQuantity": 40,
                  "DistinctRetainerCount": 3,
                  "DistinctWorldCount": 2,
                  "SupportScore": 0.91,
                  "ListingShare": 0.8,
                  "SourceShare": 0.75,
                  "WorldShare": 1,
                  "DataQualityBucket": 0,
                  "Credibility": 3
                },
                "Thresholds": {
                  "DealCeilingUnitPrice": 92,
                  "CompetitiveCeilingUnitPrice": 110,
                  "SaneCeilingUnitPrice": 150,
                  "InsaneFloorUnitPrice": 300
                },
                "ListingClassCounts": {
                  "DealCount": 1,
                  "CompetitiveCount": 2,
                  "FairCount": 3,
                  "UncompetitiveCount": 4,
                  "ExcludedCount": 5,
                  "LowOutlierCount": 6,
                  "SaneCount": 7,
                  "OutlierCount": 8,
                  "InsaneCount": 9
                },
                "Confidence": 3,
                "Diagnostics": {
                  "CompactReasonCodes": [1],
                  "CompactRegionSummaries": [
                    {
                      "MinUnitPrice": 90,
                      "MaxUnitPrice": 110,
                      "ListingCount": 8,
                      "TotalQuantity": 40,
                      "Credibility": 3,
                      "ReasonCode": 1
                    }
                  ],
                  "DetectedPriceGapSummaries": [
                    {
                      "BeforeUnitPrice": 110,
                      "AfterUnitPrice": 165,
                      "BreakPercent": 0.5
                    }
                  ],
                  "DebugDetailAvailable": true
                }
              },
              "ScopePriceBands": [
                {
                  "MinUnitPrice": 90,
                  "MaxUnitPrice": 110,
                  "WeightedAverageUnitPrice": 99.5,
                  "TotalQuantity": 40,
                  "ListingCount": 8,
                  "DistinctWorldCount": 2,
                  "DistinctRetainerCount": 3,
                  "Competitiveness": 2,
                  "Depth": 3,
                  "BreakPercentToNextBand": 0.5
                }
              ],
              "Worlds": [
                {
                  "DataCenter": "Aether",
                  "WorldName": "Siren",
                  "QuantityNeeded": 2,
                  "PrimaryUsableQuantity": 30,
                  "PriceSignalQuantity": 25,
                  "ScopeSaneQuantity": 20,
                  "ScopeUncompetitiveQuantity": 5,
                  "ScopeInsaneQuantity": 1,
                  "TotalSaneQuantity": 26,
                  "TotalListingQuantity": 40,
                  "ActionableQuantity": 20,
                  "ActionableAverageUnitPrice": 100,
                  "ComparableQuantity": 25,
                  "ComparableAverageUnitPrice": 99.75,
                  "ActionableCostToCoverTotalGil": 200,
                  "ActionableCostToCoverUnitPrice": 100,
                  "ActionableCostToCoverMaxUnitPrice": 100,
                  "WorldAverageUnitPrice": 102.5,
                  "ReferenceSupportScore": 0.91,
                  "ReferencePriceCredibility": 3,
                  "CostToCoverTotalGil": 200,
                  "CostToCoverUnitPrice": 100,
                  "CostToCoverMaxUnitPrice": 100,
                  "PrimaryUsableCoverageRatio": 0.75,
                  "PriceSignalCoverageRatio": 0.625,
                  "ScopeSaneCoverageRatio": 0.5,
                  "SaneCoverageRatio": 0.65,
                  "AnalysisScopeBaselineUnitPrice": 101.25,
                  "AnalysisScopeAverageUnitPrice": 102.5,
                  "AnalysisCompetitiveAverageUnitPrice": 99.75,
                  "PrimaryUsableAverageUnitPrice": 100,
                  "PriceSignalAverageUnitPrice": 99.75,
                  "AnalysisScopeMedianUnitPrice": 101,
                  "CompetitiveThresholdUnitPrice": 110,
                  "SaneThresholdUnitPrice": 150,
                  "CoverageBucket": 0,
                  "PriceSignalDepth": 3,
                  "FetchedAtUtc": "2026-07-20T08:59:00Z",
                  "MarketUploadedAtUtc": "2026-07-20T08:55:00Z",
                  "DataAgeSource": 0,
                  "DataAge": "00:05:00",
                  "DataQualityScore": 87.5,
                  "DataQualityBucket": 1,
                  "PriceBands": [
                    {
                      "FirstListingIndex": 0,
                      "LastListingIndex": 7,
                      "MinUnitPrice": 90,
                      "MaxUnitPrice": 110,
                      "WeightedAverageUnitPrice": 99.5,
                      "ListingCount": 8,
                      "Quantity": 40,
                      "NextBreakPercent": 0.5,
                      "Competitiveness": 2,
                      "Depth": 3,
                      "IsPriceSignalBand": true,
                      "IsPrimaryUsableBand": true
                    }
                  ],
                  "Listings": [
                    {
                      "SortIndex": 0,
                      "Quantity": 3,
                      "PricePerUnit": 100,
                      "RetainerName": "Frozen Retainer",
                      "IsHq": true,
                      "PriceSanity": 0,
                      "Competitiveness": 2,
                      "IsInPriceSignalBand": true,
                      "IsInPrimaryUsableBand": true,
                      "LastReviewTimeUtc": "2026-07-20T08:54:00Z"
                    }
                  ],
                  "Scores": [
                    {
                      "Lens": 1,
                      "Score": 98.75,
                      "Rank": 1,
                      "ScoreBucket": 0,
                      "Summary": "frozen score"
                    }
                  ]
                }
              ],
              "Warning": "frozen warning"
            }
          ],
          "Recommendations": [
            {
              "ItemId": 100,
              "Name": "Varnish",
              "IconId": 9876,
              "QuantityNeeded": 2,
              "HqQuantityNeeded": 2,
              "DCAveragePrice": 102.5,
              "WorldOptions": [
                {
                  "DataCenter": "Aether",
                  "WorldName": "Siren",
                  "WorldId": 57,
                  "TotalCost": 300,
                  "AveragePricePerUnit": 100,
                  "ListingsUsed": 1,
                  "Listings": [
                    {
                      "Quantity": 3,
                      "PricePerUnit": 100,
                      "RetainerName": "Frozen Retainer",
                      "IsUnderAverage": true,
                      "IsHq": true,
                      "NeededFromStack": 2,
                      "ExcessQuantity": 1,
                      "IsAdditionalOption": false
                    }
                  ],
                  "ExcludedListings": [
                    {
                      "Quantity": 99,
                      "PricePerUnit": 999,
                      "RetainerName": "Excluded Retainer",
                      "IsUnderAverage": false,
                      "IsHq": false,
                      "NeededFromStack": 0,
                      "ExcessQuantity": 99,
                      "IsAdditionalOption": true
                    }
                  ],
                  "IsFullyUnderAverage": true,
                  "TotalQuantityPurchased": 3,
                  "ExcessQuantity": 1,
                  "ModePricePerUnit": 100,
                  "ValueScore": 150,
                  "MarketDataQualityScore": 87.5,
                  "MarketDataQualityBucket": 1,
                  "MarketDataAgeSource": 0,
                  "MarketDataAge": "00:05:00",
                  "MarketUploadedAtUtc": "2026-07-20T08:55:00Z",
                  "LensRank": 1,
                  "LensScoreBucket": 0,
                  "ProcurementPriorityScore": 315,
                  "VendorName": null,
                  "HasSufficientStock": true,
                  "ShortfallQuantity": 0,
                  "BestSingleListing": {
                    "Quantity": 3,
                    "PricePerUnit": 100,
                    "RetainerName": "Frozen Retainer",
                    "IsUnderAverage": true,
                    "IsHq": true,
                    "NeededFromStack": 2,
                    "ExcessQuantity": 1,
                    "IsAdditionalOption": false
                  },
                  "Classification": 1,
                  "IsHomeWorld": true,
                  "IsBlacklisted": false,
                  "IsTravelProhibited": false,
                  "CongestedWarning": null
                }
              ],
              "RecommendedWorld": {
                "DataCenter": "Aether",
                "WorldName": "Siren",
                "WorldId": 57,
                "TotalCost": 300,
                "AveragePricePerUnit": 100,
                "ListingsUsed": 1,
                "Listings": [{"Quantity":3,"PricePerUnit":100,"RetainerName":"Frozen Retainer","IsUnderAverage":true,"IsHq":true,"NeededFromStack":2,"ExcessQuantity":1,"IsAdditionalOption":false}],
                "ExcludedListings": [],
                "IsFullyUnderAverage": true,
                "TotalQuantityPurchased": 3,
                "ExcessQuantity": 1,
                "ModePricePerUnit": 100,
                "ValueScore": 150,
                "MarketDataQualityScore": 87.5,
                "MarketDataQualityBucket": 1,
                "MarketDataAgeSource": 0,
                "MarketDataAge": "00:05:00",
                "MarketUploadedAtUtc": "2026-07-20T08:55:00Z",
                "LensRank": 1,
                "LensScoreBucket": 0,
                "ProcurementPriorityScore": 315,
                "VendorName": null,
                "HasSufficientStock": true,
                "ShortfallQuantity": 0,
                "BestSingleListing": {"Quantity":3,"PricePerUnit":100,"RetainerName":"Frozen Retainer","IsUnderAverage":true,"IsHq":true,"NeededFromStack":2,"ExcessQuantity":1,"IsAdditionalOption":false},
                "Classification": 1,
                "IsHomeWorld": true,
                "IsBlacklisted": false,
                "IsTravelProhibited": false,
                "CongestedWarning": null
              },
              "CoverageSet": {
                "ItemId": 100,
                "ItemName": "Varnish",
                "QuantityNeeded": 2,
                "SingleWorld": {
                  "CandidateId": "single-aether-siren",
                  "Tier": 0,
                  "Kind": 0,
                  "QualityPolicy": 1,
                  "QuantityCovered": 2,
                  "QuantityToPurchase": 3,
                  "ExcessQuantity": 1,
                  "ExactNeededCost": 200,
                  "CashOutCost": 300,
                  "AverageUnitCost": 100,
                  "PriceBand": 2,
                  "Worlds": [{"DataCenter":"Aether","WorldName":"Siren","QuantityCovered":2,"QuantityToPurchase":3,"ExactNeededCost":200,"CashOutCost":300}],
                  "Listings": [{"DataCenter":"Aether","WorldName":"Siren","QuantityAvailable":3,"QuantityUsed":2,"QuantityPurchased":3,"PricePerUnit":100,"IsHq":true}],
                  "Friction": {"WorldCount":1,"DataCenterCount":1,"SmallestContribution":2,"LargestContribution":2,"ExcessQuantity":1},
                  "Savings": {"VersusSingleWorld":25,"VersusSingleWorldPercent":7.5},
                  "IsDefaultEligible": true,
                  "DegradedReason": null
                },
                "CompactSplit": null,
                "WideSplit": null,
                "CheapestObserved": null,
                "AllCandidates": [
                  {
                    "CandidateId": "single-aether-siren",
                    "Tier": 0,
                    "Kind": 0,
                    "QualityPolicy": 1,
                    "QuantityCovered": 2,
                    "QuantityToPurchase": 3,
                    "ExcessQuantity": 1,
                    "ExactNeededCost": 200,
                    "CashOutCost": 300,
                    "AverageUnitCost": 100,
                    "PriceBand": 2,
                    "Worlds": [{"DataCenter":"Aether","WorldName":"Siren","QuantityCovered":2,"QuantityToPurchase":3,"ExactNeededCost":200,"CashOutCost":300}],
                    "Listings": [{"DataCenter":"Aether","WorldName":"Siren","QuantityAvailable":3,"QuantityUsed":2,"QuantityPurchased":3,"PricePerUnit":100,"IsHq":true}],
                    "Friction": {"WorldCount":1,"DataCenterCount":1,"SmallestContribution":2,"LargestContribution":2,"ExcessQuantity":1},
                    "Savings": {"VersusSingleWorld":25,"VersusSingleWorldPercent":7.5},
                    "IsDefaultEligible": true,
                    "DegradedReason": null
                  }
                ]
              },
              "Error": null,
              "MarketDataWarning": "frozen market warning",
              "HQAveragePrice": 120.25,
              "Vendors": [{"name":"Material Supplier","location":"Mist","price":250,"currency":"gil","coordinates":[10.5,12.25]}],
              "RecommendedSplit": [
                {
                  "DataCenter": "Aether",
                  "WorldName": "Siren",
                  "QuantityToBuy": 2,
                  "PricePerUnit": 100,
                  "EffectivePricePerNeededUnit": 150,
                  "TotalCost": 300,
                  "IsPartial": false,
                  "TravelContext": "Primary",
                  "ExcessAvailable": 1,
                  "Listings": [{"Quantity":3,"PricePerUnit":100,"RetainerName":"Frozen Retainer","IsUnderAverage":true,"IsHq":true,"NeededFromStack":2,"ExcessQuantity":1,"IsAdditionalOption":false}]
                }
              ]
            }
          ],
          "UnavailableMarketItems": [{"ItemId":404,"Name":"Missing Dye"}],
          "PublicationContext": {
            "Kind": 2,
            "Scope": 1,
            "SelectedDataCenter": "Aether",
            "SelectedRegion": "North America",
            "RequestedDataCenters": ["Aether", "Primal"],
            "ExpectedWorldsByDataCenter": {"Aether":["Siren"],"Primal":["Leviathan"]},
            "MaxAge": "00:15:00",
            "ForceRefreshData": true,
            "RecommendationMode": 2,
            "Lens": 1,
            "CoreVersionStamp": {"PlanSession":21,"PlanCore":22,"PlanDecision":23,"PlanPrice":24,"MarketAnalysis":25,"Procurement":26,"SettingsContext":27,"ViewState":28},
            "WebPlanSessionVersion": 31,
            "WebMarketAnalysisVersion": 32,
            "PublishedAtUtc": "2026-07-20T10:00:00Z"
          },
          "RecipeBasis": {
            "SchemaVersion": 2,
            "Metadata": {
              "PlanSessionVersion": 11,
              "PlanStructureVersion": 12,
              "PlanDecisionVersion": 13,
              "PlanPriceVersion": 14,
              "SettingsVersion": 15,
              "RecipeDataIdentity": "garland-contract-1",
              "CompletedAtUtc": "2026-07-20T10:00:00Z",
              "NodeCount": 1,
              "UniqueItemIdCount": 2,
              "DiagnosticCount": 3
            },
            "Operations": [
              {
                "NodeId": "root",
                "ParentNodeId": null,
                "AncestorNodeIds": [],
                "Depth": 0,
                "ResultItemId": 100,
                "ResultItemName": "Varnish",
                "RequestedQuantity": 2,
                "Source": 1,
                "SourceReason": 2,
                "MustBeHq": true,
                "CanCraft": true,
                "State": 0,
                "SuppressedByNodeId": null,
                "SuppressedByItemName": null,
                "Kind": 0,
                "RecipeId": 1234,
                "JobId": 8,
                "JobName": "Carpenter",
                "RecipeLevel": 90,
                "RecipeDisplayLevel": 100,
                "RecipeUnlockItemId": 777,
                "Yield": 2,
                "CraftCount": 1,
                "Ingredients": [
                  {
                    "ItemId": 101,
                    "Name": "Beeswax",
                    "AmountPerCraft": 3,
                    "TotalQuantity": 3,
                    "ChildNodeId": "wax",
                    "ChildSource": 4,
                    "ChildCanCraft": false,
                    "LinkStatus": 1,
                    "ExpectedTotalQuantity": 3,
                    "PlanChildQuantity": 3
                  }
                ],
                "ResolutionConfidence": 1,
                "RecipeDataSource": 1,
                "HasStructuralDiagnostics": true
              }
            ],
            "MarketAnalysisDemandItems": [{"ItemId":100,"Name":"Varnish","IconId":9876,"TotalQuantity":2,"RequiresHq":true}],
            "UnavailableMarketItemIds": [404]
          }
        }
        """;

    private sealed class JsonRoundTripPayrollStore : ITradePayrollDraftStore
    {
        private string json = "[]";

        public Task<IReadOnlyList<TradePayrollWorkflowDraft>> LoadDraftsAsync(Guid companyProfileId)
        {
            var drafts = JsonSerializer.Deserialize<TradePayrollWorkflowDraft[]>(json) ?? [];
            return Task.FromResult<IReadOnlyList<TradePayrollWorkflowDraft>>(
                drafts.Where(draft => draft.CompanyProfileId == companyProfileId).ToArray());
        }

        public Task<bool> SaveDraftAsync(TradePayrollWorkflowDraft draft)
        {
            json = JsonSerializer.Serialize(new[] { draft });
            return Task.FromResult(true);
        }

        public Task<bool> DeleteDraftAsync(string draftId)
        {
            json = "[]";
            return Task.FromResult(true);
        }
    }

    private static void AssertMarketEvidenceCleared(StoredMarketIntelligenceRestoreResult result)
    {
        Assert.Empty(result.MarketItemAnalyses);
        Assert.Empty(result.Recommendations);
        Assert.Empty(result.UnavailableMarketItemIds);
        Assert.Null(result.MarketIntelligence);
        Assert.Null(result.RecipeBasis);
    }
}
