using System.Collections.ObjectModel;
using System.ComponentModel;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.ViewModels;

/// <summary>
/// ViewModel that owns all plan-related state.
/// Acts as the single source of truth for plan state across the application.
/// </summary>
public class PlanViewModel : ViewModelBase
{
    private readonly RecipePlannerViewModel _recipePlanner;
    private readonly MarketAnalysisViewModel _marketAnalysis;
    private string? _currentPlanPath;
    private DetailedShoppingPlan? _expandedSplitPanePlan;

    /// <summary>
    /// Creates a new PlanViewModel with the specified child ViewModels.
    /// </summary>
    public PlanViewModel(RecipePlannerViewModel recipePlanner, MarketAnalysisViewModel marketAnalysis)
    {
        _recipePlanner = recipePlanner;
        _marketAnalysis = marketAnalysis;

        // Subscribe to child ViewModel property changes for bubbling
        _recipePlanner.PropertyChanged += OnRecipePlannerPropertyChanged;
        _marketAnalysis.PropertyChanged += OnMarketAnalysisPropertyChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _recipePlanner.PropertyChanged -= OnRecipePlannerPropertyChanged;
            _marketAnalysis.PropertyChanged -= OnMarketAnalysisPropertyChanged;
        }
        base.Dispose(disposing);
    }

    private void OnRecipePlannerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Bubble up properties that PlanViewModel exposes
        switch (e.PropertyName)
        {
            case nameof(RecipePlannerViewModel.CurrentPlan):
                OnPropertyChanged(nameof(CurrentPlan));
                OnPropertyChanged(nameof(HasPlan));
                OnPropertyChanged(nameof(PlanName));
                OnPropertyChanged(nameof(AggregatedMaterials));
                PlanChanged?.Invoke(this, EventArgs.Empty);
                break;

            case nameof(RecipePlannerViewModel.ProjectItems):
                OnPropertyChanged(nameof(ProjectItems));
                break;

            case nameof(RecipePlannerViewModel.RootNodes):
                OnPropertyChanged(nameof(RootNodes));
                break;
        }
    }

    private void OnMarketAnalysisPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Bubble up properties that PlanViewModel exposes
        switch (e.PropertyName)
        {
            case nameof(MarketAnalysisViewModel.ShoppingPlans):
                OnPropertyChanged(nameof(ShoppingPlans));
                OnPropertyChanged(nameof(HasMarketData));
                OnPropertyChanged(nameof(TotalCost));
                break;

            case nameof(MarketAnalysisViewModel.HasData):
                OnPropertyChanged(nameof(HasMarketData));
                break;

            case nameof(MarketAnalysisViewModel.TotalCost):
                OnPropertyChanged(nameof(TotalCost));
                break;
        }
    }

    // ========================================================================
    // Child ViewModel Access (for MainWindow code-behind compatibility)
    // ========================================================================

    /// <summary>
    /// The RecipePlanner child ViewModel.
    /// </summary>
    public RecipePlannerViewModel RecipePlanner => _recipePlanner;

    /// <summary>
    /// The MarketAnalysis child ViewModel.
    /// </summary>
    public MarketAnalysisViewModel MarketAnalysis => _marketAnalysis;

    // ========================================================================
    // Delegating Properties (from RecipePlannerViewModel)
    // ========================================================================

    /// <summary>
    /// The current crafting plan being edited.
    /// </summary>
    public CraftingPlan? CurrentPlan
    {
        get => _recipePlanner.CurrentPlan;
        set => _recipePlanner.CurrentPlan = value;
    }

    /// <summary>
    /// Whether a plan is currently loaded.
    /// </summary>
    public bool HasPlan => CurrentPlan != null;

    /// <summary>
    /// The name of the current plan, or a default value if none is loaded.
    /// </summary>
    public string PlanName => CurrentPlan?.Name ?? "Untitled Plan";

    /// <summary>
    /// Root-level items in the project (what the user wants to craft).
    /// </summary>
    public ObservableCollection<ProjectItem> ProjectItems => _recipePlanner.ProjectItems;

    /// <summary>
    /// Root nodes of the recipe tree (derived from CurrentPlan).
    /// </summary>
    public ObservableCollection<PlanNodeViewModel> RootNodes => _recipePlanner.RootNodes;

    /// <summary>
    /// Aggregated materials from the current plan.
    /// </summary>
    public List<MaterialAggregate> AggregatedMaterials => _recipePlanner.AggregatedMaterials;

    // ========================================================================
    // Delegating Properties (from MarketAnalysisViewModel)
    // ========================================================================

    /// <summary>
    /// Shopping plans for each material needed.
    /// </summary>
    public ObservableCollection<ShoppingPlanViewModel> ShoppingPlans => _marketAnalysis.ShoppingPlans;

    /// <summary>
    /// Whether any market data is available.
    /// </summary>
    public bool HasMarketData => _marketAnalysis.HasData;

    /// <summary>
    /// Total cost across all shopping plans.
    /// </summary>
    public long TotalCost => _marketAnalysis.TotalCost;

    // ========================================================================
    // Own Properties (previously in MainWindow)
    // ========================================================================

    /// <summary>
    /// The file path of the currently loaded/saved plan.
    /// </summary>
    public string? CurrentPlanPath
    {
        get => _currentPlanPath;
        set
        {
            _currentPlanPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPlanPath));
        }
    }

    /// <summary>
    /// Whether a plan path is set (plan has been saved/loaded from file).
    /// </summary>
    public bool HasPlanPath => !string.IsNullOrEmpty(_currentPlanPath);

    /// <summary>
    /// The currently expanded plan in the split-pane market view.
    /// </summary>
    public DetailedShoppingPlan? ExpandedSplitPanePlan
    {
        get => _expandedSplitPanePlan;
        set
        {
            _expandedSplitPanePlan = value;
            OnPropertyChanged();
        }
    }

    // ========================================================================
    // Events
    // ========================================================================

    /// <summary>
    /// Raised when the current plan changes.
    /// </summary>
    public event EventHandler? PlanChanged;

    /// <summary>
    /// Raised when the plan is saved.
    /// </summary>
    public event EventHandler? PlanSaved;

    // ========================================================================
    // Methods
    // ========================================================================

    /// <summary>
    /// Clears all plan state (project items, plan, market data).
    /// </summary>
    public void Clear()
    {
        _recipePlanner.Clear();
        _marketAnalysis.Clear();
        CurrentPlanPath = null;
        ExpandedSplitPanePlan = null;
    }

    /// <summary>
    /// Sets a new plan and optionally updates the plan path.
    /// </summary>
    public void SetPlan(CraftingPlan plan, string? planPath = null)
    {
        CurrentPlan = plan;
        if (planPath != null)
        {
            CurrentPlanPath = planPath;
        }
    }

    /// <summary>
    /// Called when the plan is saved to update the path and raise the PlanSaved event.
    /// </summary>
    public void OnPlanSaved(string planPath)
    {
        CurrentPlanPath = planPath;
        PlanSaved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets the current plan suitable for watch state saving.
    /// Includes saved market plans if available.
    /// </summary>
    public CraftingPlan? GetPlanForWatch()
    {
        var plan = _recipePlanner.GetPlanForWatch();
        if (plan != null && _marketAnalysis.ShoppingPlans.Any())
        {
            plan.SavedMarketPlans = GetShoppingPlans();
        }
        return plan;
    }

    /// <summary>
    /// Restores a plan from watch state.
    /// </summary>
    public void RestoreFromWatch(CraftingPlan plan, List<ProjectItem> projectItems)
    {
        _recipePlanner.RestoreFromWatch(plan, projectItems);

        // Restore saved market plans if available
        if (plan.SavedMarketPlans?.Count > 0)
        {
            _marketAnalysis.SetShoppingPlans(plan.SavedMarketPlans);
        }
    }

    /// <summary>
    /// Gets the shopping plans as a list of DetailedShoppingPlan models.
    /// </summary>
    public List<DetailedShoppingPlan> GetShoppingPlans()
    {
        return _marketAnalysis.GetPlansForWatch();
    }

    /// <summary>
    /// Sets the shopping plans from service results.
    /// </summary>
    public void SetShoppingPlans(List<DetailedShoppingPlan> plans)
    {
        _marketAnalysis.SetShoppingPlans(plans);
    }
}
