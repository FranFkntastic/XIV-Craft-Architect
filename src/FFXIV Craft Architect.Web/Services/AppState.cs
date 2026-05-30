using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Global application state that persists across pages.
/// Centralizes WorldData, auto-save, and plan state management.
/// </summary>

/// <summary>
/// Singleton application state service for the Blazor WebAssembly app.
/// Replaces WPF's ViewModel-based state management with a centralized state container.
///
/// PURPOSE:
/// Unlike WPF where each ViewModel manages its own state, Blazor uses a singleton
/// AppState that persists across page navigations. This ensures:
/// 1. State survives when switching between Recipe Planner, Market Analysis, and Procurement Plan tabs
/// 2. Components can subscribe to changes via events
/// 3. Auto-save functionality can access complete application state
///
/// DATA FLOW:
/// 1. Recipe Planner (Index.razor):
///    - User adds items to ProjectItems list
///    - User clicks "Build Plan" → RecipeCalculationService.BuildPlanAsync()
///    - Result stored in CurrentPlan
///    - NotifyPlanChanged() triggers UI updates
///
/// 2. Market Analysis (MarketAnalysis.razor):
///    - Reads CurrentPlan.AggregatedMaterials
///    - Calls MarketCacheService.EnsurePopulatedAsync()
///    - Calls MarketShoppingService.CalculateDetailedShoppingPlansAsync()
///    - Stores results in ShoppingPlans
///    - NotifyShoppingListChanged() triggers updates
///
/// 3. Procurement Plan (ProcurementPlan.razor):
///    - Reads ShoppingPlans from AppState (no re-fetch)
///    - Groups by world for display
///    - Can temporarily blacklist worlds (session-only)
///
/// 4. Auto-Save Flow:
///    - 30-second timer in MainLayout
///    - Calls IndexedDbService.SavePlanAsync() with complete state
///    - Saves: CurrentPlan, ProjectItems, ShoppingPlans, settings
///
/// STATE CATEGORIES:
/// - Recipe Planner: CurrentPlan, ProjectItems
/// - Procurement: ShoppingItems, ShoppingPlans
/// - Settings: SelectedDataCenter, RecommendationMode, market scope preferences
/// - UI State: StatusMessage, IsBusy, ProgressPercent
/// - Session: TemporarilyBlacklistedWorlds (NOT persisted)
///
/// EVENTS:
/// Components subscribe to events for reactive updates:
/// - OnPlanChanged: Recipe tree structure modified
/// - OnShoppingListChanged: Market analysis results updated
/// - OnStatusChanged: Status bar needs update
/// - OnRecipeTreeExpandChanged: Expand/collapse state changed
///
/// PERSISTENCE:
/// - Auto-save: Every 30 seconds to IndexedDB
/// - Named saves: User-triggered with custom names
/// - Session restore: Auto-loads on app startup
/// - What persists: Plan data, project items, shopping plans, settings
/// - What doesn't persist: Blacklisted worlds, temporary UI state
///
/// WPF EQUIVALENT:
/// This replaces multiple WPF ViewModels:
/// - RecipePlannerViewModel → CurrentPlan, ProjectItems, OnPlanChanged
/// - MarketAnalysisViewModel → ShoppingPlans, OnShoppingListChanged
/// - Status is centralized here instead of separate StatusBarViewModel
/// </summary>
public class AppState
{
    private long _planStructureVersion;
    private long _planDecisionVersion;
    private long _planPriceVersion;
    private long _planCoreVersion;
    private long _marketAnalysisVersion;
    private long _procurementOverlayVersion;
    private long _settingsVersion;
    private long _statusVersion;
    private long _lastPersistedPlanCoreVersion = -1;
    private long _lastPersistedMarketAnalysisVersion = -1;
    private readonly SemaphoreSlim _autoSaveSemaphore = new(1, 1);
    private int _changeBatchDepth;
    private AppStateChangeScope _batchedScopes = AppStateChangeScope.None;
    private bool _raisePlanChanged;
    private bool _raiseShoppingListChanged;
    private bool _raiseStatusChanged;

    // Recipe Planner State
    public CraftingPlan? CurrentPlan { get; set; }
    public List<ProjectItem> ProjectItems { get; set; } = new();
    public string SelectedDataCenter { get; set; } = "Aether";
    public string SelectedRegion { get; set; } = "North America";
    
    // Procurement Planner State
    public List<MarketShoppingItem> ShoppingItems { get; set; } = new();

    /// <summary>
    /// Full market analysis evidence for every market-listable candidate in the recipe plan.
    /// Procurement may derive from this list, but must not delete or replace entries for inactive choices.
    /// </summary>
    public List<DetailedShoppingPlan> ShoppingPlans { get; set; } = new();

    /// <summary>
    /// Immutable market-analysis analytics for every market-listable candidate in the recipe plan.
    /// Acquisition and procurement views may project from this data, but should not mutate or filter it.
    /// </summary>
    public List<MarketItemAnalysis> MarketItemAnalyses { get; set; } = new();

    /// <summary>
    /// Mutable procurement overlay derived from ShoppingPlans and current acquisition choices.
    /// This may be filtered, re-routed, or affected by temporary world exclusions.
    /// </summary>
    public List<DetailedShoppingPlan> ProcurementShoppingPlans { get; set; } = new();
    public IReadOnlyList<MarketDataUnavailableItem> UnavailableMarketItems { get; private set; } = Array.Empty<MarketDataUnavailableItem>();
    public RecommendationMode RecommendationMode { get; set; } = RecommendationMode.MinimizeTotalCost;
    public MarketAcquisitionLens MarketAnalysisLens { get; set; } = MarketAcquisitionLens.MinimumUpfrontCost;
    
    /// <summary>
    /// Temporarily blacklisted worlds for the current session.
    /// These worlds are excluded from procurement recommendations.
    /// Cleared on page reload - NOT persisted.
    /// </summary>
    public HashSet<string> TemporarilyBlacklistedWorlds { get; set; } = new();

    /// <summary>
    /// Structured temporary market-world exclusions for region-wide procurement analysis.
    /// </summary>
    public HashSet<MarketWorldKey> TemporarilyBlacklistedMarketWorlds { get; set; } = new();

    public MarketWorldBlacklist TemporaryMarketWorldBlacklist { get; } = new();

    public HashSet<MarketItemWorldKey> TemporarilyExcludedItemWorlds { get; set; } = new();

    public int TemporaryWorldBlacklistDurationMinutes { get; set; } = 60;
    
    // Auto-expand item ID when navigating from procurement to market analysis
    public int? AutoExpandItemId { get; set; }
    
    // Persistence state
    public bool IsAutoSaveEnabled { get; set; } = true;
    public bool AutoFetchPricesOnRebuild { get; set; } = true;
    public MarketFetchScope DefaultMarketFetchScope { get; set; } = MarketFetchScope.SelectedDataCenter;
    public DateTime? LastAutoSave { get; set; }
    public List<StoredPlanSummary> SavedPlans { get; set; } = new();
    
    // Market Analysis Settings (persist across page navigations)
    public bool EnableMultiWorldSplits { get; set; } = false;
    public int MaxWorldsPerItem { get; set; } = 0; // 0 = unlimited
    public bool SearchEntireRegion { get; set; } = false;
    public MarketSortOption MarketSortPreference { get; set; } = MarketSortOption.ByRecommended;

    // Procurement Planning Settings
    public bool ProcurementSearchEntireRegion { get; set; } = false;
    public bool ProcurementEnableSplitWorldPurchases { get; set; } = false;
    public int ProcurementTravelTolerance { get; set; } = 0; // 0 = shortest route, 11 = cheapest
    
    // Current plan tracking for save-overwrite behavior
    public string? CurrentPlanId { get; set; }
    public string? CurrentPlanName { get; set; }
    
    // Status Bar State
    public string StatusMessage { get; set; } = "Ready";
    public bool IsBusy { get; set; } = false;
    public double ProgressPercent { get; set; } = 0;
    public string? CurrentOperation { get; set; } = null;
    public DateTime LastStatusUpdate { get; set; } = DateTime.Now;
    
    // Event to notify subscribers when plan changes
    public event Action? OnPlanChanged;
    public event Action? OnShoppingListChanged;
    public event Action? OnSavedPlansChanged;
    public event Action? OnStatusChanged;
    public event Action? OnRecipeTreeExpandChanged;
    public event Action<AppStateChange>? OnStateChanged;

    public AppStateVersionSnapshot CurrentVersions => new(
        _planStructureVersion,
        _planDecisionVersion,
        _planPriceVersion,
        _planCoreVersion,
        _marketAnalysisVersion,
        _procurementOverlayVersion,
        _settingsVersion,
        _statusVersion);
    
    // Recipe tree expand/collapse state (incremented to trigger re-evaluation)
    private int _recipeTreeExpandVersion = 0;
    private bool _recipeTreeAllExpanded = true;
    
    /// <summary>
    /// Current version of expand state. Components should watch this to sync.
    /// </summary>
    public int RecipeTreeExpandVersion => _recipeTreeExpandVersion;
    
    /// <summary>
    /// Whether all nodes should be expanded (true) or collapsed (false).
    /// </summary>
    public bool RecipeTreeAllExpanded => _recipeTreeAllExpanded;
    
    public void NotifyPlanChanged()
    {
        PublishChange(AppStateChangeScope.PlanStructure, raisePlanChanged: true);
    }
    
    public void NotifyShoppingListChanged()
    {
        PublishChange(
            AppStateChangeScope.MarketAnalysis | AppStateChangeScope.ShoppingItems,
            raiseShoppingListChanged: true);
    }

    public void NotifyShoppingItemsChanged()
    {
        PublishChange(AppStateChangeScope.ShoppingItems, raiseShoppingListChanged: true);
    }

    public void NotifyPlanDecisionChanged()
    {
        PublishChange(AppStateChangeScope.PlanDecision, raisePlanChanged: true);
    }

    public void NotifyPlanPriceChanged()
    {
        PublishChange(AppStateChangeScope.PlanPrice, raisePlanChanged: true);
    }

    public void NotifyProcurementOverlayChanged()
    {
        PublishChange(AppStateChangeScope.ProcurementOverlay, raiseShoppingListChanged: true);
    }

    public void NotifySettingsChanged()
    {
        PublishChange(AppStateChangeScope.Settings);
    }

    public void SetUnavailableMarketItems(IReadOnlyList<MarketDataUnavailableItem> items)
    {
        UnavailableMarketItems = items;
        NotifyShoppingListChanged();
    }

    public void ClearUnavailableMarketItems()
    {
        SetUnavailableMarketItems(Array.Empty<MarketDataUnavailableItem>());
    }

    public void ClearMarketAnalysisState()
    {
        ShoppingPlans.Clear();
        MarketItemAnalyses.Clear();
        ProcurementShoppingPlans.Clear();
        UnavailableMarketItems = Array.Empty<MarketDataUnavailableItem>();
        PublishChange(
            AppStateChangeScope.MarketAnalysis | AppStateChangeScope.ProcurementOverlay,
            raiseShoppingListChanged: true);
    }

    public void BlacklistMarketWorldTemporarily(MarketWorldKey world)
    {
        var duration = TimeSpan.FromMinutes(Math.Max(1, TemporaryWorldBlacklistDurationMinutes));
        TemporaryMarketWorldBlacklist.Add(world, duration);
        SyncTemporaryBlacklistSets();
        ProcurementShoppingPlans.Clear();
        NotifyProcurementOverlayChanged();
    }

    public void ExcludeItemWorldTemporarily(int itemId, MarketWorldKey world)
    {
        TemporarilyExcludedItemWorlds.Add(new MarketItemWorldKey(itemId, world));
        ProcurementShoppingPlans.Clear();
        NotifyProcurementOverlayChanged();
    }

    public int ActiveTemporaryExclusionCount =>
        GetActiveBlacklistedMarketWorlds().Count + TemporarilyExcludedItemWorlds.Count;

    public HashSet<MarketWorldKey> GetActiveBlacklistedMarketWorlds()
    {
        SyncTemporaryBlacklistSets();
        return TemporarilyBlacklistedMarketWorlds.ToHashSet();
    }

    public HashSet<string> GetActiveBlacklistedWorldNames()
    {
        SyncTemporaryBlacklistSets();
        return TemporarilyBlacklistedWorlds.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetExpectedMarketWorlds(MarketFetchScope scope)
    {
        var dataCenters = MarketFetchScopeResolver.GetDataCenters(
            scope,
            SelectedDataCenter,
            SelectedRegion);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var dataCenter in dataCenters)
        {
            if (WorldData?.DataCenterToWorlds.TryGetValue(dataCenter, out var worlds) == true)
            {
                result[dataCenter] = worlds;
            }
        }

        return result;
    }

    public void ClearTemporaryMarketWorldBlacklists()
    {
        TemporaryMarketWorldBlacklist.Clear();
        TemporarilyBlacklistedMarketWorlds.Clear();
        TemporarilyBlacklistedWorlds.Clear();
        TemporarilyExcludedItemWorlds.Clear();
        ProcurementShoppingPlans.Clear();
        NotifyProcurementOverlayChanged();
    }

    public bool PruneExpiredTemporaryMarketWorldBlacklists()
    {
        var previousCount = TemporarilyBlacklistedMarketWorlds.Count;
        SyncTemporaryBlacklistSets();
        if (TemporarilyBlacklistedMarketWorlds.Count == previousCount)
        {
            return false;
        }

        ProcurementShoppingPlans.Clear();
        NotifyProcurementOverlayChanged();
        return true;
    }
    
    public void NotifySavedPlansChanged()
    {
        OnSavedPlansChanged?.Invoke();
    }
    
    public void NotifyStatusChanged()
    {
        LastStatusUpdate = DateTime.Now;
        PublishChange(AppStateChangeScope.Status, raiseStatusChanged: true);
    }
    
    /// <summary>
    /// Expand all nodes in the recipe tree.
    /// </summary>
    public void ExpandAllRecipeNodes()
    {
        _recipeTreeAllExpanded = true;
        _recipeTreeExpandVersion++;
        OnRecipeTreeExpandChanged?.Invoke();
    }
    
    /// <summary>
    /// Collapse all nodes in the recipe tree.
    /// </summary>
    public void CollapseAllRecipeNodes()
    {
        _recipeTreeAllExpanded = false;
        _recipeTreeExpandVersion++;
        OnRecipeTreeExpandChanged?.Invoke();
    }
    
    /// <summary>
    /// Set status message and optionally show busy state.
    /// </summary>
    public void SetStatus(string message, bool busy = false, double? progress = null)
    {
        StatusMessage = message;
        IsBusy = busy;
        if (progress.HasValue)
        {
            ProgressPercent = Math.Clamp(progress.Value, 0, 100);
        }
        else if (!busy)
        {
            ProgressPercent = 0;
        }
        NotifyStatusChanged();
    }
    
    /// <summary>
    /// Start a long-running operation with a name.
    /// </summary>
    public void BeginOperation(string operationName, string? message = null)
    {
        CurrentOperation = operationName;
        SetStatus(message ?? $"{operationName}...", busy: true);
    }
    
    /// <summary>
    /// End the current operation.
    /// </summary>
    public void EndOperation(string? message = null)
    {
        CurrentOperation = null;
        IsBusy = false;
        ProgressPercent = 0;
        // Set status directly to avoid any race conditions with progress callbacks
        StatusMessage = message ?? "Ready";
        NotifyStatusChanged();
    }
    
    /// <summary>
    /// Update progress of current operation (0-100).
    /// </summary>
    public void UpdateProgress(double percent, string? message = null)
    {
        ProgressPercent = Math.Clamp(percent, 0, 100);
        if (!string.IsNullOrEmpty(message))
        {
            StatusMessage = message;
        }
        NotifyStatusChanged();
    }
    
    /// <summary>
    /// Convert shopping items to project items for Recipe Planner
    /// </summary>
    public void SyncShoppingToProject()
    {
        ProjectItems = ShoppingItems.Select(s => new ProjectItem
        {
            Id = s.Id,
            Name = s.Name,
            IconId = s.IconId,
            Quantity = s.Quantity,
            MustBeHq = false
        }).ToList();
        NotifyPlanChanged();
    }
    
    /// <summary>
    /// Convert project items to shopping items for Market Logistics
    /// </summary>
    public void SyncProjectToShopping()
    {
        ShoppingItems = ProjectItems.Select(p => new MarketShoppingItem
        {
            Id = p.Id,
            Name = p.Name,
            IconId = p.IconId,
            Quantity = p.Quantity
        }).ToList();
        NotifyShoppingListChanged();
    }

    /// <summary>
    /// Clear all current plan data.
    /// </summary>
    public void ClearPlan()
    {
        CurrentPlan = null;
        ProjectItems.Clear();
        ShoppingItems.Clear();
        ClearMarketAnalysisState();
        CurrentPlanId = null;  // Reset plan ID for new plan
        CurrentPlanName = null;
        NotifyPlanChanged();
        NotifyShoppingListChanged();
    }

    public StoredPlan CreateStoredPlanSnapshot(
        string planId,
        string planName,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false)
    {
        return StoredPlanSnapshotBuilder.Build(
            this,
            planId,
            planName,
            savedAt,
            includeSourcePlanIdentity);
    }

    /// <summary>
    /// Load a stored plan into the current state.
    /// </summary>
    public void LoadStoredPlan(
        StoredPlan storedPlan,
        CraftingPlan? deserializedPlan,
        bool trackStoredPlanIdentity = true)
    {
        ApplyLoadedPlanSession(
            PlanSessionLoadService.Prepare(storedPlan, deserializedPlan),
            trackStoredPlanIdentity);
    }

    public void ApplyLoadedPlanSession(
        PlanSessionLoadResult session,
        bool trackStoredPlanIdentity = true)
    {
        using var batch = BeginStateChangeBatch();
        var storedPlan = session.StoredPlan;

        SelectedDataCenter = storedPlan.DataCenter;
        ProjectItems = session.ProjectItems.ToList();
        CurrentPlan = session.Plan;
        MarketItemAnalyses = session.MarketItemAnalyses.ToList();
        ShoppingPlans = session.ShoppingPlans.ToList();
        RecommendationMode = storedPlan.SavedRecommendationMode;
        MarketAnalysisLens = storedPlan.SavedMarketAnalysisLens;
        ProcurementShoppingPlans.Clear();
        
        // Track the loaded plan ID for save-overwrite behavior
        if (trackStoredPlanIdentity)
        {
            CurrentPlanId = storedPlan.Id;
            CurrentPlanName = storedPlan.Name;
        }
        else
        {
            CurrentPlanId = storedPlan.SourcePlanId;
            CurrentPlanName = storedPlan.SourcePlanName;
        }

        ShoppingItems.Clear();
        SyncProjectToShopping();

        NotifyPlanChanged();
        NotifyShoppingListChanged();
    }

    public IDisposable BeginStateChangeBatch()
    {
        _changeBatchDepth++;
        return new StateChangeBatch(this);
    }

    public PersistedStateBucket GetDirtyPersistedBuckets()
    {
        var dirtyBuckets = PersistedStateBucket.None;

        if (IsDirtyVersion(_planCoreVersion, _lastPersistedPlanCoreVersion, CurrentPlan != null || ProjectItems.Any()))
        {
            dirtyBuckets |= PersistedStateBucket.PlanCore;
        }

        if (IsDirtyVersion(_marketAnalysisVersion, _lastPersistedMarketAnalysisVersion, ShoppingPlans.Any() || MarketItemAnalyses.Any()))
        {
            dirtyBuckets |= PersistedStateBucket.MarketAnalysis;
        }

        return dirtyBuckets;
    }

    public bool IsPersistedBucketDirty(PersistedStateBucket bucket)
    {
        return (GetDirtyPersistedBuckets() & bucket) != PersistedStateBucket.None;
    }

    public void MarkPersisted(PersistedStateBucket buckets, AppStateVersionSnapshot versions)
    {
        if (buckets.HasFlag(PersistedStateBucket.PlanCore) &&
            versions.PlanCoreVersion == _planCoreVersion)
        {
            _lastPersistedPlanCoreVersion = versions.PlanCoreVersion;
        }

        if (buckets.HasFlag(PersistedStateBucket.MarketAnalysis) &&
            versions.MarketAnalysisVersion == _marketAnalysisVersion)
        {
            _lastPersistedMarketAnalysisVersion = versions.MarketAnalysisVersion;
        }
    }

    public bool TryBeginAutoSave(
        out AppStateVersionSnapshot capturedVersions,
        out PersistedStateBucket dirtyBuckets)
    {
        capturedVersions = CurrentVersions;
        dirtyBuckets = PersistedStateBucket.None;

        if (CurrentPlan == null && !ProjectItems.Any())
        {
            return false;
        }

        if (!_autoSaveSemaphore.WaitAsync(0).GetAwaiter().GetResult())
        {
            return false;
        }

        dirtyBuckets = GetDirtyPersistedBuckets();
        if (dirtyBuckets == PersistedStateBucket.None)
        {
            _autoSaveSemaphore.Release();
            return false;
        }

        return true;
    }

    public async Task<AppStateAutoSaveLease?> BeginAutoSaveAsync(bool skipIfInFlight = false)
    {
        if (CurrentPlan == null && !ProjectItems.Any())
        {
            return null;
        }

        bool acquired;
        if (skipIfInFlight)
        {
            acquired = await _autoSaveSemaphore.WaitAsync(0);
        }
        else
        {
            await _autoSaveSemaphore.WaitAsync();
            acquired = true;
        }

        if (!acquired)
        {
            return null;
        }

        var capturedVersions = CurrentVersions;
        var dirtyBuckets = GetDirtyPersistedBuckets();
        if (dirtyBuckets == PersistedStateBucket.None)
        {
            _autoSaveSemaphore.Release();
            return null;
        }

        return new AppStateAutoSaveLease(capturedVersions, dirtyBuckets);
    }

    public void CompleteAutoSave(
        bool succeeded,
        AppStateVersionSnapshot capturedVersions,
        PersistedStateBucket dirtyBuckets)
    {
        try
        {
            if (succeeded)
            {
                MarkPersisted(dirtyBuckets, capturedVersions);
            }
        }
        finally
        {
            _autoSaveSemaphore.Release();
        }
    }

    /// <summary>
    /// Clear the current plan ID (called when starting a new plan or after explicit "Save As")
    /// </summary>
    public void ClearCurrentPlanId()
    {
        CurrentPlanId = null;
        CurrentPlanName = null;
    }
    
    /// <summary>
    /// Cached world data for data center/world selection.
    /// Loaded once and shared across all pages.
    /// </summary>
    public WorldData? WorldData { get; set; }
    
    /// <summary>
    /// Auto-save timer reference for cleanup.
    /// </summary>
    public System.Threading.Timer? AutoSaveTimer { get; set; }
    
    /// <summary>
    /// Initialize the world data cache.
    /// </summary>
    public async Task InitializeWorldDataAsync(UniversalisService universalisService)
    {
        if (WorldData != null) return;
        
        try
        {
            WorldData = await universalisService.GetWorldDataAsync();
            
            if (string.IsNullOrEmpty(SelectedDataCenter))
            {
                SelectedDataCenter = WorldData.DataCenters.FirstOrDefault() ?? "Aether";
            }
        }
        catch
        {
            // Fallback to hardcoded data centers
            WorldData = new WorldData
            {
                DataCenterToWorlds = new Dictionary<string, List<string>>
                {
                    ["Aether"] = new() { "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren" },
                    ["Primal"] = new() { "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros" },
                    ["Crystal"] = new() { "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera" },
                    ["Dynamis"] = new() { "Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph" }
                }
            };
            SelectedDataCenter = "Aether";
        }
    }
    
    /// <summary>
    /// Start the auto-save timer.
    /// </summary>
    public void StartAutoSaveTimer(Func<Task> saveCallback, int intervalSeconds = 30)
    {
        StopAutoSaveTimer();
        
        AutoSaveTimer = new System.Threading.Timer(
            async _ => await saveCallback(),
            null,
            TimeSpan.FromSeconds(intervalSeconds),
            TimeSpan.FromSeconds(intervalSeconds));
    }
    
    /// <summary>
    /// Stop the auto-save timer.
    /// </summary>
    public void StopAutoSaveTimer()
    {
        AutoSaveTimer?.Dispose();
        AutoSaveTimer = null;
    }

    private void SyncTemporaryBlacklistSets()
    {
        TemporarilyBlacklistedMarketWorlds = TemporaryMarketWorldBlacklist.GetActiveWorlds();
        TemporarilyBlacklistedWorlds = TemporarilyBlacklistedMarketWorlds
            .Select(world => world.WorldName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsDirtyVersion(long currentVersion, long persistedVersion, bool hasPersistableData)
    {
        return currentVersion != persistedVersion &&
               (persistedVersion >= 0 || currentVersion > 0 || hasPersistableData);
    }

    private void PublishChange(
        AppStateChangeScope scopes,
        bool raisePlanChanged = false,
        bool raiseShoppingListChanged = false,
        bool raiseStatusChanged = false)
    {
        if (_changeBatchDepth > 0)
        {
            var newScopes = scopes & ~_batchedScopes;
            IncrementVersions(newScopes);
            _batchedScopes |= scopes;
            _raisePlanChanged |= raisePlanChanged;
            _raiseShoppingListChanged |= raiseShoppingListChanged;
            _raiseStatusChanged |= raiseStatusChanged;
            return;
        }

        IncrementVersions(scopes);
        EmitChange(scopes, raisePlanChanged, raiseShoppingListChanged, raiseStatusChanged);
    }

    private void IncrementVersions(AppStateChangeScope scopes)
    {
        if (scopes.HasFlag(AppStateChangeScope.PlanStructure))
        {
            _planStructureVersion++;
        }

        if (scopes.HasFlag(AppStateChangeScope.PlanDecision))
        {
            _planDecisionVersion++;
        }

        if (scopes.HasFlag(AppStateChangeScope.PlanPrice))
        {
            _planPriceVersion++;
        }

        if (scopes.HasFlag(AppStateChangeScope.MarketAnalysis))
        {
            _marketAnalysisVersion++;
        }

        if (scopes.HasFlag(AppStateChangeScope.ProcurementOverlay))
        {
            _procurementOverlayVersion++;
        }

        if (scopes.HasFlag(AppStateChangeScope.Settings))
        {
            _settingsVersion++;
        }

        if (scopes.HasFlag(AppStateChangeScope.Status))
        {
            _statusVersion++;
        }

        if ((scopes & AppStateChangeScope.PlanCore) != AppStateChangeScope.None)
        {
            _planCoreVersion++;
        }
    }

    private void EndStateChangeBatch()
    {
        if (_changeBatchDepth == 0)
        {
            return;
        }

        _changeBatchDepth--;
        if (_changeBatchDepth > 0 || _batchedScopes == AppStateChangeScope.None)
        {
            return;
        }

        var scopes = _batchedScopes;
        var raisePlanChanged = _raisePlanChanged;
        var raiseShoppingListChanged = _raiseShoppingListChanged;
        var raiseStatusChanged = _raiseStatusChanged;

        _batchedScopes = AppStateChangeScope.None;
        _raisePlanChanged = false;
        _raiseShoppingListChanged = false;
        _raiseStatusChanged = false;

        EmitChange(scopes, raisePlanChanged, raiseShoppingListChanged, raiseStatusChanged);
    }

    private void EmitChange(
        AppStateChangeScope scopes,
        bool raisePlanChanged,
        bool raiseShoppingListChanged,
        bool raiseStatusChanged)
    {
        OnStateChanged?.Invoke(new AppStateChange(scopes, CurrentVersions));

        if (raisePlanChanged)
        {
            OnPlanChanged?.Invoke();
        }

        if (raiseShoppingListChanged)
        {
            OnShoppingListChanged?.Invoke();
        }

        if (raiseStatusChanged)
        {
            OnStatusChanged?.Invoke();
        }
    }

    private sealed class StateChangeBatch : IDisposable
    {
        private readonly AppState _state;
        private bool _disposed;

        public StateChangeBatch(AppState state)
        {
            _state = state;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _state.EndStateChangeBatch();
        }
    }

}

[Flags]
public enum AppStateChangeScope
{
    None = 0,
    PlanStructure = 1 << 0,
    PlanDecision = 1 << 1,
    PlanPrice = 1 << 2,
    MarketAnalysis = 1 << 3,
    ProcurementOverlay = 1 << 4,
    ShoppingItems = 1 << 5,
    Settings = 1 << 6,
    Status = 1 << 7,
    PlanCore = PlanStructure | PlanDecision | PlanPrice | Settings
}

[Flags]
public enum PersistedStateBucket
{
    None = 0,
    PlanCore = 1 << 0,
    MarketAnalysis = 1 << 1,
    All = PlanCore | MarketAnalysis
}

public sealed record AppStateVersionSnapshot(
    long PlanStructureVersion,
    long PlanDecisionVersion,
    long PlanPriceVersion,
    long PlanCoreVersion,
    long MarketAnalysisVersion,
    long ProcurementOverlayVersion,
    long SettingsVersion,
    long StatusVersion);

public sealed record AppStateChange(
    AppStateChangeScope Scopes,
    AppStateVersionSnapshot Versions)
{
    public bool HasScope(AppStateChangeScope scope)
    {
        return (Scopes & scope) == scope;
    }
}

public sealed record AppStateAutoSaveLease(
    AppStateVersionSnapshot CapturedVersions,
    PersistedStateBucket DirtyBuckets);

// PlannerProjectItem removed - now using FFXIV_Craft_Architect.Core.Models.ProjectItem

public class MarketShoppingItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    public int Quantity { get; set; }
}

/// <summary>
/// Summary info for the saved plans list.
/// </summary>
public class StoredPlanSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime ModifiedAt { get; set; }
    public DateTime SavedAt { get; set; }
    public string DataCenter { get; set; } = string.Empty;
    public int ItemCount { get; set; }
}
