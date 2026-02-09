using FFXIVCraftArchitect.Core.Models;

namespace FFXIVCraftArchitect.Services.Interfaces;

/// <summary>
/// Service for importing/exporting crafting lists to/from Artisan format.
/// Artisan is a Dalamud plugin for FFXIV crafting automation.
/// </summary>
public interface IArtisanService
{
    /// <summary>
    /// Import an Artisan crafting list from JSON.
    /// </summary>
    /// <param name="artisanJson">Artisan JSON export</param>
    /// <param name="dataCenter">Data center name</param>
    /// <param name="world">World name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Crafting plan or null if import failed</returns>
    Task<CraftingPlan?> ImportFromArtisanAsync(
        string artisanJson, 
        string dataCenter, 
        string world,
        CancellationToken ct = default);
    
    /// <summary>
    /// Export a crafting plan to Artisan JSON format.
    /// Returns JSON that can be pasted into Artisan's "Import List From Clipboard" feature.
    /// </summary>
    /// <param name="plan">Crafting plan to export</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Export result with JSON and metadata</returns>
    Task<ArtisanExportResult> ExportToArtisanAsync(CraftingPlan plan, CancellationToken ct = default);
    
    /// <summary>
    /// Create a summary text of the export result for display to the user.
    /// </summary>
    /// <param name="result">Export result</param>
    /// <returns>Human-readable summary</returns>
    string CreateExportSummary(ArtisanExportResult result);
}
