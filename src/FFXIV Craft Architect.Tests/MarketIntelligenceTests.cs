using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketIntelligenceTests
{
    [Fact]
    public void Empty_UsesNoPublicationContextAndNoRecommendations()
    {
        var intelligence = MarketIntelligence.Empty;

        Assert.Equal(Guid.Empty, intelligence.MarketIntelligenceId);
        Assert.False(intelligence.HasPublishedMarketAnalysis);
        Assert.False(intelligence.HasRecommendations);
        Assert.False(intelligence.HasCompletePublicationContext);
        Assert.Equal(MarketIntelligencePublicationContextKind.None, intelligence.PublicationContext.Kind);
        Assert.Empty(intelligence.ItemAnalyses);
        Assert.Empty(intelligence.Recommendations);
        Assert.Empty(intelligence.UnavailableMarketItems);
        Assert.Empty(intelligence.UnavailableMarketItemIds);
        Assert.Null(intelligence.RecipeBasis);
    }

    [Fact]
    public void CreateLegacy_ProjectsRecommendationAndUnavailableCompatibilityProperties()
    {
        var analysis = new MarketItemAnalysis { ItemId = 100, Name = "Ore" };
        var recommendation = new DetailedShoppingPlan { ItemId = 100, Name = "Ore", QuantityNeeded = 3 };
        var publishedAt = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);

        var intelligence = MarketIntelligence.CreateKnown(
            [analysis],
            [recommendation],
            [new CoreMarketDataUnavailableItem(200, "Missing Ore")],
            new MarketIntelligencePublicationContext(
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
                WebPlanSessionVersion: 7,
                WebMarketAnalysisVersion: 8,
                publishedAt));

        Assert.NotEqual(Guid.Empty, intelligence.MarketIntelligenceId);
        Assert.True(intelligence.HasPublishedMarketAnalysis);
        Assert.True(intelligence.HasRecommendations);
        Assert.True(intelligence.HasCompletePublicationContext);
        Assert.Same(analysis, Assert.Single(intelligence.ItemAnalyses));
        Assert.Same(recommendation, Assert.Single(intelligence.Recommendations));
        Assert.Same(recommendation, Assert.Single(intelligence.ShoppingPlans!));
        Assert.Equal(200, Assert.Single(intelligence.UnavailableMarketItemIds));
        Assert.Equal(RecommendationMode.MinimizeTotalCost, intelligence.RecommendationMode);
        Assert.Equal(MarketAcquisitionLens.MinimumUpfrontCost, intelligence.Lens);
        Assert.Equal(publishedAt, intelligence.PublicationContext.PublishedAtUtc);
    }

    [Fact]
    public void FromLegacy_WithVersionStampKeepsContextUnknown()
    {
        var version = new CraftSessionVersionStamp(
            PlanSession: 4,
            PlanCore: 1,
            PlanDecision: 1,
            PlanPrice: 1,
            MarketAnalysis: 9,
            Procurement: 1,
            SettingsContext: 1,
            ViewState: 1);

        var intelligence = MarketIntelligence.FromLegacy(
            [new MarketItemAnalysis { ItemId = 100, Name = "Ore" }],
            [new DetailedShoppingPlan { ItemId = 100, Name = "Ore", QuantityNeeded = 3 }],
            new HashSet<int>(),
            version,
            RecommendationMode.MaximizeValue,
            MarketAcquisitionLens.BulkValue);

        Assert.Equal(MarketIntelligencePublicationContextKind.UnknownLegacy, intelligence.PublicationContext.Kind);
        Assert.False(intelligence.HasCompletePublicationContext);
        Assert.Equal(version, intelligence.PublishedAgainstVersion);
        Assert.Equal(RecommendationMode.MaximizeValue, intelligence.RecommendationMode);
        Assert.Equal(MarketAcquisitionLens.BulkValue, intelligence.Lens);
    }
}
