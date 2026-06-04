using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class StoredPlanSnapshotBuilder
{
    private readonly AppState _appState;

    public StoredPlanSnapshotBuilder(AppState appState)
    {
        _appState = appState;
    }

    public StoredPlan Build(
        string planId,
        string planName,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false)
    {
        return Build(_appState, planId, planName, savedAt, includeSourcePlanIdentity);
    }

    public static StoredPlan Build(
        AppState appState,
        string planId,
        string planName,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false)
    {
        return new StoredPlan
        {
            Id = planId,
            Name = planName,
            DataCenter = appState.SelectedDataCenter,
            ModifiedAt = DateTime.UtcNow,
            SavedAt = savedAt ?? DateTime.UtcNow,
            ProjectItems = appState.ProjectItems.Select(p => new StoredProjectItem
            {
                Id = p.Id,
                Name = p.Name,
                IconId = p.IconId,
                Quantity = p.Quantity,
                MustBeHq = p.MustBeHq
            }).ToList(),
            PlanJson = appState.CurrentPlan != null
                ? JsonSerializer.Serialize(appState.CurrentPlan)
                : null,
            MarketPlansJson = appState.ShoppingPlans.Any()
                ? JsonSerializer.Serialize(appState.ShoppingPlans)
                : null,
            MarketItemAnalysesJson = appState.MarketItemAnalyses.Any()
                ? JsonSerializer.Serialize(appState.MarketItemAnalyses)
                : null,
            MarketAnalysisRecipeBasisJson = appState.MarketAnalysisRecipeBasis != null
                ? JsonSerializer.Serialize(appState.MarketAnalysisRecipeBasis)
                : null,
            MarketAnalysisScopeSnapshotJson = appState.PublishedMarketAnalysisScope != null
                ? JsonSerializer.Serialize(appState.PublishedMarketAnalysisScope)
                : null,
            SavedRecommendationMode = appState.RecommendationMode,
            SavedMarketAnalysisLens = appState.MarketAnalysisLens,
            SourcePlanId = includeSourcePlanIdentity ? appState.CurrentPlanId : null,
            SourcePlanName = includeSourcePlanIdentity ? appState.CurrentPlanName : null
        };
    }
}
