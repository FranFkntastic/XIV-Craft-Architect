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
        bool includeSourcePlanIdentity = false)
    {
        var plan = _session.ActivePlan;
        var projectItems = _session.ProjectItems;
        var activeContext = _session.ActiveContext;
        var evidence = _session.MarketEvidence;
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
            MarketPlansJson = evidence.ShoppingPlans?.Any() == true
                ? JsonSerializer.Serialize(evidence.ShoppingPlans)
                : null,
            MarketItemAnalysesJson = evidence.ItemAnalyses.Any()
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
