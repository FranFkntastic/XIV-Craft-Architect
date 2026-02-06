using System.IO;
using System.Windows;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Coordinators;

/// <summary>
/// Coordinates plan persistence operations (Save, Load, Rename).
/// Separates persistence logic from MainWindow.
/// </summary>
public class PlanPersistenceCoordinator
{
    private readonly IPlanPersistenceService _persistenceService;
    private readonly ILogger<PlanPersistenceCoordinator> _logger;

    public PlanPersistenceCoordinator(
        IPlanPersistenceService persistenceService,
        ILogger<PlanPersistenceCoordinator> logger)
    {
        _persistenceService = persistenceService;
        _logger = logger;
    }

    /// <summary>
    /// Result of a persistence operation.
    /// </summary>
    public record PersistenceResult(
        bool Success,
        string? PlanPath,
        string Message);

    /// <summary>
    /// Shows the plan browser dialog to load a plan.
    /// </summary>
    public async Task<(bool Selected, CraftingPlan? Plan, List<ProjectItem>? ProjectItems)> ShowPlanBrowserAsync(
        Window ownerWindow,
        CraftingPlan? currentPlan,
        List<ProjectItem> currentProjectItems,
        string currentPlanPath)
    {
        var mainWindow = ownerWindow as MainWindow;
        var browser = new PlanBrowserWindow(_persistenceService, mainWindow)
        {
            Owner = ownerWindow
        };

        if (browser.ShowDialog() != true)
        {
            return (false, null, null);
        }

        // User selected a plan to load
        if (!string.IsNullOrEmpty(browser.SelectedPlanPath))
        {
            try
            {
                var plan = await _persistenceService.LoadPlanAsync(browser.SelectedPlanPath);

                if (plan != null)
                {
                    // Convert to ProjectItem list
                    var items = plan.RootItems.Select(r => new ProjectItem
                    {
                        Id = r.ItemId,
                        Name = r.Name,
                        Quantity = r.Quantity,
                        IsHqRequired = r.MustBeHq
                    }).ToList();

                    return (true, plan, items);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plan from {Path}", browser.SelectedPlanPath);
            }
        }

        return (false, null, null);
    }

    /// <summary>
    /// Shows the save plan dialog and saves if confirmed.
    /// </summary>
    public async Task<PersistenceResult> SavePlanAsync(
        Window ownerWindow,
        CraftingPlan plan,
        List<ProjectItem> projectItems,
        string? currentPlanPath)
    {
        var saveDialog = new SavePlanDialog(_persistenceService, plan.Name)
        {
            Owner = ownerWindow
        };

        if (saveDialog.ShowDialog() != true)
        {
            return new PersistenceResult(false, null, "Save cancelled");
        }

        try
        {
            // Save using the dialog results
            bool success;
            string savedPath;

            if (saveDialog.IsOverwrite && !string.IsNullOrEmpty(saveDialog.OverwritePath))
            {
                // Overwrite existing file
                success = await _persistenceService.SavePlanAsync(plan, saveDialog.PlanName, saveDialog.OverwritePath);
                savedPath = saveDialog.OverwritePath;
            }
            else
            {
                // Save as new
                success = await _persistenceService.SavePlanAsync(plan, saveDialog.PlanName);
                savedPath = Path.Combine(_persistenceService.GetPlansDirectory(), $"{saveDialog.PlanName}.json");
            }

            if (success)
            {
                return new PersistenceResult(true, savedPath, $"Plan saved: {saveDialog.PlanName}");
            }
            else
            {
                return new PersistenceResult(false, null, "Failed to save plan");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save plan");
            return new PersistenceResult(false, null, $"Failed to save: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows the rename plan dialog and renames if confirmed.
    /// </summary>
    public async Task<PersistenceResult> RenamePlanAsync(Window ownerWindow, string currentPlanPath)
    {
        if (string.IsNullOrEmpty(currentPlanPath))
        {
            return new PersistenceResult(false, null, "No plan to rename - save the plan first");
        }

        // Load the plan to get its current name
        var plan = await _persistenceService.LoadPlanAsync(currentPlanPath);
        if (plan == null)
        {
            return new PersistenceResult(false, null, "Failed to load plan for renaming");
        }

        var renameDialog = new RenamePlanDialog(plan.Name)
        {
            Owner = ownerWindow
        };

        if (renameDialog.ShowDialog() != true)
        {
            return new PersistenceResult(false, null, "Rename cancelled");
        }

        try
        {
            // Rename: update name, delete old file, save with new name
            var oldName = plan.Name;
            plan.Name = renameDialog.NewName;
            plan.MarkModified();

            _persistenceService.DeletePlan(currentPlanPath);
            bool success = await _persistenceService.SavePlanAsync(plan);

            if (success)
            {
                string newPath = Path.Combine(_persistenceService.GetPlansDirectory(), $"{renameDialog.NewName}.json");
                return new PersistenceResult(true, newPath, $"Plan renamed to: {renameDialog.NewName}");
            }
            else
            {
                return new PersistenceResult(false, null, "Failed to rename plan");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename plan");
            return new PersistenceResult(false, null, $"Failed to rename: {ex.Message}");
        }
    }
}
