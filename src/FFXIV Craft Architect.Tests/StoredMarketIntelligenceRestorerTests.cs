using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class StoredMarketIntelligenceRestorerTests
{
    [Fact]
    public void Restore_CanonicalPayloadWithInvalidRecipeBasis_DropsMarketIntelligenceAndWarns()
    {
        var result = RestoreCanonical(CreateStoredIntelligence(
            recipeBasis: CreateStoredRecipeBasis(duplicateDemand: true)));

        AssertMarketEvidenceCleared(result);
        Assert.Contains("duplicate market analysis demand item id", result.Warning);
    }

    [Fact]
    public void Restore_LegacyPayloadHydratesUnknownLegacyMarketIntelligence()
    {
        var result = RestoreLegacy(
            legacyAnalyses: [CreateAnalysis()],
            legacyPlans: [CreateShoppingPlan()],
            unavailableItemIds: new HashSet<int> { 404 },
            legacyMode: RecommendationMode.MaximizeValue,
            legacyLens: MarketAcquisitionLens.BulkValue,
            projectQuantity: 2);

        Assert.Equal(100, Assert.Single(result.MarketItemAnalyses).ItemId);
        Assert.Equal(100, Assert.Single(result.Recommendations).ItemId);
        Assert.Contains(404, result.UnavailableMarketItemIds);
        Assert.NotNull(result.MarketIntelligence);
        Assert.Equal(
            MarketIntelligencePublicationContextKind.UnknownLegacy,
            result.MarketIntelligence!.PublicationContext.Kind);
        Assert.Equal(RecommendationMode.MaximizeValue, result.MarketIntelligence.RecommendationMode);
        Assert.Equal(MarketAcquisitionLens.BulkValue, result.MarketIntelligence.Lens);
    }

    [Fact]
    public void Restore_CanonicalPayloadWithExplicitNulls_NormalizesToEmptyIntelligence()
    {
        var result = RestoreCanonical(new StoredMarketIntelligence
        {
            MarketIntelligenceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ItemAnalyses = null!,
            Recommendations = null!,
            UnavailableMarketItems = null!,
            PublicationContext = null!,
            RecipeBasis = null
        });

        AssertMarketEvidenceCleared(result);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Restore_CanonicalPayloadWithMarketDataAndNullContext_RepairsLegacySettings()
    {
        var result = RestoreCanonical(
            new StoredMarketIntelligence
            {
                MarketIntelligenceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                ItemAnalyses = [CreateAnalysis()],
                Recommendations = [CreateShoppingPlan()],
                UnavailableMarketItems = [],
                PublicationContext = null!,
                RecipeBasis = null
            },
            legacyMode: RecommendationMode.MaximizeValue,
            legacyLens: MarketAcquisitionLens.BulkValue,
            projectQuantity: 2);

        Assert.NotNull(result.MarketIntelligence);
        Assert.Equal(
            MarketIntelligencePublicationContextKind.UnknownLegacy,
            result.MarketIntelligence!.PublicationContext.Kind);
        Assert.Equal(RecommendationMode.MaximizeValue, result.MarketIntelligence.RecommendationMode);
        Assert.Equal(MarketAcquisitionLens.BulkValue, result.MarketIntelligence.Lens);
    }

    [Fact]
    public void Restore_LegacyAnalysisWithOutlierListingField_ClearsMarketEvidence()
    {
        var result = RestoreLegacy(
            legacyAnalysesJson: """
                [
                  {
                    "ItemId": 100,
                    "Name": "Item",
                    "QuantityNeeded": 2,
                    "Worlds": [
                      {
                        "WorldName": "Siren",
                        "Listings": [
                          {
                            "PricePerUnit": 10,
                            "Quantity": 1,
                            "IsOutlier": true
                          }
                        ]
                      }
                    ]
                  }
                ]
                """,
            legacyPlans: [CreateShoppingPlan()],
            projectQuantity: 2);

        AssertMarketEvidenceCleared(result);
    }

    [Theory]
    [InlineData("missing-analysis-source")]
    [InlineData("missing-analysis-json")]
    [InlineData("different-project-item")]
    [InlineData("different-project-quantity")]
    public void Restore_LegacyPayloadWithoutCompatibleDemand_ClearsMarketEvidence(string scenario)
    {
        var legacyAnalysesJson = scenario == "missing-analysis-json"
            ? "{not valid json}"
            : scenario == "missing-analysis-source"
                ? null
            : JsonSerializer.Serialize(new List<MarketItemAnalysis> { CreateAnalysis() });
        var projectItemId = scenario == "different-project-item" ? 200 : 100;
        var projectQuantity = scenario == "different-project-quantity" ? 99 : 2;

        var result = RestoreLegacy(
            legacyAnalysesJson: legacyAnalysesJson,
            legacyPlans: [CreateShoppingPlan()],
            projectItemId: projectItemId,
            projectQuantity: projectQuantity);

        AssertMarketEvidenceCleared(result);
    }

    [Fact]
    public void Restore_LegacyProjectionForDifferentAnalysis_ClearsRecommendationsButKeepsAnalysis()
    {
        var result = RestoreLegacy(
            legacyAnalyses: [CreateAnalysis()],
            legacyPlans: [CreateShoppingPlan(itemId: 200)],
            projectQuantity: 2);

        Assert.Equal(100, Assert.Single(result.MarketItemAnalyses).ItemId);
        Assert.Empty(result.Recommendations);
        Assert.NotNull(result.MarketIntelligence);
        Assert.Empty(result.MarketIntelligence!.Recommendations);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("malformed", false)]
    [InlineData("duplicate-demand", false)]
    [InlineData("extra-available-demand", false)]
    public void Restore_LegacyRecipeBasisPayload_ControlsWhetherMarketEvidenceRestores(
        string scenario,
        bool restores)
    {
        var recipeBasisJson = scenario switch
        {
            "malformed" => "{not json}",
            "duplicate-demand" => JsonSerializer.Serialize(CreateStoredRecipeBasis(duplicateDemand: true)),
            "extra-available-demand" => JsonSerializer.Serialize(CreateStoredRecipeBasis(extraDemandItemId: 200)),
            _ => JsonSerializer.Serialize(CreateStoredRecipeBasis())
        };

        var result = RestoreLegacy(
            legacyAnalyses: [CreateAnalysis()],
            legacyPlans: [CreateShoppingPlan()],
            legacyRecipeBasisJson: recipeBasisJson,
            projectQuantity: 99);

        if (restores)
        {
            Assert.Single(result.MarketItemAnalyses);
            Assert.Single(result.Recommendations);
            Assert.NotNull(result.RecipeBasis);
        }
        else
        {
            AssertMarketEvidenceCleared(result);
            if (scenario == "malformed")
            {
                Assert.Contains("recipe basis", result.Warning, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static StoredMarketIntelligenceRestoreResult RestoreCanonical(
        StoredMarketIntelligence stored,
        RecommendationMode legacyMode = RecommendationMode.MinimizeTotalCost,
        MarketAcquisitionLens legacyLens = MarketAcquisitionLens.MinimumUpfrontCost,
        int projectQuantity = 1)
    {
        return Restore(
            marketIntelligenceJson: JsonSerializer.Serialize(stored),
            legacyMode: legacyMode,
            legacyLens: legacyLens,
            projectQuantity: projectQuantity);
    }

    private static StoredMarketIntelligenceRestoreResult RestoreLegacy(
        IReadOnlyList<MarketItemAnalysis>? legacyAnalyses = null,
        IReadOnlyList<DetailedShoppingPlan>? legacyPlans = null,
        IReadOnlySet<int>? unavailableItemIds = null,
        string? legacyAnalysesJson = null,
        string? legacyRecipeBasisJson = null,
        RecommendationMode legacyMode = RecommendationMode.MinimizeTotalCost,
        MarketAcquisitionLens legacyLens = MarketAcquisitionLens.MinimumUpfrontCost,
        int projectItemId = 100,
        int projectQuantity = 1)
    {
        return Restore(
            legacyAnalysesJson: legacyAnalysesJson ??
                (legacyAnalyses != null ? JsonSerializer.Serialize(legacyAnalyses) : null),
            legacyPlansJson: legacyPlans != null ? JsonSerializer.Serialize(legacyPlans) : null,
            legacyRecipeBasisJson: legacyRecipeBasisJson,
            unavailableItemIds: unavailableItemIds,
            legacyMode: legacyMode,
            legacyLens: legacyLens,
            projectItemId: projectItemId,
            projectQuantity: projectQuantity);
    }

    private static StoredMarketIntelligenceRestoreResult Restore(
        string? marketIntelligenceJson = null,
        string? legacyAnalysesJson = null,
        string? legacyPlansJson = null,
        string? legacyRecipeBasisJson = null,
        IReadOnlySet<int>? unavailableItemIds = null,
        RecommendationMode legacyMode = RecommendationMode.MinimizeTotalCost,
        MarketAcquisitionLens legacyLens = MarketAcquisitionLens.MinimumUpfrontCost,
        int projectItemId = 100,
        int projectQuantity = 1)
    {
        return StoredMarketIntelligenceRestorer.Restore(new StoredMarketIntelligenceRestoreInput(
            MarketIntelligenceJson: marketIntelligenceJson,
            LegacyMarketItemAnalysesJson: legacyAnalysesJson,
            LegacyMarketPlansJson: legacyPlansJson,
            LegacyMarketAnalysisRecipeBasisJson: legacyRecipeBasisJson,
            LegacyUnavailableMarketItemIds: unavailableItemIds ?? new HashSet<int>(),
            LegacyRecommendationMode: legacyMode,
            LegacyLens: legacyLens,
            Plan: null,
            ProjectItems:
            [
                new ProjectItem { Id = projectItemId, Name = "Item", Quantity = projectQuantity }
            ],
            BuildMarketAnalysisCandidates: _ => []));
    }

    private static StoredMarketIntelligence CreateStoredIntelligence(
        StoredRecipeOperationSnapshot? recipeBasis = null)
    {
        return StoredMarketIntelligence.FromMarketIntelligence(new MarketIntelligence(
            Guid.NewGuid(),
            [CreateAnalysis(quantityNeeded: 1)],
            [CreateShoppingPlan(quantityNeeded: 1)],
            [],
            MarketIntelligencePublicationContext.UnknownLegacy(
                RecommendationMode.MinimizeTotalCost,
                MarketAcquisitionLens.MinimumUpfrontCost),
            recipeBasis));
    }

    private static MarketItemAnalysis CreateAnalysis(
        int itemId = 100,
        string name = "Item",
        int quantityNeeded = 2)
    {
        return new MarketItemAnalysis
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = quantityNeeded
        };
    }

    private static DetailedShoppingPlan CreateShoppingPlan(
        int itemId = 100,
        string name = "Item",
        int quantityNeeded = 2)
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = quantityNeeded
        };
    }

    private static StoredRecipeOperationSnapshot CreateStoredRecipeBasis(
        bool duplicateDemand = false,
        int? extraDemandItemId = null)
    {
        var snapshot = new StoredRecipeOperationSnapshot
        {
            MarketAnalysisDemandItems =
            [
                new StoredMarketAnalysisDemandItem
                {
                    ItemId = 100,
                    Name = "Item",
                    TotalQuantity = 2
                }
            ]
        };

        if (duplicateDemand)
        {
            snapshot.MarketAnalysisDemandItems.Add(new StoredMarketAnalysisDemandItem
            {
                ItemId = 100,
                Name = "Duplicate Item",
                TotalQuantity = 2
            });
        }

        if (extraDemandItemId is { } itemId)
        {
            snapshot.MarketAnalysisDemandItems.Add(new StoredMarketAnalysisDemandItem
            {
                ItemId = itemId,
                Name = "Extra Demand",
                TotalQuantity = 1
            });
        }

        return snapshot;
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
