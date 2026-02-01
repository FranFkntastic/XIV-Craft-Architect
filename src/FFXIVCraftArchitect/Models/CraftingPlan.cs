using System.Text.Json.Serialization;

namespace FFXIVCraftArchitect.Models;

/// <summary>
/// Represents the root of a crafting plan containing all items to be crafted.
/// Serializable for save/load functionality.
/// </summary>
public class CraftingPlan
{
    /// <summary>
    /// Unique identifier for this plan
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// User-defined name for the plan
    /// </summary>
    public string Name { get; set; } = "New Plan";
    
    /// <summary>
    /// When the plan was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When the plan was last modified
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Data center used for market prices
    /// </summary>
    public string DataCenter { get; set; } = string.Empty;
    
    /// <summary>
    /// World used for market prices (empty = entire DC)
    /// </summary>
    public string World { get; set; } = string.Empty;
    
    /// <summary>
    /// Top-level items in the plan (the "project items")
    /// </summary>
    public List<PlanNode> RootItems { get; set; } = new();
    
    /// <summary>
    /// Flattened list of all materials needed (aggregated)
    /// </summary>
    [JsonIgnore]
    public List<MaterialAggregate> AggregatedMaterials => AggregateMaterials();
    
    /// <summary>
    /// Total estimated cost based on current buy/craft decisions
    /// </summary>
    [JsonIgnore]
    public decimal TotalEstimatedCost => CalculateTotalCost();
    
    /// <summary>
    /// Recursively find a node by item ID
    /// </summary>
    public PlanNode? FindNode(int itemId)
    {
        foreach (var root in RootItems)
        {
            var found = FindNodeRecursive(root, itemId);
            if (found != null) return found;
        }
        return null;
    }
    
    private PlanNode? FindNodeRecursive(PlanNode node, int itemId)
    {
        if (node.ItemId == itemId) return node;
        foreach (var child in node.Children)
        {
            var found = FindNodeRecursive(child, itemId);
            if (found != null) return found;
        }
        return null;
    }
    
    /// <summary>
    /// Aggregate all materials needed across the entire plan
    /// </summary>
    private List<MaterialAggregate> AggregateMaterials()
    {
        var aggregates = new Dictionary<int, MaterialAggregate>();
        
        foreach (var root in RootItems)
        {
            AggregateNode(root, aggregates);
        }
        
        return aggregates.Values.OrderBy(m => m.Name).ToList();
    }
    
    private void AggregateNode(PlanNode node, Dictionary<int, MaterialAggregate> aggregates)
    {
        // Only count materials marked as "buy" or leaf nodes (can't be crafted)
        if (node.IsBuy || !node.Children.Any())
        {
            if (!aggregates.TryGetValue(node.ItemId, out var aggregate))
            {
                aggregate = new MaterialAggregate
                {
                    ItemId = node.ItemId,
                    Name = node.Name,
                    IconId = node.IconId
                };
                aggregates[node.ItemId] = aggregate;
            }
            aggregate.TotalQuantity += node.Quantity;
            aggregate.Sources.Add(new MaterialSource
            {
                ParentItemName = node.Parent?.Name ?? "Direct",
                Quantity = node.Quantity,
                IsCrafted = !node.IsBuy && node.Children.Any()
            });
        }
        
        // Recurse into children (sub-materials that need to be crafted)
        foreach (var child in node.Children)
        {
            AggregateNode(child, aggregates);
        }
    }
    
    private decimal CalculateTotalCost()
    {
        return AggregatedMaterials.Sum(m => m.TotalQuantity * m.UnitPrice);
    }
    
    /// <summary>
    /// Mark the plan as modified and update timestamp
    /// </summary>
    public void MarkModified()
    {
        ModifiedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// A node in the crafting tree representing one item and its sub-ingredients.
/// Can be edited after construction (quantity, buy/craft decision).
/// </summary>
public class PlanNode
{
    /// <summary>
    /// Item ID from Garland/FFXIV
    /// </summary>
    public int ItemId { get; set; }
    
    /// <summary>
    /// Item name
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Icon ID for displaying the item image
    /// </summary>
    public int IconId { get; set; }
    
    /// <summary>
    /// How many of this item are needed
    /// </summary>
    public int Quantity { get; set; } = 1;
    
    /// <summary>
    /// If true, buy this item from market. If false, craft it.
    /// </summary>
    public bool IsBuy { get; set; }
    
    /// <summary>
    /// If true, this item cannot be crafted (gathered, dropped, etc.)
    /// </summary>
    public bool IsUncraftable { get; set; }
    
    /// <summary>
    /// Recipe level required to craft this item
    /// </summary>
    public int RecipeLevel { get; set; }
    
    /// <summary>
    /// Job required to craft (e.g., "Blacksmith", "Weaver")
    /// </summary>
    public string Job { get; set; } = string.Empty;
    
    /// <summary>
    /// How many items the recipe produces per craft
    /// </summary>
    public int Yield { get; set; } = 1;
    
    /// <summary>
    /// Number of crafts needed = Ceiling(Quantity / Yield)
    /// </summary>
    [JsonIgnore]
    public int CraftCount => (int)Math.Ceiling((double)Quantity / Yield);
    
    /// <summary>
    /// Current market price per unit (fetched from Universalis)
    /// </summary>
    public decimal MarketPrice { get; set; }
    
    /// <summary>
    /// Parent node in the tree (null for root items)
    /// </summary>
    [JsonIgnore]
    public PlanNode? Parent { get; set; }
    
    /// <summary>
    /// Child nodes representing ingredients needed to craft this item
    /// </summary>
    public List<PlanNode> Children { get; set; } = new();
    
    /// <summary>
    /// Unique identifier for serialization (to handle parent references)
    /// </summary>
    public string NodeId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    
    /// <summary>
    /// Reference to parent's NodeId for serialization
    /// </summary>
    public string? ParentNodeId { get; set; }
    
    /// <summary>
    /// User notes for this item
    /// </summary>
    public string? Notes { get; set; }
    
    /// <summary>
    /// Deep clone this node and all children
    /// </summary>
    public PlanNode Clone()
    {
        var clone = new PlanNode
        {
            ItemId = ItemId,
            Name = Name,
            IconId = IconId,
            Quantity = Quantity,
            IsBuy = IsBuy,
            IsUncraftable = IsUncraftable,
            RecipeLevel = RecipeLevel,
            Job = Job,
            Yield = Yield,
            MarketPrice = MarketPrice,
            NodeId = NodeId,
            ParentNodeId = ParentNodeId,
            Notes = Notes
        };
        
        foreach (var child in Children)
        {
            var childClone = child.Clone();
            childClone.Parent = clone;
            clone.Children.Add(childClone);
        }
        
        return clone;
    }
    
    /// <summary>
    /// Recalculate quantities when parent quantity changes
    /// </summary>
    public void PropagateQuantityChange(int parentQuantity)
    {
        // Recalculate based on parent's new quantity
        // This is a placeholder - actual logic depends on recipe structure
        Quantity = parentQuantity * (Yield > 0 ? (Quantity / Yield) : Quantity);
        
        foreach (var child in Children)
        {
            child.PropagateQuantityChange(Quantity);
        }
    }
    
    /// <summary>
    /// Toggle between buy and craft, with smart defaults for children
    /// </summary>
    public void SetBuyMode(bool buy)
    {
        IsBuy = buy;
        
        if (buy)
        {
            // If buying this item, its children are no longer needed
            // Keep them for reference but mark as resolved
            foreach (var child in Children)
            {
                child.Quantity = 0;
            }
        }
    }
    
    public override string ToString() => $"{Name} x{Quantity}";
}

/// <summary>
/// Aggregated view of materials needed across the entire plan
/// </summary>
public class MaterialAggregate
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    public int TotalQuantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalCost => TotalQuantity * UnitPrice;
    public List<MaterialSource> Sources { get; set; } = new();
}

/// <summary>
/// Tracks where a material is being used in the plan
/// </summary>
public class MaterialSource
{
    public string ParentItemName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public bool IsCrafted { get; set; }
}

/// <summary>
/// Wrapper for serializing/deserializing plans with proper parent references
/// </summary>
public class SerializablePlanNode
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    public int Quantity { get; set; }
    public bool IsBuy { get; set; }
    public bool IsUncraftable { get; set; }
    public int RecipeLevel { get; set; }
    public string Job { get; set; } = string.Empty;
    public int Yield { get; set; } = 1;
    public decimal MarketPrice { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string? ParentNodeId { get; set; }
    public string? Notes { get; set; }
    public List<string> ChildNodeIds { get; set; } = new();
}
