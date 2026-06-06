using System.Text.Json;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class WebPlanPersistenceService
{
    private readonly IndexedDbService _indexedDb;
    private readonly StoredPlanSnapshotBuilder _snapshotBuilder;
    private readonly PlanSessionLoadService _sessionLoadService;
    private readonly IMarketIntelligenceStore? _marketIntelligenceStore;

    public WebPlanPersistenceService(
        IndexedDbService indexedDb,
        StoredPlanSnapshotBuilder snapshotBuilder,
        PlanSessionLoadService sessionLoadService,
        IMarketIntelligenceStore? marketIntelligenceStore = null)
    {
        _indexedDb = indexedDb;
        _snapshotBuilder = snapshotBuilder;
        _sessionLoadService = sessionLoadService;
        _marketIntelligenceStore = marketIntelligenceStore;
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

        await HydrateReferencedMarketIntelligenceSummaryAsync(storedPlan);
        return _sessionLoadService.Load(storedPlan, trackStoredPlanIdentity);
    }

    public async Task<PlanSessionLoadResult?> LoadAutoSaveIntoSessionAsync()
    {
        var storedPlan = await _indexedDb.LoadAutoSaveAsync();
        if (storedPlan == null)
        {
            return null;
        }

        await HydrateReferencedMarketIntelligenceSummaryAsync(storedPlan);
        return _sessionLoadService.Load(storedPlan, trackStoredPlanIdentity: false);
    }

    private async Task HydrateReferencedMarketIntelligenceSummaryAsync(StoredPlan storedPlan)
    {
        if (_marketIntelligenceStore == null ||
            storedPlan.ActiveMarketIntelligencePublicationId is not { } publicationId ||
            publicationId == Guid.Empty ||
            EmbeddedSummaryMatchesReference(storedPlan.MarketIntelligenceSummaryJson, publicationId))
        {
            return;
        }

        var summary = await _marketIntelligenceStore.LoadPublicationSummaryAsync(publicationId);
        if (summary == null)
        {
            return;
        }

        storedPlan.MarketIntelligenceSummaryJson = JsonSerializer.Serialize(summary);
    }

    private static bool EmbeddedSummaryMatchesReference(string? summaryJson, Guid publicationId)
    {
        if (string.IsNullOrWhiteSpace(summaryJson))
        {
            return false;
        }

        try
        {
            var summary = JsonSerializer.Deserialize<MarketIntelligencePublicationSummary>(summaryJson);
            return summary is
            {
                SchemaVersion: <= MarketIntelligencePublicationSummary.CurrentSchemaVersion
            } &&
                summary.PublicationId == publicationId;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public StoredPlan BuildSnapshot(
        string planId,
        string planName,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false)
    {
        return _snapshotBuilder.Build(planId, planName, savedAt, includeSourcePlanIdentity);
    }

    public async Task<bool> SaveSnapshotAsync(StoredPlan snapshot)
    {
        return await _indexedDb.SavePlanAsync(snapshot);
    }

    public async Task<bool> SaveCurrentPlanAsync(
        string planId,
        string planName,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false)
    {
        var snapshot = BuildSnapshot(planId, planName, savedAt, includeSourcePlanIdentity);
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
        MarketIntelligence? marketIntelligence = null,
        MarketIntelligencePublicationSummary? marketIntelligenceSummary = null)
    {
        return await _indexedDb.SaveMarketAnalysisAsync(
            planId,
            shoppingPlans,
            marketItemAnalyses,
            mode,
            lens,
            recipeBasis,
            publishedScope,
            marketIntelligence,
            marketIntelligenceSummary);
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
