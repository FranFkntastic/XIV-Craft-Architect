using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Core.Services;

namespace FFXIVCraftArchitect.Web.Services;

/// <summary>
/// Global application state that persists across pages.
/// Centralizes WorldData, auto-save, and plan state management.
/// </summary>

/// <summary>
/// Application-wide state service to share data between pages.
/// Plans persist when switching between Market Logistics and Recipe Planner tabs.
/// </summary>
public class AppState
{
    // Recipe Planner State
    public CraftingPlan? CurrentPlan { get; set; }
    public List<ProjectItem> ProjectItems { get; set; } = new();
    public List<CraftVsBuyAnalysis> CraftAnalyses { get; set; } = new();
    public string SelectedDataCenter { get; set; } = "Aether";
    public string SelectedRegion { get; set; } = "North America";
    
    // Procurement Planner State
    public List<MarketShoppingItem> ShoppingItems { get; set; } = new();
    public List<DetailedShoppingPlan> ShoppingPlans { get; set; } = new();
    public RecommendationMode RecommendationMode { get; set; } = RecommendationMode.MinimizeTotalCost;
    public ProcurementAnalysis? CurrentProcurementAnalysis { get; set; }
    
    /// <summary>
    /// Temporarily blacklisted worlds for the current session.
    /// These worlds are excluded from procurement recommendations.
    /// Cleared on page reload - NOT persisted.
    /// </summary>
    public HashSet<string> TemporarilyBlacklistedWorlds { get; set; } = new();
    
    // Auto-expand item ID when navigating from procurement to market analysis
    public int? AutoExpandItemId { get; set; }
    
    // Persistence state
    public bool IsAutoSaveEnabled { get; set; } = true;
    public bool AutoFetchPricesOnRebuild { get; set; } = true;
    public DateTime? LastAutoSave { get; set; }
    public List<StoredPlanSummary> SavedPlans { get; set; } = new();
    
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
    public event Action? OnProcurementAnalysisChanged;
    public event Action? OnRecipeTreeExpandChanged;
    
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
        OnPlanChanged?.Invoke();
    }
    
    public void NotifyShoppingListChanged()
    {
        OnShoppingListChanged?.Invoke();
    }
    
    public void NotifySavedPlansChanged()
    {
        OnSavedPlansChanged?.Invoke();
    }
    
    public void NotifyStatusChanged()
    {
        LastStatusUpdate = DateTime.Now;
        OnStatusChanged?.Invoke();
    }
    
    public void NotifyProcurementAnalysisChanged()
    {
        OnProcurementAnalysisChanged?.Invoke();
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
        CraftAnalyses.Clear();
        ShoppingItems.Clear();
        ShoppingPlans.Clear();
        CurrentPlanId = null;  // Reset plan ID for new plan
        CurrentPlanName = null;
        NotifyPlanChanged();
        NotifyShoppingListChanged();
    }

    /// <summary>
    /// Load a stored plan into the current state.
    /// </summary>
    public void LoadStoredPlan(StoredPlan storedPlan, CraftingPlan? deserializedPlan)
    {
        SelectedDataCenter = storedPlan.DataCenter;
        
        ProjectItems = storedPlan.ProjectItems.Select(p => new ProjectItem
        {
            Id = p.Id,
            Name = p.Name,
            IconId = p.IconId,
            Quantity = p.Quantity,
            MustBeHq = p.MustBeHq
        }).ToList();
        
        CurrentPlan = deserializedPlan;
        
        // Track the loaded plan ID for save-overwrite behavior
        CurrentPlanId = storedPlan.Id;
        CurrentPlanName = storedPlan.Name;
        
        // Only sync to shopping if shopping list is empty (preserve existing shopping list otherwise)
        if (!ShoppingItems.Any())
        {
            SyncProjectToShopping();
        }
        
        NotifyPlanChanged();
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
}

// PlannerProjectItem removed - now using FFXIVCraftArchitect.Core.Models.ProjectItem

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
