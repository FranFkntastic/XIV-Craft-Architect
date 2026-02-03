using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFXIVCraftArchitect.Models;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for importing/exporting crafting lists to/from Artisan format.
/// Artisan is a Dalamud plugin for FFXIV crafting automation.
/// </summary>
public class ArtisanService
{
    private readonly ILogger<ArtisanService> _logger;
    private readonly GarlandService _garlandService;
    private readonly RecipeCalculationService _recipeCalcService;
    private readonly HttpClient _httpClient;

    public ArtisanService(
        ILogger<ArtisanService> logger, 
        GarlandService garlandService, 
        RecipeCalculationService recipeCalcService,
        HttpClient httpClient)
    {
        _logger = logger;
        _garlandService = garlandService;
        _recipeCalcService = recipeCalcService;
        _httpClient = httpClient;
    }

    #region Import FROM Artisan

    /// <summary>
    /// Import an Artisan crafting list from JSON.
    /// Artisan exports its lists as JSON which can be pasted into the "Import List From Clipboard" feature.
    /// 
    /// The JSON structure has:
    /// - ID: A random list ID (100-50000), NOT an item ID
    /// - Recipes: Array of recipes to craft (these are RECIPE IDs, each with quantity)
    /// - Name: The name of the crafting list
    /// 
    /// We import each recipe in the Recipes array by converting recipe IDs to item IDs,
    /// then let RecipeCalculationService calculate all subcrafts.
    /// </summary>
    public async Task<CraftingPlan?> ImportFromArtisanAsync(
        string artisanJson, 
        string dataCenter, 
        string world,
        CancellationToken ct = default)
    {
        ArtisanCraftingList? artisanList;
        try
        {
            artisanList = JsonSerializer.Deserialize<ArtisanCraftingList>(artisanJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Artisan] Failed to parse Artisan JSON");
            return null;
        }

        if (artisanList == null)
        {
            _logger.LogWarning("[Artisan] Failed to deserialize Artisan list");
            return null;
        }

        if (artisanList.Recipes == null || artisanList.Recipes.Count == 0)
        {
            _logger.LogWarning("[Artisan] No recipes found in Artisan list");
            return null;
        }

        _logger.LogInformation("[Artisan] Importing list '{ListName}' with {RecipeCount} recipes", 
            artisanList.Name, artisanList.Recipes.Count);

        // Convert each recipe in the Recipes array to an item
        // The Recipes array contains the actual items to craft (with recipe IDs)
        var targetItems = new List<(int itemId, string name, int quantity, bool isHqRequired)>();
        
        foreach (var artisanItem in artisanList.Recipes)
        {
            try
            {
                var targetItem = await ConvertToTargetItemAsync(artisanItem, ct);
                if (targetItem.HasValue)
                {
                    targetItems.Add(targetItem.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Artisan] Error converting Artisan recipe ID {RecipeId}", artisanItem.ID);
            }
        }

        if (targetItems.Count == 0)
        {
            _logger.LogWarning("[Artisan] No valid items could be imported from Artisan list");
            return null;
        }

        // Use RecipeCalculationService to build complete recipe trees with all subcrafts
        var plan = await _recipeCalcService.BuildPlanAsync(
            targetItems, 
            dataCenter, 
            world, 
            ct);

        // Update plan name to indicate it came from Artisan
        plan.Name = string.IsNullOrWhiteSpace(artisanList.Name) 
            ? "Imported from Artisan" 
            : $"{artisanList.Name} (Artisan)";

        _logger.LogInformation("[Artisan] Successfully imported plan '{PlanName}' with {RootCount} root items and full recipe trees", 
            plan.Name, plan.RootItems.Count);

        return plan;
    }

    /// <summary>
    /// Convert an ArtisanListItem to a target item tuple for RecipeCalculationService.
    /// 
    /// For older recipes: The ID is a Recipe ID that needs to be looked up via XIVAPI.
    /// For newer recipes (Dawntrail+): The ID is actually an Item ID because XIVAPI doesn't have them yet.
    /// </summary>
    private async Task<(int itemId, string name, int quantity, bool isHqRequired)?> ConvertToTargetItemAsync(
        ArtisanListItem artisanItem, 
        CancellationToken ct)
    {
        var id = (int)artisanItem.ID;
        
        // First, try to treat the ID as an item ID directly (for newer content)
        // This handles cases where Artisan exports item IDs instead of recipe IDs
        var itemData = await _garlandService.GetItemAsync(id, ct);
        
        if (itemData != null && itemData.Crafts?.Any() == true)
        {
            // This is an item ID with a craft recipe - use it directly
            var craft = itemData.Crafts.OrderBy(c => c.RecipeLevel).First();
            var yield = Math.Max(1, craft.Yield);
            var totalQuantity = artisanItem.Quantity * yield;
            var isHqRequired = !artisanItem.ListItemOptions.NQOnly;

            _logger.LogDebug("[Artisan] Direct item lookup: ID {Id} -> {ItemName} x{Quantity} (HQ: {Hq})", 
                id, itemData.Name, totalQuantity, isHqRequired);

            return (itemData.Id, itemData.Name, totalQuantity, isHqRequired);
        }
        
        // If direct lookup failed or item has no crafts, try recipe lookup (older content)
        _logger.LogDebug("[Artisan] Direct item lookup failed for ID {Id}, trying recipe lookup", id);
        
        itemData = await TryFindItemByRecipeIdAsync(id, ct);
        
        if (itemData == null)
        {
            _logger.LogWarning("[Artisan] Could not find item for ID {Id} (tried as both item and recipe)", id);
            return null;
        }

        // Find the specific craft that matches our recipe ID to get yield
        var craftByRecipe = itemData.Crafts?.FirstOrDefault(c => c.Id == id);
        if (craftByRecipe == null)
        {
            _logger.LogWarning("[Artisan] Recipe ID {RecipeId} not found in item {ItemName}", 
                id, itemData.Name);
            return null;
        }

        // Calculate total item quantity based on recipe yield and craft count
        var yieldByRecipe = Math.Max(1, craftByRecipe.Yield);
        var totalQuantityByRecipe = artisanItem.Quantity * yieldByRecipe;

        // HQ requirement from Artisan options
        var isHqRequiredByRecipe = !artisanItem.ListItemOptions.NQOnly;

        _logger.LogDebug("[Artisan] Mapped recipe {RecipeId} -> {ItemName} (ID: {ItemId}) x{Quantity} (HQ: {Hq})", 
            id, itemData.Name, itemData.Id, totalQuantityByRecipe, isHqRequiredByRecipe);

        return (itemData.Id, itemData.Name, totalQuantityByRecipe, isHqRequiredByRecipe);
    }

    /// <summary>
    /// Try to find an item by its recipe ID using XIVAPI.
    /// XIVAPI provides recipe lookups which give us the resulting Item ID.
    /// </summary>
    private async Task<GarlandItem?> TryFindItemByRecipeIdAsync(int recipeId, CancellationToken ct)
    {
        // Approach 1: Use XIVAPI to look up the recipe
        try
        {
            _logger.LogDebug("[Artisan] Looking up recipe ID {RecipeId} via XIVAPI", recipeId);
            var recipeInfo = await GetXivApiRecipeAsync(recipeId, ct);
            if (recipeInfo?.ItemResultId > 0)
            {
                var item = await _garlandService.GetItemAsync(recipeInfo.ItemResultId, ct);
                if (item != null)
                {
                    _logger.LogDebug("[Artisan] Mapped recipe ID {RecipeId} to item ID {ItemId} ({ItemName}) via XIVAPI",
                        recipeId, item.Id, item.Name);
                    return item;
                }
            }
            else
            {
                _logger.LogWarning("[Artisan] XIVAPI returned no ItemResult for recipe ID {RecipeId}", recipeId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Artisan] XIVAPI recipe lookup failed for recipe ID {RecipeId}", recipeId);
        }

        // Approach 2: Try searching Garland by recipe ID directly
        // This works for many recipes even without XIVAPI
        try
        {
            _logger.LogDebug("[Artisan] Trying Garland direct lookup for recipe ID {RecipeId}", recipeId);
            var item = await TryFindItemByRecipeInGarlandAsync(recipeId, ct);
            if (item != null)
            {
                _logger.LogDebug("[Artisan] Mapped recipe ID {RecipeId} to item ID {ItemId} ({ItemName}) via Garland",
                    recipeId, item.Id, item.Name);
                return item;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Artisan] Garland recipe lookup failed for recipe ID {RecipeId}", recipeId);
        }

        // Approach 3: Try the recipe ID as an item ID (fallback for some old recipes)
        try
        {
            _logger.LogDebug("[Artisan] Trying recipe ID {RecipeId} as item ID", recipeId);
            var item = await _garlandService.GetItemAsync(recipeId, ct);
            if (item?.Crafts?.Any(c => c.Id == recipeId) == true)
            {
                _logger.LogDebug("[Artisan] Found item for recipe ID {RecipeId} using recipe ID as item ID", recipeId);
                return item;
            }
        }
        catch { /* Ignore and continue */ }
        
        _logger.LogWarning("[Artisan] Could not find item for recipe ID {RecipeId}. " +
            "The item may not be available in Garland Tools or XIVAPI.", recipeId);
        
        return null;
    }

    /// <summary>
    /// Try to find an item by searching for a craft with the given recipe ID.
    /// This searches common recipe ID ranges and uses Garland's search.
    /// </summary>
    private async Task<GarlandItem?> TryFindItemByRecipeInGarlandAsync(int recipeId, CancellationToken ct)
    {
        // Recipe IDs are typically in specific ranges by job/craft type
        // We can try to estimate the item ID from the recipe ID
        // This is a heuristic approach but works for many cases
        
        // First, try direct item lookup with offsets
        // Many recipes have item IDs that are close to their recipe IDs
        // Offset 11415: Patch 7.4+ items (Courtly Lover's gear, etc.)
        // Offsets 100000/200000: Common legacy offsets for older content
        var offsets = new[] { 0, 11415, -100000, -200000, 100000, 200000 };
        
        foreach (var offset in offsets)
        {
            var estimatedItemId = recipeId + offset;
            if (estimatedItemId <= 0) continue;
            
            try
            {
                var item = await _garlandService.GetItemAsync(estimatedItemId, ct);
                if (item?.Crafts?.Any(c => c.Id == recipeId) == true)
                {
                    return item;
                }
            }
            catch { /* Continue to next offset */ }
        }
        
        return null;
    }

    /// <summary>
    /// Look up a recipe by ID using XIVAPI.
    /// Returns the resulting item ID.
    /// </summary>
    private async Task<XivApiRecipe?> GetXivApiRecipeAsync(int recipeId, CancellationToken ct)
    {
        try
        {
            // Use the beta xivapi.com endpoint (v2 API)
            var url = $"https://xivapi.com/recipe/{recipeId}";
            _logger.LogDebug("[Artisan] Querying XIVAPI: {Url}", url);
            
            var response = await _httpClient.GetAsync(url, ct);
            
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("[Artisan] XIVAPI rate limit hit for recipe {RecipeId}", recipeId);
                return null;
            }
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("[Artisan] XIVAPI returned {StatusCode} for recipe {RecipeId}", 
                    response.StatusCode, recipeId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("[Artisan] XIVAPI response for recipe {RecipeId}: {Json}", recipeId, json[..Math.Min(200, json.Length)]);
            
            var recipe = JsonSerializer.Deserialize<XivApiRecipe>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (recipe == null)
            {
                _logger.LogWarning("[Artisan] Failed to deserialize XIVAPI response for recipe {RecipeId}", recipeId);
            }
            else if (recipe.ItemResultId == 0)
            {
                _logger.LogWarning("[Artisan] XIVAPI response for recipe {RecipeId} has no ItemResult (ID: {ItemResultId}, Name: {ItemResultName})", 
                    recipeId, recipe.ItemResult?.Id ?? 0, recipe.ItemResult?.Name ?? "null");
            }

            return recipe;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Artisan] Error looking up recipe {RecipeId} from XIVAPI", recipeId);
            return null;
        }
    }

    /// <summary>
    /// XIVAPI recipe response model (minimal)
    /// Uses PascalCase properties - JsonSerializer will handle camelCase with PropertyNameCaseInsensitive
    /// </summary>
    private class XivApiRecipe
    {
        [JsonPropertyName("ID")]
        public int Id { get; set; }

        [JsonPropertyName("ItemResult")]
        public XivApiItemResult? ItemResult { get; set; }

        [JsonPropertyName("AmountResult")]
        public int AmountResult { get; set; }

        public int ItemResultId => ItemResult?.Id ?? 0;
    }

    private class XivApiItemResult
    {
        [JsonPropertyName("ID")]
        public int Id { get; set; }

        [JsonPropertyName("Name")]
        public string? Name { get; set; }
    }

    private static string GetJobName(int jobId)
    {
        return jobId switch
        {
            1 => "Carpenter",
            2 => "Blacksmith",
            3 => "Armorer",
            4 => "Goldsmith",
            5 => "Leatherworker",
            6 => "Weaver",
            7 => "Alchemist",
            8 => "Culinarian",
            _ => "Unknown"
        };
    }

    #endregion

    #region Export TO Artisan

    /// <summary>
    /// Export a crafting plan to Artisan JSON format.
    /// Returns JSON that can be pasted into Artisan's "Import List From Clipboard" feature.
    /// </summary>
    public async Task<ArtisanExportResult> ExportToArtisanAsync(CraftingPlan plan, CancellationToken ct = default)
    {
        var result = new ArtisanExportResult();
        var artisanList = new ArtisanCraftingList
        {
            ID = GenerateArtisanListId(),
            Name = string.IsNullOrWhiteSpace(plan.Name) ? "Exported Plan" : plan.Name,
            Recipes = new List<ArtisanListItem>()
        };

        // Process each root item in the plan
        foreach (var rootItem in plan.RootItems)
        {
            try
            {
                var artisanItem = await ConvertToArtisanItemAsync(rootItem, ct);
                if (artisanItem != null)
                {
                    artisanList.Recipes.Add(artisanItem);
                }
                else
                {
                    result.MissingRecipes.Add(rootItem.Name);
                    _logger.LogWarning("[Artisan] Could not find recipe for item: {ItemName} (ID: {ItemId})", 
                        rootItem.Name, rootItem.ItemId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Artisan] Error converting item {ItemName} to Artisan format", rootItem.Name);
                result.MissingRecipes.Add(rootItem.Name);
            }
        }

        // Build the expanded list (required by Artisan)
        // Each recipe ID appears 'Quantity' times in the list
        foreach (var recipe in artisanList.Recipes)
        {
            for (int i = 0; i < recipe.Quantity; i++)
            {
                artisanList.ExpandedList.Add(recipe.ID);
            }
        }

        // Serialize to JSON
        result.Json = JsonSerializer.Serialize(artisanList, new JsonSerializerOptions
        {
            WriteIndented = false // Compact format for clipboard
        });

        result.RecipeCount = artisanList.Recipes.Count;

        _logger.LogInformation("[Artisan] Exported plan '{PlanName}' with {RecipeCount} recipes to Artisan format", 
            plan.Name, result.RecipeCount);

        return result;
    }

    /// <summary>
    /// Export a crafting plan to Artisan JSON format (synchronous version for simple cases).
    /// Note: This will not be able to look up recipes for items that only have ItemId without RecipeId.
    /// </summary>
    public ArtisanExportResult ExportToArtisanSimple(CraftingPlan plan)
    {
        var result = new ArtisanExportResult();
        var artisanList = new ArtisanCraftingList
        {
            ID = GenerateArtisanListId(),
            Name = string.IsNullOrWhiteSpace(plan.Name) ? "Exported Plan" : plan.Name,
            Recipes = new List<ArtisanListItem>()
        };

        // Process each root item in the plan
        // This simplified version assumes we can't look up recipes without async calls
        // In practice, use ExportToArtisanAsync instead
        foreach (var rootItem in plan.RootItems)
        {
            // We can't do async lookup here, so we'll add items without recipe IDs
            // This is a limitation - the export won't work properly
            result.MissingRecipes.Add(rootItem.Name);
        }

        result.Json = JsonSerializer.Serialize(artisanList);
        return result;
    }

    /// <summary>
    /// Convert a PlanNode to an ArtisanListItem.
    /// Looks up the recipe ID from the item ID.
    /// </summary>
    private async Task<ArtisanListItem?> ConvertToArtisanItemAsync(PlanNode node, CancellationToken ct)
    {
        // Get item data from Garland to find the recipe ID
        var itemData = await _garlandService.GetItemAsync(node.ItemId, ct);
        
        if (itemData?.Crafts?.Any() != true)
        {
            // No crafting recipe available for this item
            return null;
        }

        // Get the first (usually lowest level) recipe
        // Artisan expects Recipe ID, not Item ID
        var recipe = itemData.Crafts.OrderBy(r => r.RecipeLevel).First();
        
        // Calculate quantity based on recipe yield
        // Artisan needs the number of recipe executions, not the total item count
        var yield = Math.Max(1, recipe.Yield);
        var recipeCount = (int)Math.Ceiling((double)node.Quantity / yield);

        var artisanItem = new ArtisanListItem
        {
            ID = (uint)recipe.Id,
            Quantity = recipeCount,
            ListItemOptions = new ArtisanListItemOptions
            {
                // NQOnly = true for items marked as Quick Synth or non-HQ
                NQOnly = false, // Default to allowing HQ
                Skipping = false
            }
        };

        _logger.LogDebug("[Artisan] Mapped item {ItemName} (ID: {ItemId}) to recipe ID {RecipeId} with quantity {Quantity}",
            node.Name, node.ItemId, recipe.Id, recipeCount);

        return artisanItem;
    }

    /// <summary>
    /// Generate a random ID for the Artisan list.
    /// Artisan uses IDs between 100 and 50000.
    /// </summary>
    private int GenerateArtisanListId()
    {
        var random = new Random();
        return random.Next(100, 50000);
    }

    /// <summary>
    /// Create a summary text of the export result for display to the user.
    /// </summary>
    public string CreateExportSummary(ArtisanExportResult result)
    {
        if (result.Success)
        {
            return $"Exported {result.RecipeCount} recipes to Artisan format.";
        }
        else
        {
            var missingText = string.Join(", ", result.MissingRecipes.Take(3));
            if (result.MissingRecipes.Count > 3)
            {
                missingText += $" and {result.MissingRecipes.Count - 3} more";
            }
            return $"Exported {result.RecipeCount} recipes. Warning: Could not find recipes for: {missingText}";
        }
    }

    #endregion
}
