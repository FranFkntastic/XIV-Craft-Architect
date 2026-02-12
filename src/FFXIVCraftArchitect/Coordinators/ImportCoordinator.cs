using System.Collections.ObjectModel;
using System.Windows;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Core.Services.Interfaces;
using FFXIVCraftArchitect.ViewModels;
using FFXIVCraftArchitect.Views;
using Microsoft.Extensions.Logging;
using ITeamcraftService = FFXIVCraftArchitect.Core.Services.ITeamcraftService;

namespace FFXIVCraftArchitect.Coordinators;

/// <summary>
/// Coordinates all import operations (Teamcraft, Artisan) into the application.
/// Separates import logic from MainWindow.
/// </summary>
public class ImportCoordinator
{
    private readonly ITeamcraftService _teamcraftService;
    private readonly IArtisanService _artisanService;
    private readonly ILogger<ImportCoordinator> _logger;

    public ImportCoordinator(
        ITeamcraftService teamcraftService,
        IArtisanService artisanService,
        ILogger<ImportCoordinator> logger)
    {
        _teamcraftService = teamcraftService;
        _artisanService = artisanService;
        _logger = logger;
    }

    /// <summary>
    /// Result of an import operation.
    /// </summary>
    public record ImportResult(
        bool Success,
        CraftingPlan? Plan,
        List<ProjectItem>? ProjectItems,
        string Message);

    /// <summary>
    /// Import from Teamcraft "Copy as Text" format using a dialog.
    /// </summary>
    public ImportResult ImportFromTeamcraft(Window ownerWindow, string dataCenter, string world)
    {
        var importDialog = new TeamcraftImportWindow(_teamcraftService, dataCenter, world)
        {
            Owner = ownerWindow
        };

        if (importDialog.ShowDialog() != true || importDialog.ImportedPlan == null)
        {
            return new ImportResult(false, null, null, "Import cancelled");
        }

        var plan = importDialog.ImportedPlan;
        var projectItems = CreateProjectItemsFromPlan(plan);

        return new ImportResult(
            true,
            plan,
            projectItems,
            $"Imported plan with {plan.RootItems.Count} items from Teamcraft");
    }

    /// <summary>
    /// Import from Artisan JSON format from clipboard.
    /// </summary>
    public async Task<ImportResult> ImportFromArtisanAsync(string dataCenter, string world)
    {
        // Get clipboard content
        string clipboardText;
        try
        {
            clipboardText = Clipboard.GetText();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read clipboard for Artisan import");
            return new ImportResult(false, null, null, "Failed to read clipboard. Please try again.");
        }

        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            return new ImportResult(false, null, null, "Clipboard is empty. Copy an Artisan export first.");
        }

        try
        {
            var plan = await _artisanService.ImportFromArtisanAsync(clipboardText, dataCenter, world);

            if (plan == null)
            {
                return new ImportResult(false, null, null, "Failed to import - invalid Artisan format or no recipes found.");
            }

            var projectItems = CreateProjectItemsFromPlan(plan);

            return new ImportResult(
                true,
                plan,
                projectItems,
                $"Imported plan with {plan.RootItems.Count} items from Artisan");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import from Artisan format");
            return new ImportResult(false, null, null, $"Import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Syncs a plan's root items to the ViewModel's project items.
    /// </summary>
    public static List<ProjectItem> CreateProjectItemsFromPlan(CraftingPlan plan)
    {
        var projectItems = new List<ProjectItem>();
        foreach (var rootItem in plan.RootItems)
        {
            projectItems.Add(new ProjectItem
            {
                Id = rootItem.ItemId,
                Name = rootItem.Name,
                Quantity = rootItem.Quantity,
                IsHqRequired = rootItem.MustBeHq
            });
        }
        return projectItems;
    }
}
