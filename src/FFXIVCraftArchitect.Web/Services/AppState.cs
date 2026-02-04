using FFXIVCraftArchitect.Core.Models;

namespace FFXIVCraftArchitect.Web.Services;

/// <summary>
/// Application-wide state service to share data between pages.
/// Plans persist when switching between Market Logistics and Recipe Planner tabs.
/// </summary>
public class AppState
{
    // Recipe Planner State
    public CraftingPlan? CurrentPlan { get; set; }
    public List<PlannerProjectItem> ProjectItems { get; set; } = new();
    public List<CraftVsBuyAnalysis> CraftAnalyses { get; set; } = new();
    public string SelectedDataCenter { get; set; } = "Aether";
    
    // Market Logistics State
    public List<MarketShoppingItem> ShoppingItems { get; set; } = new();
    public List<DetailedShoppingPlan> ShoppingPlans { get; set; } = new();
    public RecommendationMode RecommendationMode { get; set; } = RecommendationMode.MinimizeTotalCost;
    
    // Persistence state
    public bool IsAutoSaveEnabled { get; set; } = true;
    public DateTime? LastAutoSave { get; set; }
    public List<StoredPlanSummary> SavedPlans { get; set; } = new();
    
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
        SetStatus(message ?? "Ready", busy: false, progress: 0);
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
        ProjectItems = ShoppingItems.Select(s => new PlannerProjectItem
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
        NotifyPlanChanged();
        NotifyShoppingListChanged();
    }

    /// <summary>
    /// Load a stored plan into the current state.
    /// </summary>
    public void LoadStoredPlan(StoredPlan storedPlan, CraftingPlan? deserializedPlan)
    {
        SelectedDataCenter = storedPlan.DataCenter;
        
        ProjectItems = storedPlan.ProjectItems.Select(p => new PlannerProjectItem
        {
            Id = p.Id,
            Name = p.Name,
            IconId = p.IconId,
            Quantity = p.Quantity,
            MustBeHq = p.MustBeHq
        }).ToList();
        
        CurrentPlan = deserializedPlan;
        
        // Sync to shopping
        SyncProjectToShopping();
        
        NotifyPlanChanged();
    }
}

public class PlannerProjectItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    public int Quantity { get; set; }
    public bool MustBeHq { get; set; }
}

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
