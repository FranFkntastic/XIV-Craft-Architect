using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed record StoredMarketIntelligenceRestoreInput(
    string? MarketIntelligenceJson,
    string? LegacyMarketItemAnalysesJson,
    string? LegacyMarketPlansJson,
    string? LegacyMarketAnalysisRecipeBasisJson,
    IReadOnlySet<int> LegacyUnavailableMarketItemIds,
    RecommendationMode LegacyRecommendationMode,
    MarketAcquisitionLens LegacyLens,
    CraftingPlan? Plan,
    IReadOnlyList<ProjectItem> ProjectItems,
    Func<CraftingPlan?, IReadOnlyList<MaterialAggregate>> BuildMarketAnalysisCandidates);

public sealed record StoredMarketIntelligenceRestoreResult(
    IReadOnlyList<MarketItemAnalysis> MarketItemAnalyses,
    IReadOnlyList<DetailedShoppingPlan> Recommendations,
    IReadOnlySet<int> UnavailableMarketItemIds,
    MarketIntelligence? MarketIntelligence,
    StoredRecipeOperationSnapshot? RecipeBasis,
    string? Warning);

public static class StoredMarketIntelligenceRestorer
{
    public static StoredMarketIntelligenceRestoreResult Restore(
        StoredMarketIntelligenceRestoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.ProjectItems);
        ArgumentNullException.ThrowIfNull(input.BuildMarketAnalysisCandidates);

        string? warning = null;
        var marketIntelligence = DeserializeMarketIntelligence(
            input.MarketIntelligenceJson,
            out var hasMarketIntelligenceRecipeBasisPayload,
            out var hasLegacyCoverageCostSemantics,
            out var marketIntelligenceWarning);
        warning = AppendWarning(warning, marketIntelligenceWarning);

        var marketAnalyses = marketIntelligence?.ItemAnalyses.ToList()
            ?? DeserializeOrEmpty<MarketItemAnalysis>(input.LegacyMarketItemAnalysesJson);
        var unavailableMarketItemIds = marketIntelligence?.UnavailableMarketItemIds.ToHashSet()
            ?? input.LegacyUnavailableMarketItemIds.ToHashSet();
        var hasUnavailableOnlyMarketIntelligence = marketIntelligence?.HasUnavailableMarketItems == true &&
                                                   marketAnalyses.Count == 0;
        var hasRecipeBasisPayload = hasMarketIntelligenceRecipeBasisPayload ||
                                    !string.IsNullOrWhiteSpace(input.LegacyMarketAnalysisRecipeBasisJson);
        var recipeBasis = marketIntelligence?.RecipeBasis;
        if (!hasMarketIntelligenceRecipeBasisPayload)
        {
            recipeBasis = StoredRecipeBasisMapper.TryDeserialize(
                input.LegacyMarketAnalysisRecipeBasisJson,
                out var recipeBasisWarning);
            warning = AppendWarning(warning, recipeBasisWarning);
        }

        if (!hasUnavailableOnlyMarketIntelligence &&
            marketIntelligence == null &&
            ContainsLegacyListingOutlierField(input.LegacyMarketItemAnalysesJson))
        {
            marketAnalyses.Clear();
            recipeBasis = null;
            unavailableMarketItemIds.Clear();
            marketIntelligence = null;
        }
        else if (!hasUnavailableOnlyMarketIntelligence && hasRecipeBasisPayload)
        {
            if (recipeBasis == null ||
                !RestoredMarketAnalysisMatchesRecipeBasis(recipeBasis, marketAnalyses))
            {
                marketAnalyses.Clear();
                recipeBasis = null;
                unavailableMarketItemIds.Clear();
                marketIntelligence = null;
            }
        }
        else if (!hasUnavailableOnlyMarketIntelligence &&
                 !RestoredMarketAnalysisMatchesPlan(
                     input.Plan,
                     input.ProjectItems,
                     marketAnalyses,
                     input.BuildMarketAnalysisCandidates))
        {
            marketAnalyses.Clear();
            unavailableMarketItemIds.Clear();
            marketIntelligence = null;
        }

        var restoredRecommendations = marketIntelligence?.Recommendations.ToList()
            ?? DeserializeOrEmpty<DetailedShoppingPlan>(input.LegacyMarketPlansJson);
        var recommendations = marketAnalyses.Count > 0 &&
                              RestoredShoppingPlansMatchMarketAnalysis(restoredRecommendations, marketAnalyses)
            ? restoredRecommendations
            : new List<DetailedShoppingPlan>();
        EnsureCoverageState(
            recommendations,
            legacyCoverage: marketIntelligence == null || hasLegacyCoverageCostSemantics);

        if (marketAnalyses.Count == 0 && marketIntelligence?.HasUnavailableMarketItems != true)
        {
            recipeBasis = null;
            unavailableMarketItemIds.Clear();
            marketIntelligence = null;
        }
        else if (marketIntelligence != null)
        {
            var hasRestoredMarketPayload = marketAnalyses.Count > 0 ||
                                           recommendations.Count > 0 ||
                                           unavailableMarketItemIds.Count > 0;

            marketIntelligence = marketIntelligence with
            {
                ItemAnalyses = marketAnalyses.ToArray(),
                Recommendations = recommendations.ToArray(),
                UnavailableMarketItems = marketIntelligence.UnavailableMarketItems
                    .Where(item => unavailableMarketItemIds.Contains(item.ItemId))
                    .ToArray(),
                PublicationContext = RepairMissingPublicationContext(
                    marketIntelligence.PublicationContext,
                    hasRestoredMarketPayload,
                    input.LegacyRecommendationMode,
                    input.LegacyLens),
                RecipeBasis = recipeBasis
            };
        }
        else if (marketAnalyses.Count > 0 || unavailableMarketItemIds.Count > 0)
        {
            marketIntelligence = MarketIntelligence.FromLegacy(
                marketAnalyses,
                recommendations,
                unavailableMarketItemIds,
                publishedAgainstVersion: null,
                input.LegacyRecommendationMode,
                input.LegacyLens,
                recipeBasis);
        }

        return new StoredMarketIntelligenceRestoreResult(
            marketAnalyses,
            recommendations,
            unavailableMarketItemIds,
            marketIntelligence,
            recipeBasis,
            warning);
    }

    private static void EnsureCoverageState(IReadOnlyList<DetailedShoppingPlan> plans, bool legacyCoverage)
    {
        foreach (var plan in plans)
        {
            if (plan.CoverageSet != null)
            {
                continue;
            }

            plan.CoverageSet = legacyCoverage
                ? CreateLegacyDegradedCoverage(plan)
                : MarketCoverageBuilder.Build(plan);
        }
    }

    private static MarketCoverageSet CreateLegacyDegradedCoverage(DetailedShoppingPlan plan)
    {
        var projectedCost = plan.RecommendedWorld?.TotalCost ??
                            plan.SplitTotalCost ??
                            0;
        var option = new MarketCoverageOption(
            $"legacy-degraded-{plan.ItemId}-{plan.QuantityNeeded}",
            MarketCoverageTier.CheapestObserved,
            MarketCoverageKind.ProjectedAverage,
            MarketCoverageQualityPolicy.NqOrHq,
            plan.QuantityNeeded,
            plan.QuantityNeeded,
            ExcessQuantity: 0,
            projectedCost,
            projectedCost,
            plan.QuantityNeeded > 0 ? projectedCost / (decimal)plan.QuantityNeeded : 0,
            MarketCoveragePriceBand.Unknown,
            Array.Empty<MarketCoverageWorld>(),
            Array.Empty<MarketCoverageListing>(),
            new MarketCoverageFriction(
                WorldCount: 0,
                DataCenterCount: 0,
                SmallestContribution: 0,
                LargestContribution: 0,
                ExcessQuantity: 0),
            MarketCoverageSavings.None,
            IsDefaultEligible: false,
            DegradedReason: "Legacy market intelligence did not include coverage candidates.");

        return new MarketCoverageSet(
            plan.ItemId,
            plan.Name,
            plan.QuantityNeeded,
            SingleWorld: null,
            CompactSplit: null,
            WideSplit: null,
            CheapestObserved: option,
            AllCandidates: [option]);
    }

    private static MarketIntelligencePublicationContext RepairMissingPublicationContext(
        MarketIntelligencePublicationContext context,
        bool hasRestoredMarketPayload,
        RecommendationMode legacyRecommendationMode,
        MarketAcquisitionLens legacyLens)
    {
        if (!hasRestoredMarketPayload ||
            context.Kind != MarketIntelligencePublicationContextKind.None)
        {
            return context;
        }

        return MarketIntelligencePublicationContext.UnknownLegacy(
            legacyRecommendationMode,
            legacyLens);
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

    private static List<T> DeserializeOrEmpty<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static MarketIntelligence? DeserializeMarketIntelligence(
        string? json,
        out bool hasRecipeBasisPayload,
        out bool hasLegacyCoverageCostSemantics,
        out string? warning)
    {
        hasRecipeBasisPayload = false;
        hasLegacyCoverageCostSemantics = false;
        warning = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var stored = MarketIntelligencePayloadCodec.Deserialize(json);
            if (stored == null)
            {
                warning = "Stored market intelligence payload was empty.";
                return null;
            }

            if (stored.SchemaVersion != StoredMarketIntelligence.CurrentSchemaVersion)
            {
                warning = stored.SchemaVersion > StoredMarketIntelligence.CurrentSchemaVersion
                    ? "Stored market intelligence was saved with a newer schema version."
                    : "Stored market intelligence was saved with an obsolete schema version.";
                return null;
            }

            hasRecipeBasisPayload = stored.RecipeBasis != null;
            hasLegacyCoverageCostSemantics =
                stored.CoverageCostSemanticsVersion < StoredMarketIntelligence.CurrentCoverageCostSemanticsVersion;
            var recipeBasis = StoredRecipeBasisMapper.TryNormalize(stored.RecipeBasis, out var recipeBasisWarning);
            warning = AppendWarning(warning, recipeBasisWarning);

            return stored.ToMarketIntelligence() with
            {
                RecipeBasis = recipeBasis
            };
        }
        catch (JsonException ex)
        {
            warning = $"Stored market intelligence could not be deserialized: {ex.Message}";
            return null;
        }
        catch (NotSupportedException ex)
        {
            warning = $"Stored market intelligence could not be deserialized: {ex.Message}";
            return null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            warning = $"Stored market intelligence could not be deserialized: {ex.Message}";
            return null;
        }
    }

    private static bool ContainsLegacyListingOutlierField(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }
        if (MarketIntelligencePayloadCodec.IsCompressed(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ContainsLegacyListingOutlierField(document.RootElement);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsLegacyListingOutlierField(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Any(property =>
                string.Equals(property.Name, "IsOutlier", StringComparison.OrdinalIgnoreCase) ||
                ContainsLegacyListingOutlierField(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Any(ContainsLegacyListingOutlierField),
            _ => false
        };
    }

    private static bool RestoredMarketAnalysisMatchesPlan(
        CraftingPlan? plan,
        IReadOnlyList<ProjectItem> projectItems,
        IReadOnlyList<MarketItemAnalysis> analyses,
        Func<CraftingPlan?, IReadOnlyList<MaterialAggregate>> buildMarketAnalysisCandidates)
    {
        if (analyses.Count == 0)
        {
            return false;
        }

        var candidates = plan != null
            ? buildMarketAnalysisCandidates(plan)
            : projectItems
                .Where(item => item.Quantity > 0)
                .Select(item => new MaterialAggregate
                {
                    ItemId = item.Id,
                    Name = item.Name,
                    IconId = item.IconId,
                    TotalQuantity = item.Quantity
                })
                .ToList();
        var expected = candidates.ToDictionary(candidate => candidate.ItemId, candidate => candidate.TotalQuantity);

        return expected.Count == analyses.Count &&
               analyses.All(analysis =>
                   expected.TryGetValue(analysis.ItemId, out var quantityNeeded) &&
                   quantityNeeded == analysis.QuantityNeeded);
    }

    private static bool RestoredMarketAnalysisMatchesRecipeBasis(
        StoredRecipeOperationSnapshot recipeBasis,
        IReadOnlyList<MarketItemAnalysis> analyses)
    {
        if (analyses.Count == 0)
        {
            return false;
        }

        var expected = recipeBasis.MarketAnalysisDemandItems
            .ToDictionary(item => item.ItemId, item => item.TotalQuantity);
        var analyzedItemIds = analyses
            .Select(analysis => analysis.ItemId)
            .ToHashSet();

        return analyses.All(analysis =>
                   expected.TryGetValue(analysis.ItemId, out var quantityNeeded) &&
                   quantityNeeded == analysis.QuantityNeeded) &&
               expected.Keys.All(itemId =>
                   analyzedItemIds.Contains(itemId) ||
                   recipeBasis.UnavailableMarketItemIds.Contains(itemId));
    }

    private static bool RestoredShoppingPlansMatchMarketAnalysis(
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        IReadOnlyList<MarketItemAnalysis> analyses)
    {
        if (shoppingPlans.Count == 0)
        {
            return false;
        }

        var expected = analyses.ToDictionary(analysis => analysis.ItemId, analysis => analysis.QuantityNeeded);
        return expected.Count == shoppingPlans.Count &&
               shoppingPlans.All(plan =>
                   expected.TryGetValue(plan.ItemId, out var quantityNeeded) &&
                   quantityNeeded == plan.QuantityNeeded);
    }
}
