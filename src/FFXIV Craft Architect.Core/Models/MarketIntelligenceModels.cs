using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Core.Models;

public enum MarketIntelligencePublicationContextKind
{
    None,
    UnknownLegacy,
    Known
}

public sealed record MarketIntelligencePublicationContext(
    MarketIntelligencePublicationContextKind Kind,
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    IReadOnlyList<string> RequestedDataCenters,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectedWorldsByDataCenter,
    TimeSpan? MaxAge,
    bool ForceRefreshData,
    RecommendationMode RecommendationMode,
    MarketAcquisitionLens Lens,
    CraftSessionVersionStamp? CoreVersionStamp,
    long? WebPlanSessionVersion,
    long? WebMarketAnalysisVersion,
    DateTime PublishedAtUtc)
{
    public static MarketIntelligencePublicationContext None { get; } =
        new(
            MarketIntelligencePublicationContextKind.None,
            MarketFetchScope.SelectedDataCenter,
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            null,
            false,
            RecommendationMode.MinimizeTotalCost,
            MarketAcquisitionLens.MinimumUpfrontCost,
            null,
            null,
            null,
            DateTime.MinValue);

    public static MarketIntelligencePublicationContext UnknownLegacy(
        RecommendationMode recommendationMode,
        MarketAcquisitionLens lens,
        DateTime? publishedAtUtc = null) =>
        new(
            MarketIntelligencePublicationContextKind.UnknownLegacy,
            MarketFetchScope.SelectedDataCenter,
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            null,
            false,
            recommendationMode,
            lens,
            null,
            null,
            null,
            publishedAtUtc ?? DateTime.MinValue);
}

public sealed record MarketIntelligence(
    Guid MarketIntelligenceId,
    IReadOnlyList<MarketItemAnalysis> ItemAnalyses,
    IReadOnlyList<DetailedShoppingPlan> Recommendations,
    IReadOnlyList<CoreMarketDataUnavailableItem> UnavailableMarketItems,
    MarketIntelligencePublicationContext PublicationContext,
    StoredRecipeOperationSnapshot? RecipeBasis)
{
    public static MarketIntelligence Empty { get; } =
        new(
            Guid.Empty,
            Array.Empty<MarketItemAnalysis>(),
            Array.Empty<DetailedShoppingPlan>(),
            Array.Empty<CoreMarketDataUnavailableItem>(),
            MarketIntelligencePublicationContext.None,
            null);

    public bool HasPublishedMarketAnalysis => ItemAnalyses.Count > 0;

    public bool HasRecommendations => Recommendations.Count > 0;

    public bool HasUnavailableMarketItems => UnavailableMarketItems.Count > 0;

    public bool HasCompletePublicationContext =>
        PublicationContext.Kind == MarketIntelligencePublicationContextKind.Known;

    public IReadOnlySet<int> UnavailableMarketItemIds =>
        UnavailableMarketItems.Select(item => item.ItemId).ToHashSet();

    public IReadOnlyList<DetailedShoppingPlan>? ShoppingPlans => Recommendations;

    public CraftSessionVersionStamp? PublishedAgainstVersion => PublicationContext.CoreVersionStamp;

    public RecommendationMode RecommendationMode => PublicationContext.RecommendationMode;

    public MarketAcquisitionLens Lens => PublicationContext.Lens;

    public static MarketIntelligence CreateKnown(
        IReadOnlyList<MarketItemAnalysis> itemAnalyses,
        IReadOnlyList<DetailedShoppingPlan> recommendations,
        IReadOnlyList<CoreMarketDataUnavailableItem> unavailableMarketItems,
        MarketIntelligencePublicationContext publicationContext,
        StoredRecipeOperationSnapshot? recipeBasis = null)
    {
        ArgumentNullException.ThrowIfNull(itemAnalyses);
        ArgumentNullException.ThrowIfNull(recommendations);
        ArgumentNullException.ThrowIfNull(unavailableMarketItems);
        ArgumentNullException.ThrowIfNull(publicationContext);

        return new MarketIntelligence(
            Guid.NewGuid(),
            itemAnalyses.ToArray(),
            recommendations.ToArray(),
            unavailableMarketItems.ToArray(),
            publicationContext,
            recipeBasis);
    }

    public static MarketIntelligence FromLegacy(
        IReadOnlyList<MarketItemAnalysis> itemAnalyses,
        IReadOnlyList<DetailedShoppingPlan>? recommendations,
        IReadOnlySet<int> unavailableMarketItemIds,
        CraftSessionVersionStamp? publishedAgainstVersion,
        RecommendationMode recommendationMode,
        MarketAcquisitionLens lens,
        StoredRecipeOperationSnapshot? recipeBasis = null)
    {
        ArgumentNullException.ThrowIfNull(itemAnalyses);
        ArgumentNullException.ThrowIfNull(unavailableMarketItemIds);

        var context = new MarketIntelligencePublicationContext(
            MarketIntelligencePublicationContextKind.UnknownLegacy,
            MarketFetchScope.SelectedDataCenter,
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            null,
            false,
            recommendationMode,
            lens,
            publishedAgainstVersion,
            null,
            null,
            DateTime.UtcNow);

        return new MarketIntelligence(
            itemAnalyses.Count > 0 || recommendations?.Count > 0
                ? Guid.NewGuid()
                : Guid.Empty,
            itemAnalyses.ToArray(),
            recommendations?.ToArray() ?? Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItemIds
                .Select(itemId => new CoreMarketDataUnavailableItem(itemId, string.Empty))
                .ToArray(),
            context,
            recipeBasis);
    }

    public static MarketIntelligence FromCraftSessionMarketEvidence(CraftSessionMarketEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        return FromLegacy(
            evidence.ItemAnalyses,
            evidence.ShoppingPlans,
            evidence.UnavailableMarketItemIds,
            evidence.PublishedAgainstVersion,
            evidence.RecommendationMode,
            evidence.Lens,
            evidence.RecipeBasis);
    }
}

public sealed class StoredMarketIntelligence
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Guid MarketIntelligenceId { get; set; }

    public List<MarketItemAnalysis> ItemAnalyses { get; set; } = new();

    public List<DetailedShoppingPlan> Recommendations { get; set; } = new();

    public List<CoreMarketDataUnavailableItem> UnavailableMarketItems { get; set; } = new();

    public MarketIntelligencePublicationContext PublicationContext { get; set; } =
        MarketIntelligencePublicationContext.None;

    public StoredRecipeOperationSnapshot? RecipeBasis { get; set; }

    public static StoredMarketIntelligence FromMarketIntelligence(MarketIntelligence intelligence)
    {
        ArgumentNullException.ThrowIfNull(intelligence);

        return new StoredMarketIntelligence
        {
            MarketIntelligenceId = intelligence.MarketIntelligenceId,
            ItemAnalyses = intelligence.ItemAnalyses.ToList(),
            Recommendations = intelligence.Recommendations.ToList(),
            UnavailableMarketItems = intelligence.UnavailableMarketItems.ToList(),
            PublicationContext = intelligence.PublicationContext,
            RecipeBasis = intelligence.RecipeBasis
        };
    }

    public MarketIntelligence ToMarketIntelligence()
    {
        return new MarketIntelligence(
            MarketIntelligenceId,
            ItemAnalyses ?? new List<MarketItemAnalysis>(),
            Recommendations ?? new List<DetailedShoppingPlan>(),
            UnavailableMarketItems ?? new List<CoreMarketDataUnavailableItem>(),
            PublicationContext ?? MarketIntelligencePublicationContext.None,
            RecipeBasis);
    }
}
