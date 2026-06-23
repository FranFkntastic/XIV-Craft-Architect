using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFXIV_Craft_Architect.Coordinators;
using FFXIV_Craft_Architect.Core.Coordinators;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Services;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.ViewModels;

/// <summary>
/// ViewModel for market analysis and shopping plan management.
/// Owns market-analysis display projection and live market analysis.
///
/// RESPONSIBILITIES:
/// 1. Live Market Analysis:
///    - AnalyzeLiveMarketDataAsync: Calculates shopping plans from market data
///    - Delegates to MarketShoppingService for single-DC or multi-DC searches
///    - Updates ShoppingPlans collection with results
///
/// 2. ViewModel Wrapping & Presentation:
///    - Wraps DetailedShoppingPlan in ShoppingPlanViewModel
///    - Groups plans by world via GroupedByWorld
///    - Applies user-selected sort order
///
/// 3. Selection State (MVVM Binding):
///    - SelectedExpandedPanel: Bindable property for the expanded panel ContentControl
///    - Delegates to IMarketLogisticsCoordinator for actual selection management
///
/// UI BINDINGS:
/// - ShoppingPlans → Market cards grid
/// - GroupedByWorld → Procurement world cards
/// - StatusMessage → Progress/status display
/// - IsLoading → Loading indicator
/// - SelectedExpandedPanel → Expanded panel ContentControl
///
/// STATE MANAGEMENT:
/// - ShoppingPlans persists when switching tabs
/// - Cleared when new plan is loaded or Clear() is called
/// - Saved with plan via CraftingPlan.SavedMarketPlans
/// </summary>
public partial class MarketAnalysisViewModel : ViewModelBase
{
    private ObservableCollection<ShoppingPlanViewModel> _shoppingPlans = new();
    private ObservableCollection<ProcurementWorldViewModel> _groupedByWorld = new();
    private ObservableCollection<ProcurementItemViewModel> _procurementItems = new();
    private string _statusMessage = string.Empty;
    private bool _isLoading;
    private MarketSortOption _currentSort = MarketSortOption.RecommendedWorld;
    private RecommendationMode _recommendationMode = RecommendationMode.MinimizeTotalCost;
    private bool _searchAllNaDcs;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly IPriceRefreshCoordinator _priceRefreshCoordinator;
    private readonly IMarketLogisticsCoordinator? _marketLogisticsCoordinator;
    private readonly ILogger<MarketAnalysisViewModel>? _logger;
    private readonly CraftSessionState? _session;
    private readonly CoreMarketAnalysisWorkflowService? _coreMarketAnalysisWorkflow;

    public MarketAnalysisViewModel(
        MarketShoppingService marketShoppingService,
        IPriceRefreshCoordinator priceRefreshCoordinator,
        IMarketLogisticsCoordinator? marketLogisticsCoordinator = null,
        ILogger<MarketAnalysisViewModel>? logger = null,
        CraftSessionState? session = null,
        CoreMarketAnalysisWorkflowService? coreMarketAnalysisWorkflow = null)
    {
        _marketShoppingService = marketShoppingService;
        _priceRefreshCoordinator = priceRefreshCoordinator;
        _marketLogisticsCoordinator = marketLogisticsCoordinator;
        _logger = logger;
        _session = session;
        _coreMarketAnalysisWorkflow = coreMarketAnalysisWorkflow;
        _shoppingPlans.CollectionChanged += OnShoppingPlansCollectionChanged;
        
        // Forward property changes from coordinator
        if (_marketLogisticsCoordinator is INotifyPropertyChanged notifier)
        {
            notifier.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(IMarketLogisticsCoordinator.SelectedExpandedPanel) ||
                    e.PropertyName == nameof(IMarketLogisticsCoordinator.SelectedItemId))
                {
                    OnPropertyChanged(nameof(SelectedExpandedPanel));
                    OnPropertyChanged(nameof(SelectedItemId));
                    OnPropertyChanged(nameof(IsStripMode));
                    OnPropertyChanged(nameof(IsExpandedMode));
                    RefreshTopPaneHeight();
                }
            };
        }
    }
    
    /// <summary>
    /// The ViewModel for the currently selected expanded panel.
    /// Bind ContentControl.Content to this property for the split-pane view.
    /// Returns null when nothing is selected (triggers placeholder display).
    /// </summary>
    public ExpandedPanelViewModel? SelectedExpandedPanel => _marketLogisticsCoordinator?.SelectedExpandedPanel;
    
    /// <summary>
    /// The ItemId of the currently selected card, or null if nothing is selected.
    /// Used for card highlighting in the collapsed cards grid.
    /// </summary>
    public int? SelectedItemId => _marketLogisticsCoordinator?.SelectedItemId;

    private void OnShoppingPlansCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShoppingPlans));
        OnPropertyChanged(nameof(TotalCost));
        OnPropertyChanged(nameof(HasData));
        RegroupByWorld();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shoppingPlans.CollectionChanged -= OnShoppingPlansCollectionChanged;
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Shopping plans for each material needed.
    /// </summary>
    public ObservableCollection<ShoppingPlanViewModel> ShoppingPlans
    {
        get => _shoppingPlans;
        set
        {
            _shoppingPlans = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TotalCost));
            OnPropertyChanged(nameof(HasData));
            RegroupByWorld();
        }
    }

    /// <summary>
    /// Shopping plans grouped by recommended world for efficient procurement.
    /// </summary>
    public ObservableCollection<ProcurementWorldViewModel> GroupedByWorld
    {
        get => _groupedByWorld;
        private set
        {
            _groupedByWorld = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Flat list of procurement items (for simple view without market data).
    /// </summary>
    public ObservableCollection<ProcurementItemViewModel> ProcurementItems
    {
        get => _procurementItems;
        set
        {
            _procurementItems = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Total cost across all shopping plans.
    /// </summary>
    public long TotalCost => _shoppingPlans.Sum(p => ProcurementPlanCost.GetRecommendedCost(p.Plan));

    /// <summary>
    /// Whether any shopping data is available.
    /// </summary>
    public bool HasData => _shoppingPlans.Any();

    /// <summary>
    /// Number of items with viable market options.
    /// </summary>
    public int ItemsWithOptions => _shoppingPlans.Count(p => p.HasOptions);

    /// <summary>
    /// Number of items without market options.
    /// </summary>
    public int ItemsWithoutOptions => _shoppingPlans.Count(p => !p.HasOptions);

    /// <summary>
    /// Current status message.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether a loading operation is in progress.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            _isLoading = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Current sort option for shopping plans.
    /// </summary>
    public MarketSortOption CurrentSort
    {
        get => _currentSort;
        set
        {
            _currentSort = value;
            OnPropertyChanged();
            ApplySort();
        }
    }

    /// <summary>
    /// Current recommendation mode.
    /// </summary>
    public RecommendationMode RecommendationMode
    {
        get => _recommendationMode;
        set
        {
            _recommendationMode = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether to search all NA data centers.
    /// </summary>
    public bool SearchAllNaDcs
    {
        get => _searchAllNaDcs;
        set
        {
            _searchAllNaDcs = value;
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private bool _enableSplitWorld;

    private double _topPaneHeight = 34;
    private double _availableContainerHeight = 600;

    public double TopPaneHeight
    {
        get => _topPaneHeight;
        private set
        {
            if (SetProperty(ref _topPaneHeight, value))
            {
                OnPropertyChanged(nameof(IsStripMode));
                OnPropertyChanged(nameof(IsExpandedMode));
            }
        }
    }

    public bool IsStripMode => SelectedExpandedPanel == null;
    public bool IsExpandedMode => SelectedExpandedPanel != null;

    public void RecalculateTopPaneHeight(double availableHeight)
    {
        _availableContainerHeight = availableHeight;

        if (SelectedExpandedPanel == null)
        {
            TopPaneHeight = 34;
        }
        else
        {
            var expandedHeight = Math.Max(200, availableHeight * 0.6);
            TopPaneHeight = expandedHeight;
        }
    }

    private void RefreshTopPaneHeight()
    {
        RecalculateTopPaneHeight(_availableContainerHeight);
    }

    /// <summary>
    /// Event raised when shopping plans change.
    /// </summary>
    public event EventHandler? PlansChanged;

    /// <summary>
    /// Sets the shopping plans from service results.
    /// Must be called on UI thread or will be marshaled to UI thread.
    /// </summary>
    public void SetShoppingPlans(List<DetailedShoppingPlan> plans)
    {
        _logger?.LogInformation("[SetShoppingPlans] START - Received {Count} plans, Thread={ThreadId}", 
            plans.Count, Environment.CurrentManagedThreadId);
        
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            _logger?.LogInformation("[SetShoppingPlans] Marshaling to UI thread...");
            dispatcher.Invoke(() => SetShoppingPlans(plans));
            return;
        }

        _logger?.LogInformation("[SetShoppingPlans] Clearing existing plans and adding {Count} new plans...", plans.Count);
        ReplaceShoppingPlans(plans);
        PublishShoppingPlansToSession(plans);
        _logger?.LogInformation("[SetShoppingPlans] Firing PlansChanged event...");
        PlansChanged?.Invoke(this, EventArgs.Empty);
        _logger?.LogInformation("[SetShoppingPlans] END - _shoppingPlans.Count={Count}, HasData={HasData}",
            _shoppingPlans.Count, HasData);
    }

    public void DisplayShoppingPlans(List<DetailedShoppingPlan> plans)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => DisplayShoppingPlans(plans));
            return;
        }

        ReplaceShoppingPlans(plans);
        PlansChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReplaceShoppingPlans(List<DetailedShoppingPlan> plans)
    {
        _shoppingPlans.Clear();
        foreach (var plan in plans)
        {
            _shoppingPlans.Add(new ShoppingPlanViewModel(plan));
        }

        _logger?.LogInformation("[ReplaceShoppingPlans] Added {Count} plans, calling ApplySort...", _shoppingPlans.Count);
        ApplySort();
    }

    /// <summary>
    /// Sets procurement items from aggregated materials (when no market data).
    /// </summary>
    public void SetProcurementItems(List<MaterialAggregate> materials)
    {
        _procurementItems.Clear();
        foreach (var material in materials.OrderBy(m => m.Name))
        {
            _procurementItems.Add(new ProcurementItemViewModel
            {
                ItemId = material.ItemId,
                Name = material.Name,
                Quantity = material.TotalQuantity,
                RequiresHq = material.RequiresHq
            });
        }
    }

    /// <summary>
    /// Clears all market data.
    /// </summary>
    [RelayCommand]
    public void Clear()
    {
        ClearDisplay();
        _session?.ClearMarketAnalysis("wpf market analysis cleared");
    }

    public void ClearDisplay()
    {
        _shoppingPlans.Clear();
        _groupedByWorld.Clear();
        _procurementItems.Clear();
    }

    public void MarkMarketContextChanged(string reason)
    {
        ClearDisplay();
        _session?.MarkProcurementSettingsChanged(reason);
    }

    public void MarkProcurementRouteSettingsChanged(string reason)
    {
        _session?.MarkProcurementRouteSettingsChanged(reason);
    }

    private void PublishShoppingPlansToSession(IEnumerable<DetailedShoppingPlan> plans)
    {
        if (_session == null)
        {
            return;
        }

        var plan = _session.ActivePlan;
        if (plan == null)
        {
            return;
        }

        if (_session.MarketEvidence.ItemAnalyses.Count > 0 ||
            _session.MarketEvidence.ShoppingPlans?.Count > 0)
        {
            return;
        }

        _session.TryPublishMarketAnalysis(
            _session.CaptureVersionStamp(),
            plan,
            _session.PlanSessionVersion,
            Array.Empty<MarketItemAnalysis>(),
            plans,
            acquisitionDecisionsChanged: false,
            "wpf market shopping plans updated");
    }

    /// <summary>
    /// Inspects market cache coverage for a plan without fetching from external APIs.
    /// </summary>
    public Task<PlanCacheInspectionContext> InspectPlanCacheAsync(
        CraftingPlan? plan,
        string dataCenter,
        bool searchAllNa,
        CancellationToken ct = default)
    {
        if (plan == null || plan.RootItems.Count == 0)
        {
            return Task.FromResult(new PlanCacheInspectionContext(
                new List<(int itemId, string name, int quantity)>(),
                new HashSet<int>(),
                new Dictionary<int, ItemCacheInspectionResult>(),
                Array.Empty<string>()));
        }

        return _priceRefreshCoordinator.InspectPlanCacheAsync(plan, dataCenter, searchAllNa, ct);
    }

    /// <summary>
    /// Analyzes market data for the specified items and generates shopping plans.
    /// Delegates to MarketShoppingService for single-DC or cross-DC market analysis.
    /// </summary>
    /// <param name="marketItems">The materials to analyze for optimal purchases.</param>
    /// <param name="dataCenter">The data center to search (ignored if searchAllNa is true).</param>
    /// <param name="searchAllNa">If true, searches all NA data centers for best prices.</param>
    /// <param name="mode">The recommendation mode (minimize cost vs maximize value).</param>
    /// <param name="ct">Cancellation token for aborting the operation.</param>
    /// <returns>
    /// A <see cref="LiveMarketAnalysisResult"/> containing the generated shopping plans.
    /// Updates <see cref="ShoppingPlans"/> and <see cref="StatusMessage"/> on success.
    /// </returns>
    /// <remarks>
    /// This method reads from the market cache - callers must ensure cache is populated.
    /// Progress is reported via <see cref="StatusMessage"/> updates.
    /// </remarks>
    public async Task<LiveMarketAnalysisResult> AnalyzeLiveMarketDataAsync(
        List<MaterialAggregate> marketItems,
        string dataCenter,
        bool searchAllNa,
        RecommendationMode mode,
        CancellationToken ct = default)
    {
        if (marketItems.Count == 0)
        {
            SetShoppingPlans(new List<DetailedShoppingPlan>());
            const string emptyMessage = "Market analysis complete. 0 items analyzed.";
            StatusMessage = emptyMessage;
            return LiveMarketAnalysisResult.FromSuccess(emptyMessage, new List<DetailedShoppingPlan>());
        }

        IsLoading = true;

        try
        {
            var progress = new Progress<string>(message =>
            {
                StatusMessage = $"Analyzing market: {message}";
            });

            List<DetailedShoppingPlan> shoppingPlans;
            if (searchAllNa)
            {
                shoppingPlans = await _marketShoppingService.CalculateDetailedShoppingPlansMultiDCAsync(
                    marketItems,
                    progress,
                    ct,
                    mode);
            }
            else if (EnableSplitWorld)
            {
                var config = new MarketAnalysisConfig
                {
                    EnableSplitWorld = true,
                    MaxWorldsPerItem = null
                };
                
                shoppingPlans = await _marketShoppingService.CalculateShoppingPlansWithSplitsAsync(
                    marketItems,
                    dataCenter,
                    progress,
                    ct,
                    config: config);
            }
            else
            {
                shoppingPlans = await _marketShoppingService.CalculateDetailedShoppingPlansAsync(
                    marketItems,
                    dataCenter,
                    progress,
                    ct,
                    mode);
            }

            SetShoppingPlans(shoppingPlans);
            var successMessage = $"Market analysis complete. {shoppingPlans.Count} items analyzed.";
            StatusMessage = successMessage;
            return LiveMarketAnalysisResult.FromSuccess(successMessage, shoppingPlans);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[AnalyzeLiveMarketDataAsync] Failed to analyze market data");
            var errorMessage = $"Error fetching listings: {ex.Message}";
            StatusMessage = errorMessage;
            return LiveMarketAnalysisResult.FromFailure(errorMessage);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<CoreMarketAnalysisWorkflowResult> RunCoreMarketAnalysisAsync(
        CoreMarketAnalysisWorkflowRequest request,
        CancellationToken ct = default)
    {
        if (_coreMarketAnalysisWorkflow == null || _session == null)
        {
            const string unavailableMessage = "Core market analysis workflow is unavailable.";
            StatusMessage = unavailableMessage;
            return new CoreMarketAnalysisWorkflowResult(false, 0, 0, 0);
        }

        IsLoading = true;

        try
        {
            var progress = new Progress<string>(message =>
            {
                StatusMessage = $"Analyzing market: {message}";
            });

            var result = await _coreMarketAnalysisWorkflow.RunAnalysisAsync(request, progress, ct);
            if (result.Published)
            {
                DisplayShoppingPlans(_session.MarketEvidence.ShoppingPlans?.ToList() ?? []);
                StatusMessage = $"Market analysis complete. {result.AnalyzedCount} items analyzed.";
            }
            else
            {
                StatusMessage = "Market analysis did not publish.";
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[RunCoreMarketAnalysisAsync] Failed to analyze market data");
            StatusMessage = $"Error fetching listings: {ex.Message}";
            return new CoreMarketAnalysisWorkflowResult(false, 0, 0, 0);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<CoreMarketAnalysisWorkflowResult> ApplyCoreMarketLensAsync(
        MarketAcquisitionLens lens,
        CancellationToken ct = default)
    {
        if (_coreMarketAnalysisWorkflow == null || _session == null)
        {
            const string unavailableMessage = "Core market analysis workflow is unavailable.";
            StatusMessage = unavailableMessage;
            return new CoreMarketAnalysisWorkflowResult(false, 0, 0, 0);
        }

        if (_session.MarketEvidence.ItemAnalyses.Count == 0)
        {
            _session.MarkMarketAnalysisSettingsChanged("wpf market acquisition lens changed");
            return new CoreMarketAnalysisWorkflowResult(false, 0, 0, 0);
        }

        IsLoading = true;

        try
        {
            _session.MarkMarketAnalysisSettingsChanged("wpf market acquisition lens changed");
            var result = await _coreMarketAnalysisWorkflow.ApplyLensAsync(
                new CoreApplyMarketAnalysisLensRequest(lens),
                ct);
            if (result.Published)
            {
                var displayPlans = PreserveFixedVendorDisplayPlans(
                    _session.MarketEvidence.ShoppingPlans?.ToList() ?? []);
                DisplayShoppingPlans(displayPlans);
                StatusMessage = $"Market analysis lens applied. {result.AnalyzedCount} items analyzed.";
            }
            else
            {
                StatusMessage = "Market analysis lens was not applied.";
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[ApplyCoreMarketLensAsync] Failed to apply market lens");
            StatusMessage = $"Error applying market lens: {ex.Message}";
            return new CoreMarketAnalysisWorkflowResult(false, 0, 0, 0);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private List<DetailedShoppingPlan> PreserveFixedVendorDisplayPlans(List<DetailedShoppingPlan> marketPlans)
    {
        var marketItemIds = marketPlans
            .Select(plan => plan.ItemId)
            .ToHashSet();
        var vendorPlans = _shoppingPlans
            .Select(plan => plan.Plan)
            .Where(plan => !marketItemIds.Contains(plan.ItemId) && IsFixedVendorDisplayPlan(plan))
            .ToList();

        vendorPlans.AddRange(marketPlans);
        return vendorPlans;
    }

    private static bool IsFixedVendorDisplayPlan(DetailedShoppingPlan plan) =>
        string.Equals(
            plan.RecommendedWorld?.WorldName,
            MarketShoppingConstants.VendorWorldName,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies the current sort to shopping plans.
    /// </summary>
    public void ApplySort()
    {
        var sorted = _currentSort switch
        {
            MarketSortOption.RecommendedWorld => _shoppingPlans.OrderBy(p => p.RecommendedWorld?.WorldName ?? "ZZZ"),
            MarketSortOption.Alphabetical => _shoppingPlans.OrderBy(p => p.Name),
            MarketSortOption.PriceHighToLow => _shoppingPlans.OrderByDescending(p => ProcurementPlanCost.GetRecommendedCost(p.Plan)),
            _ => _shoppingPlans.OrderBy(p => p.Name)
        };

        // Re-add in sorted order
        var list = sorted.ToList();
        _shoppingPlans.Clear();
        foreach (var item in list)
        {
            _shoppingPlans.Add(item);
        }
    }

    /// <summary>
    /// Gets the shopping plans for watch state saving.
    /// </summary>
    public List<DetailedShoppingPlan> GetPlansForWatch()
    {
        return _shoppingPlans.Select(vm => vm.Plan).ToList();
    }

    private void RegroupByWorld()
    {
        var groups = _shoppingPlans
            .Where(p => p.RecommendedWorld != null)
            .GroupBy(p => p.RecommendedWorld!.WorldName)
            .OrderBy(g => g.Key)
            .Select(g => new ProcurementWorldViewModel
            {
                WorldName = g.Key,
                Items = new ObservableCollection<ShoppingPlanViewModel>(g),
                TotalCost = g.Sum(i => i.RecommendedCost),
                IsHomeWorld = g.First().RecommendedWorld?.IsHomeWorld ?? false
            })
            .ToList();

        GroupedByWorld = new ObservableCollection<ProcurementWorldViewModel>(groups);
    }

}

/// <summary>
/// Result of a live market analysis operation.
/// Contains generated shopping plans with world recommendations.
/// </summary>
/// <param name="Success">Whether the analysis completed successfully.</param>
/// <param name="Message">Human-readable status message.</param>
/// <param name="Plans">Shopping plans for each analyzed item.</param>
public record LiveMarketAnalysisResult(
    bool Success,
    string Message,
    List<DetailedShoppingPlan> Plans)
{
    public static LiveMarketAnalysisResult FromSuccess(string message, List<DetailedShoppingPlan> plans) =>
        new(true, message, plans);

    public static LiveMarketAnalysisResult FromFailure(string message) =>
        new(false, message, new List<DetailedShoppingPlan>());
}

/// <summary>
/// ViewModel wrapper for a DetailedShoppingPlan.
/// </summary>
public partial class ShoppingPlanViewModel : ObservableObject
{
    private readonly DetailedShoppingPlan _plan;

    public ShoppingPlanViewModel(DetailedShoppingPlan plan)
    {
        _plan = plan;
    }

    public DetailedShoppingPlan Plan => _plan;

    public int ItemId => _plan.ItemId;
    public string Name => _plan.Name;
    public int QuantityNeeded => _plan.QuantityNeeded;
    public decimal DCAveragePrice => _plan.DCAveragePrice;
    public WorldShoppingSummary? RecommendedWorld => _plan.RecommendedWorld;
    public long RecommendedCost => ProcurementPlanCost.GetRecommendedCost(_plan);
    public List<WorldShoppingSummary> WorldOptions => _plan.WorldOptions;
    public bool HasOptions => _plan.HasOptions;
    public bool HasHqData => _plan.HasHqData;
    public decimal? HQAveragePrice => _plan.HQAveragePrice;
    public string? Error => _plan.Error;
}

/// <summary>
/// ViewModel for procurement items grouped by world.
/// </summary>
public partial class ProcurementWorldViewModel : ObservableObject
{
    [ObservableProperty]
    private string _worldName = string.Empty;

    [ObservableProperty]
    private long _totalCost;

    [ObservableProperty]
    private bool _isHomeWorld;

    [ObservableProperty]
    private ObservableCollection<ShoppingPlanViewModel> _items = new();
}

/// <summary>
/// ViewModel for simple procurement items (without market data).
/// </summary>
public partial class ProcurementItemViewModel : ObservableObject
{
    [ObservableProperty]
    private int _itemId;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _quantity;

    [ObservableProperty]
    private bool _requiresHq;
}

/// <summary>
/// Sort options for market analysis.
/// </summary>
public enum MarketSortOption
{
    RecommendedWorld,
    Alphabetical,
    PriceHighToLow
}
