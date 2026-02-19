using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFXIV_Craft_Architect.Core.Coordinators;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.Logging;
using PriceInfo = FFXIV_Craft_Architect.Core.Models.PriceInfo;

namespace FFXIV_Craft_Architect.ViewModels;

/// <summary>
/// ViewModel for market analysis and shopping plan management.
/// Owns price refresh orchestration, cache rebuild logic, and live market analysis.
///
/// RESPONSIBILITIES:
/// 1. Price Refresh Orchestration:
///    - RefreshPlanPricesAsync: Fetches current market prices via IPriceRefreshCoordinator
///    - Mutates CraftingPlan with updated prices via RecipeCalculationService
///    - Reports progress via PriceRefreshProgressReported event
///
/// 2. Cache Rebuild:
///    - RebuildFromCacheAsync: Rebuilds market analysis from cached plan prices
///    - Used when user wants to recalculate without network calls
///
/// 3. Live Market Analysis:
///    - AnalyzeLiveMarketDataAsync: Calculates shopping plans from market data
///    - Delegates to MarketShoppingService for single-DC or multi-DC searches
///    - Updates ShoppingPlans collection with results
///
/// 4. ViewModel Wrapping & Presentation:
///    - Wraps DetailedShoppingPlan in ShoppingPlanViewModel
///    - Groups plans by world via GroupedByWorld
///    - Applies user-selected sort order
///
/// UI BINDINGS:
/// - ShoppingPlans → Market cards grid
/// - GroupedByWorld → Procurement world cards
/// - StatusMessage → Progress/status display
/// - IsLoading → Loading indicator
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
    private readonly RecipeCalculationService _recipeCalcService;
    private readonly IPriceRefreshCoordinator _priceRefreshCoordinator;
    private readonly ILogger<MarketAnalysisViewModel>? _logger;

    public MarketAnalysisViewModel(
        MarketShoppingService marketShoppingService,
        IPriceRefreshCoordinator priceRefreshCoordinator,
        RecipeCalculationService recipeCalcService,
        ILogger<MarketAnalysisViewModel>? logger = null)
    {
        _marketShoppingService = marketShoppingService;
        _priceRefreshCoordinator = priceRefreshCoordinator;
        _recipeCalcService = recipeCalcService;
        _logger = logger;
        _shoppingPlans.CollectionChanged += OnShoppingPlansCollectionChanged;
    }

    /// <summary>
    /// Raised whenever plan price refresh progress updates.
    /// UI layers can subscribe to update progress widgets/windows.
    /// </summary>
    public event EventHandler<PriceRefreshProgress>? PriceRefreshProgressReported;

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
    public long TotalCost => _shoppingPlans.Sum(p => p.RecommendedWorld?.TotalCost ?? 0);

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
        _shoppingPlans.Clear();
        foreach (var plan in plans)
        {
            _shoppingPlans.Add(new ShoppingPlanViewModel(plan));
        }
        _logger?.LogInformation("[SetShoppingPlans] Added {Count} plans, calling ApplySort...", _shoppingPlans.Count);
        ApplySort();
        _logger?.LogInformation("[SetShoppingPlans] Firing PlansChanged event...");
        PlansChanged?.Invoke(this, EventArgs.Empty);
        _logger?.LogInformation("[SetShoppingPlans] END - _shoppingPlans.Count={Count}, HasData={HasData}", 
            _shoppingPlans.Count, HasData);
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
        _shoppingPlans.Clear();
        _groupedByWorld.Clear();
        _procurementItems.Clear();
    }

    /// <summary>
    /// Fetches current market prices for all items in the plan and updates plan nodes.
    /// Orchestrates price refresh via IPriceRefreshCoordinator and mutates the plan in-place.
    /// </summary>
    /// <param name="plan">The crafting plan to refresh prices for. Modified in-place with new prices.</param>
    /// <param name="dataCenter">The data center context for price lookups.</param>
    /// <param name="worldOrDc">Specific world or data center-wide scope for market queries.</param>
    /// <param name="searchAllNa">If true, searches all NA data centers for best prices.</param>
    /// <param name="forceRefresh">If true, bypasses cache and fetches fresh data.</param>
    /// <param name="ct">Cancellation token for aborting the operation.</param>
    /// <returns>
    /// A <see cref="PlanPriceRefreshResult"/> containing refresh statistics,
    /// price data, and cache metadata for UI status display.
    /// </returns>
    /// <remarks>
    /// Progress is reported via <see cref="PriceRefreshProgressReported"/> event.
    /// UI layers should subscribe to update progress widgets and status windows.
    /// </remarks>
    public async Task<PlanPriceRefreshResult> RefreshPlanPricesAsync(
        CraftingPlan? plan,
        string dataCenter,
        string worldOrDc,
        bool searchAllNa,
        bool forceRefresh,
        CancellationToken ct = default)
    {
        if (plan == null || plan.RootItems.Count == 0)
        {
            StatusMessage = "No plan - build a plan first";
            return PlanPriceRefreshResult.NoPlan(StatusMessage);
        }

        IsLoading = true;

        try
        {
            _logger?.LogInformation(
                "[RefreshPlanPricesAsync] Starting refresh: DC={DataCenter}, WorldOrDc={WorldOrDc}, SearchAllNa={SearchAllNa}, ForceRefresh={ForceRefresh}",
                dataCenter,
                worldOrDc,
                searchAllNa,
                forceRefresh);

            var progress = new Progress<PriceRefreshProgress>(p =>
            {
                StatusMessage = p.Message ?? ComputeProgressStatusMessage(p);
                PriceRefreshProgressReported?.Invoke(this, p);
            });

            var refreshContext = await _priceRefreshCoordinator.FetchPlanPricesAsync(
                plan,
                dataCenter,
                worldOrDc,
                searchAllNa,
                progress,
                ct);

            int successCount = 0;
            int failedCount = 0;
            int skippedCount = 0;
            int cachedCount = 0;

            foreach (var kvp in refreshContext.Prices)
            {
                var itemId = kvp.Key;
                var priceInfo = kvp.Value;

                if (priceInfo.Source == PriceSource.Unknown)
                {
                    if (!refreshContext.WarmCacheForCraftedItems && !refreshContext.CacheCandidateItemIds.Contains(itemId))
                    {
                        skippedCount++;
                    }
                    else
                    {
                        failedCount++;
                    }
                }
                else if (priceInfo.Source == PriceSource.Market)
                {
                    var isFetchedThisRun = refreshContext.ScopeDataCenters.Any(itemDc =>
                        refreshContext.FetchedThisRunKeys.Contains((itemId, itemDc)));

                    if (isFetchedThisRun)
                    {
                        successCount++;
                    }
                    else
                    {
                        cachedCount++;
                    }
                }
                else if (priceInfo.Source == PriceSource.Vendor || priceInfo.Source == PriceSource.Untradeable)
                {
                    successCount++;
                }
                else
                {
                    cachedCount++;
                }

                _recipeCalcService.UpdateSingleNodePrice(plan.RootItems, itemId, priceInfo);
            }

            var message = BuildRefreshSummaryMessage(plan, successCount, failedCount, skippedCount, cachedCount);
            StatusMessage = message;

            return new PlanPriceRefreshResult(
                true,
                message,
                refreshContext.AllItems,
                refreshContext.Prices,
                successCount,
                failedCount,
                skippedCount,
                cachedCount,
                refreshContext.CacheCandidateItemIds,
                refreshContext.WarmCacheForCraftedItems,
                refreshContext.FetchedThisRunKeys,
                refreshContext.DataScopeByItemId,
                refreshContext.ScopeDataCenters);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[RefreshPlanPricesAsync] Failed");
            StatusMessage = $"Failed to fetch prices: {ex.Message}. Cached prices preserved.";
            return PlanPriceRefreshResult.Failed(StatusMessage);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Rebuilds market analysis from cached prices stored in the plan.
    /// Does not make network calls - extracts prices already present in plan nodes.
    /// </summary>
    /// <param name="plan">The crafting plan containing cached price data. Modified in-place.</param>
    /// <returns>
    /// A <see cref="CacheRebuildResult"/> indicating success and containing extracted prices.
    /// Returns failure if plan is null, empty, or has no cached prices.
    /// </returns>
    public Task<CacheRebuildResult> RebuildFromCacheAsync(CraftingPlan? plan)
    {
        if (plan == null || plan.RootItems.Count == 0)
        {
            StatusMessage = "No plan - build a plan first";
            return Task.FromResult(CacheRebuildResult.NoPlan(StatusMessage));
        }

        var cachedPrices = _recipeCalcService.ExtractPricesFromPlan(plan);
        if (cachedPrices.Count == 0)
        {
            StatusMessage = "No cached prices available. Click 'Refresh Market Data' to fetch prices.";
            return Task.FromResult(CacheRebuildResult.Failed(StatusMessage));
        }

        foreach (var kvp in cachedPrices)
        {
            _recipeCalcService.UpdateSingleNodePrice(plan.RootItems, kvp.Key, kvp.Value);
        }

        StatusMessage = $"Market analysis rebuilt from {cachedPrices.Count} cached prices.";
        return Task.FromResult(new CacheRebuildResult(true, StatusMessage, cachedPrices));
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

    private static string ComputeProgressStatusMessage(PriceRefreshProgress progress)
    {
        return progress.Stage switch
        {
            PriceRefreshStage.Starting => $"Checking cache... {progress.Current}/{progress.Total}",
            PriceRefreshStage.Fetching when string.IsNullOrWhiteSpace(progress.ItemName) =>
                $"Fetching market prices... {progress.Current}/{progress.Total}",
            PriceRefreshStage.Fetching =>
                $"Loading item data: {progress.ItemName} ({progress.Current}/{progress.Total})",
            PriceRefreshStage.Updating => $"Processing results... {progress.Current}/{progress.Total}",
            PriceRefreshStage.Complete => $"Complete! ({progress.Total} items)",
            _ => $"Fetching prices... {progress.Current}/{progress.Total}"
        };
    }

    private static string BuildRefreshSummaryMessage(
        CraftingPlan plan,
        int successCount,
        int failedCount,
        int skippedCount,
        int cachedCount)
    {
        var totalCost = plan.AggregatedMaterials.Sum(m => m.TotalCost);

        if (failedCount > 0 && successCount == 0)
        {
            return $"Price fetch failed! Using cached prices. Total: {totalCost:N0}g";
        }

        if (failedCount > 0)
        {
            return $"Prices updated! Total: {totalCost:N0}g ({successCount} success, {failedCount} failed, {skippedCount} skipped, {cachedCount} cached)";
        }

        return $"Prices fetched! Total: {totalCost:N0}g ({successCount} success, {skippedCount} skipped, {cachedCount} cached)";
    }

    /// <summary>
    /// Applies the current sort to shopping plans.
    /// </summary>
    public void ApplySort()
    {
        var sorted = _currentSort switch
        {
            MarketSortOption.RecommendedWorld => _shoppingPlans.OrderBy(p => p.RecommendedWorld?.WorldName ?? "ZZZ"),
            MarketSortOption.Alphabetical => _shoppingPlans.OrderBy(p => p.Name),
            MarketSortOption.PriceHighToLow => _shoppingPlans.OrderByDescending(p => p.RecommendedWorld?.TotalCost ?? 0),
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
                TotalCost = g.Sum(i => i.RecommendedWorld?.TotalCost ?? 0),
                IsHomeWorld = g.First().RecommendedWorld?.IsHomeWorld ?? false
            })
            .ToList();

        GroupedByWorld = new ObservableCollection<ProcurementWorldViewModel>(groups);
    }

}

/// <summary>
/// Result of a plan price refresh operation.
/// Contains price data, refresh statistics, and cache metadata for UI status display.
/// </summary>
/// <param name="Success">Whether the refresh completed successfully.</param>
/// <param name="Message">Human-readable status message.</param>
/// <param name="AllItems">All items that were processed during refresh.</param>
/// <param name="Prices">Price information indexed by item ID.</param>
/// <param name="SuccessCount">Number of items fetched successfully this run.</param>
/// <param name="FailedCount">Number of items that failed to fetch.</param>
/// <param name="SkippedCount">Number of items skipped (e.g., crafted items with warming disabled).</param>
/// <param name="CachedCount">Number of items served from cache.</param>
/// <param name="CacheCandidateItemIds">Items eligible for cache warming.</param>
/// <param name="WarmCacheForCraftedItems">Whether crafted item cache warming was enabled.</param>
/// <param name="FetchedThisRunKeys">Item/DC pairs that were actually fetched this run.</param>
/// <param name="DataScopeByItemId">Cache scope metadata per item for status display.</param>
/// <param name="ScopeDataCenters">Data centers included in the search scope.</param>
public record PlanPriceRefreshResult(
    bool Success,
    string Message,
    List<(int itemId, string name, int quantity)> AllItems,
    Dictionary<int, PriceInfo> Prices,
    int SuccessCount,
    int FailedCount,
    int SkippedCount,
    int CachedCount,
    HashSet<int> CacheCandidateItemIds,
    bool WarmCacheForCraftedItems,
    HashSet<(int itemId, string dataCenter)> FetchedThisRunKeys,
    Dictionary<int, (int CachedDataCenterCount, int CachedWorldCount)> DataScopeByItemId,
    IReadOnlyList<string> ScopeDataCenters)
{
    public static PlanPriceRefreshResult NoPlan(string message) =>
        new(
            false,
            message,
            new List<(int itemId, string name, int quantity)>(),
            new Dictionary<int, PriceInfo>(),
            0,
            0,
            0,
            0,
            new HashSet<int>(),
            false,
            new HashSet<(int itemId, string dataCenter)>(),
            new Dictionary<int, (int CachedDataCenterCount, int CachedWorldCount)>(),
            Array.Empty<string>());

    public static PlanPriceRefreshResult Failed(string message) => NoPlan(message);
}

/// <summary>
/// Result of a cache rebuild operation.
/// Contains extracted prices from cached plan data.
/// </summary>
/// <param name="Success">Whether the rebuild completed successfully.</param>
/// <param name="Message">Human-readable status message.</param>
/// <param name="CachedPrices">Prices extracted from plan, indexed by item ID.</param>
public record CacheRebuildResult(
    bool Success,
    string Message,
    Dictionary<int, PriceInfo> CachedPrices)
{
    public static CacheRebuildResult NoPlan(string message) =>
        new(false, message, new Dictionary<int, PriceInfo>());

    public static CacheRebuildResult Failed(string message) =>
        new(false, message, new Dictionary<int, PriceInfo>());
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
