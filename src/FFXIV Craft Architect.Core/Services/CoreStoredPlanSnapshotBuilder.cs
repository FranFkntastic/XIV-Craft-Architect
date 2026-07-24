using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CoreStoredPlanSnapshotBuilder
{
    private readonly CraftSessionState _session;

    public CoreStoredPlanSnapshotBuilder(CraftSessionState session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public CoreStoredPlanSnapshot Build(
        string planId,
        string planName,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false,
        bool includeLegacyMarketAnalysisFields = true,
        bool borrowCanonicalState = false,
        bool compressMarketIntelligence = false)
    {
        var plan = borrowCanonicalState
            ? _session.BorrowActivePlan()
            : _session.ActivePlan;
        var projectItems = _session.ProjectItems;
        var activeContext = _session.ActiveContext;
        var evidence = borrowCanonicalState
            ? _session.BorrowMarketEvidence()
            : _session.MarketEvidence;
        var intelligence = MarketIntelligence.FromCraftSessionMarketEvidence(evidence);
        var identity = _session.Identity;
        var now = DateTime.UtcNow;

        return new CoreStoredPlanSnapshot
        {
            Id = planId,
            Name = planName,
            DataCenter = activeContext.DataCenter ?? plan?.DataCenter ?? "Aether",
            ModifiedAt = now,
            SavedAt = savedAt ?? now,
            ProjectItems = projectItems.Select(item => new CoreStoredProjectItem
            {
                Id = item.Id,
                Name = item.Name,
                IconId = item.IconId,
                Quantity = item.Quantity,
                MustBeHq = item.MustBeHq
            }).ToList(),
            PlanJson = plan != null ? JsonSerializer.Serialize(plan) : null,
            MarketPlansJson = includeLegacyMarketAnalysisFields &&
                              evidence.ShoppingPlans?.Any() == true
                ? JsonSerializer.Serialize(evidence.ShoppingPlans)
                : null,
            MarketIntelligenceJson = intelligence.HasPublishedMarketAnalysis ||
                                     intelligence.HasRecommendations ||
                                     intelligence.HasUnavailableMarketItems
                ? MarketIntelligencePayloadCodec.Serialize(
                    StoredMarketIntelligence.FromMarketIntelligence(intelligence),
                    compressMarketIntelligence)
                : null,
            MarketItemAnalysesJson = includeLegacyMarketAnalysisFields &&
                                     evidence.ItemAnalyses.Any()
                ? JsonSerializer.Serialize(evidence.ItemAnalyses)
                : null,
            MarketAnalysisRecipeBasisJson = evidence.RecipeBasis != null
                ? JsonSerializer.Serialize(evidence.RecipeBasis)
                : null,
            UnavailableMarketItemIds = evidence.UnavailableMarketItemIds.ToHashSet(),
            SavedRecommendationMode = evidence.RecommendationMode,
            SavedMarketAnalysisLens = evidence.Lens,
            SourcePlanId = includeSourcePlanIdentity ? identity.SourcePlanId : null,
            SourcePlanName = includeSourcePlanIdentity ? identity.SourcePlanName : null
        };
    }
}
