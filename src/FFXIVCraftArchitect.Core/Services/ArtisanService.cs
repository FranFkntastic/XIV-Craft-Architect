using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Core.Services;

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

        var subcraftItemIds = new HashSet<int>();
        foreach (var recipeInfo in recipeInfos)
        {
            foreach (var ingredientId in recipeInfo.IngredientItemIds)
            {
                subcraftItemIds.Add(ingredientId);
            }
        }

        var rootRecipes = recipeInfos
            .Where(r => !subcraftItemIds.Contains(r.ResultItemId))
            .ToList();

        _logger.LogInformation("[Artisan] Identified {RootCount} root recipes out of {TotalCount} total recipes", 
            rootRecipes.Count, recipeInfos.Count);

        if (rootRecipes.Count == 0)
        {
            _logger.LogWarning("[Artisan] No root recipes identified, falling back to importing all recipes");
            rootRecipes = recipeInfos;
        }

        var targetItems = rootRecipes
            .Select(r => (r.ResultItemId, r.ResultItemName, r.Quantity, r.IsHqRequired))
            .ToList();

        var plan = await _recipeCalcService.BuildPlanAsync(
            targetItems, 
            dataCenter, 
            world, 
            ct);

        plan.Name = string.IsNullOrWhiteSpace(artisanList.Name) 
            ? "Imported from Artisan" 
            : $"{artisanList.Name} (Artisan)";

        _logger.LogInformation("[Artisan] Successfully imported plan '{PlanName}' with {RootCount} root items", 
            plan.Name, plan.RootItems.Count);

        return plan;
    }

    private class ArtisanRecipeInfo
    {
        public uint RecipeId { get; set; }
        public int ResultItemId { get; set; }
        public string ResultItemName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public bool IsHqRequired { get; set; }
        public List<int> IngredientItemIds { get; set; } = new();
    }

    private async Task<ArtisanRecipeInfo?> GetRecipeInfoAsync(ArtisanListItem artisanItem, CancellationToken ct)
    {
        var id = (int)artisanItem.ID;
        
        var itemData = await TryFindItemByRecipeIdAsync(id, ct);
        
        if (itemData == null)
        {
            itemData = await _garlandService.GetItemAsync(id, ct);
        }
        
        if (itemData == null)
        {
            _logger.LogWarning("[Artisan] Could not find item for recipe ID {RecipeId}", id);
            return null;
        }

        var craft = itemData.Crafts?.FirstOrDefault(c => c.Id == id.ToString());
        if (craft == null && itemData.Crafts?.Any() == true)
        {
            craft = itemData.Crafts.OrderBy(c => c.RecipeLevel).First();
        }

        if (craft == null)
        {
            _logger.LogWarning("[Artisan] No craft found for item {ItemName} (ID: {ItemId})", 
                itemData.Name, itemData.Id);
            return null;
        }

        var yield = Math.Max(1, craft.Yield);
        var totalQuantity = artisanItem.Quantity * yield;

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

    private async Task<GarlandItem?> TryFindItemByRecipeIdAsync(int recipeId, CancellationToken ct)
    {
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
        
        _logger.LogWarning("[Artisan] Could not find item for recipe ID {RecipeId}", recipeId);
        
        return null;
    }

    private async Task<XivApiRecipe?> GetXivApiRecipeAsync(int recipeId, CancellationToken ct)
    {
        try
        {
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
            
            var recipe = JsonSerializer.Deserialize<XivApiRecipe>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            return recipe;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Artisan] Error looking up recipe {RecipeId} from XIVAPI", recipeId);
            return null;
        }
    }

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

    #endregion

    #region Export TO Artisan

    public async Task<ArtisanExportResult> ExportToArtisanAsync(CraftingPlan plan, CancellationToken ct = default)
    {
        var result = new ArtisanExportResult();
        var artisanList = new ArtisanCraftingList
        {
            ID = GenerateArtisanListId(),
            Name = string.IsNullOrWhiteSpace(plan.Name) ? "Exported Plan" : plan.Name,
            Recipes = new List<ArtisanListItem>()
        };

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

        foreach (var recipe in artisanList.Recipes)
        {
            for (int i = 0; i < recipe.Quantity; i++)
            {
                artisanList.ExpandedList.Add(recipe.ID);
            }
        }

        result.Json = JsonSerializer.Serialize(artisanList, new JsonSerializerOptions
        {
            WriteIndented = false
        });

        result.RecipeCount = artisanList.Recipes.Count;

        _logger.LogInformation("[Artisan] Exported plan '{PlanName}' with {RecipeCount} recipes to Artisan format", 
            plan.Name, result.RecipeCount);

        return result;
    }

    private async Task<ArtisanListItem?> ConvertToArtisanItemAsync(PlanNode node, CancellationToken ct)
    {
        var itemData = await _garlandService.GetItemAsync(node.ItemId, ct);
        
        if (itemData?.Crafts?.Any() != true)
        {
            return null;
        }

        var recipe = itemData.Crafts.OrderBy(r => r.RecipeLevel).First();
        
        var yield = Math.Max(1, recipe.Yield);
        var recipeCount = (int)Math.Ceiling((double)node.Quantity / yield);

        var artisanItem = new ArtisanListItem
        {
            ID = uint.TryParse(recipe.Id, out var recipeId) ? recipeId : 0,
            Quantity = recipeCount,
            ListItemOptions = new ArtisanListItemOptions
            {
                NQOnly = false,
                Skipping = false
            }
        };

        _logger.LogDebug("[Artisan] Mapped item {ItemName} (ID: {ItemId}) to recipe ID {RecipeId}",
            node.Name, node.ItemId, recipe.Id);

        return artisanItem;
    }

    private int GenerateArtisanListId()
    {
        var random = new Random();
        return random.Next(100, 50000);
    }

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
