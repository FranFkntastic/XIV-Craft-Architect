using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Core.Services;
using FFXIVCraftArchitect.Models;
using FFXIVCraftArchitect.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for importing/exporting crafting lists to/from Artisan format.
/// Artisan is a Dalamud plugin for FFXIV crafting automation.
/// </summary>
public class ArtisanService : IArtisanService
{
    private readonly ILogger<ArtisanService> _logger;
    private readonly GarlandService _garlandService;
    private readonly RecipeCalculationService _recipeCalcService;
    private readonly ITeamcraftRecipeService _teamcraftService;
    private readonly HttpClient _httpClient;

    public ArtisanService(
        ILogger<ArtisanService> logger, 
        GarlandService garlandService, 
        RecipeCalculationService recipeCalcService,
        ITeamcraftRecipeService teamcraftService,
        HttpClient httpClient)
    {
        _logger = logger;
        _garlandService = garlandService;
        _recipeCalcService = recipeCalcService;
        _teamcraftService = teamcraftService;
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
    /// IMPORTANT: Artisan's Recipes array includes subcrafts. We only import the ROOT recipes
    /// (recipes whose resulting items are not ingredients of other recipes in the list).
    /// Subcrafts are calculated automatically by RecipeCalculationService.
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

        // First, collect all recipe info and their ingredients
        var recipeInfos = new List<ArtisanRecipeInfo>();
        var recipeIdToItemId = new Dictionary<uint, int>();
        
        foreach (var artisanItem in artisanList.Recipes)
        {
            try
            {
                var recipeInfo = await GetRecipeInfoAsync(artisanItem, ct);
                if (recipeInfo != null)
                {
                    recipeInfos.Add(recipeInfo);
                    recipeIdToItemId[artisanItem.ID] = recipeInfo.ResultItemId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Artisan] Error getting recipe info for ID {RecipeId}", artisanItem.ID);
            }
        }

        if (recipeInfos.Count == 0)
        {
            _logger.LogWarning("[Artisan] No valid recipes could be processed from Artisan list");
            return null;
        }

        // Build a set of all item IDs that are ingredients (subcrafts)
        var subcraftItemIds = new HashSet<int>();
        foreach (var recipeInfo in recipeInfos)
        {
            foreach (var ingredientId in recipeInfo.IngredientItemIds)
            {
                subcraftItemIds.Add(ingredientId);
            }
        }

        // Root recipes are those whose result items are NOT ingredients of other recipes
        var rootRecipes = recipeInfos
            .Where(r => !subcraftItemIds.Contains(r.ResultItemId))
            .ToList();

        _logger.LogInformation("[Artisan] Identified {RootCount} root recipes out of {TotalCount} total recipes", 
            rootRecipes.Count, recipeInfos.Count);

        if (rootRecipes.Count == 0)
        {
            // Fallback: if no roots found (circular dependency?), use all recipes
            _logger.LogWarning("[Artisan] No root recipes identified, falling back to importing all recipes");
            rootRecipes = recipeInfos;
        }

        // Convert root recipes to target items
        var targetItems = rootRecipes
            .Select(r => (r.ResultItemId, r.ResultItemName, r.Quantity, r.IsHqRequired))
            .ToList();

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
    /// Internal class to hold recipe information for dependency analysis.
    /// </summary>
    private class ArtisanRecipeInfo
    {
        public uint RecipeId { get; set; }
        public int ResultItemId { get; set; }
        public string ResultItemName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public bool IsHqRequired { get; set; }
        public List<int> IngredientItemIds { get; set; } = new();
    }

    /// <summary>
    /// Get recipe information including ingredients for dependency analysis.
    /// </summary>
    private async Task<ArtisanRecipeInfo?> GetRecipeInfoAsync(ArtisanListItem artisanItem, CancellationToken ct)
    {
        var id = (int)artisanItem.ID;
        
        // Try to find the item for this recipe
        var itemData = await TryFindItemByRecipeIdAsync(id, ct);
        
        if (itemData == null)
        {
            // Try direct item lookup as fallback
            itemData = await _garlandService.GetItemAsync(id, ct);
        }
        
        if (itemData == null)
        {
            _logger.LogWarning("[Artisan] Could not find item for recipe ID {RecipeId}", id);
            return null;
        }

        // Find the specific craft
        var craft = itemData.Crafts?.FirstOrDefault(c => c.Id == id);
        if (craft == null && itemData.Crafts?.Any() == true)
        {
            // If no exact match, use first craft
            craft = itemData.Crafts.OrderBy(c => c.RecipeLevel).First();
        }

        if (craft == null)
        {
            _logger.LogWarning("[Artisan] No craft found for item {ItemName} (ID: {ItemId})", 
                itemData.Name, itemData.Id);
            return null;
        }

        // Calculate quantity based on recipe yield
        var yield = Math.Max(1, craft.Yield);
        var totalQuantity = artisanItem.Quantity * yield;

        // Get ingredient item IDs
        var ingredientIds = craft.Ingredients
            .Select(i => i.Id)
            .ToList();

        return new ArtisanRecipeInfo
        {
            RecipeId = artisanItem.ID,
            ResultItemId = itemData.Id,
            ResultItemName = itemData.Name,
            Quantity = totalQuantity,
            IsHqRequired = !artisanItem.ListItemOptions.NQOnly,
            IngredientItemIds = ingredientIds
        };
    }

    /// <summary>
    /// Try to find an item by its recipe ID.
    /// Uses multiple strategies in order of reliability:
    /// 1. Teamcraft CDN (authoritative, covers all recipes including Dawntrail)
    /// 2. XIVAPI recipe lookup (fallback)
    /// </summary>
    private async Task<GarlandItem?> TryFindItemByRecipeIdAsync(int recipeId, CancellationToken ct)
    {
        // Strategy 1: Use Teamcraft CDN (primary source - has all recipes including Dawntrail)
        try
        {
            _logger.LogDebug("[Artisan] Looking up recipe ID {RecipeId} via Teamcraft CDN", recipeId);
            var itemId = await _teamcraftService.GetItemIdForRecipeAsync(recipeId, ct);
            if (itemId.HasValue)
            {
                var item = await _garlandService.GetItemAsync(itemId.Value, ct);
                if (item != null)
                {
                    _logger.LogInformation("[Artisan] Mapped recipe ID {RecipeId} to item ID {ItemId} ({ItemName}) via Teamcraft CDN",
                        recipeId, item.Id, item.Name);
                    return item;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Artisan] Teamcraft CDN lookup failed for recipe ID {RecipeId}", recipeId);
        }

        // Strategy 2: Use XIVAPI as fallback
        try
        {
            _logger.LogDebug("[Artisan] Falling back to XIVAPI for recipe ID {RecipeId}", recipeId);
            var recipeInfo = await GetXivApiRecipeAsync(recipeId, ct);
            if (recipeInfo?.ItemResultId > 0)
            {
                var item = await _garlandService.GetItemAsync(recipeInfo.ItemResultId, ct);
                if (item != null)
                {
                    _logger.LogInformation("[Artisan] Mapped recipe ID {RecipeId} to item ID {ItemId} ({ItemName}) via XIVAPI",
                        recipeId, item.Id, item.Name);
                    return item;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Artisan] XIVAPI recipe lookup failed for recipe ID {RecipeId}", recipeId);
        }
        
        _logger.LogWarning("[Artisan] Could not find item for recipe ID {RecipeId}. " +
            "The recipe may not be available in Teamcraft CDN or XIVAPI.", recipeId);
        
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
