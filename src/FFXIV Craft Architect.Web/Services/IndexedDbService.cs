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
            plan.SavedAt = DateTime.UtcNow;
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
    public async Task<bool> AutoSaveStateAsync(AppState state, string planName = "AutoSave")
    {
        try
        {
            if (state.CurrentPlan == null && !state.ProjectItems.Any())
                return false;

            var planData = new StoredPlan
            {
                Id = "autosave",
                Name = planName,
                DataCenter = state.SelectedDataCenter,
                ModifiedAt = DateTime.UtcNow,
                ProjectItems = state.ProjectItems.Select(p => new StoredProjectItem
                {
                    Id = p.Id,
                    Name = p.Name,
                    IconId = p.IconId,
                    Quantity = p.Quantity,
                    MustBeHq = p.MustBeHq
                }).ToList(),
                PlanJson = state.CurrentPlan != null 
                    ? System.Text.Json.JsonSerializer.Serialize(state.CurrentPlan) 
                    : null,
                MarketPlansJson = state.ShoppingPlans?.Any() == true
                    ? System.Text.Json.JsonSerializer.Serialize(state.ShoppingPlans)
                    : null,
                SavedRecommendationMode = state.RecommendationMode
            };

            return await SavePlanAsync(planData);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to auto-save state");
            return false;
        }
    }

    /// <summary>
    /// Save market analysis results for a plan.
    /// </summary>
    public async Task<bool> SaveMarketAnalysisAsync(string planId, List<DetailedShoppingPlan> shoppingPlans, RecommendationMode mode)
    {
        try
        {
            await EnsureInitialized();
            
            // Load existing plan first
            var existingPlan = await LoadPlanAsync(planId);
            if (existingPlan == null)
                return false;
            
            // Update with market data
            existingPlan.MarketPlansJson = System.Text.Json.JsonSerializer.Serialize(shoppingPlans);
            existingPlan.SavedRecommendationMode = mode;
            existingPlan.ModifiedAt = DateTime.UtcNow;
            
            return await SavePlanAsync(existingPlan);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save market analysis for plan {PlanId}", planId);
            return false;
        }
    }

    /// <summary>
    /// Load market analysis results.
    /// </summary>
    public async Task<(List<DetailedShoppingPlan>? Plans, RecommendationMode Mode)> LoadMarketAnalysisAsync(string planId)
    {
        try
        {
            await EnsureInitialized();
            
            var plan = await LoadPlanAsync(planId);
            if (plan?.MarketPlansJson == null)
                return (null, RecommendationMode.MinimizeTotalCost);
            
            var plans = System.Text.Json.JsonSerializer.Deserialize<List<DetailedShoppingPlan>>(plan.MarketPlansJson);
            return (plans, plan.SavedRecommendationMode);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load market analysis for plan {PlanId}", planId);
            return (null, RecommendationMode.MinimizeTotalCost);
        }
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
    /// Recommendation mode used for the saved market analysis.
    /// </summary>
    public RecommendationMode SavedRecommendationMode { get; set; } = RecommendationMode.MinimizeTotalCost;
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
