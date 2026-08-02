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
        original.Recommendations[0].WorldOptions[0].ValueScore = decimal.MaxValue;
        original.Recommendations[0].WorldOptions[0].ProcurementPriorityScore = decimal.MaxValue;
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
        Assert.Equal(
            decimal.MaxValue,
            restored.Recommendations[0].WorldOptions[0].ValueScore);
        Assert.Equal(
            decimal.MaxValue,
            restored.Recommendations[0].WorldOptions[0].ProcurementPriorityScore);
        var legacyRounded = MarketIntelligencePayloadCodec.Deserialize(
            CurrentMarketIntelligenceJson.Replace(
                "\"ValueScore\":150",
                "\"ValueScore\": 7.922816251426434e+28",
                StringComparison.Ordinal));
        Assert.Equal(
            decimal.MaxValue,
            legacyRounded!.Recommendations[0].WorldOptions[0].ValueScore);
    }
    [Fact]
    public void CurrentRecipeBasis_RestoresIdentityOperationsAndDemand()
    {
        using var document = JsonDocument.Parse(CurrentMarketIntelligenceJson);
        var json = document.RootElement.GetProperty("RecipeBasis").GetRawText();
        var parsed = StoredRecipeBasisMapper.TryDeserialize(json, out var warning);
        var hydrated = StoredRecipeBasisMapper.Hydrate(parsed!);
        Assert.Null(warning);
        AssertValues((2, parsed!.SchemaVersion), (11L, parsed.Metadata.PlanSessionVersion), (12L, parsed.Metadata.PlanStructureVersion), (13L, parsed.Metadata.PlanDecisionVersion), (14L, parsed.Metadata.PlanPriceVersion), (15L, parsed.Metadata.SettingsVersion), (new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc), parsed.Metadata.CompletedAtUtc), (1, parsed.Metadata.NodeCount), (2, parsed.Metadata.UniqueItemIdCount), (3, parsed.Metadata.DiagnosticCount), ("garland-contract-1", hydrated.Metadata.Identity.RecipeDataIdentity));
        var operation = Assert.Single(parsed.Operations);
        Assert.Null(operation.ParentNodeId);
        Assert.Empty(operation.AncestorNodeIds);
        Assert.True(operation.MustBeHq);
        Assert.True(operation.CanCraft);
        Assert.Null(operation.SuppressedByNodeId);
        Assert.Null(operation.SuppressedByItemName);
        Assert.True(operation.HasStructuralDiagnostics);
        AssertValues(("root", operation.NodeId), (0, operation.Depth), (100, operation.ResultItemId), ("Varnish", operation.ResultItemName), (2, operation.RequestedQuantity), (AcquisitionSource.MarketBuyNq, operation.Source), (AcquisitionSourceReason.Restored, operation.SourceReason), (RecipeOperationState.Active, operation.State), (RecipeOperationKind.StandardCraft, operation.Kind), ((uint)1234, operation.RecipeId), (8, operation.JobId), ("Carpenter", operation.JobName), (90, operation.RecipeLevel), (100, operation.RecipeDisplayLevel), (777, operation.RecipeUnlockItemId), (2, operation.Yield), (1, operation.CraftCount), (RecipeResolutionConfidence.Exact, operation.ResolutionConfidence), (RecipeDataSourceKind.GarlandStandardCraft, operation.RecipeDataSource));
        var ingredient = Assert.Single(operation.Ingredients);
        Assert.False(ingredient.ChildCanCraft);
        var demand = Assert.Single(parsed.MarketAnalysisDemandItems);
        Assert.True(demand.RequiresHq);
        Assert.Contains(404, parsed.UnavailableMarketItemIds);
        var hydratedOperation = Assert.Single(hydrated.Operations);
        AssertValues((101, ingredient.ItemId), ("Beeswax", ingredient.Name), (3, ingredient.AmountPerCraft), (3, ingredient.TotalQuantity), ("wax", ingredient.ChildNodeId), (AcquisitionSource.VendorBuy, ingredient.ChildSource), (RecipeIngredientLinkStatus.Matched, ingredient.LinkStatus), (3, ingredient.ExpectedTotalQuantity), (3, ingredient.PlanChildQuantity), (100, demand.ItemId), ("Varnish", demand.Name), (9876, demand.IconId), (2, demand.TotalQuantity), (100, hydratedOperation.RecipeDisplayLevel), (777, hydratedOperation.RecipeUnlockItemId), (3, Assert.Single(hydratedOperation.Ingredients).ExpectedTotalQuantity));
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
        var intelligence = Assert.IsType<MarketIntelligence>(result.MarketIntelligence);
        Assert.Null(result.Warning);
        var analysis = Assert.Single(result.MarketItemAnalyses);
        AssertValues((Guid.Parse("11111111-1111-1111-1111-111111111111"), intelligence.MarketIntelligenceId), (100, analysis.ItemId), ("Varnish", analysis.Name), (2, analysis.QuantityNeeded), (MarketFetchScope.EntireRegion, analysis.Scope), (new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc), analysis.LoadedAtUtc), (new DateTime(2026, 7, 20, 9, 30, 0, DateTimeKind.Utc), analysis.LastReconciledAtUtc), (101.25m, analysis.AnalysisScopeBaselineUnitPrice), (102.5m, analysis.AnalysisScopeAverageUnitPrice), (99.75m, analysis.AnalysisCompetitiveAverageUnitPrice), (12, analysis.ProcurementSignalQuantity), (100m, analysis.PrimaryProcurementShelfAverageUnitPrice), (200, analysis.CostToCoverTotalGil), (100m, analysis.CostToCoverUnitPrice), (100, analysis.CostToCoverMaxUnitPrice), (101m, analysis.AnalysisScopeMedianUnitPrice), (110m, analysis.CompetitiveThresholdUnitPrice), (150m, analysis.SaneThresholdUnitPrice));
        Assert.Equal(new[] { "Aether", "Primal" }, analysis.RequestedDataCenters);
        Assert.Equal(new[] { "Aether", "Primal" }, analysis.PresentDataCenters);
        Assert.Empty(analysis.MissingDataCenters);
        Assert.True(analysis.HasCompleteScopeData);
        var evaluation = Assert.IsType<MarketPriceEvaluation>(analysis.PriceEvaluation);
        AssertValues((MarketDataQualityBucket.Aging, analysis.WorstDataQualityBucket), ("frozen warning", analysis.Warning), (MarketPriceQualityPolicy.DualChannel, evaluation.QualityPolicy), (90, evaluation.CentralRegion.MinUnitPrice), (110, evaluation.CentralRegion.MaxUnitPrice), (100m, evaluation.CentralRegion.MedianUnitPrice), (99.5m, evaluation.CentralRegion.WeightedAverageUnitPrice), (8, evaluation.CentralRegion.ListingCount), (40, evaluation.CentralRegion.TotalQuantity), (3, evaluation.CentralRegion.DistinctRetainerCount), (2, evaluation.CentralRegion.DistinctWorldCount), (0.91m, evaluation.CentralRegion.SupportScore), (0.8m, evaluation.CentralRegion.ListingShare), (0.75m, evaluation.CentralRegion.SourceShare), (1m, evaluation.CentralRegion.WorldShare), (MarketPriceRegionCredibility.Strong, evaluation.CentralRegion.Credibility), (92m, evaluation.Thresholds.DealCeilingUnitPrice), (110m, evaluation.Thresholds.CompetitiveCeilingUnitPrice), (150m, evaluation.Thresholds.SaneCeilingUnitPrice), (300m, evaluation.Thresholds.InsaneFloorUnitPrice), (1, evaluation.ListingClassCounts.DealCount), (2, evaluation.ListingClassCounts.CompetitiveCount), (3, evaluation.ListingClassCounts.FairCount), (4, evaluation.ListingClassCounts.UncompetitiveCount), (5, evaluation.ListingClassCounts.ExcludedCount), (6, evaluation.ListingClassCounts.LowOutlierCount), (7, evaluation.ListingClassCounts.SaneCount), (8, evaluation.ListingClassCounts.OutlierCount), (9, evaluation.ListingClassCounts.InsaneCount), (MarketPriceEvaluationConfidence.High, evaluation.Confidence), (MarketPriceEvaluationReasonCode.AcceptedDueToQuantityDespiteLowDiversity, Assert.Single(evaluation.Diagnostics.CompactReasonCodes)), (0.5m, Assert.Single(evaluation.Diagnostics.DetectedPriceGapSummaries).BreakPercent));
        Assert.True(evaluation.Diagnostics.DebugDetailAvailable);
        var scopeBand = Assert.Single(analysis.ScopePriceBands);
        var worldAnalysis = Assert.Single(analysis.Worlds);
        var priceBand = Assert.Single(worldAnalysis.PriceBands);
        var analyzedListing = Assert.Single(worldAnalysis.Listings);
        AssertValues((90, scopeBand.MinUnitPrice), (110, scopeBand.MaxUnitPrice), (99.5m, scopeBand.WeightedAverageUnitPrice), (40, scopeBand.TotalQuantity), (8, scopeBand.ListingCount), (2, scopeBand.DistinctWorldCount), (3, scopeBand.DistinctRetainerCount), (PriceBandCompetitiveness.Competitive, scopeBand.Competitiveness), (PriceBandDepth.Deep, scopeBand.Depth), (0.5m, scopeBand.BreakPercentToNextBand), (30, worldAnalysis.PrimaryUsableQuantity), (25, worldAnalysis.PriceSignalQuantity), (20, worldAnalysis.ActionableQuantity), (100m, worldAnalysis.ActionableAverageUnitPrice), (200, worldAnalysis.ActionableCostToCoverTotalGil), (100m, worldAnalysis.ActionableCostToCoverUnitPrice), (100, worldAnalysis.ActionableCostToCoverMaxUnitPrice), (0.75m, worldAnalysis.PrimaryUsableCoverageRatio), (0.625m, worldAnalysis.PriceSignalCoverageRatio), (0.5m, worldAnalysis.ScopeSaneCoverageRatio), (0.65m, worldAnalysis.SaneCoverageRatio), (87.5m, worldAnalysis.DataQualityScore), (90, priceBand.MinUnitPrice), (110, priceBand.MaxUnitPrice), (99.5m, priceBand.WeightedAverageUnitPrice), (40, priceBand.Quantity), (3, analyzedListing.Quantity), (100, analyzedListing.PricePerUnit), (MarketListingPriceSanity.Sane, analyzedListing.PriceSanity), (MarketListingCompetitiveness.Competitive, analyzedListing.Competitiveness), (98.75m, Assert.Single(worldAnalysis.Scores).Score));
        var recommendation = Assert.Single(result.Recommendations);
        Assert.Null(recommendation.Error);
        var world = Assert.Single(recommendation.WorldOptions);
        var listing = Assert.Single(world.Listings);
        var split = Assert.Single(recommendation.RecommendedSplit!);
        var coverage = Assert.IsType<MarketCoverageSet>(recommendation.CoverageSet);
        var option = Assert.IsType<MarketCoverageOption>(coverage.SingleWorld);
        Assert.True(option.IsDefaultEligible);
        Assert.Null(option.DegradedReason);
        Assert.True(intelligence.PublicationContext.ForceRefreshData);
        AssertValues((100, recommendation.ItemId), (9876, recommendation.IconId), (2, recommendation.QuantityNeeded), (2, recommendation.HqQuantityNeeded), (102.5m, recommendation.DCAveragePrice), (120.25m, recommendation.HQAveragePrice), ("frozen market warning", recommendation.MarketDataWarning), ("Siren", recommendation.RecommendedWorld?.WorldName), (300, recommendation.RecommendedWorld?.TotalCost), (300, world.TotalCost), (100m, world.AveragePricePerUnit), (3, world.TotalQuantityPurchased), (1, world.ExcessQuantity), (100, world.ModePricePerUnit), (150m, world.ValueScore), (87.5m, world.MarketDataQualityScore), (TimeSpan.FromMinutes(5), world.MarketDataAge), (315m, world.ProcurementPriorityScore), (3, listing.Quantity), (100, listing.PricePerUnit), (2, listing.NeededFromStack), (1, listing.ExcessQuantity), (999, Assert.Single(world.ExcludedListings).PricePerUnit), (100, world.BestSingleListing?.PricePerUnit), (250m, Assert.Single(recommendation.Vendors).Price), (2, split.QuantityToBuy), (100m, split.PricePerUnit), (150m, split.EffectivePricePerNeededUnit), (300, split.TotalCost), (1, split.ExcessAvailable), ("single-aether-siren", option.CandidateId), (MarketCoverageKind.SupportedListings, option.Kind), (MarketCoverageQualityPolicy.HqOnly, option.QualityPolicy), (2, option.QuantityCovered), (3, option.QuantityToPurchase), (1, option.ExcessQuantity), (200m, option.ExactNeededCost), (300m, option.CashOutCost), (100m, option.AverageUnitCost), (MarketCoveragePriceBand.Competitive, option.PriceBand), (1, option.Friction.WorldCount), (25m, option.Savings.VersusSingleWorld), (7.5m, option.Savings.VersusSingleWorldPercent), (300m, Assert.Single(option.Worlds).CashOutCost), (3, Assert.Single(option.Listings).QuantityPurchased), ("single-aether-siren", Assert.Single(coverage.AllCandidates).CandidateId), (404, Assert.Single(result.UnavailableMarketItemIds)), ("Missing Dye", Assert.Single(intelligence.UnavailableMarketItems).Name), (MarketIntelligencePublicationContextKind.Known, intelligence.PublicationContext.Kind), (MarketFetchScope.EntireRegion, intelligence.PublicationContext.Scope), (TimeSpan.FromMinutes(15), intelligence.PublicationContext.MaxAge), (RecommendationMode.BestUnitPrice, intelligence.RecommendationMode), (MarketAcquisitionLens.BulkValue, intelligence.Lens), (21L, intelligence.PublicationContext.CoreVersionStamp?.PlanSession), (31L, intelligence.PublicationContext.WebPlanSessionVersion), (32L, intelligence.PublicationContext.WebMarketAnalysisVersion));
        Assert.NotNull(result.RecipeBasis);
    }
    [Fact]
    public void HistoricalLegacyEvidence_RestoresOmittedDefaultsAndDegradedCoverage()
    {
        const string analysesJson = """[{"ItemId":100,"Name":"Varnish","QuantityNeeded":2}]""";
        const string recommendationsJson = """[{"ItemId":100,"Name":"Varnish","QuantityNeeded":2,"DCAveragePrice":123,"RecommendedWorld":{"DataCenter":"Aether","WorldName":"Siren","TotalCost":246,"AveragePricePerUnit":123,"TotalQuantityPurchased":2}}]""";
        const string recipeBasisJson = """{"SchemaVersion":1,"Metadata":{"RecipeDataIdentity":"garland-legacy-2024"},"Operations":[{"NodeId":"root","ResultItemId":100,"ResultItemName":"Varnish","RequestedQuantity":2}],"MarketAnalysisDemandItems":[{"ItemId":100,"Name":"Varnish","TotalQuantity":2}],"UnavailableMarketItemIds":[404]}""";
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
    public void StoredAutomaticAcquisitionDecision_ReconcilesAgainstRestoredEvidence()
    {
        var ingredient = new PlanNode
        {
            NodeId = "ingredient",
            ItemId = 101,
            Name = "Ingredient",
            Quantity = 2,
            Source = AcquisitionSource.VendorBuy,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanBuyFromVendor = true,
            VendorPrice = 10
        };
        var root = new PlanNode
        {
            NodeId = "root",
            ItemId = 100,
            Name = "Varnish",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanCraft = true,
            CanBuyFromMarket = true,
            Children = [ingredient]
        };
        var plan = new CraftingPlan { Name = "Stored automatic decision", DataCenter = "Aether", World = "Siren", RootItems = [root] };
        var stored = new CoreStoredPlanSnapshot
        {
            Id = "stored-automatic-decision",
            Name = plan.Name,
            DataCenter = plan.DataCenter,
            ProjectItems = [new() { Id = root.ItemId, Name = root.Name, Quantity = root.Quantity }],
            PlanJson = JsonSerializer.Serialize(plan),
            PlanStateJson = StoredPlanRuntimeState.Capture(plan),
            MarketIntelligenceJson = CurrentMarketIntelligenceJson
        };
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var result = new CorePlanSessionLoadService(session).Load(stored);
        Assert.Equal(1, result.ReconciledAcquisitionDecisionCount);
        Assert.Equal(AcquisitionSource.Craft, Assert.Single(result.Plan!.RootItems).Source);
        Assert.Equal(AcquisitionSource.Craft, Assert.Single(session.ActivePlan!.RootItems).Source);
        Assert.Equal(MarketFetchScope.EntireRegion, session.ActiveContext.MarketFetchScope);
    }
    [Fact]
    public void RuntimeState_OverlaysMutableDecisionsAndPricesWithoutRewritingGraph()
    {
        var baseline = Plan(100, "Varnish");
        var graphJson = JsonSerializer.Serialize(baseline);
        var changed = JsonSerializer.Deserialize<CraftingPlan>(graphJson)!;
        var node = Assert.Single(changed.RootItems);
        node.Source = AcquisitionSource.VendorBuy;
        node.SourceReason = AcquisitionSourceReason.UserSelected;
        node.MustBeHq = true;
        node.MarketPrice = 12_345;
        node.HqMarketPrice = 23_456;
        node.VendorPrice = 120;
        node.SelectedVendorIndex = 2;
        var stateJson = StoredPlanRuntimeState.Capture(changed);
        var restored = JsonSerializer.Deserialize<CraftingPlan>(graphJson)!;
        StoredPlanRuntimeState.Apply(restored, stateJson);
        var restoredNode = Assert.Single(restored.RootItems);
        Assert.Equal(AcquisitionSource.VendorBuy, restoredNode.Source);
        Assert.Equal(AcquisitionSourceReason.UserSelected, restoredNode.SourceReason);
        Assert.True(restoredNode.MustBeHq);
        Assert.Equal(12_345, restoredNode.MarketPrice);
        Assert.Equal(23_456, restoredNode.HqMarketPrice);
        Assert.Equal(120, restoredNode.VendorPrice);
        Assert.Equal(2, restoredNode.SelectedVendorIndex);
        Assert.Equal(graphJson, JsonSerializer.Serialize(baseline));
    }
    [Fact]
    public void SessionLoad_InfersRegionalScopeFromLegacyMultiDataCenterEvidence()
    {
        var plan = Plan(100, "Varnish");
        var intelligence = new StoredMarketIntelligence
        {
            MarketIntelligenceId = Guid.NewGuid(),
            ItemAnalyses = [new()
            {
                ItemId = 100, Name = "Varnish", QuantityNeeded = 2,
                Scope = MarketFetchScope.SelectedDataCenter,
                RequestedDataCenters = ["Aether", "Primal"], PresentDataCenters = ["Aether", "Primal"],
                Worlds = [new() { DataCenter = "Aether", WorldName = "Siren" }, new() { DataCenter = "Primal", WorldName = "Exodus" }]
            }],
            PublicationContext = MarketIntelligencePublicationContext.UnknownLegacy(
                RecommendationMode.MinimizeTotalCost,
                MarketAcquisitionLens.MinimumUpfrontCost)
        };
        var stored = new CoreStoredPlanSnapshot
        {
            Id = "legacy-regional-evidence",
            Name = plan.Name,
            DataCenter = plan.DataCenter,
            PlanJson = JsonSerializer.Serialize(plan),
            MarketIntelligenceJson = JsonSerializer.Serialize(intelligence)
        };
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var result = new CorePlanSessionLoadService(session).Load(stored);
        Assert.True(result.CanLoad);
        Assert.Equal(MarketFetchScope.EntireRegion, session.ActiveContext.MarketFetchScope);
    }
    [Fact]
    public void ActivePlanClone_NormalizesSerializedParentIdentity()
    {
        var root = new PlanNode { NodeId = "root", ParentNodeId = "stale-root-parent", ItemId = 100, Name = "Root" };
        root.Children.Add(new PlanNode
        {
            NodeId = "child",
            ParentNodeId = null,
            ItemId = 101,
            Name = "Child"
        });
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.ActivatePlan(
            new CraftingPlan { RootItems = [root] },
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", "Siren", MarketFetchScope.SelectedDataCenter),
            "parent identity fixture");
        var activeRoot = Assert.Single(session.ActivePlan!.RootItems);
        var activeChild = Assert.Single(activeRoot.Children);
        Assert.Null(activeRoot.Parent);
        Assert.Null(activeRoot.ParentNodeId);
        Assert.Same(activeRoot, activeChild.Parent);
        Assert.Equal(activeRoot.NodeId, activeChild.ParentNodeId);
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
    private static StoredRecipeOperationSnapshot RecipeBasis() => new()
    {
        Metadata = new()
        {
            RecipeDataIdentity = "garland-contract-1",
            CompletedAtUtc = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc),
            NodeCount = 1,
            UniqueItemIdCount = 1
        },
        Operations = [new() { NodeId = "root", ResultItemId = 100, ResultItemName = "Varnish", RequestedQuantity = 2, Source = AcquisitionSource.MarketBuyNq, State = RecipeOperationState.Active }],
        MarketAnalysisDemandItems = [new() { ItemId = 100, Name = "Varnish", TotalQuantity = 2 }],
        UnavailableMarketItemIds = [404]
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
        RootItems = [new() { NodeId = "root", ItemId = itemId, Name = itemName, Quantity = 2, Source = AcquisitionSource.MarketBuyNq, CanBuyFromMarket = true }]
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
            ProjectItems = [new() { Id = 100, Name = "Varnish", Quantity = 2 }],
            SourcePlanId = sourcePlanId,
            SourcePlanName = sourcePlanName
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
            paymentPolicy: new TradePaymentPolicy(
                TradePaymentContractMode.LegacyCommission,
                0m,
                TradePaymentPolicy.DefaultLaborGilPerSynth));
        var reloaded = Assert.Single(await new TradePayrollPersistenceService(store).LoadDraftsAsync(companyProfileId));
        Assert.Equal(0m, reloaded.CommissionPercent);
        Assert.Equal(TradePaymentContractMode.LegacyCommission, reloaded.ActivePaymentContract);
    }
    private const string CurrentMarketIntelligenceJson = """{"SchemaVersion":3,"CoverageCostSemanticsVersion":1,"MarketIntelligenceId":"11111111-1111-1111-1111-111111111111","ItemAnalyses":[{"ItemId":100,"Name":"Varnish","QuantityNeeded":2,"Scope":1,"LoadedAtUtc":"2026-07-20T09:00:00Z","LastReconciledAtUtc":"2026-07-20T09:30:00Z","AnalysisScopeBaselineUnitPrice":101.25,"AnalysisScopeAverageUnitPrice":102.5,"AnalysisCompetitiveAverageUnitPrice":99.75,"ProcurementSignalQuantity":12,"PrimaryProcurementShelfAverageUnitPrice":100,"CostToCoverTotalGil":200,"CostToCoverUnitPrice":100,"CostToCoverMaxUnitPrice":100,"AnalysisScopeMedianUnitPrice":101,"CompetitiveThresholdUnitPrice":110,"SaneThresholdUnitPrice":150,"RequestedDataCenters":["Aether","Primal"],"PresentDataCenters":["Aether","Primal"],"MissingDataCenters":[],"WorstDataQualityBucket":1,"PriceEvaluation":{"ItemId":100,"Scope":1,"QualityPolicy":4,"EvaluatedAtUtc":"2026-07-20T09:01:00Z","CentralRegion":{"MinUnitPrice":90,"MaxUnitPrice":110,"MedianUnitPrice":100,"WeightedAverageUnitPrice":99.5,"ListingCount":8,"TotalQuantity":40,"DistinctRetainerCount":3,"DistinctWorldCount":2,"SupportScore":0.91,"ListingShare":0.8,"SourceShare":0.75,"WorldShare":1,"DataQualityBucket":0,"Credibility":3},"Thresholds":{"DealCeilingUnitPrice":92,"CompetitiveCeilingUnitPrice":110,"SaneCeilingUnitPrice":150,"InsaneFloorUnitPrice":300},"ListingClassCounts":{"DealCount":1,"CompetitiveCount":2,"FairCount":3,"UncompetitiveCount":4,"ExcludedCount":5,"LowOutlierCount":6,"SaneCount":7,"OutlierCount":8,"InsaneCount":9},"Confidence":3,"Diagnostics":{"CompactReasonCodes":[1],"CompactRegionSummaries":[{"MinUnitPrice":90,"MaxUnitPrice":110,"ListingCount":8,"TotalQuantity":40,"Credibility":3,"ReasonCode":1}],"DetectedPriceGapSummaries":[{"BeforeUnitPrice":110,"AfterUnitPrice":165,"BreakPercent":0.5}],"DebugDetailAvailable":true}},"ScopePriceBands":[{"MinUnitPrice":90,"MaxUnitPrice":110,"WeightedAverageUnitPrice":99.5,"TotalQuantity":40,"ListingCount":8,"DistinctWorldCount":2,"DistinctRetainerCount":3,"Competitiveness":2,"Depth":3,"BreakPercentToNextBand":0.5}],"Worlds":[{"DataCenter":"Aether","WorldName":"Siren","QuantityNeeded":2,"PrimaryUsableQuantity":30,"PriceSignalQuantity":25,"ScopeSaneQuantity":20,"ScopeUncompetitiveQuantity":5,"ScopeInsaneQuantity":1,"TotalSaneQuantity":26,"TotalListingQuantity":40,"ActionableQuantity":20,"ActionableAverageUnitPrice":100,"ComparableQuantity":25,"ComparableAverageUnitPrice":99.75,"ActionableCostToCoverTotalGil":200,"ActionableCostToCoverUnitPrice":100,"ActionableCostToCoverMaxUnitPrice":100,"WorldAverageUnitPrice":102.5,"ReferenceSupportScore":0.91,"ReferencePriceCredibility":3,"CostToCoverTotalGil":200,"CostToCoverUnitPrice":100,"CostToCoverMaxUnitPrice":100,"PrimaryUsableCoverageRatio":0.75,"PriceSignalCoverageRatio":0.625,"ScopeSaneCoverageRatio":0.5,"SaneCoverageRatio":0.65,"AnalysisScopeBaselineUnitPrice":101.25,"AnalysisScopeAverageUnitPrice":102.5,"AnalysisCompetitiveAverageUnitPrice":99.75,"PrimaryUsableAverageUnitPrice":100,"PriceSignalAverageUnitPrice":99.75,"AnalysisScopeMedianUnitPrice":101,"CompetitiveThresholdUnitPrice":110,"SaneThresholdUnitPrice":150,"CoverageBucket":0,"PriceSignalDepth":3,"FetchedAtUtc":"2026-07-20T08:59:00Z","MarketUploadedAtUtc":"2026-07-20T08:55:00Z","DataAgeSource":0,"DataAge":"00:05:00","DataQualityScore":87.5,"DataQualityBucket":1,"PriceBands":[{"FirstListingIndex":0,"LastListingIndex":7,"MinUnitPrice":90,"MaxUnitPrice":110,"WeightedAverageUnitPrice":99.5,"ListingCount":8,"Quantity":40,"NextBreakPercent":0.5,"Competitiveness":2,"Depth":3,"IsPriceSignalBand":true,"IsPrimaryUsableBand":true}],"Listings":[{"SortIndex":0,"Quantity":3,"PricePerUnit":100,"RetainerName":"Frozen Retainer","IsHq":true,"PriceSanity":0,"Competitiveness":2,"IsInPriceSignalBand":true,"IsInPrimaryUsableBand":true,"LastReviewTimeUtc":"2026-07-20T08:54:00Z"}],"Scores":[{"Lens":1,"Score":98.75,"Rank":1,"ScoreBucket":0,"Summary":"frozen score"}]}],"Warning":"frozen warning"}],"Recommendations":[{"ItemId":100,"Name":"Varnish","IconId":9876,"QuantityNeeded":2,"HqQuantityNeeded":2,"DCAveragePrice":102.5,"WorldOptions":[{"DataCenter":"Aether","WorldName":"Siren","WorldId":57,"TotalCost":300,"AveragePricePerUnit":100,"ListingsUsed":1,"Listings":[{"Quantity":3,"PricePerUnit":100,"RetainerName":"Frozen Retainer","IsUnderAverage":true,"IsHq":true,"NeededFromStack":2,"ExcessQuantity":1,"IsAdditionalOption":false}],"ExcludedListings":[{"Quantity":99,"PricePerUnit":999,"RetainerName":"Excluded Retainer","IsUnderAverage":false,"IsHq":false,"NeededFromStack":0,"ExcessQuantity":99,"IsAdditionalOption":true}],"IsFullyUnderAverage":true,"TotalQuantityPurchased":3,"ExcessQuantity":1,"ModePricePerUnit":100,"ValueScore":150,"MarketDataQualityScore":87.5,"MarketDataQualityBucket":1,"MarketDataAgeSource":0,"MarketDataAge":"00:05:00","MarketUploadedAtUtc":"2026-07-20T08:55:00Z","LensRank":1,"LensScoreBucket":0,"ProcurementPriorityScore":315,"VendorName":null,"HasSufficientStock":true,"ShortfallQuantity":0,"BestSingleListing":{"Quantity":3,"PricePerUnit":100,"RetainerName":"Frozen Retainer","IsUnderAverage":true,"IsHq":true,"NeededFromStack":2,"ExcessQuantity":1,"IsAdditionalOption":false},"Classification":1,"IsHomeWorld":true,"IsBlacklisted":false,"IsTravelProhibited":false,"CongestedWarning":null}],"RecommendedWorld":{"DataCenter":"Aether","WorldName":"Siren","WorldId":57,"TotalCost":300,"AveragePricePerUnit":100,"ListingsUsed":1,"Listings":[{"Quantity":3,"PricePerUnit":100,"RetainerName":"Frozen Retainer","IsUnderAverage":true,"IsHq":true,"NeededFromStack":2,"ExcessQuantity":1,"IsAdditionalOption":false}],"ExcludedListings":[],"IsFullyUnderAverage":true,"TotalQuantityPurchased":3,"ExcessQuantity":1,"ModePricePerUnit":100,"ValueScore":150,"MarketDataQualityScore":87.5,"MarketDataQualityBucket":1,"MarketDataAgeSource":0,"MarketDataAge":"00:05:00","MarketUploadedAtUtc":"2026-07-20T08:55:00Z","LensRank":1,"LensScoreBucket":0,"ProcurementPriorityScore":315,"VendorName":null,"HasSufficientStock":true,"ShortfallQuantity":0,"BestSingleListing":{"Quantity":3,"PricePerUnit":100,"RetainerName":"Frozen Retainer","IsUnderAverage":true,"IsHq":true,"NeededFromStack":2,"ExcessQuantity":1,"IsAdditionalOption":false},"Classification":1,"IsHomeWorld":true,"IsBlacklisted":false,"IsTravelProhibited":false,"CongestedWarning":null},"CoverageSet":{"ItemId":100,"ItemName":"Varnish","QuantityNeeded":2,"SingleWorld":{"CandidateId":"single-aether-siren","Tier":0,"Kind":0,"QualityPolicy":1,"QuantityCovered":2,"QuantityToPurchase":3,"ExcessQuantity":1,"ExactNeededCost":200,"CashOutCost":300,"AverageUnitCost":100,"PriceBand":2,"Worlds":[{"DataCenter":"Aether","WorldName":"Siren","QuantityCovered":2,"QuantityToPurchase":3,"ExactNeededCost":200,"CashOutCost":300}],"Listings":[{"DataCenter":"Aether","WorldName":"Siren","QuantityAvailable":3,"QuantityUsed":2,"QuantityPurchased":3,"PricePerUnit":100,"IsHq":true}],"Friction":{"WorldCount":1,"DataCenterCount":1,"SmallestContribution":2,"LargestContribution":2,"ExcessQuantity":1},"Savings":{"VersusSingleWorld":25,"VersusSingleWorldPercent":7.5},"IsDefaultEligible":true,"DegradedReason":null},"CompactSplit":null,"WideSplit":null,"CheapestObserved":null,"AllCandidates":[{"CandidateId":"single-aether-siren","Tier":0,"Kind":0,"QualityPolicy":1,"QuantityCovered":2,"QuantityToPurchase":3,"ExcessQuantity":1,"ExactNeededCost":200,"CashOutCost":300,"AverageUnitCost":100,"PriceBand":2,"Worlds":[{"DataCenter":"Aether","WorldName":"Siren","QuantityCovered":2,"QuantityToPurchase":3,"ExactNeededCost":200,"CashOutCost":300}],"Listings":[{"DataCenter":"Aether","WorldName":"Siren","QuantityAvailable":3,"QuantityUsed":2,"QuantityPurchased":3,"PricePerUnit":100,"IsHq":true}],"Friction":{"WorldCount":1,"DataCenterCount":1,"SmallestContribution":2,"LargestContribution":2,"ExcessQuantity":1},"Savings":{"VersusSingleWorld":25,"VersusSingleWorldPercent":7.5},"IsDefaultEligible":true,"DegradedReason":null}]},"Error":null,"MarketDataWarning":"frozen market warning","HQAveragePrice":120.25,"Vendors":[{"name":"Material Supplier","location":"Mist","price":250,"currency":"gil","coordinates":[10.5,12.25]}],"RecommendedSplit":[{"DataCenter":"Aether","WorldName":"Siren","QuantityToBuy":2,"PricePerUnit":100,"EffectivePricePerNeededUnit":150,"TotalCost":300,"IsPartial":false,"TravelContext":"Primary","ExcessAvailable":1,"Listings":[{"Quantity":3,"PricePerUnit":100,"RetainerName":"Frozen Retainer","IsUnderAverage":true,"IsHq":true,"NeededFromStack":2,"ExcessQuantity":1,"IsAdditionalOption":false}]}]}],"UnavailableMarketItems":[{"ItemId":404,"Name":"Missing Dye"}],"PublicationContext":{"Kind":2,"Scope":1,"SelectedDataCenter":"Aether","SelectedRegion":"North America","RequestedDataCenters":["Aether","Primal"],"ExpectedWorldsByDataCenter":{"Aether":["Siren"],"Primal":["Leviathan"]},"MaxAge":"00:15:00","ForceRefreshData":true,"RecommendationMode":2,"Lens":1,"CoreVersionStamp":{"PlanSession":21,"PlanCore":22,"PlanDecision":23,"PlanPrice":24,"MarketAnalysis":25,"Procurement":26,"SettingsContext":27,"ViewState":28},"WebPlanSessionVersion":31,"WebMarketAnalysisVersion":32,"PublishedAtUtc":"2026-07-20T10:00:00Z"},"RecipeBasis":{"SchemaVersion":2,"Metadata":{"PlanSessionVersion":11,"PlanStructureVersion":12,"PlanDecisionVersion":13,"PlanPriceVersion":14,"SettingsVersion":15,"RecipeDataIdentity":"garland-contract-1","CompletedAtUtc":"2026-07-20T10:00:00Z","NodeCount":1,"UniqueItemIdCount":2,"DiagnosticCount":3},"Operations":[{"NodeId":"root","ParentNodeId":null,"AncestorNodeIds":[],"Depth":0,"ResultItemId":100,"ResultItemName":"Varnish","RequestedQuantity":2,"Source":1,"SourceReason":2,"MustBeHq":true,"CanCraft":true,"State":0,"SuppressedByNodeId":null,"SuppressedByItemName":null,"Kind":0,"RecipeId":1234,"JobId":8,"JobName":"Carpenter","RecipeLevel":90,"RecipeDisplayLevel":100,"RecipeUnlockItemId":777,"Yield":2,"CraftCount":1,"Ingredients":[{"ItemId":101,"Name":"Beeswax","AmountPerCraft":3,"TotalQuantity":3,"ChildNodeId":"wax","ChildSource":4,"ChildCanCraft":false,"LinkStatus":1,"ExpectedTotalQuantity":3,"PlanChildQuantity":3}],"ResolutionConfidence":1,"RecipeDataSource":1,"HasStructuralDiagnostics":true}],"MarketAnalysisDemandItems":[{"ItemId":100,"Name":"Varnish","IconId":9876,"TotalQuantity":2,"RequiresHq":true}],"UnavailableMarketItemIds":[404]}}""";
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
    private static void AssertValues(params (object? Expected, object? Actual)[] values)
    {
        foreach (var (expected, actual) in values)
        {
            if (expected is int expectedInt && actual is long actualLong)
            {
                Assert.Equal((long)expectedInt, actualLong);
                continue;
            }
            Assert.Equal(expected, actual);
        }
    }
}
