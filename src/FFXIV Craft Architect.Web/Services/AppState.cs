using System.Collections.Frozen;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

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
///    - User edits project items through AppState methods
///    - User clicks "Build Plan" → RecipeCalculationService.BuildPlanAsync()
///    - Plan changes advance PlanSessionVersion so stale async work can be ignored
///    - Plan-scoped notifications trigger UI updates
///
/// 2. Market Analysis (MarketAnalysis.razor):
///    - Reads CurrentPlan materials
///    - MarketAnalysisWorkflowService publishes evidence through AppState replacement methods
///    - Stale analysis output is ignored when the active plan session or market context changes
///    - Shopping-list notifications trigger result updates
///
/// 3. Procurement Plan (ProcurementPlan.razor):
///    - Reads market evidence from AppState
///    - ProcurementWorkflowService publishes route overlays through AppState
///    - Temporary exclusions are session-only and clear derived procurement overlays
///
/// 4. Auto-Save Flow:
///    - Timer lifecycle is owned by AppState and started by MainLayout
///    - Dirty persisted buckets are computed from AppState version counters
///    - Saves plan core, market analysis, and settings through IndexedDB
///
/// STATE CATEGORIES:
/// - Recipe Planner: CurrentPlan, ProjectItems snapshots
/// - Procurement: ShoppingItems, ShoppingPlans, ProcurementShoppingPlans
/// - Settings: SelectedDataCenter, RecommendationMode, market scope preferences
/// - UI State: StatusMessage, IsBusy, ProgressPercent
/// - Session: TemporarilyBlacklistedWorlds (NOT persisted)
///
/// EVENTS:
/// Components subscribe to events for reactive updates:
/// - OnPlanChanged: Recipe tree structure modified
/// - OnShoppingListChanged: Market analysis, shopping items, or procurement overlay updated
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
    private long _planSessionVersion;
    private long _marketAnalysisVersion;
    private long _procurementOverlayVersion;
    private long _settingsVersion;
    private long _statusVersion;
    private long _nextOperationId;
    private long? _currentOperationId;
    private long _lastPersistedPlanCoreVersion = -1;
    private long _lastPersistedMarketAnalysisVersion = -1;
    private readonly SemaphoreSlim _autoSaveSemaphore = new(1, 1);
    private int _changeBatchDepth;
    private AppStateChangeScope _batchedScopes = AppStateChangeScope.None;
    private bool _raisePlanChanged;
    private bool _raiseShoppingListChanged;
    private bool _raiseStatusChanged;
    private System.Threading.Timer? _autoSaveTimer;
    private readonly List<DetailedShoppingPlan> _shoppingPlans = [];
    private readonly List<MarketItemAnalysis> _marketItemAnalyses = [];
    private readonly List<DetailedShoppingPlan> _procurementShoppingPlans = [];
    private readonly List<MarketShoppingItem> _shoppingItems = [];
    private readonly List<ProjectItem> _projectItems = [];
    private HashSet<string> _temporarilyBlacklistedWorlds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<MarketWorldKey> _temporarilyBlacklistedMarketWorlds = [];
    private HashSet<MarketItemWorldKey> _temporarilyExcludedItemWorlds = [];
    private IReadOnlySet<string> _temporarilyBlacklistedWorldsView = new HashSet<string>(StringComparer.OrdinalIgnoreCase).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<MarketWorldKey> _temporarilyBlacklistedMarketWorldsView = FrozenSet<MarketWorldKey>.Empty;
    private IReadOnlySet<MarketItemWorldKey> _temporarilyExcludedItemWorldsView = FrozenSet<MarketItemWorldKey>.Empty;

    // Recipe Planner State
    public CraftingPlan? CurrentPlan { get; private set; }
    public IReadOnlyList<ProjectItem> ProjectItems => _projectItems.Select(CloneProjectItem).ToArray();
    public int ProjectItemCount => _projectItems.Count;
    public bool HasProjectItems => _projectItems.Count > 0;
    public bool HasPlanOrProjectItems => CurrentPlan != null || _projectItems.Count > 0;
    public string SelectedDataCenter { get; private set; } = "Aether";
    public string SelectedRegion { get; private set; } = "North America";
    
    // Procurement Planner State
    public IReadOnlyList<MarketShoppingItem> ShoppingItems => _shoppingItems.AsReadOnly();

    /// <summary>
    /// Full market analysis evidence for every market-listable candidate in the recipe plan.
    /// Procurement may derive from this list, but must not delete or replace entries for inactive choices.
    /// </summary>
    public IReadOnlyList<DetailedShoppingPlan> ShoppingPlans => _shoppingPlans.AsReadOnly();

    /// <summary>
    /// Immutable market-analysis analytics for every market-listable candidate in the recipe plan.
    /// Acquisition and procurement views may project from this data, but should not mutate or filter it.
    /// </summary>
    public IReadOnlyList<MarketItemAnalysis> MarketItemAnalyses => _marketItemAnalyses.AsReadOnly();

    /// <summary>
    /// Mutable procurement overlay derived from ShoppingPlans and current acquisition choices.
    /// This may be filtered, re-routed, or affected by temporary world exclusions.
    /// </summary>
    public IReadOnlyList<DetailedShoppingPlan> ProcurementShoppingPlans => _procurementShoppingPlans.AsReadOnly();
    public IReadOnlyList<MarketDataUnavailableItem> UnavailableMarketItems { get; private set; } = Array.Empty<MarketDataUnavailableItem>();
    public RecommendationMode RecommendationMode { get; private set; } = RecommendationMode.MinimizeTotalCost;
    public MarketAcquisitionLens MarketAnalysisLens { get; private set; } = MarketAcquisitionLens.MinimumUpfrontCost;
    
    /// <summary>
    /// Temporarily blacklisted worlds for the current session.
    /// These worlds are excluded from procurement recommendations.
    /// Cleared on page reload - NOT persisted.
    /// </summary>
    public IReadOnlySet<string> TemporarilyBlacklistedWorlds => _temporarilyBlacklistedWorldsView;

    /// <summary>
    /// Structured temporary market-world exclusions for region-wide procurement analysis.
    /// </summary>
    public IReadOnlySet<MarketWorldKey> TemporarilyBlacklistedMarketWorlds => _temporarilyBlacklistedMarketWorldsView;

    public MarketWorldBlacklist TemporaryMarketWorldBlacklist { get; } = new();

    public IReadOnlySet<MarketItemWorldKey> TemporarilyExcludedItemWorlds => _temporarilyExcludedItemWorldsView;

    public int TemporaryWorldBlacklistDurationMinutes { get; private set; } = 60;
    
    // Auto-expand item ID when navigating from procurement to market analysis
    public int? AutoExpandItemId { get; private set; }
    
    // Persistence state
    public bool IsAutoSaveEnabled { get; private set; } = true;
    public bool AutoFetchPricesOnRebuild { get; private set; } = true;
    public MarketFetchScope DefaultMarketFetchScope { get; private set; } = MarketFetchScope.SelectedDataCenter;
    public DateTime? LastAutoSave { get; private set; }
    public IReadOnlyList<StoredPlanSummary> SavedPlans { get; private set; } = Array.AsReadOnly(Array.Empty<StoredPlanSummary>());
    
    // Market Analysis Settings (persist across page navigations)
    public bool EnableMultiWorldSplits { get; private set; } = false;
    public int MaxWorldsPerItem { get; private set; } = 0; // 0 = unlimited
    public bool SearchEntireRegion { get; private set; } = false;
    public MarketSortOption MarketSortPreference { get; private set; } = MarketSortOption.ByRecommended;

    // Procurement Planning Settings
    public bool ProcurementSearchEntireRegion { get; private set; } = false;
    public bool ProcurementEnableSplitWorldPurchases { get; private set; } = false;
    public int ProcurementTravelTolerance { get; private set; } = 0; // 0 = shortest route, 11 = cheapest
    
    // Current plan tracking for save-overwrite behavior
    public string? CurrentPlanId { get; private set; }
    public string? CurrentPlanName { get; private set; }
    
    // Status Bar State
    public string StatusMessage { get; private set; } = "Ready";
    public bool IsBusy { get; private set; } = false;
    public double ProgressPercent { get; private set; } = 0;
    public string? CurrentOperation { get; private set; } = null;
    public DateTime LastStatusUpdate { get; private set; } = DateTime.Now;
    
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

    public long PlanSessionVersion => _planSessionVersion;
    
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

    public bool AddProjectItem(ProjectItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_projectItems.Any(existing => existing.Id == item.Id))
        {
            return false;
        }

        _projectItems.Add(CloneProjectItem(item));
        NotifyPlanChanged();
        return true;
    }

    public bool RemoveProjectItem(ProjectItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var existingItem = _projectItems.FirstOrDefault(existing => existing.Id == item.Id);
        var removed = existingItem != null && _projectItems.Remove(existingItem);
        if (removed)
        {
            NotifyPlanChanged();
        }

        return removed;
    }

    public void ReplaceProjectItems(IEnumerable<ProjectItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        ReplaceListContents(_projectItems, items.Select(CloneProjectItem));
        NotifyPlanChanged();
    }

    public void ToggleProjectItemHq(ProjectItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var existingItem = _projectItems.FirstOrDefault(existing => existing.Id == item.Id);
        if (existingItem == null)
        {
            return;
        }

        existingItem.MustBeHq = !existingItem.MustBeHq;
        NotifyPlanChanged();
    }

    public bool UpdateProjectItemQuantity(ProjectItem item, int quantity)
    {
        ArgumentNullException.ThrowIfNull(item);

        var existingItem = _projectItems.FirstOrDefault(existing => existing.Id == item.Id);
        if (existingItem == null)
        {
            return false;
        }

        var normalizedQuantity = Math.Clamp(quantity, 1, 9999);
        if (existingItem.Quantity == normalizedQuantity)
        {
            return false;
        }

        existingItem.Quantity = normalizedQuantity;
        NotifyPlanChanged();
        return true;
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

    public void ReplaceMarketAnalysis(
        IEnumerable<MarketItemAnalysis> analyses,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        ArgumentNullException.ThrowIfNull(shoppingPlans);

        ReplaceListContents(_marketItemAnalyses, analyses);
        ReplaceListContents(_shoppingPlans, shoppingPlans);
        _procurementShoppingPlans.Clear();
        using (BeginStateChangeBatch())
        {
            NotifyShoppingListChanged();
            NotifyProcurementOverlayChanged();
        }
    }

    public void ReplaceMarketAnalysisItem(
        MarketItemAnalysis analysis,
        DetailedShoppingPlan shoppingPlan)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(shoppingPlan);

        ReplaceListContents(_marketItemAnalyses, ReplaceAnalysisByItemId(_marketItemAnalyses, analysis));
        ReplaceListContents(_shoppingPlans, ReplaceShoppingPlanByItemId(_shoppingPlans, shoppingPlan));
        _procurementShoppingPlans.Clear();
        using (BeginStateChangeBatch())
        {
            NotifyShoppingListChanged();
            NotifyProcurementOverlayChanged();
        }
    }

    public void ClearProcurementOverlay()
    {
        _procurementShoppingPlans.Clear();
        NotifyProcurementOverlayChanged();
    }

    public void ReplaceProcurementOverlay(IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        ReplaceListContents(_procurementShoppingPlans, shoppingPlans);
        NotifyProcurementOverlayChanged();
    }

    public void ReplaceShoppingItemsFromActivePlan(IReadOnlyList<MaterialAggregate>? activeProcurementItems = null)
    {
        ReplaceListContents(_shoppingItems, (activeProcurementItems ?? AcquisitionPlanningService.GetActiveProcurementItems(CurrentPlan))
            .Where(item => item.TotalQuantity > 0)
            .Select(item => new MarketShoppingItem
            {
                Id = item.ItemId,
                Name = item.Name,
                IconId = item.IconId,
                Quantity = item.TotalQuantity
            }));
        NotifyShoppingItemsChanged();
    }

    public void ApplyBuiltRecipePlan(CraftingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        CurrentPlan = plan;
        AdvancePlanSession();
        AutoExpandItemId = null;
        using (BeginStateChangeBatch())
        {
            ClearMarketAnalysisState();
            ReplaceShoppingItemsFromActivePlan();
            NotifyPlanChanged();
        }
    }

    public void ApplyImportedProjectItems(IEnumerable<ProjectItem> projectItems)
    {
        ArgumentNullException.ThrowIfNull(projectItems);

        using (BeginStateChangeBatch())
        {
            ReplaceProjectItems(projectItems);
            CurrentPlan = null;
            AdvancePlanSession();
            AutoExpandItemId = null;
            ClearCurrentPlanId();
            _shoppingItems.Clear();
            NotifyShoppingItemsChanged();
            ClearMarketAnalysisState();
        }
    }

    public void ActivateRecipePlan(
        CraftingPlan plan,
        IEnumerable<ProjectItem> projectItems,
        string? selectedDataCenter,
        bool clearCurrentPlanId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(projectItems);

        using (BeginStateChangeBatch())
        {
            CurrentPlan = plan;
            AdvancePlanSession();
            AutoExpandItemId = null;
            ReplaceProjectItems(projectItems);

            if (!string.IsNullOrWhiteSpace(selectedDataCenter))
            {
                SelectedDataCenter = selectedDataCenter;
            }

            if (clearCurrentPlanId)
            {
                ClearCurrentPlanId();
            }

            ClearMarketAnalysisState();
            ReplaceShoppingItemsFromActivePlan();
        }
    }

    public bool IsCurrentPlanSession(CraftingPlan? plan, long planSessionVersion)
    {
        return ReferenceEquals(CurrentPlan, plan) &&
               _planSessionVersion == planSessionVersion;
    }

    public void NotifySettingsChanged()
    {
        PublishChange(AppStateChangeScope.Settings);
    }

    public void SetMarketEvidenceSettings(
        string dataCenter,
        string region,
        MarketFetchScope defaultFetchScope,
        bool searchEntireRegion,
        bool autoFetchPricesOnRebuild)
    {
        var changesMarketContext =
            !string.Equals(SelectedDataCenter, dataCenter, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(SelectedRegion, region, StringComparison.OrdinalIgnoreCase) ||
            SearchEntireRegion != searchEntireRegion;
        var changesSettings =
            changesMarketContext ||
            DefaultMarketFetchScope != defaultFetchScope ||
            AutoFetchPricesOnRebuild != autoFetchPricesOnRebuild;

        if (!changesSettings)
        {
            return;
        }

        SelectedDataCenter = dataCenter;
        SelectedRegion = region;
        DefaultMarketFetchScope = defaultFetchScope;
        SearchEntireRegion = searchEntireRegion;
        AutoFetchPricesOnRebuild = autoFetchPricesOnRebuild;

        if (changesMarketContext)
        {
            _shoppingPlans.Clear();
            _marketItemAnalyses.Clear();
            _procurementShoppingPlans.Clear();
            UnavailableMarketItems = Array.Empty<MarketDataUnavailableItem>();
            PublishChange(
                AppStateChangeScope.Settings |
                AppStateChangeScope.MarketAnalysis |
                AppStateChangeScope.ProcurementOverlay,
                raiseShoppingListChanged: true);
            return;
        }

        NotifySettingsChanged();
    }

    public void SetProcurementSettings(
        bool searchEntireRegion,
        bool enableSplitWorldPurchases,
        int travelTolerance,
        int temporaryWorldBlacklistDurationMinutes)
    {
        var normalizedTravelTolerance = Math.Clamp(travelTolerance, 0, 11);
        var normalizedDuration = Math.Max(1, temporaryWorldBlacklistDurationMinutes);
        var changesRouteMeaning =
            ProcurementSearchEntireRegion != searchEntireRegion ||
            ProcurementEnableSplitWorldPurchases != enableSplitWorldPurchases ||
            ProcurementTravelTolerance != normalizedTravelTolerance;
        var changesSettings =
            changesRouteMeaning ||
            TemporaryWorldBlacklistDurationMinutes != normalizedDuration;

        if (!changesSettings)
        {
            return;
        }

        ProcurementSearchEntireRegion = searchEntireRegion;
        ProcurementEnableSplitWorldPurchases = enableSplitWorldPurchases;
        ProcurementTravelTolerance = normalizedTravelTolerance;
        TemporaryWorldBlacklistDurationMinutes = normalizedDuration;

        if (changesRouteMeaning)
        {
            _procurementShoppingPlans.Clear();
            PublishChange(
                AppStateChangeScope.Settings | AppStateChangeScope.ProcurementOverlay,
                raiseShoppingListChanged: true);
            return;
        }

        NotifySettingsChanged();
    }

    public bool SetMarketAnalysisLens(MarketAcquisitionLens lens)
    {
        if (MarketAnalysisLens == lens)
        {
            return false;
        }

        MarketAnalysisLens = lens;
        NotifySettingsChanged();
        return true;
    }

    public bool SetRecommendationMode(RecommendationMode mode)
    {
        if (RecommendationMode == mode)
        {
            return false;
        }

        RecommendationMode = mode;
        NotifySettingsChanged();
        return true;
    }

    public bool SetMarketSplitSettings(bool enableMultiWorldSplits, int maxWorldsPerItem)
    {
        var normalizedMaxWorlds = Math.Max(0, maxWorldsPerItem);
        if (EnableMultiWorldSplits == enableMultiWorldSplits &&
            MaxWorldsPerItem == normalizedMaxWorlds)
        {
            return false;
        }

        EnableMultiWorldSplits = enableMultiWorldSplits;
        MaxWorldsPerItem = normalizedMaxWorlds;
        _procurementShoppingPlans.Clear();
        PublishChange(
            AppStateChangeScope.Settings | AppStateChangeScope.ProcurementOverlay,
            raiseShoppingListChanged: true);
        return true;
    }

    public bool SetAutoSaveEnabled(bool enabled)
    {
        if (IsAutoSaveEnabled == enabled)
        {
            return false;
        }

        IsAutoSaveEnabled = enabled;
        NotifySettingsChanged();
        return true;
    }

    public bool SetMarketSortPreference(MarketSortOption preference)
    {
        if (MarketSortPreference == preference)
        {
            return false;
        }

        MarketSortPreference = preference;
        NotifySettingsChanged();
        return true;
    }

    public void SetUnavailableMarketItems(IReadOnlyList<MarketDataUnavailableItem> items)
    {
        UnavailableMarketItems = items.ToArray();
        NotifyShoppingListChanged();
    }

    public void ClearUnavailableMarketItems()
    {
        SetUnavailableMarketItems(Array.Empty<MarketDataUnavailableItem>());
    }

    public void RequestMarketItemAutoExpand(int itemId)
    {
        AutoExpandItemId = itemId;
    }

    public bool ConsumeMarketItemAutoExpand(int itemId)
    {
        if (AutoExpandItemId != itemId)
        {
            return false;
        }

        AutoExpandItemId = null;
        return true;
    }

    public void ClearMarketAnalysisState()
    {
        _shoppingPlans.Clear();
        _marketItemAnalyses.Clear();
        _procurementShoppingPlans.Clear();
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
        ClearProcurementOverlay();
    }

    public void ExcludeItemWorldTemporarily(int itemId, MarketWorldKey world)
    {
        _temporarilyExcludedItemWorlds.Add(new MarketItemWorldKey(itemId, world));
        RefreshTemporaryExclusionViews();
        ClearProcurementOverlay();
    }

    public int ActiveTemporaryExclusionCount =>
        GetActiveBlacklistedMarketWorlds().Count + _temporarilyExcludedItemWorlds.Count;

    public HashSet<MarketWorldKey> GetActiveBlacklistedMarketWorlds()
    {
        SyncTemporaryBlacklistSets();
        return _temporarilyBlacklistedMarketWorlds.ToHashSet();
    }

    public HashSet<string> GetActiveBlacklistedWorldNames()
    {
        SyncTemporaryBlacklistSets();
        return _temporarilyBlacklistedWorlds.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        _temporarilyBlacklistedMarketWorlds.Clear();
        _temporarilyBlacklistedWorlds.Clear();
        _temporarilyExcludedItemWorlds.Clear();
        RefreshTemporaryExclusionViews();
        ClearProcurementOverlay();
    }

    public bool PruneExpiredTemporaryMarketWorldBlacklists()
    {
        var previousCount = _temporarilyBlacklistedMarketWorlds.Count;
        SyncTemporaryBlacklistSets();
        if (_temporarilyBlacklistedMarketWorlds.Count == previousCount)
        {
            return false;
        }

        ClearProcurementOverlay();
        return true;
    }
    
    public void NotifySavedPlansChanged()
    {
        OnSavedPlansChanged?.Invoke();
    }

    public void ReplaceSavedPlans(IEnumerable<StoredPlanSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        SavedPlans = Array.AsReadOnly(summaries.ToArray());
        NotifySavedPlansChanged();
    }

    public void ClearSavedPlans()
    {
        if (SavedPlans.Count == 0)
        {
            return;
        }

        SavedPlans = Array.AsReadOnly(Array.Empty<StoredPlanSummary>());
        NotifySavedPlansChanged();
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
    public AppStateOperation BeginOperation(string operationName, string? message = null)
    {
        var operation = new AppStateOperation(++_nextOperationId, operationName);
        _currentOperationId = operation.Id;
        CurrentOperation = operationName;
        SetStatus(message ?? $"{operationName}...", busy: true);
        return operation;
    }
    
    /// <summary>
    /// End the current operation.
    /// </summary>
    public void EndOperation(string? message = null)
    {
        _currentOperationId = null;
        CurrentOperation = null;
        IsBusy = false;
        ProgressPercent = 0;
        // Set status directly to avoid any race conditions with progress callbacks
        StatusMessage = message ?? "Ready";
        NotifyStatusChanged();
    }

    public bool EndOperation(AppStateOperation operation, string? message = null)
    {
        if (!IsCurrentOperation(operation))
        {
            return false;
        }

        EndOperation(message);
        return true;
    }

    public bool SetStatusForOperation(
        AppStateOperation operation,
        string message,
        bool busy = true,
        double? progress = null)
    {
        if (!IsCurrentOperation(operation))
        {
            return false;
        }

        SetStatus(message, busy, progress);
        return true;
    }

    public bool CancelOperation(AppStateOperation operation, string? message = null)
    {
        if (!IsCurrentOperation(operation))
        {
            return false;
        }

        EndOperation(message);
        return true;
    }

    private bool IsCurrentOperation(AppStateOperation operation)
    {
        return _currentOperationId == operation.Id
            && string.Equals(CurrentOperation, operation.Name, StringComparison.Ordinal);
    }

    private static void ReplaceListContents<T>(List<T> target, IEnumerable<T> items)
    {
        target.Clear();
        target.AddRange(items);
    }

    private static ProjectItem CloneProjectItem(ProjectItem item)
    {
        return new ProjectItem
        {
            Id = item.Id,
            Name = item.Name,
            IconId = item.IconId,
            Quantity = item.Quantity,
            MustBeHq = item.MustBeHq
        };
    }

    private static List<MarketItemAnalysis> ReplaceAnalysisByItemId(
        IEnumerable<MarketItemAnalysis> analyses,
        MarketItemAnalysis replacement)
    {
        var replaced = false;
        var result = new List<MarketItemAnalysis>();
        foreach (var analysis in analyses)
        {
            if (analysis.ItemId == replacement.ItemId)
            {
                result.Add(replacement);
                replaced = true;
                continue;
            }

            result.Add(analysis);
        }

        if (!replaced)
        {
            result.Add(replacement);
        }

        return result;
    }

    private static List<DetailedShoppingPlan> ReplaceShoppingPlanByItemId(
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        DetailedShoppingPlan replacement)
    {
        var replaced = false;
        var result = new List<DetailedShoppingPlan>();
        foreach (var shoppingPlan in shoppingPlans)
        {
            if (shoppingPlan.ItemId == replacement.ItemId)
            {
                result.Add(replacement);
                replaced = true;
                continue;
            }

            result.Add(shoppingPlan);
        }

        if (!replaced)
        {
            result.Add(replacement);
        }

        return result;
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
        ReplaceListContents(_projectItems, _shoppingItems.Select(s => new ProjectItem
        {
            Id = s.Id,
            Name = s.Name,
            IconId = s.IconId,
            Quantity = s.Quantity,
            MustBeHq = false
        }));
        NotifyPlanChanged();
    }
    
    /// <summary>
    /// Convert project items to shopping items for Market Logistics
    /// </summary>
    public void SyncProjectToShopping()
    {
        ReplaceListContents(_shoppingItems, _projectItems.Select(p => new MarketShoppingItem
        {
            Id = p.Id,
            Name = p.Name,
            IconId = p.IconId,
            Quantity = p.Quantity
        }));
        NotifyShoppingListChanged();
    }

    /// <summary>
    /// Clear all current plan data.
    /// </summary>
    public void ClearPlan()
    {
        CurrentPlan = null;
        AdvancePlanSession();
        _projectItems.Clear();
        _shoppingItems.Clear();
        AutoExpandItemId = null;
        ClearMarketAnalysisState();
        ClearCurrentPlanId();
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
        ReplaceListContents(_projectItems, session.ProjectItems.Select(CloneProjectItem));
        CurrentPlan = session.Plan;
        AdvancePlanSession();
        AutoExpandItemId = null;
        ReplaceListContents(_marketItemAnalyses, session.MarketItemAnalyses);
        ReplaceListContents(_shoppingPlans, session.ShoppingPlans);
        RecommendationMode = storedPlan.SavedRecommendationMode;
        MarketAnalysisLens = storedPlan.SavedMarketAnalysisLens;
        ClearProcurementOverlay();
        
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

        _shoppingItems.Clear();
        SyncProjectToShopping();

        NotifySettingsChanged();
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

        if (IsDirtyVersion(_planCoreVersion, _lastPersistedPlanCoreVersion, CurrentPlan != null || _projectItems.Any()))
        {
            dirtyBuckets |= PersistedStateBucket.PlanCore;
        }

        if (IsDirtyVersion(_marketAnalysisVersion, _lastPersistedMarketAnalysisVersion, _shoppingPlans.Any() || _marketItemAnalyses.Any()))
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

        if (CurrentPlan == null && !_projectItems.Any())
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
        if (CurrentPlan == null && !_projectItems.Any())
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
        TrackCurrentPlanIdentity(null, null);
    }

    public void TrackCurrentPlanIdentity(string? planId, string? planName)
    {
        CurrentPlanId = planId;
        CurrentPlanName = planName;
    }

    public bool RenameCurrentPlanIdentity(string planId, string name)
    {
        if (!string.Equals(CurrentPlanId, planId, StringComparison.Ordinal))
        {
            return false;
        }

        CurrentPlanName = name;
        return true;
    }
    
    /// <summary>
    /// Cached world data for data center/world selection.
    /// Loaded once and shared across all pages.
    /// </summary>
    public WorldData? WorldData { get; private set; }
    
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
        
        _autoSaveTimer = new System.Threading.Timer(
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
        _autoSaveTimer?.Dispose();
        _autoSaveTimer = null;
    }

    public void RecordAutoSaveCompleted(DateTime completedAt)
    {
        LastAutoSave = completedAt;
    }

    private void SyncTemporaryBlacklistSets()
    {
        _temporarilyBlacklistedMarketWorlds = TemporaryMarketWorldBlacklist.GetActiveWorlds();
        _temporarilyBlacklistedWorlds = _temporarilyBlacklistedMarketWorlds
            .Select(world => world.WorldName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RefreshTemporaryExclusionViews();
    }

    private void RefreshTemporaryExclusionViews()
    {
        _temporarilyBlacklistedWorldsView = _temporarilyBlacklistedWorlds.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _temporarilyBlacklistedMarketWorldsView = _temporarilyBlacklistedMarketWorlds.ToFrozenSet();
        _temporarilyExcludedItemWorldsView = _temporarilyExcludedItemWorlds.ToFrozenSet();
    }

    private static bool IsDirtyVersion(long currentVersion, long persistedVersion, bool hasPersistableData)
    {
        return currentVersion != persistedVersion &&
               (persistedVersion >= 0 || currentVersion > 0 || hasPersistableData);
    }

    private void AdvancePlanSession()
    {
        _planSessionVersion++;
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

public sealed record AppStateOperation(long Id, string Name);

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
