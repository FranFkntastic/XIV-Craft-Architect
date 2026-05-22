using System.Collections.ObjectModel;
using System.Windows;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.ViewModels;
using FFXIV_Craft_Architect.Views;
using Microsoft.Extensions.Logging;
using ITeamcraftService = FFXIV_Craft_Architect.Core.Services.ITeamcraftService;

namespace FFXIV_Craft_Architect.Coordinators;

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
    /// Import from Artisan JSON format using a dialog.
    /// </summary>
    public ImportResult ImportFromArtisan(Window ownerWindow, string dataCenter, string world)
    {
        var importDialog = new ArtisanImportWindow(_artisanService, dataCenter, world)
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
            $"Imported plan with {plan.RootItems.Count} items from Artisan");
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
