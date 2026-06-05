using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CorePlanSessionLoadService
{
    private readonly CraftSessionState _session;

    public CorePlanSessionLoadService(CraftSessionState session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public CorePlanSessionLoadResult Load(
        CoreStoredPlanSnapshot storedPlan,
        bool trackStoredPlanIdentity = true)
    {
        var result = Prepare(storedPlan);
        if (!result.CanLoad)
        {
            return result;
        }

        var identity = trackStoredPlanIdentity
            ? new CraftSessionIdentity(
                Guid.NewGuid(),
                storedPlan.Name,
                storedPlan.Id,
                storedPlan.Name)
            : new CraftSessionIdentity(
                Guid.NewGuid(),
                storedPlan.SourcePlanName ?? storedPlan.Name,
                storedPlan.SourcePlanId,
                storedPlan.SourcePlanName);

        _session.ActivatePlan(
            result.Plan,
            result.ProjectItems,
            new CraftSessionActiveContext(
                MarketFetchScopeResolver.ResolveRegionForDataCenter(storedPlan.DataCenter, "North America"),
                storedPlan.DataCenter,
                result.Plan?.World,
                MarketFetchScope.SelectedDataCenter),
            "stored session loaded",
            identity);

        if (result.Plan != null &&
            (result.MarketItemAnalyses.Count > 0 || result.UnavailableMarketItemIds.Count > 0))
        {
            _session.TryPublishMarketAnalysis(
                _session.CaptureVersionStamp(),
                _session.ActivePlan!,
                _session.PlanSessionVersion,
                result.MarketItemAnalyses,
                result.ShoppingPlans,
                acquisitionDecisionsChanged: false,
                "stored market analysis restored",
                result.UnavailableMarketItemIds,
                result.MarketIntelligence?.RecommendationMode ?? storedPlan.SavedRecommendationMode,
                result.MarketIntelligence?.Lens ?? storedPlan.SavedMarketAnalysisLens,
                result.MarketAnalysisRecipeBasis);
        }

        return result;
    }

    public static CorePlanSessionLoadResult Prepare(CoreStoredPlanSnapshot storedPlan)
    {
        ArgumentNullException.ThrowIfNull(storedPlan);

        if (storedPlan.SchemaVersion > CoreStoredPlanSnapshot.CurrentSchemaVersion)
        {
            return new CorePlanSessionLoadResult(
                storedPlan,
                null,
                Array.Empty<ProjectItem>(),
                Array.Empty<MarketItemAnalysis>(),
                Array.Empty<DetailedShoppingPlan>(),
                new HashSet<int>(),
                null,
                null,
                CanLoad: false,
                $"Stored plan '{storedPlan.Name}' uses newer session schema version {storedPlan.SchemaVersion}; this app supports version {CoreStoredPlanSnapshot.CurrentSchemaVersion}.");
        }

        CraftingPlan? plan = null;
        string? warning = storedPlan.SchemaVersion < CoreStoredPlanSnapshot.CurrentSchemaVersion
            ? $"Stored plan '{storedPlan.Name}' uses older session schema version {storedPlan.SchemaVersion}; loaded with compatibility defaults for version {CoreStoredPlanSnapshot.CurrentSchemaVersion}."
            : null;
        if (!string.IsNullOrWhiteSpace(storedPlan.PlanJson))
        {
            try
            {
                plan = JsonSerializer.Deserialize<CraftingPlan>(storedPlan.PlanJson);
                RestoreParentLinks(plan);
            }
            catch (Exception ex)
            {
                warning = AppendWarning(warning, $"Could not load full plan data: {ex.Message}");
            }
        }

        var projectItems = storedPlan.ProjectItems.Select(item => new ProjectItem
        {
            Id = item.Id,
            Name = item.Name,
            IconId = item.IconId,
            Quantity = item.Quantity,
            MustBeHq = item.MustBeHq
        }).ToList();

        var marketRestore = StoredMarketIntelligenceRestorer.Restore(
            new StoredMarketIntelligenceRestoreInput(
                storedPlan.MarketIntelligenceJson,
                storedPlan.MarketItemAnalysesJson,
                storedPlan.MarketPlansJson,
                storedPlan.MarketAnalysisRecipeBasisJson,
                storedPlan.UnavailableMarketItemIds,
                storedPlan.SavedRecommendationMode,
                storedPlan.SavedMarketAnalysisLens,
                plan,
                projectItems,
                BuildMarketAnalysisCandidates));
        warning = AppendWarning(warning, marketRestore.Warning);
        var marketIntelligenceSummary = DeserializeMarketIntelligenceSummary(
            storedPlan.MarketIntelligenceSummaryJson,
            out var summaryWarning);
        warning = AppendWarning(warning, summaryWarning);
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

        var marketItemAnalyses = marketRestore.MarketItemAnalyses.Count > 0
            ? marketRestore.MarketItemAnalyses
            : HydrateMarketItemAnalyses(marketIntelligenceSummary);
        var recommendations = marketRestore.Recommendations.Count > 0
            ? marketRestore.Recommendations
            : HydrateShoppingPlans(marketIntelligenceSummary);
        var unavailableMarketItemIds = marketRestore.UnavailableMarketItemIds.Count > 0
            ? marketRestore.UnavailableMarketItemIds
            : marketIntelligenceSummary?.UnavailableMarketItems.Select(item => item.ItemId).ToHashSet()
              ?? new HashSet<int>();
        var marketIntelligence = marketRestore.MarketIntelligence;
        if (marketIntelligence == null &&
            (marketItemAnalyses.Count > 0 || recommendations.Count > 0 || unavailableMarketItemIds.Count > 0) &&
            marketIntelligenceSummary != null)
        {
            marketIntelligence = new MarketIntelligence(
                marketIntelligenceSummary.PublicationId,
                marketItemAnalyses,
                recommendations,
                marketIntelligenceSummary.UnavailableMarketItems,
                marketIntelligenceSummary.PublicationContext,
                marketRestore.RecipeBasis);
        }

        return new CorePlanSessionLoadResult(
            storedPlan,
            plan,
            projectItems,
            marketItemAnalyses,
            recommendations,
            unavailableMarketItemIds,
            marketIntelligence,
            marketRestore.RecipeBasis,
            CanLoad: true,
            warning);
    }

    private static string? AppendWarning(string? existingWarning, string? warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return existingWarning;
        }

        return string.IsNullOrWhiteSpace(existingWarning)
            ? warning
            : $"{existingWarning} {warning}";
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

    private static IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan)
    {
        return new RecipeDemandProjectionService()
            .Build(plan, snapshot: null)
            .ToMarketAnalysisMaterialAggregates();
    }

    private static MarketIntelligencePublicationSummary? DeserializeMarketIntelligenceSummary(
        string? json,
        out string? warning)
    {
        warning = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var summary = JsonSerializer.Deserialize<MarketIntelligencePublicationSummary>(json);
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
        catch (JsonException ex)
        {
            warning = $"Stored market intelligence summary could not be deserialized: {ex.Message}";
            return null;
        }
        catch (NotSupportedException ex)
        {
            warning = $"Stored market intelligence summary could not be deserialized: {ex.Message}";
            return null;
        }
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
}
