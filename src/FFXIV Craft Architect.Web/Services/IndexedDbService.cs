using System.Diagnostics;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services;

public enum AutoSaveStateOutcome
{
    Saved,
    AlreadyPersisted,
    Skipped,
    Failed
}

public sealed record AutoSavePerformanceTiming(
    long SnapshotMilliseconds,
    long MetricsMilliseconds,
    long SaveMilliseconds,
    long TotalMilliseconds,
    bool ReusedMarketEvidence);

/// <summary>
/// Service for persisting plans and settings in browser IndexedDB.
/// Provides local storage that survives page refreshes.
/// </summary>
public class IndexedDbService
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger<IndexedDbService>? _logger;
    private bool _isInitialized = false;
    private ReusableStoredMarketEvidence? _reusableStoredMarketEvidence;

    public AutoSavePerformanceTiming? LastAutoSavePerformanceTiming { get; private set; }

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
        catch (OutOfMemoryException)
        {
            // Do not attach the exception here. Blazor's managed OOM stack can contain
            // hundreds of thousands of characters, and Chromium retains console messages;
            // logging the full exception compounds the memory failure we are reporting.
            _logger?.LogError(
                "Failed to save plan '{PlanName}' to IndexedDB because browser memory was exhausted",
                plan.Name);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save plan '{PlanName}' to IndexedDB", plan.Name);
            return false;
        }
    }

    public async Task<bool> PatchPlanAndProcurementRouteAsync(
        string planId,
        StoredPlanCorePatch planPatch)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<bool>(
                "IndexedDB.patchPlanAndProcurementRoute",
                planId,
                planPatch);
        }
        catch (OutOfMemoryException)
        {
            _logger?.LogError(
                "Failed to patch plan decisions and procurement route for {PlanId} because browser memory was exhausted",
                planId);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to patch plan decisions and the procurement route for plan {PlanId}", planId);
            return false;
        }
    }

    public void RememberRestoredMarketEvidence(AppState state, StoredPlan storedPlan)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(storedPlan);

        if (string.IsNullOrWhiteSpace(storedPlan.MarketIntelligenceJson))
        {
            _reusableStoredMarketEvidence = null;
            return;
        }

        string? marketEvidenceHash = null;
        if (state.ProcurementRouteValidity == ProcurementRoutePublicationValidity.Current &&
            !string.IsNullOrWhiteSpace(storedPlan.ProcurementRouteJson))
        {
            try
            {
                marketEvidenceHash = JsonSerializer.Deserialize<StoredProcurementRoute>(
                    storedPlan.ProcurementRouteJson)?.MarketEvidenceHash;
            }
            catch (JsonException)
            {
            }
        }

        var versions = state.CurrentVersions;
        _reusableStoredMarketEvidence = new ReusableStoredMarketEvidence(
            versions.MarketAnalysisVersion,
            versions.SettingsVersion,
            storedPlan.MarketIntelligenceJson,
            storedPlan.MarketAnalysisRecipeBasisJson,
            storedPlan.MarketAnalysisScopeSnapshotJson,
            marketEvidenceHash);
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
        catch (OutOfMemoryException)
        {
            _logger?.LogError(
                "Failed to load plan {PlanId} from IndexedDB because browser memory was exhausted",
                planId);
            return null;
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
            {
                return defaultValue;
            }

            return JsonSerializer.Deserialize<T>(serialized);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load setting {Key} from IndexedDB", key);
            return defaultValue;
        }
    }

    public async Task<Dictionary<string, string>> LoadAllSettingsAsync()
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<Dictionary<string, string>>("IndexedDB.loadAllSettings");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load all settings from IndexedDB");
            return new Dictionary<string, string>();
        }
    }

    public async Task<bool> SaveSettingsBatchAsync(Dictionary<string, string> settings)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<bool>("IndexedDB.saveSettingsBatch", settings);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save settings batch to IndexedDB");
            return false;
        }
    }

    public async Task<bool> SavePlansBatchAsync(IReadOnlyList<StoredPlan> plans)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<bool>("IndexedDB.savePlansBatch", plans);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save plans batch to IndexedDB");
            return false;
        }
    }

    public async Task<bool> SaveTradeCraftersBatchAsync(IReadOnlyList<TradeCrafterProfile> crafters)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<bool>("IndexedDB.saveTradeCraftersBatch", crafters);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save Trade crafters batch to IndexedDB");
            return false;
        }
    }

    public async Task<bool> SaveTradeOrdersBatchAsync(IReadOnlyList<TradeOrder> orders)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<bool>("IndexedDB.saveTradeOrdersBatch", orders);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save Trade orders batch to IndexedDB");
            return false;
        }
    }

    public async Task<bool> SaveTradePayrollDraftsBatchAsync(IReadOnlyList<TradePayrollWorkflowDraft> drafts)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<bool>("IndexedDB.saveTradePayrollDraftsBatch", drafts);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save Trade payroll drafts batch to IndexedDB");
            return false;
        }
    }

    public async Task<bool> SaveTradeCompanyProfileAsync(TradeCompanyProfile profile)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<bool>("IndexedDB.saveTradeCompanyProfile", profile);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save Trade company profile {ProfileId}", profile.Id);
            return false;
        }
    }

    public async Task<TradeIndexedDbDiagnostics> GetTradeStoreDiagnosticsAsync()
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<TradeIndexedDbDiagnostics>("IndexedDB.getTradeStoreDiagnostics");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get Trade IndexedDB diagnostics");
            return new TradeIndexedDbDiagnostics
            {
                ErrorMessage = $"Could not run Trade storage diagnostics: {ex.Message}"
            };
        }
    }

    public async Task<List<TradeCompanyProfile>> LoadTradeCompanyProfilesAsync()
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<List<TradeCompanyProfile>>("IndexedDB.loadTradeCompanyProfiles");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load Trade company profiles");
            throw new InvalidOperationException("Failed to load Trade company profiles from browser storage.", ex);
        }
    }

    public async Task<bool> SaveTradeCrafterAsync(TradeCrafterProfile crafter)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<bool>("IndexedDB.saveTradeCrafter", crafter);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save Trade crafter {CrafterId}", crafter.Id);
            return false;
        }
    }

    public async Task<List<TradeCrafterProfile>> LoadTradeCraftersAsync(Guid companyProfileId)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<List<TradeCrafterProfile>>("IndexedDB.loadTradeCrafters", companyProfileId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load Trade crafters for company profile {ProfileId}", companyProfileId);
            throw new InvalidOperationException("Failed to load Trade crafters from browser storage.", ex);
        }
    }

    public async Task<bool> SaveTradeOrderAsync(TradeOrder order)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<bool>("IndexedDB.saveTradeOrder", order);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save Trade order {OrderId}", order.Id);
            return false;
        }
    }

    public async Task<List<TradeOrder>> LoadTradeOrdersAsync(Guid companyProfileId)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<List<TradeOrder>>("IndexedDB.loadTradeOrders", companyProfileId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load Trade orders for company profile {ProfileId}", companyProfileId);
            throw new InvalidOperationException("Failed to load Trade orders from browser storage.", ex);
        }
    }

    public async Task<bool> DeleteTradeOrderAsync(Guid orderId)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<bool>("IndexedDB.deleteTradeOrder", orderId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete Trade order {OrderId}", orderId);
            return false;
        }
    }

    public async Task<bool> SaveTradePayrollDraftAsync(TradePayrollWorkflowDraft draft)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<bool>("IndexedDB.saveTradePayrollDraft", draft);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save Trade payroll draft {DraftId}", draft.Id);
            return false;
        }
    }

    public async Task<List<TradePayrollWorkflowDraft>> LoadTradePayrollDraftsAsync(Guid companyProfileId)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<List<TradePayrollWorkflowDraft>>("IndexedDB.loadTradePayrollDrafts", companyProfileId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load Trade payroll drafts for company profile {ProfileId}", companyProfileId);
            throw new InvalidOperationException("Failed to load Trade payroll drafts from browser storage.", ex);
        }
    }

    public async Task<bool> DeleteTradePayrollDraftAsync(string draftId)
    {
        try
        {
            await EnsureInitialized();
            return await _jsRuntime.InvokeAsync<bool>("IndexedDB.deleteTradePayrollDraft", draftId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete Trade payroll draft {DraftId}", draftId);
            return false;
        }
    }

    /// <summary>
    /// Save the current app state (auto-save functionality).
    /// </summary>
    public async Task<bool> AutoSaveStateAsync(
        AppState state,
        string planName = "AutoSave",
        bool skipIfInFlight = false,
        bool allowDuringEngineMemoryPressure = false) =>
        await AutoSaveStateWithOutcomeAsync(
            state,
            planName,
            skipIfInFlight,
            allowDuringEngineMemoryPressure) == AutoSaveStateOutcome.Saved;

    public async Task<AutoSaveStateOutcome> AutoSaveStateWithOutcomeAsync(
        AppState state,
        string planName = "AutoSave",
        bool skipIfInFlight = false,
        bool allowDuringEngineMemoryPressure = false)
    {
        AppStateAutoSaveLease? autoSaveLease = null;
        var success = false;
        LastAutoSavePerformanceTiming = null;

        try
        {
            var totalElapsed = Stopwatch.StartNew();

            if (!state.HasPlanOrProjectItems)
            {
                return AutoSaveStateOutcome.Failed;
            }

            autoSaveLease = await state.BeginAutoSaveAsync(
                skipIfInFlight,
                allowDuringEngineMemoryPressure);
            if (autoSaveLease == null)
            {
                return state.GetDirtyPersistedBuckets() == PersistedStateBucket.None
                    ? AutoSaveStateOutcome.AlreadyPersisted
                    : AutoSaveStateOutcome.Skipped;
            }

            if ((autoSaveLease.DirtyBuckets & PersistedStateBucket.MarketAnalysis) == PersistedStateBucket.None &&
                _reusableStoredMarketEvidence is { } reusableMarketEvidence &&
                reusableMarketEvidence.MarketAnalysisVersion == state.CurrentVersions.MarketAnalysisVersion &&
                reusableMarketEvidence.SettingsVersion == state.CurrentVersions.SettingsVersion &&
                (state.ProcurementRouteValidity != ProcurementRoutePublicationValidity.Current ||
                 !string.IsNullOrWhiteSpace(reusableMarketEvidence.MarketIntelligenceJson)))
            {
                var routeSnapshotElapsed = Stopwatch.StartNew();
                var marketEvidenceHash = reusableMarketEvidence.MarketEvidenceHash;
                if (state.ProcurementRouteValidity == ProcurementRoutePublicationValidity.Current &&
                    string.IsNullOrWhiteSpace(marketEvidenceHash))
                {
                    marketEvidenceHash = StoredPlanSnapshotBuilder.ComputeMarketEvidenceHash(
                        reusableMarketEvidence.MarketIntelligenceJson
                            ?? throw new InvalidOperationException(
                                "Reusable market evidence lost its canonical payload."));
                    _reusableStoredMarketEvidence = reusableMarketEvidence with
                    {
                        MarketEvidenceHash = marketEvidenceHash
                    };
                }
                var routeJson = StoredPlanSnapshotBuilder.BuildProcurementRouteJson(
                    state,
                    marketEvidenceHash);
                var planPatch = new StoredPlanCorePatch
                {
                    DataCenter = state.SelectedDataCenter,
                    ProjectItems = state.ProjectItems.Select(item => new StoredProjectItem
                    {
                        Id = item.Id,
                        Name = item.Name,
                        IconId = item.IconId,
                        Quantity = item.Quantity,
                        MustBeHq = item.MustBeHq
                    }).ToList(),
                    PlanJson = state.CurrentPlan is null
                        ? null
                        : JsonSerializer.Serialize(state.CurrentPlan),
                    ProcurementRouteJson = routeJson,
                    SourcePlanId = state.CurrentPlanId,
                    SourcePlanName = state.CurrentPlanName,
                    SavedAt = DateTime.UtcNow
                };
                routeSnapshotElapsed.Stop();

                await Task.Delay(50);
                var routeSaveElapsed = Stopwatch.StartNew();
                success = await PatchPlanAndProcurementRouteAsync("autosave", planPatch);
                routeSaveElapsed.Stop();
                totalElapsed.Stop();
                LastAutoSavePerformanceTiming = new AutoSavePerformanceTiming(
                    routeSnapshotElapsed.ElapsedMilliseconds,
                    0,
                    routeSaveElapsed.ElapsedMilliseconds,
                    totalElapsed.ElapsedMilliseconds,
                    ReusedMarketEvidence: true);
                _logger?.LogInformation(
                    "Auto-save patched plan decisions and the procurement route without retransferring market evidence in {TotalElapsedMs} ms",
                    totalElapsed.ElapsedMilliseconds);
                return success ? AutoSaveStateOutcome.Saved : AutoSaveStateOutcome.Failed;
            }

            var snapshotElapsed = Stopwatch.StartNew();
            var planData = StoredPlanSnapshotBuilder.BuildForAutoSave(
                state,
                "autosave",
                planName,
                savedAt: null,
                includeSourcePlanIdentity: true,
                includeLegacyMarketAnalysisFields: false,
                _reusableStoredMarketEvidence,
                out var capturedMarketEvidence);
            snapshotElapsed.Stop();

            StoredPlanSnapshotMetrics? metrics = null;
            var metricsElapsed = Stopwatch.StartNew();
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                metrics = StoredPlanSnapshotMetrics.FromStoredPlan(planData);
            }
            metricsElapsed.Stop();

            // Timer-backed yield lets the browser paint before IndexedDB serialization starts.
            await Task.Delay(50);
            var saveElapsed = Stopwatch.StartNew();
            success = await SavePlanAsync(planData);
            saveElapsed.Stop();
            totalElapsed.Stop();
            LastAutoSavePerformanceTiming = new AutoSavePerformanceTiming(
                snapshotElapsed.ElapsedMilliseconds,
                metricsElapsed.ElapsedMilliseconds,
                saveElapsed.ElapsedMilliseconds,
                totalElapsed.ElapsedMilliseconds,
                ReferenceEquals(capturedMarketEvidence, _reusableStoredMarketEvidence));
            if (success)
            {
                _reusableStoredMarketEvidence = capturedMarketEvidence;
            }

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

            return success ? AutoSaveStateOutcome.Saved : AutoSaveStateOutcome.Failed;
        }
        catch (OutOfMemoryException)
        {
            _logger?.LogError("Failed to auto-save state because browser memory was exhausted");
            return AutoSaveStateOutcome.Failed;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to auto-save state");
            return AutoSaveStateOutcome.Failed;
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
        MarketIntelligence? marketIntelligence = null)
    {
        try
        {
            await EnsureInitialized();
            var storedMarketIntelligence = CreateStoredMarketIntelligence(
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
                JsonSerializer.Serialize(shoppingPlans),
                JsonSerializer.Serialize(marketItemAnalyses),
                storedMarketIntelligence != null ? JsonSerializer.Serialize(storedMarketIntelligence) : null,
                mode,
                lens,
                recipeBasis != null ? JsonSerializer.Serialize(recipeBasis) : null,
                publishedScope != null ? JsonSerializer.Serialize(publishedScope) : null);
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

public sealed class TradeIndexedDbDiagnostics
{
    public int DatabaseVersion { get; set; }
    public bool HasCompanyProfilesStore { get; set; }
    public bool HasCraftersStore { get; set; }
    public bool HasOrdersStore { get; set; }
    public bool HasPayrollDraftsStore { get; set; }
    public string? ErrorMessage { get; set; }

    public bool IsReady =>
        string.IsNullOrWhiteSpace(ErrorMessage) &&
        HasCompanyProfilesStore &&
        HasCraftersStore &&
        HasOrdersStore &&
        HasPayrollDraftsStore;

    public string ToDisplayMessage()
    {
        var details = $"Trade storage diagnostics: database v{DatabaseVersion}; stores company={HasCompanyProfilesStore}, crafters={HasCraftersStore}, orders={HasOrdersStore}, payrollDrafts={HasPayrollDraftsStore}.";
        if (!string.IsNullOrWhiteSpace(ErrorMessage))
        {
            return $"{details} {ErrorMessage}";
        }

        if (!IsReady)
        {
            return $"{details} Reload the page after closing other FFXIV Craft Architect tabs so the browser can finish the IndexedDB upgrade.";
        }

        return details;
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
    /// Serialized current procurement route and the inputs that make it valid.
    /// </summary>
    public string? ProcurementRouteJson { get; set; }

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

public sealed class StoredPlanCorePatch
{
    public string DataCenter { get; set; } = "Aether";
    public List<StoredProjectItem> ProjectItems { get; set; } = [];
    public string? PlanJson { get; set; }
    public string? ProcurementRouteJson { get; set; }
    public string? SourcePlanId { get; set; }
    public string? SourcePlanName { get; set; }
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}

public sealed record StoredProcurementRoute(
    int SchemaVersion,
    string OptimizerVersion,
    IReadOnlyList<DetailedShoppingPlan>? ShoppingPlans,
    MarketRouteDecision? Decision,
    ProcurementRoutePublicationBasis? Basis,
    string PlanHash,
    string MarketEvidenceHash,
    string PayloadHash);

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
