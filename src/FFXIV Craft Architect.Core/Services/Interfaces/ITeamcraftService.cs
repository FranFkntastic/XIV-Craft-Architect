using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Service for importing/exporting crafting lists to/from Teamcraft.
/// </summary>
public interface ITeamcraftService
{
    /// <summary>
    /// Export a crafting plan to Teamcraft format.
    /// Returns the full URL that can be opened in browser or shared.
    /// </summary>
    /// <param name="plan">Crafting plan to export</param>
    /// <returns>Teamcraft import URL</returns>
    string ExportToTeamcraft(CraftingPlan plan);
    
    /// <summary>
    /// Create a plain text summary of the plan (for clipboard).
    /// Format: "5x Item Name\n3x Other Item"
    /// </summary>
    /// <param name="plan">Crafting plan to export</param>
    /// <returns>Plain text representation</returns>
    string ExportToPlainText(CraftingPlan plan);
    
    /// <summary>
    /// Export shopping list to CSV format.
    /// </summary>
    /// <param name="plan">Crafting plan to export</param>
    /// <returns>CSV formatted string</returns>
    string ExportToCsv(CraftingPlan plan);
    
    /// <summary>
    /// Parse Teamcraft "Copy as Text" format into a list of items.
    /// Format: "5x Item Name\n3x Other Item"
    /// </summary>
    /// <param name="text">Text to parse</param>
    /// <returns>List of item names and quantities</returns>
    List<(string name, int quantity)> ParseTeamcraftText(string text);
    
    /// <summary>
    /// Import from Teamcraft text format and create a new crafting plan.
    /// Requires searching for each item to get its ID.
    /// </summary>
    /// <param name="name">Plan name</param>
    /// <param name="precraftText">Pre-craft items text</param>
    /// <param name="finalItemsText">Final items text</param>
    /// <param name="dataCenter">Data center name</param>
    /// <param name="world">World name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Crafting plan or null if import failed</returns>
    Task<CraftingPlan?> ImportFromTeamcraftTextAsync(
        string name, 
        string precraftText, 
        string finalItemsText,
        string dataCenter, 
        string world,
        CancellationToken ct = default);
}
