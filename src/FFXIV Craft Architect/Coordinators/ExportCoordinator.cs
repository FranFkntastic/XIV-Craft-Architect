using System.IO;
using System.Windows;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;
using ITeamcraftService = FFXIV_Craft_Architect.Core.Services.ITeamcraftService;

namespace FFXIV_Craft_Architect.Coordinators;

/// <summary>
/// Coordinates all export operations (Native, Teamcraft, Artisan, Plain Text, CSV).
/// Separates export logic from MainWindow.
/// </summary>
public class ExportCoordinator
{
    private readonly ITeamcraftService _teamcraftService;
    private readonly IArtisanService _artisanService;
    private readonly RecipeCalculationService _recipeCalcService;
    private readonly ILogger<ExportCoordinator> _logger;

    public ExportCoordinator(
        ITeamcraftService teamcraftService,
        IArtisanService artisanService,
        RecipeCalculationService recipeCalcService,
        ILogger<ExportCoordinator> logger)
    {
        _teamcraftService = teamcraftService;
        _artisanService = artisanService;
        _recipeCalcService = recipeCalcService;
        _logger = logger;
    }

    /// <summary>
    /// Result of an export operation.
    /// </summary>
    public record ExportResult(
        bool Success,
        string? Content,
        string Message);

    /// <summary>
    /// Result of a native file export operation.
    /// </summary>
    public record NativeExportResult(
        bool Success,
        string? FilePath,
        string Message);

    /// <summary>
    /// Export current plan to native Craft Architect JSON format.
    /// </summary>
    public ExportResult ExportToNative(CraftingPlan plan)
    {
        if (plan == null || plan.RootItems.Count == 0)
        {
            return new ExportResult(false, null, "No plan to export - build a plan first");
        }

        try
        {
            var json = _recipeCalcService.SerializePlan(plan);
            return new ExportResult(true, json, "Native plan JSON generated!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export to native format");
            return new ExportResult(false, null, $"Export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Export current plan to native format and save to file.
    /// </summary>
    public async Task<NativeExportResult> ExportToNativeFileAsync(CraftingPlan plan, string? filePath = null)
    {
        if (plan == null || plan.RootItems.Count == 0)
        {
            return new NativeExportResult(false, null, "No plan to export - build a plan first");
        }

        try
        {
            var json = _recipeCalcService.SerializePlan(plan);
            
            // If no path specified, use save dialog
            if (string.IsNullOrEmpty(filePath))
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Craft Architect Plan (*.craftplan)|*.craftplan|JSON files (*.json)|*.json|All files (*.*)|*.*",
                    DefaultExt = "craftplan",
                    FileName = $"{plan.Name}.craftplan"
                };

                if (dialog.ShowDialog() != true)
                {
                    return new NativeExportResult(false, null, "Export cancelled");
                }

                filePath = dialog.FileName;
            }

            await File.WriteAllTextAsync(filePath, json);
            return new NativeExportResult(true, filePath, $"Plan saved to {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export native plan to file");
            return new NativeExportResult(false, null, $"Export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Export current plan to Teamcraft URL format.
    /// </summary>
    public ExportResult ExportToTeamcraft(CraftingPlan plan)
    {
        if (plan == null || plan.RootItems.Count == 0)
        {
            return new ExportResult(false, null, "No plan to export - build a plan first");
        }

        var url = _teamcraftService.ExportToTeamcraft(plan);
        return new ExportResult(true, url, "Teamcraft URL copied to clipboard!");
    }

    /// <summary>
    /// Export current plan to Artisan JSON format.
    /// </summary>
    public async Task<ExportResult> ExportToArtisanAsync(CraftingPlan plan)
    {
        if (plan == null || plan.RootItems.Count == 0)
        {
            return new ExportResult(false, null, "No plan to export - build a plan first");
        }

        try
        {
            var result = await _artisanService.ExportToArtisanAsync(plan);

            if (!result.Success)
            {
                var summary = _artisanService.CreateExportSummary(result);
                return new ExportResult(true, result.Json, summary);
            }

            return new ExportResult(
                true,
                result.Json,
                $"Artisan export complete! {result.RecipeCount} recipes copied to clipboard.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export to Artisan format");
            return new ExportResult(false, null, $"Export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Export plan as plain text.
    /// </summary>
    public ExportResult ExportToPlainText(CraftingPlan plan)
    {
        if (plan == null || plan.RootItems.Count == 0)
        {
            return new ExportResult(false, null, "No plan to export - build a plan first");
        }

        var text = _teamcraftService.ExportToPlainText(plan);
        return new ExportResult(true, text, "Plan text copied to clipboard!");
    }

    /// <summary>
    /// Export shopping list as CSV.
    /// </summary>
    public ExportResult ExportToCsv(CraftingPlan plan)
    {
        if (plan == null || plan.RootItems.Count == 0)
        {
            return new ExportResult(false, null, "No plan to export - build a plan first");
        }

        var csv = _teamcraftService.ExportToCsv(plan);
        return new ExportResult(true, csv, "CSV copied to clipboard!");
    }

    /// <summary>
    /// Try to set clipboard text with retry logic (clipboard may be locked by another app).
    /// </summary>
    public async Task<bool> TrySetClipboardAsync(string text, int maxRetries = 5)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() => Clipboard.SetText(text));
                return true;
            }
            catch
            {
                if (i == maxRetries - 1) return false;
                await Task.Delay(100);
            }
        }
        return false;
    }
}
