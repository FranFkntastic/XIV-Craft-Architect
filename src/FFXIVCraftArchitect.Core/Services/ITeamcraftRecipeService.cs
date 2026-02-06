using FFXIVCraftArchitect.Core.Models.Teamcraft;

namespace FFXIVCraftArchitect.Core.Services;

/// <summary>
/// Service for accessing Teamcraft's recipe data from their CDN.
/// Provides authoritative Recipe ID → Item ID mappings.
/// </summary>
public interface ITeamcraftRecipeService
{
    /// <summary>
    /// Gets all recipes from the CDN.
    /// Results are cached for the lifetime of the service.
    /// </summary>
    Task<IReadOnlyList<TeamcraftRecipe>> GetAllRecipesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a single recipe by its Recipe ID.
    /// Returns null if not found.
    /// </summary>
    Task<TeamcraftRecipe?> GetRecipeByIdAsync(int recipeId, CancellationToken ct = default);

    /// <summary>
    /// Gets the Item ID for a given Recipe ID.
    /// Returns null if the recipe is not found.
    /// </summary>
    Task<int?> GetItemIdForRecipeAsync(int recipeId, CancellationToken ct = default);

    /// <summary>
    /// Gets all recipes that produce a specific item.
    /// </summary>
    Task<IReadOnlyList<TeamcraftRecipe>> GetRecipesForItemAsync(int itemId, CancellationToken ct = default);

    /// <summary>
    /// Gets the underlying Recipe ID → Item ID mapping dictionary.
    /// This is the primary use case for Artisan imports.
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> GetRecipeToItemMapAsync(CancellationToken ct = default);

    /// <summary>
    /// Clears the in-memory cache, forcing a fresh fetch on next access.
    /// </summary>
    void ClearCache();
}
