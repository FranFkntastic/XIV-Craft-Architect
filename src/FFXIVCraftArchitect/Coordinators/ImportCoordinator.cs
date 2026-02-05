using System.Collections.ObjectModel;
using System.Windows;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Services;
using FFXIVCraftArchitect.ViewModels;
using FFXIVCraftArchitect.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Coordinators;

/// <summary>
/// Coordinates all import operations (Teamcraft, Artisan) into the application.
/// Separates import logic from MainWindow.
/// </summary>
public class ImportCoordinator
{
    private readonly TeamcraftService _teamcraftService;
    private readonly ArtisanService _artisanService;
    private readonly ILogger<ImportCoordinator> _logger;
    private readonly Window _ownerWindow;

    public ImportCoordinator(Window ownerWindow)
    {
        _ownerWindow = ownerWindow;
        _teamcraftService = App.Services.GetRequiredService<TeamcraftService>();
        _artisanService = App.Services.GetRequiredService<ArtisanService>();
        _logger = App.Services.GetRequiredService<ILogger<ImportCoordinator>>();
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
    public ImportResult ImportFromTeamcraft(string dataCenter, string world)
    {
        var importDialog = new TeamcraftImportWindow(_teamcraftService, dataCenter, world)
        {
            Owner = _ownerWindow
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
