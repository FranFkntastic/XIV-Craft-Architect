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
public partial class AppState
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
    private Guid _marketIntelligenceId = Guid.Empty;
    private StoredRecipeOperationSnapshot? _marketAnalysisRecipeBasis;
    private PublishedMarketAnalysisScopeSnapshot? _publishedMarketAnalysisScope;
    private bool _isMarketEvidenceHydrating;
    private readonly List<DetailedShoppingPlan> _procurementShoppingPlans = [];
    private ProcurementRoutePublicationBasis? _procurementRoutePublicationBasis;
    private readonly List<MarketShoppingItem> _shoppingItems = [];
    private readonly List<ProjectItem> _projectItems = [];
    private HashSet<string> _temporarilyBlacklistedWorlds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<MarketWorldKey> _temporarilyBlacklistedMarketWorlds = [];
    private HashSet<MarketItemWorldKey> _temporarilyExcludedItemWorlds = [];
    private HashSet<MarketAnalysisExpandedWorldKey> _expandedMarketAnalysisWorlds = [];
    private IReadOnlySet<string> _temporarilyBlacklistedWorldsView = new HashSet<string>(StringComparer.OrdinalIgnoreCase).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<MarketWorldKey> _temporarilyBlacklistedMarketWorldsView = FrozenSet<MarketWorldKey>.Empty;
    private IReadOnlySet<MarketItemWorldKey> _temporarilyExcludedItemWorldsView = FrozenSet<MarketItemWorldKey>.Empty;
    private IReadOnlySet<MarketAnalysisExpandedWorldKey> _expandedMarketAnalysisWorldsView = FrozenSet<MarketAnalysisExpandedWorldKey>.Empty;

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

    public MarketIntelligence MarketIntelligence => CreateMarketIntelligence();
    public IReadOnlyList<DetailedShoppingPlan> MarketRecommendations => ShoppingPlans;
    public StoredRecipeOperationSnapshot? MarketAnalysisRecipeBasis => CloneRecipeBasis(_marketAnalysisRecipeBasis);
    public StoredRecipeOperationSnapshot? MarketIntelligenceRecipeBasis => MarketAnalysisRecipeBasis;
    public MarketIntelligencePublicationContext MarketIntelligencePublicationContext => MarketIntelligence.PublicationContext;
    public PublishedMarketAnalysisScopeSnapshot? PublishedMarketAnalysisScope => _publishedMarketAnalysisScope;
    public string? MarketAnalysisScopeWarning => GetMarketAnalysisScopeWarning();
    public bool IsMarketEvidenceHydrating => _isMarketEvidenceHydrating;

    /// <summary>
    /// Mutable procurement overlay derived from ShoppingPlans and current acquisition choices.
    /// This may be filtered, re-routed, or affected by temporary world exclusions.
    /// </summary>
    public IReadOnlyList<DetailedShoppingPlan> ProcurementShoppingPlans => _procurementShoppingPlans.AsReadOnly();
    public MarketRouteDecision? ProcurementRouteDecision { get; private set; }
    public bool IsProcurementRouteReconciling { get; private set; }
    public ProcurementRoutePublicationBasis? ProcurementRoutePublicationBasis => _procurementRoutePublicationBasis;
    public ProcurementRoutePublicationValidity ProcurementRouteValidity => GetProcurementRouteValidity();
    public bool IsProcurementRouteStale =>
        ProcurementRouteValidity is ProcurementRoutePublicationValidity.SelectionChanged or
            ProcurementRoutePublicationValidity.InputsChanged ||
        !string.IsNullOrWhiteSpace(ProcurementRouteFailure);
    public string? ProcurementRouteStaleReason { get; private set; }
    public string? ProcurementRouteFailure { get; private set; }
    public IReadOnlyList<CoreMarketDataUnavailableItem> UnavailableMarketItems { get; private set; } = Array.Empty<CoreMarketDataUnavailableItem>();
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
    public int? SelectedMarketAnalysisItemId { get; private set; }
    public IReadOnlySet<MarketAnalysisExpandedWorldKey> ExpandedMarketAnalysisWorlds => _expandedMarketAnalysisWorldsView;
    public MarketAnalysisGridSortColumn? MarketAnalysisGridSortColumn { get; private set; }
    public bool MarketAnalysisGridSortDescending { get; private set; }
    public MarketAnalysisWorldGridSortColumn? MarketAnalysisWorldGridSortColumn { get; private set; }
    public bool MarketAnalysisWorldGridSortDescending { get; private set; }
    public Guid? SelectedTradeOrderId { get; private set; }

    // Persistence state
    public bool IsAutoSaveEnabled { get; private set; } = true;
    public bool SecretDebugToolsEnabled { get; private set; }
    public MarketFetchScope DefaultMarketFetchScope { get; private set; } = MarketFetchScope.EntireRegion;
    public DateTime? LastAutoSave { get; private set; }
    public IReadOnlyList<StoredPlanSummary> SavedPlans { get; private set; } = Array.AsReadOnly(Array.Empty<StoredPlanSummary>());

    // Market Analysis Settings (persist across page navigations)
    public bool EnableMultiWorldSplits { get; private set; } = false;
    public int MaxWorldsPerItem { get; private set; } = 0; // 0 = unlimited
    public bool SearchEntireRegion { get; private set; } = true;
    public MarketSortOption MarketSortPreference { get; private set; } = MarketSortOption.ByRecommended;
    public MarketAnalysisEvidenceOverlay MarketAnalysisEvidenceOverlay { get; private set; } = MarketAnalysisEvidenceOverlay.CompetitivenessOverlay;

    // Procurement Planning Settings
    public bool ProcurementSearchEntireRegion { get; private set; } = false;
    public bool ProcurementEnableSplitWorldPurchases { get; private set; } = true;
    public int ProcurementTravelTolerance { get; private set; } = 0; // 0 = shortest route, 11 = cheapest
    public bool ProcurementStartFromHomeDataCenter { get; private set; }
    public MarketTravelPriority ProcurementTravelPriority { get; private set; } = MarketTravelPriority.DataCenterTransfersFirst;

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
    MarketAnalysisView = 1 << 8,
    TradeOperationsView = 1 << 9,
    TradeOperationsData = 1 << 10,
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

public enum MarketAnalysisGridSortColumn
{
    Item,
    Quantity,
    Coverage,
    Worlds,
    Total
}

public enum MarketAnalysisWorldGridSortColumn
{
    World,
    StockDepth,
    Coverage,
    PriceValue,
    Value,
    Data
}

public enum MarketAnalysisEvidenceOverlay
{
    CompetitivenessOverlay,
    PriceBandOverlay
}

public readonly record struct MarketAnalysisExpandedWorldKey(
    int ItemId,
    string DataCenter,
    string WorldName);

public readonly record struct MarketAnalysisGridSortState(
    MarketAnalysisGridSortColumn Column,
    bool Descending);

public readonly record struct MarketAnalysisWorldGridSortState(
    MarketAnalysisWorldGridSortColumn Column,
    bool Descending);

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
