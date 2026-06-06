using System.Diagnostics;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Service for persisting plans and settings in browser IndexedDB.
/// Provides local storage that survives page refreshes.
/// </summary>
public class IndexedDbService
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger<IndexedDbService>? _logger;
    private bool _isInitialized = false;

    public IndexedDbService(IJSRuntime jsRuntime, ILogger<IndexedDbService>? logger = null)
    {
        _jsRuntime = jsRuntime;
        _logger = logger;
    }

    /// <summary>
    /// Save a plan to IndexedDB.
    /// </summary>
    public async Task<bool> SavePlanAsync(StoredPlan plan)
    {
        try
        {
            await EnsureInitialized();
            if (plan.SavedAt == default)
            {
                plan.SavedAt = DateTime.UtcNow;
            }
            var result = await _jsRuntime.InvokeAsync<bool>("IndexedDB.savePlan", plan);
            _logger?.LogInformation("Saved plan '{PlanName}' ({PlanId}) to IndexedDB", plan.Name, plan.Id);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save plan '{PlanName}' to IndexedDB", plan.Name);
            return false;
        }
    }

    /// <summary>
    /// Load a specific plan by ID.
    /// </summary>
    public async Task<StoredPlan?> LoadPlanAsync(string planId)
    {
        try
        {
            await EnsureInitialized();
            var result = await _jsRuntime.InvokeAsync<StoredPlan?>("IndexedDB.loadPlan", planId);
            if (result != null)
            {
                _logger?.LogDebug("Loaded plan '{PlanName}' ({PlanId}) from IndexedDB", result.Name, planId);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load plan {PlanId} from IndexedDB", planId);
            return null;
        }
    }

    /// <summary>
    /// Load all saved plans, sorted by modified date (newest first).
    /// </summary>
    public async Task<List<StoredPlan>> LoadAllPlansAsync()
    {
        try
        {
            await EnsureInitialized();
            var plans = await _jsRuntime.InvokeAsync<List<StoredPlan>>("IndexedDB.loadAllPlans");
            _logger?.LogInformation("Loaded {Count} plans from IndexedDB", plans.Count);
            return plans;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load plans from IndexedDB");
            return new List<StoredPlan>();
        }
    }

    /// <summary>
    /// Load saved plan summaries without transferring full serialized plan payloads.
    /// </summary>
    public async Task<List<StoredPlanSummary>> LoadPlanSummariesAsync()
    {
        try
        {
            await EnsureInitialized();
            var summaries = await _jsRuntime.InvokeAsync<List<StoredPlanSummary>>("IndexedDB.loadPlanSummaries");
            _logger?.LogInformation("Loaded {Count} plan summaries from IndexedDB", summaries.Count);
            return summaries;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load plan summaries from IndexedDB");
            return new List<StoredPlanSummary>();
        }
    }

    /// <summary>
    /// Delete a plan from IndexedDB.
    /// </summary>
    public async Task<bool> DeletePlanAsync(string planId)
    {
        try
        {
            await EnsureInitialized();
            var result = await _jsRuntime.InvokeAsync<bool>("IndexedDB.deletePlan", planId);
            _logger?.LogInformation("Deleted plan {PlanId} from IndexedDB", planId);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete plan {PlanId} from IndexedDB", planId);
            return false;
        }
    }

    /// <summary>
    /// Clear all plans from IndexedDB.
    /// </summary>
    public async Task<bool> ClearAllPlansAsync()
    {
        try
        {
            await EnsureInitialized();
            var result = await _jsRuntime.InvokeAsync<bool>("IndexedDB.clearAllPlans");
            _logger?.LogWarning("Cleared all plans from IndexedDB");
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to clear plans from IndexedDB");
            return false;
        }
    }

    /// <summary>
    /// Save a setting value.
    /// </summary>
    public async Task<bool> SaveSettingAsync<T>(string key, T value)
    {
        try
        {
            await EnsureInitialized();
            var serialized = JsonSerializer.Serialize(value);
            var result = await _jsRuntime.InvokeAsync<bool>("IndexedDB.saveSetting", key, serialized);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save setting {Key} to IndexedDB", key);
            return false;
        }
    }

    /// <summary>
    /// Load a setting value.
    /// </summary>
    public async Task<T?> LoadSettingAsync<T>(string key, T? defaultValue = default)
    {
        try
        {
            await EnsureInitialized();
            var serialized = await _jsRuntime.InvokeAsync<string?>("IndexedDB.loadSetting", key);
            
            if (string.IsNullOrEmpty(serialized))
                return defaultValue;
                
            return JsonSerializer.Deserialize<T>(serialized);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load setting {Key} from IndexedDB", key);
            return defaultValue;
        }
    }

    /// <summary>
    /// Save the current app state (auto-save functionality).
    /// </summary>
    public async Task<bool> AutoSaveStateAsync(
        AppState state,
        string planName = "AutoSave",
        bool skipIfInFlight = false)
    {
        AppStateAutoSaveLease? autoSaveLease = null;
        var success = false;

        try
        {
            var totalElapsed = Stopwatch.StartNew();

            if (!state.HasPlanOrProjectItems)
                return false;

            autoSaveLease = await state.BeginAutoSaveAsync(skipIfInFlight);
            if (autoSaveLease == null)
                return false;

            var snapshotElapsed = Stopwatch.StartNew();
            var planData = state.CreateStoredPlanSnapshot(
                "autosave",
                planName,
                includeSourcePlanIdentity: true,
                includeLegacyMarketAnalysisFields: false);
            snapshotElapsed.Stop();

            StoredPlanSnapshotMetrics? metrics = null;
            var metricsElapsed = Stopwatch.StartNew();
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                metrics = StoredPlanSnapshotMetrics.FromStoredPlan(planData);
            }
            metricsElapsed.Stop();

            var saveElapsed = Stopwatch.StartNew();
            success = await SavePlanAsync(planData);
            saveElapsed.Stop();
            totalElapsed.Stop();

            if (metrics != null)
            {
                _logger?.LogDebug(
                    "Auto-save wrote {PlanNodeCount} nodes, {ShoppingPlanCount} shopping plans, {MarketAnalysisCount} analyses, {TotalJsonBytes} JSON bytes in {TotalElapsedMs} ms (snapshot {SnapshotElapsedMs} ms, metrics {MetricsElapsedMs} ms, save {SaveElapsedMs} ms)",
                    metrics.PlanNodeCount,
                    metrics.ShoppingPlanCount,
                    metrics.MarketAnalysisCount,
                    metrics.TotalJsonBytes,
                    totalElapsed.ElapsedMilliseconds,
                    snapshotElapsed.ElapsedMilliseconds,
                    metricsElapsed.ElapsedMilliseconds,
                    saveElapsed.ElapsedMilliseconds);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to auto-save state");
            return false;
        }
        finally
        {
            if (autoSaveLease != null)
            {
                state.CompleteAutoSave(
                    success,
                    autoSaveLease.CapturedVersions,
                    autoSaveLease.DirtyBuckets);
            }
        }
    }

    /// <summary>
    /// Save market analysis results for a plan.
    /// </summary>
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
        try
        {
            await EnsureInitialized();
            var shouldWriteCompactSummary = marketIntelligenceSummary != null;
            var storedMarketIntelligence = shouldWriteCompactSummary
                ? null
                : CreateStoredMarketIntelligence(
                    shoppingPlans,
                    marketItemAnalyses,
                    mode,
                    lens,
                    recipeBasis,
                    publishedScope,
                    marketIntelligence);
            return await _jsRuntime.InvokeAsync<bool>(
                "IndexedDB.patchMarketAnalysis",
                planId,
                shouldWriteCompactSummary ? null : JsonSerializer.Serialize(shoppingPlans),
                shouldWriteCompactSummary ? null : JsonSerializer.Serialize(marketItemAnalyses),
                storedMarketIntelligence != null ? JsonSerializer.Serialize(storedMarketIntelligence) : null,
                mode,
                lens,
                recipeBasis != null ? JsonSerializer.Serialize(recipeBasis) : null,
                publishedScope != null ? JsonSerializer.Serialize(publishedScope) : null,
                marketIntelligenceSummary?.PublicationId,
                marketIntelligenceSummary != null ? JsonSerializer.Serialize(marketIntelligenceSummary) : null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save market analysis for plan {PlanId}", planId);
            return false;
        }
    }

    private static StoredMarketIntelligence? CreateStoredMarketIntelligence(
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        IReadOnlyList<MarketItemAnalysis> marketItemAnalyses,
        RecommendationMode mode,
        MarketAcquisitionLens lens,
        StoredRecipeOperationSnapshot? recipeBasis,
        PublishedMarketAnalysisScopeSnapshot? publishedScope,
        MarketIntelligence? marketIntelligence)
    {
        var intelligence = marketIntelligence;
        if (intelligence == null &&
            (shoppingPlans.Count > 0 || marketItemAnalyses.Count > 0))
        {
            var context = publishedScope != null
                ? new MarketIntelligencePublicationContext(
                    MarketIntelligencePublicationContextKind.Known,
                    publishedScope.Scope,
                    publishedScope.SelectedDataCenter,
                    publishedScope.SelectedRegion,
                    publishedScope.RequestedDataCenters.ToArray(),
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                    null,
                    false,
                    mode,
                    lens,
                    null,
                    publishedScope.PlanSessionVersion,
                    null,
                    publishedScope.PublishedAtUtc)
                : MarketIntelligencePublicationContext.UnknownLegacy(mode, lens);

            intelligence = new MarketIntelligence(
                Guid.NewGuid(),
                marketItemAnalyses.ToArray(),
                shoppingPlans.ToArray(),
                Array.Empty<CoreMarketDataUnavailableItem>(),
                context,
                recipeBasis);
        }

        if (intelligence == null ||
            (!intelligence.HasPublishedMarketAnalysis &&
             !intelligence.HasRecommendations &&
             !intelligence.HasUnavailableMarketItems))
        {
            return null;
        }

        return StoredMarketIntelligence.FromMarketIntelligence(intelligence);
    }

    /// <summary>
    /// Load auto-saved state.
    /// </summary>
    public async Task<StoredPlan?> LoadAutoSaveAsync()
    {
        return await LoadPlanAsync("autosave");
    }

    private async Task EnsureInitialized()
    {
        if (!_isInitialized)
        {
            // The JS module auto-initializes on first call
            _isInitialized = true;
        }
        await Task.CompletedTask;
    }
}

/// <summary>
/// Stored plan data structure for IndexedDB.
/// </summary>
public class StoredPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Plan";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
    public string DataCenter { get; set; } = "Aether";
    public List<StoredProjectItem> ProjectItems { get; set; } = new();
    public string? PlanJson { get; set; }
    
    /// <summary>
    /// Serialized market analysis shopping plans.
    /// </summary>
    public string? MarketPlansJson { get; set; }

    /// <summary>
    /// Serialized canonical market intelligence publication.
    /// </summary>
    public string? MarketIntelligenceJson { get; set; }

    /// <summary>
    /// Active cold-storage market intelligence publication for compact autosave payloads.
    /// </summary>
    public Guid? ActiveMarketIntelligencePublicationId { get; set; }

    /// <summary>
    /// Serialized compact market intelligence publication summary.
    /// Listing-level details are stored separately in cold IndexedDB stores.
    /// </summary>
    public string? MarketIntelligenceSummaryJson { get; set; }

    /// <summary>
    /// Serialized immutable market analysis source data.
    /// </summary>
    public string? MarketItemAnalysesJson { get; set; }

    /// <summary>
    /// Serialized recipe-operation basis used to validate restored market analysis.
    /// </summary>
    public string? MarketAnalysisRecipeBasisJson { get; set; }

    /// <summary>
    /// Serialized market-analysis publication scope used to detect stale-scope evidence.
    /// </summary>
    public string? MarketAnalysisScopeSnapshotJson { get; set; }
    
    /// <summary>
    /// Recommendation mode used for the saved market analysis.
    /// </summary>
    public RecommendationMode SavedRecommendationMode { get; set; } = RecommendationMode.MinimizeTotalCost;

    /// <summary>
    /// Market analysis lens used to project the saved shopping plans.
    /// </summary>
    public MarketAcquisitionLens SavedMarketAnalysisLens { get; set; } = MarketAcquisitionLens.MinimumUpfrontCost;

    /// <summary>
    /// Named plan identity active when this autosave was captured.
    /// Autosave itself should not become the user's current named plan.
    /// </summary>
    public string? SourcePlanId { get; set; }

    /// <summary>
    /// Named plan display name active when this autosave was captured.
    /// </summary>
    public string? SourcePlanName { get; set; }
}

/// <summary>
/// Stored project item for IndexedDB.
/// </summary>
public class StoredProjectItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    public int Quantity { get; set; }
    public bool MustBeHq { get; set; }
}
