using System.Net.Http.Json;
using FFXIV_Craft_Architect.Core.Models.Teamcraft;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Implementation of ITeamcraftRecipeService that fetches from Teamcraft's CDN.
/// </summary>
public class TeamcraftRecipeService : ITeamcraftRecipeService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TeamcraftRecipeService> _logger;

    // CDN URL for Teamcraft's recipe data
    // The hash is from their lazy-files-list.ts and changes with each game patch
    private const string RecipesUrl = "https://cdn.ffxivteamcraft.com/assets/data/recipes.04a176096340800905cfddd5a3966b03d61dd58d.json";

    // Cached data
    private List<TeamcraftRecipe>? _cachedRecipes;
    private Dictionary<int, TeamcraftRecipe>? _recipeById;
    private Dictionary<int, List<TeamcraftRecipe>>? _recipesByItemId;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public TeamcraftRecipeService(HttpClient httpClient, ILogger<TeamcraftRecipeService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TeamcraftRecipe>> GetAllRecipesAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _cachedRecipes!;
    }

    /// <inheritdoc />
    public async Task<TeamcraftRecipe?> GetRecipeByIdAsync(int recipeId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        _recipeById!.TryGetValue(recipeId, out var recipe);
        return recipe;
    }

    /// <inheritdoc />
    public async Task<int?> GetItemIdForRecipeAsync(int recipeId, CancellationToken ct = default)
    {
        var recipe = await GetRecipeByIdAsync(recipeId, ct);
        return recipe?.Result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TeamcraftRecipe>> GetRecipesForItemAsync(int itemId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        if (_recipesByItemId!.TryGetValue(itemId, out var recipes))
            return recipes;
        return Array.Empty<TeamcraftRecipe>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> GetRecipeToItemMapAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _recipeById!.ToDictionary(r => r.Key, r => r.Value.Result);
    }

    /// <inheritdoc />
    public void ClearCache()
    {
        _loadLock.Wait();
        try
        {
            _cachedRecipes = null;
            _recipeById = null;
            _recipesByItemId = null;
            _logger.LogInformation("Teamcraft recipe cache cleared");
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        // Fast path: already loaded
        if (_cachedRecipes != null)
            return;

        await _loadLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedRecipes != null)
                return;

            _logger.LogInformation("Loading Teamcraft recipe data from CDN...");

            var recipes = await _httpClient.GetFromJsonAsync<List<TeamcraftRecipe>>(RecipesUrl, ct);

            if (recipes == null || recipes.Count == 0)
            {
                throw new InvalidOperationException("Failed to load recipes from Teamcraft CDN");
            }

            // Filter out FC recipes (string IDs like "fc1" become 0 after conversion)
            var validRecipes = recipes.Where(r => r.Id > 0).ToList();
            
            _cachedRecipes = validRecipes;
            _recipeById = validRecipes.ToDictionary(r => r.Id);
            _recipesByItemId = validRecipes
                .GroupBy(r => r.Result)
                .ToDictionary(g => g.Key, g => g.ToList());

            _logger.LogInformation(
                "Loaded {Count} recipes from Teamcraft CDN (filtered {Filtered} FC recipes). Recipe ID range: {Min}-{Max}",
                validRecipes.Count,
                recipes.Count - validRecipes.Count,
                validRecipes.Min(r => r.Id),
                validRecipes.Max(r => r.Id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Teamcraft recipe data");
            throw;
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
