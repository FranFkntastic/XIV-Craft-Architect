using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class WebPlanPersistenceService
{
    private readonly IndexedDbService _indexedDb;
    private readonly StoredPlanSnapshotBuilder _snapshotBuilder;
    private readonly PlanSessionLoadService _sessionLoadService;
    private readonly MarketEvidenceHydrationService? _marketEvidenceHydration;

    public WebPlanPersistenceService(
        IndexedDbService indexedDb,
        StoredPlanSnapshotBuilder snapshotBuilder,
        PlanSessionLoadService sessionLoadService,
        MarketEvidenceHydrationService? marketEvidenceHydration = null)
    {
        _indexedDb = indexedDb;
        _snapshotBuilder = snapshotBuilder;
        _sessionLoadService = sessionLoadService;
        _marketEvidenceHydration = marketEvidenceHydration;
    }

    public async Task<IReadOnlyList<StoredPlanSummary>> LoadPlanSummariesAsync()
    {
        return await _indexedDb.LoadPlanSummariesAsync();
    }

    public async Task<StoredPlan?> LoadPlanPayloadAsync(string planId)
    {
        return await _indexedDb.LoadPlanAsync(planId);
    }

    public async Task<PlanSessionLoadResult?> LoadPlanIntoSessionAsync(
        string planId,
        bool trackStoredPlanIdentity = true)
    {
        var storedPlan = await LoadPlanPayloadAsync(planId);
        if (storedPlan == null)
        {
            return null;
        }

        var result = _sessionLoadService.Load(storedPlan, trackStoredPlanIdentity);
        _marketEvidenceHydration?.ScheduleAfterPlanLoad(result);
        return result;
    }

    public async Task<PlanSessionLoadResult?> LoadAutoSaveIntoSessionAsync()
    {
        var storedPlan = await _indexedDb.LoadAutoSaveAsync();
        if (storedPlan == null)
        {
            return null;
        }

        var result = _sessionLoadService.Load(storedPlan, trackStoredPlanIdentity: false);
        _marketEvidenceHydration?.ScheduleAfterPlanLoad(result);
        return result;
    }

    public StoredPlan BuildSnapshot(
        string planId,
        string planName,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false,
        bool includeLegacyMarketAnalysisFields = true)
    {
        return _snapshotBuilder.Build(
            planId,
            planName,
            savedAt,
            includeSourcePlanIdentity,
            includeLegacyMarketAnalysisFields);
    }

    public async Task<bool> SaveSnapshotAsync(StoredPlan snapshot)
    {
        return await _indexedDb.SavePlanAsync(snapshot);
    }

    public async Task<bool> SaveCurrentPlanAsync(
        string planId,
        string planName,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false,
        bool includeLegacyMarketAnalysisFields = true)
    {
        var snapshot = BuildSnapshot(
            planId,
            planName,
            savedAt,
            includeSourcePlanIdentity,
            includeLegacyMarketAnalysisFields);
        return await SaveSnapshotAsync(snapshot);
    }

    public async Task<bool> SaveGeneratedOrderPlanAsync(
        string planId,
        string planName,
        CraftingPlan plan,
        IReadOnlyList<TradeOrderRootItemSnapshot> rootItems,
        DateTime? savedAt = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rootItems);

        var timestamp = savedAt ?? DateTime.UtcNow;
        var activeSnapshot = _snapshotBuilder.BuildForCurrentPlan(
            planId,
            planName,
            plan,
            timestamp);
        if (activeSnapshot != null)
        {
            return await SaveSnapshotAsync(activeSnapshot);
        }

        var snapshot = new StoredPlan
        {
            Id = planId,
            Name = planName,
            DataCenter = plan.DataCenter,
            ModifiedAt = timestamp,
            SavedAt = timestamp,
            ProjectItems = rootItems
                .Where(item => item.Quantity > 0)
                .Select(item => new StoredProjectItem
                {
                    Id = item.ItemId,
                    Name = item.Name,
                    Quantity = item.Quantity,
                    MustBeHq = item.MustBeHq
                })
                .ToList(),
            PlanJson = JsonSerializer.Serialize(plan),
            MarketPlansJson = null,
            MarketIntelligenceJson = null,
            MarketItemAnalysesJson = null,
            MarketAnalysisRecipeBasisJson = null,
            MarketAnalysisScopeSnapshotJson = null,
            SavedRecommendationMode = RecommendationMode.MinimizeTotalCost,
            SavedMarketAnalysisLens = MarketAcquisitionLens.MinimumUpfrontCost
        };

        return await SaveSnapshotAsync(snapshot);
    }

    public async Task<bool> DeletePlanAsync(string planId)
    {
        return await _indexedDb.DeletePlanAsync(planId);
    }

    public async Task<bool> SaveMarketAnalysisAsync(
        string planId,
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        IReadOnlyList<MarketItemAnalysis> marketItemAnalyses,
        RecommendationMode mode,
        MarketAcquisitionLens lens,
        StoredRecipeOperationSnapshot? recipeBasis = null,
        PublishedMarketAnalysisScopeSnapshot? publishedScope = null,
        MarketIntelligence? marketIntelligence = null)
    {
        return await _indexedDb.SaveMarketAnalysisAsync(
            planId,
            shoppingPlans,
            marketItemAnalyses,
            mode,
            lens,
            recipeBasis,
            publishedScope,
            marketIntelligence);
    }

    public async Task<RenameStoredPlanResult> RenamePlanAsync(string planId, string newName)
    {
        var plan = await LoadPlanPayloadAsync(planId);
        if (plan == null)
        {
            return new RenameStoredPlanResult(false, null, null);
        }

        var oldName = plan.Name;
        plan.Name = newName;
        plan.ModifiedAt = DateTime.UtcNow;
        var saved = await SaveSnapshotAsync(plan);
        return new RenameStoredPlanResult(saved, oldName, newName);
    }
}

public sealed record RenameStoredPlanResult(bool Success, string? OldName, string? NewName);
