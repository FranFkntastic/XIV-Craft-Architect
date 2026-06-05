using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class PlanSessionLoadService
{
    private readonly AppState _appState;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;

    public PlanSessionLoadService(
        AppState appState,
        IRecipeLayerWorkflowService? recipeLayerWorkflow = null)
    {
        _appState = appState;
        _recipeLayerWorkflow = recipeLayerWorkflow ?? new LightweightRecipeLayerWorkflowService();
    }

    public PlanSessionLoadResult Load(StoredPlan storedPlan, bool trackStoredPlanIdentity = true)
    {
        var result = PrepareSession(storedPlan);
        _appState.ApplyLoadedPlanSession(result, trackStoredPlanIdentity);
        return result;
    }

    public PlanSessionLoadResult PrepareSession(StoredPlan storedPlan)
    {
        return Prepare(
            storedPlan,
            deserializedPlan: null,
            buildMarketAnalysisCandidates: _recipeLayerWorkflow.BuildMarketAnalysisCandidates);
    }

    public static PlanSessionLoadResult Prepare(StoredPlan storedPlan)
    {
        return Prepare(storedPlan, deserializedPlan: null);
    }

    public static PlanSessionLoadResult Prepare(StoredPlan storedPlan, CraftingPlan? deserializedPlan)
    {
        return Prepare(
            storedPlan,
            deserializedPlan,
            buildMarketAnalysisCandidates: BuildLightweightMarketAnalysisCandidates);
    }

    private static PlanSessionLoadResult Prepare(
        StoredPlan storedPlan,
        CraftingPlan? deserializedPlan,
        Func<CraftingPlan?, IReadOnlyList<MaterialAggregate>> buildMarketAnalysisCandidates)
    {
        CraftingPlan? plan = null;
        string? warning = null;

        if (deserializedPlan != null)
        {
            plan = deserializedPlan;
        }
        else if (!string.IsNullOrWhiteSpace(storedPlan.PlanJson))
        {
            try
            {
                plan = JsonSerializer.Deserialize<CraftingPlan>(storedPlan.PlanJson);
                RestoreParentLinks(plan);
            }
            catch (Exception ex)
            {
                warning = $"Could not load full plan data: {ex.Message}";
            }
        }

        var projectItems = storedPlan.ProjectItems.Select(p => new ProjectItem
        {
            Id = p.Id,
            Name = p.Name,
            IconId = p.IconId,
            Quantity = p.Quantity,
            MustBeHq = p.MustBeHq
        }).ToList();

        var marketRestore = StoredMarketIntelligenceRestorer.Restore(
            new StoredMarketIntelligenceRestoreInput(
                MarketIntelligenceJson: storedPlan.MarketIntelligenceJson,
                LegacyMarketItemAnalysesJson: storedPlan.MarketItemAnalysesJson,
                LegacyMarketPlansJson: storedPlan.MarketPlansJson,
                LegacyMarketAnalysisRecipeBasisJson: storedPlan.MarketAnalysisRecipeBasisJson,
                LegacyUnavailableMarketItemIds: new HashSet<int>(),
                LegacyRecommendationMode: storedPlan.SavedRecommendationMode,
                LegacyLens: storedPlan.SavedMarketAnalysisLens,
                Plan: plan,
                ProjectItems: projectItems,
                BuildMarketAnalysisCandidates: buildMarketAnalysisCandidates));
        warning = AppendWarning(warning, marketRestore.Warning);
        var marketIntelligenceSummary = DeserializeMarketIntelligenceSummary(
            storedPlan.MarketIntelligenceSummaryJson,
            out var marketIntelligenceSummaryWarning);
        warning = AppendWarning(warning, marketIntelligenceSummaryWarning);
        if (marketIntelligenceSummary != null &&
            storedPlan.ActiveMarketIntelligencePublicationId is { } activePublicationId &&
            activePublicationId != Guid.Empty &&
            marketIntelligenceSummary.PublicationId != activePublicationId)
        {
            warning = AppendWarning(
                warning,
                "Stored market intelligence summary does not match the active publication reference.");
            marketIntelligenceSummary = null;
        }

        var publishedScope = ResolvePublishedScope(storedPlan, marketRestore, marketIntelligenceSummary);
        var marketIntelligence = ApplyPublishedScopeFallback(
            marketRestore.MarketIntelligence,
            publishedScope);
        var marketItemAnalyses = marketRestore.MarketItemAnalyses.Count > 0
            ? marketRestore.MarketItemAnalyses
            : HydrateMarketItemAnalyses(marketIntelligenceSummary);
        var recommendations = marketRestore.Recommendations.Count > 0
            ? marketRestore.Recommendations
            : HydrateShoppingPlans(marketIntelligenceSummary);

        return new PlanSessionLoadResult(
            storedPlan,
            plan,
            projectItems,
            marketItemAnalyses,
            recommendations,
            marketIntelligence,
            marketIntelligenceSummary,
            marketRestore.RecipeBasis,
            publishedScope,
            warning);
    }

    private static string? AppendWarning(string? existingWarning, string? newWarning)
    {
        if (string.IsNullOrWhiteSpace(newWarning))
        {
            return existingWarning;
        }

        if (string.IsNullOrWhiteSpace(existingWarning))
        {
            return newWarning;
        }

        return $"{existingWarning} {newWarning}";
    }

    private static PublishedMarketAnalysisScopeSnapshot? ResolvePublishedScope(
        StoredPlan storedPlan,
        StoredMarketIntelligenceRestoreResult marketRestore,
        MarketIntelligencePublicationSummary? marketIntelligenceSummary)
    {
        var hasMarketPayload = marketRestore.MarketItemAnalyses.Count > 0 ||
                               marketRestore.MarketIntelligence?.HasUnavailableMarketItems == true ||
                               marketIntelligenceSummary?.Items.Count > 0 ||
                               marketIntelligenceSummary?.UnavailableMarketItems.Count > 0;
        if (!hasMarketPayload)
        {
            return null;
        }

        if (marketRestore.MarketIntelligence?.HasCompletePublicationContext == true)
        {
            return ToPublishedScope(marketRestore.MarketIntelligence.PublicationContext);
        }

        if (marketIntelligenceSummary?.PublicationContext.Kind == MarketIntelligencePublicationContextKind.Known)
        {
            return ToPublishedScope(marketIntelligenceSummary.PublicationContext);
        }

        return DeserializeOrNull<PublishedMarketAnalysisScopeSnapshot>(storedPlan.MarketAnalysisScopeSnapshotJson);
    }

    private static IReadOnlyList<MarketItemAnalysis> HydrateMarketItemAnalyses(
        MarketIntelligencePublicationSummary? summary)
    {
        if (summary == null)
        {
            return [];
        }

        return summary.Items
            .Select(item => new MarketItemAnalysis
            {
                ItemId = item.ItemId,
                Name = item.Name,
                QuantityNeeded = item.QuantityNeeded,
                Scope = item.Scope,
                LoadedAtUtc = summary.PublicationContext.PublishedAtUtc,
                AnalysisScopeBaselineUnitPrice = item.BaselineUnitPrice,
                AnalysisScopeAverageUnitPrice = item.AverageUnitPrice,
                AnalysisScopeCompetitiveAverageUnitPrice = item.CompetitiveAverageUnitPrice,
                AnalysisScopeMedianUnitPrice = item.MedianUnitPrice,
                RequestedDataCenters = summary.PublicationContext.RequestedDataCenters,
                PresentDataCenters = item.Worlds
                    .Select(world => world.World.DataCenter)
                    .Where(dataCenter => !string.IsNullOrWhiteSpace(dataCenter))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                MissingDataCenters = [],
                WorstDataQualityBucket = item.DataQualityBucket,
                Worlds = item.Worlds.Select(HydrateWorldMarketAnalysis).ToList(),
                Warning = item.Warning
            })
            .ToArray();
    }

    private static WorldMarketAnalysis HydrateWorldMarketAnalysis(WorldMarketSummary summary)
    {
        return new WorldMarketAnalysis
        {
            DataCenter = summary.World.DataCenter,
            WorldName = summary.World.WorldName,
            QuantityNeeded = summary.QuantityNeeded,
            CompetitiveQuantity = summary.CompetitiveQuantity,
            ScopeCompetitiveQuantity = summary.CompetitiveQuantity,
            ScopeSaneQuantity = summary.TotalListingQuantity,
            TotalListingQuantity = summary.TotalListingQuantity,
            CompetitiveCoverageRatio = summary.CompetitiveCoverageRatio,
            ScopeCompetitiveCoverageRatio = summary.CompetitiveCoverageRatio,
            AnalysisScopeCompetitiveAverageUnitPrice = summary.CompetitiveAverageUnitPrice,
            ScopeCompetitiveAverageUnitPrice = summary.CompetitiveAverageUnitPrice,
            CoverageBucket = summary.CoverageBucket,
            FetchedAtUtc = summary.FetchedAtUtc,
            MarketUploadedAtUtc = summary.MarketUploadedAtUtc,
            DataAge = summary.DataAge,
            DataAgeSource = summary.DataAgeSource,
            DataQualityBucket = summary.DataQualityBucket,
            Scores = summary.Scores.ToList()
        };
    }

    private static IReadOnlyList<DetailedShoppingPlan> HydrateShoppingPlans(
        MarketIntelligencePublicationSummary? summary)
    {
        if (summary == null)
        {
            return [];
        }

        return summary.Items
            .Select(item => new DetailedShoppingPlan
            {
                ItemId = item.ItemId,
                Name = item.Name,
                QuantityNeeded = item.QuantityNeeded,
                DCAveragePrice = item.AverageUnitPrice,
                WorldOptions = item.Worlds.Select(HydrateWorldShoppingSummary).ToList(),
                RecommendedWorld = item.RecommendedWorld is null
                    ? null
                    : new WorldShoppingSummary
                    {
                        DataCenter = item.RecommendedWorld.Value.DataCenter,
                        WorldName = item.RecommendedWorld.Value.WorldName,
                        TotalCost = item.RecommendedTotalCost,
                        AveragePricePerUnit = item.CompetitiveAverageUnitPrice,
                        TotalQuantityPurchased = item.QuantityNeeded
                    },
                RecommendedSplit = item.RecommendedSplit.Select(HydrateSplitPurchase).ToList(),
                MarketDataWarning = item.Warning
            })
            .ToArray();
    }

    private static WorldShoppingSummary HydrateWorldShoppingSummary(WorldMarketSummary summary)
    {
        return new WorldShoppingSummary
        {
            DataCenter = summary.World.DataCenter,
            WorldName = summary.World.WorldName,
            AveragePricePerUnit = summary.CompetitiveAverageUnitPrice,
            TotalQuantityPurchased = summary.CompetitiveQuantity
        };
    }

    private static SplitWorldPurchase HydrateSplitPurchase(MarketSplitPurchaseSummary summary)
    {
        return new SplitWorldPurchase
        {
            DataCenter = summary.World.DataCenter,
            WorldName = summary.World.WorldName,
            QuantityToBuy = summary.QuantityToBuy,
            PricePerUnit = summary.PricePerUnit,
            EffectivePricePerNeededUnit = summary.EffectivePricePerNeededUnit,
            TotalCost = summary.TotalCost,
            IsPartial = summary.IsPartial,
            TravelContext = summary.TravelContext,
            ExcessAvailable = summary.ExcessAvailable
        };
    }

    private static void RestoreParentLinks(CraftingPlan? plan)
    {
        if (plan == null)
        {
            return;
        }

        foreach (var root in plan.RootItems)
        {
            RestoreParentLinks(root, parent: null);
        }
    }

    private static void RestoreParentLinks(PlanNode node, PlanNode? parent)
    {
        node.Parent = parent;
        node.ParentNodeId = parent?.NodeId;
        foreach (var child in node.Children)
        {
            RestoreParentLinks(child, node);
        }
    }

    private static T? DeserializeOrNull<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }

    private static MarketIntelligencePublicationSummary? DeserializeMarketIntelligenceSummary(
        string? json,
        out string? warning)
    {
        warning = null;
        var summary = DeserializeOrNull<MarketIntelligencePublicationSummary>(json);
        if (summary == null)
        {
            return null;
        }

        if (summary.SchemaVersion > MarketIntelligencePublicationSummary.CurrentSchemaVersion)
        {
            warning = "Stored market intelligence summary was saved with a newer schema version.";
            return null;
        }

        return summary;
    }

    private static PublishedMarketAnalysisScopeSnapshot? ToPublishedScope(
        MarketIntelligencePublicationContext context)
    {
        if (context.Kind != MarketIntelligencePublicationContextKind.Known)
        {
            return null;
        }

        return new PublishedMarketAnalysisScopeSnapshot(
            context.Scope,
            context.SelectedDataCenter,
            context.SelectedRegion,
            context.RequestedDataCenters.ToArray(),
            context.Lens,
            context.WebPlanSessionVersion ?? 0,
            context.PublishedAtUtc);
    }

    private static MarketIntelligence? ApplyPublishedScopeFallback(
        MarketIntelligence? intelligence,
        PublishedMarketAnalysisScopeSnapshot? scope)
    {
        if (intelligence == null ||
            scope == null ||
            intelligence.HasCompletePublicationContext)
        {
            return intelligence;
        }

        return intelligence with
        {
            PublicationContext = ToPublicationContext(
                scope,
                intelligence.RecommendationMode,
                intelligence.PublicationContext.WebMarketAnalysisVersion)
        };
    }

    private static MarketIntelligencePublicationContext ToPublicationContext(
        PublishedMarketAnalysisScopeSnapshot scope,
        RecommendationMode recommendationMode,
        long? webMarketAnalysisVersion)
    {
        return new MarketIntelligencePublicationContext(
            MarketIntelligencePublicationContextKind.Known,
            scope.Scope,
            scope.SelectedDataCenter,
            scope.SelectedRegion,
            scope.RequestedDataCenters.ToArray(),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            null,
            false,
            recommendationMode,
            scope.Lens,
            null,
            scope.PlanSessionVersion,
            webMarketAnalysisVersion,
            scope.PublishedAtUtc);
    }

    private static IReadOnlyList<MaterialAggregate> BuildLightweightMarketAnalysisCandidates(CraftingPlan? plan)
    {
        return new RecipeDemandProjectionService()
            .Build(plan, snapshot: null)
            .ToMarketAnalysisMaterialAggregates();
    }
}

public sealed record PlanSessionLoadResult(
    StoredPlan StoredPlan,
    CraftingPlan? Plan,
    IReadOnlyList<ProjectItem> ProjectItems,
    IReadOnlyList<MarketItemAnalysis> MarketItemAnalyses,
    IReadOnlyList<DetailedShoppingPlan> ShoppingPlans,
    MarketIntelligence? MarketIntelligence,
    MarketIntelligencePublicationSummary? MarketIntelligenceSummary,
    StoredRecipeOperationSnapshot? MarketAnalysisRecipeBasis,
    PublishedMarketAnalysisScopeSnapshot? PublishedMarketAnalysisScope,
    string? Warning);
