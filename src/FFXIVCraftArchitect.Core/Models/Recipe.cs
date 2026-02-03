namespace FFXIVCraftArchitect.Core.Models;

/// <summary>
/// Represents a crafting recipe from Garland Tools API.
/// Maps to Python: item['craft'][0] structure
/// </summary>
public class Recipe
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    
    /// <summary>
    /// Crafting class required (e.g., "Alchemist", "Weaver")
    /// </summary>
    public string? Job { get; set; }
    
    /// <summary>
    /// Recipe level
    /// </summary>
    public int Level { get; set; }
    
    /// <summary>
    /// Number of items produced per craft
    /// </summary>
    public int Yield { get; set; } = 1;
    
    /// <summary>
    /// Required ingredients
    /// </summary>
    public List<Ingredient> Ingredients { get; set; } = new();
}

/// <summary>
/// Represents a single ingredient in a recipe.
/// </summary>
public class Ingredient
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Amount { get; set; }
    public int IconId { get; set; }
    
    /// <summary>
    /// Whether this ingredient is crafted (has its own recipe)
    /// </summary>
    public bool IsCrafted { get; set; }
}

/// <summary>
/// Represents a flattened crafting tree node for UI display.
/// This is what gets shown in the TreeView.
/// </summary>
public class RecipeTreeNode
{
    public string Name { get; set; } = string.Empty;
    public int ItemId { get; set; }
    public int NeededQuantity { get; set; }
    public int OwnedQuantity { get; set; }
    public int ToBuyQuantity => System.Math.Max(0, NeededQuantity - OwnedQuantity);
    public long UnitPrice { get; set; }
    public long TotalCost => ToBuyQuantity * UnitPrice;
    public List<RecipeTreeNode> Children { get; set; } = new();
    public bool IsRawMaterial => Children.Count == 0;
}
