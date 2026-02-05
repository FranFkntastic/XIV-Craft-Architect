using System.IO;
using System.Text;
using FFXIVCraftArchitect.Models;
using FFXIVCraftArchitect.Core.Models;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for importing/exporting crafting lists to/from Teamcraft.
/// </summary>
public class TeamcraftService
{
    private readonly ILogger<TeamcraftService> _logger;
    private readonly GarlandService _garlandService;

    public TeamcraftService(ILogger<TeamcraftService> logger, GarlandService garlandService)
    {
        _logger = logger;
        _garlandService = garlandService;
    }

    /// <summary>
    /// Export a crafting plan to Teamcraft format.
    /// Returns the full URL that can be opened in browser or shared.
    /// </summary>
    public string ExportToTeamcraft(CraftingPlan plan)
    {
        // Teamcraft format: itemId,null,quantity;itemId,null,quantity;
        var sb = new StringBuilder();
        
        foreach (var rootItem in plan.RootItems)
        {
            // Get the item ID from the root node
            // Teamcraft uses the resulting item ID, not recipe ID
            sb.Append($"{rootItem.ItemId},null,{rootItem.Quantity};");
        }
        
        // Remove trailing semicolon
        var exportString = sb.ToString().TrimEnd(';');
        
        // Base64 encode
        var plainTextBytes = Encoding.UTF8.GetBytes(exportString);
        var base64 = Convert.ToBase64String(plainTextBytes);
        
        var url = $"https://ffxivteamcraft.com/import/{base64}";
        
        _logger.LogInformation("[Teamcraft] Exported plan '{PlanName}' to Teamcraft format", plan.Name);
        return url;
    }

    /// <summary>
    /// Parse Teamcraft "Copy as Text" format into a list of items.
    /// Format: "5x Item Name\n3x Other Item"
    /// </summary>
    public List<(string name, int quantity)> ParseTeamcraftText(string text)
    {
        var items = new List<(string name, int quantity)>();
        
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            
            // Parse "5x Item Name" format
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            
            // First part should be "5x" or similar
            var quantityPart = parts[0];
            if (quantityPart.EndsWith('x') || quantityPart.EndsWith('×'))
            {
                quantityPart = quantityPart.TrimEnd('x', '×');
            }
            
            if (!int.TryParse(quantityPart, out var quantity))
                continue;
            
            // Rest is the item name
            var name = string.Join(" ", parts.Skip(1)).Trim();
            
            // Remove HQ symbol if present
            name = name.Replace("", "").Trim(); // HQ symbol
            
            if (!string.IsNullOrWhiteSpace(name))
            {
                items.Add((name, quantity));
            }
        }
        
        _logger.LogInformation("[Teamcraft] Parsed {Count} items from Teamcraft text", items.Count);
        return items;
    }

    /// <summary>
    /// Import from Teamcraft text format and create a new crafting plan.
    /// Requires searching for each item to get its ID.
    /// </summary>
    public async Task<CraftingPlan?> ImportFromTeamcraftTextAsync(
        string name, 
        string precraftText, 
        string finalItemsText,
        string dataCenter,
        string world,
        CancellationToken ct = default)
    {
        var plan = new CraftingPlan
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Imported {DateTime.Now:yyyy-MM-dd HH:mm}" : name,
            DataCenter = dataCenter,
            World = world
        };

        var allItems = new List<(string name, int quantity)>();
        
        // Parse pre-craft items
        if (!string.IsNullOrWhiteSpace(precraftText))
        {
            allItems.AddRange(ParseTeamcraftText(precraftText));
        }
        
        // Parse final items
        if (!string.IsNullOrWhiteSpace(finalItemsText))
        {
            allItems.AddRange(ParseTeamcraftText(finalItemsText));
        }

        if (allItems.Count == 0)
        {
            _logger.LogWarning("[Teamcraft] No items found to import");
            return null;
        }

        // Search for each item to get its ID
        foreach (var (itemName, quantity) in allItems)
        {
            try
            {
                var results = await _garlandService.SearchAsync(itemName, ct);
                var match = results.FirstOrDefault(r => 
                    string.Equals(r.Object?.Name, itemName, StringComparison.OrdinalIgnoreCase));
                
                if (match != null)
                {
                    plan.RootItems.Add(new PlanNode
                    {
                        ItemId = match.Id,
                        Name = match.Object?.Name ?? itemName,
                        IconId = match.Object?.IconId ?? 0,
                        Quantity = quantity,
                        Source = AcquisitionSource.Craft
                    });
                }
                else
                {
                    _logger.LogWarning("[Teamcraft] Could not find item: {ItemName}", itemName);
                    // Add as placeholder
                    plan.RootItems.Add(new PlanNode
                    {
                        ItemId = 0,
                        Name = $"{itemName} (Not Found)",
                        Quantity = quantity,
                        Source = AcquisitionSource.MarketBuyNq,
                        IsUncraftable = true
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Teamcraft] Error searching for item: {ItemName}", itemName);
            }
        }

        if (plan.RootItems.Count == 0)
        {
            return null;
        }

        _logger.LogInformation("[Teamcraft] Imported plan with {Count} items", plan.RootItems.Count);
        return plan;
    }

    /// <summary>
    /// Create a plain text summary of the plan (for clipboard).
    /// Format: "5x Item Name\n3x Other Item"
    /// </summary>
    public string ExportToPlainText(CraftingPlan plan)
    {
        var sb = new StringBuilder();
        
        foreach (var item in plan.RootItems)
        {
            sb.AppendLine($"{item.Quantity}x {item.Name}");
        }
        
        return sb.ToString();
    }

    /// <summary>
    /// Export shopping list to CSV format.
    /// </summary>
    public string ExportToCsv(CraftingPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Item Name,Quantity,Buy/Craft,Estimated Price");
        
        foreach (var material in plan.AggregatedMaterials)
        {
            var source = material.Sources.FirstOrDefault();
            var buyCraft = source?.IsCrafted == true ? "Craft" : "Buy";
            sb.AppendLine($"\"{material.Name}\",{material.TotalQuantity},{buyCraft},{material.TotalCost:N0}");
        }
        
        return sb.ToString();
    }
}
