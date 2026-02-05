using System.Windows;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Coordinators;

/// <summary>
/// Coordinates all export operations (Teamcraft, Artisan, Plain Text, CSV).
/// Separates export logic from MainWindow.
/// </summary>
public class ExportCoordinator
{
    private readonly TeamcraftService _teamcraftService;
    private readonly ArtisanService _artisanService;
    private readonly ILogger<ExportCoordinator> _logger;

    public ExportCoordinator()
    {
        _teamcraftService = App.Services.GetRequiredService<TeamcraftService>();
        _artisanService = App.Services.GetRequiredService<ArtisanService>();
        _logger = App.Services.GetRequiredService<ILogger<ExportCoordinator>>();
    }

    /// <summary>
    /// Result of an export operation.
    /// </summary>
    public record ExportResult(
        bool Success,
        string? Content,
        string Message);

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
