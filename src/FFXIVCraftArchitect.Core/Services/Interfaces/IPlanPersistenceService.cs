using FFXIVCraftArchitect.Core.Models;

namespace FFXIVCraftArchitect.Core.Services.Interfaces;

/// <summary>
/// Service for saving and loading crafting plans to/from disk.
/// </summary>
public interface IPlanPersistenceService
{
    /// <summary>
    /// Save a plan to disk.
    /// </summary>
    /// <param name="plan">The plan to save.</param>
    /// <param name="customName">Optional custom name for the plan.</param>
    /// <param name="overwritePath">Optional path to an existing plan file to overwrite.</param>
    Task<bool> SavePlanAsync(CraftingPlan plan, string? customName = null, string? overwritePath = null);

    /// <summary>
    /// Load a plan from disk.
    /// </summary>
    Task<CraftingPlan?> LoadPlanAsync(string filePath);

    /// <summary>
    /// Get a list of all saved plans.
    /// </summary>
    List<PlanInfo> ListSavedPlans();

    /// <summary>
    /// Delete a saved plan.
    /// </summary>
    bool DeletePlan(string filePath);

    /// <summary>
    /// Export a plan to a specific location (for sharing).
    /// Includes both JSON and CSV files.
    /// </summary>
    Task<bool> ExportPlanAsync(CraftingPlan plan, string filePath);

    /// <summary>
    /// Import a plan from a specific location.
    /// </summary>
    Task<CraftingPlan?> ImportPlanAsync(string filePath);

    /// <summary>
    /// Get the default directory for saving plans.
    /// </summary>
    string GetPlansDirectory();
}
