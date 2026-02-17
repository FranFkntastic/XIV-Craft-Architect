using System.Text.Json.Serialization;

namespace FFXIV_Craft_Architect.Core.Models;

/// <summary>
/// Price source type - where the price came from
/// </summary>
public enum PriceSource
{
    Unknown,
    Vendor,
    Market,
    Untradeable
}

/// <summary>
/// Source for acquiring an item in a crafting plan.
/// </summary>
public enum AcquisitionSource
{
    /// <summary>Craft the item using components</summary>
    Craft,
    /// <summary>Buy NQ from market board</summary>
    MarketBuyNq,
    /// <summary>Buy HQ from market board</summary>
    MarketBuyHq,
    /// <summary>
    /// Buy from NPC vendor using gil (standard currency).
    /// 
    /// BEHAVIOR:
    /// - Item treated as atomic purchase (children not aggregated)
    /// - Price is fixed from vendor data (no market lookup)
    /// - Unlimited stock assumed
    /// - Displayed in "Vendor" procurement group
    /// 
    /// UI:
    /// - Dropdown shows vendor location(s)
    /// - Gold background in procurement plan
    /// - Shop icon distinguishes from market
    /// 
    /// PRIORITIZATION:
    /// VendorBuy is prioritized in smart defaults because vendors offer:
    /// - Fixed prices (no market fluctuation)
    /// - Often cheaper than market for basic materials
    /// - Convenient locations (main cities)
    /// 
    /// NOTE: Only applies to GIL vendors. Special currency vendors use VendorSpecialCurrency.
    /// </summary>
    VendorBuy,
    /// <summary>
    /// Buy from NPC vendor using special currency (tomestones, beast tribe tokens, etc.).
    /// 
    /// DIFFERENCE FROM VendorBuy:
    /// - Uses non-gil currency (Allagan Tomestones, Ixali Oaknots, etc.)
    /// - Not used in cost calculations (currency value is subjective)
    /// - Tracked in VendorOptions for display purposes
    /// - User can still select, but no price comparison made
    /// 
    /// UI:
    /// - Shown in vendor dropdown with currency icon
    /// - Price shown in special currency units
    /// - Not included in procurement plan cost totals
    /// 
    /// USE CASE:
    /// Some items can only be obtained via special currency (e.g., certain housing items).
    /// This option allows users to track these items even though no gil cost is calculated.
    /// </summary>
    VendorSpecialCurrency
}

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
    /// Saved market shopping plans with recommended worlds and listings.
    /// Populated when prices are fetched and saved with the plan.
    /// </summary>
    [JsonIgnore]
    public List<DetailedShoppingPlan> SavedMarketPlans { get; set; } = new();
    
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
    /// Version counter incremented when market prices are updated.
    /// Used to trigger UI re-renders in RecipeNodeView components.
    /// </summary>
    [JsonIgnore]
    public int PriceVersion { get; set; }
    
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
    /// Get all unique item IDs in the plan (including all children)
    /// </summary>
    public List<int> GetAllItemIds()
    {
        var ids = new HashSet<int>();
        foreach (var root in RootItems)
        {
            CollectItemIdsRecursive(root, ids);
        }
        return ids.ToList();
    }
    
    private void CollectItemIdsRecursive(PlanNode node, HashSet<int> ids)
    {
        ids.Add(node.ItemId);
        foreach (var child in node.Children)
        {
            CollectItemIdsRecursive(child, ids);
        }
    }
    
    /// <summary>
    /// Aggregates all materials needed across the entire plan into a flat shopping list.
    /// 
    /// ALGORITHM:
    /// 1. Traverse the recipe tree depth-first starting from each root item
    /// 2. For each node, check its AcquisitionSource to determine behavior:
    ///    - MarketBuyNq/MarketBuyHq: Add item to aggregation, STOP recursion (don't include children)
    ///    - VendorBuy: Add item to aggregation, STOP recursion
    ///    - Craft: Continue recursing into children
    /// 3. Leaf nodes (no children) are always added to aggregation
    /// 4. Quantities are summed by ItemId to handle multiple occurrences of the same material
    /// 
    /// INTENDED USE:
    /// This produces the final shopping list displayed to users. Items marked as "buy" 
    /// or "vendor" are treated as atomic purchases - their sub-ingredients are ignored.
    /// Items marked as "craft" contribute their sub-ingredients to the list.
    /// 
    /// EXAMPLE:
    /// If Cedar Lumber is marked as "Craft", its children (Lumber, Sand, etc.) are included.
    /// If Cedar Lumber is marked as "Buy", only Cedar Lumber appears in the list.
    /// </summary>
    /// <returns>Flat list of materials to acquire, aggregated by ItemId</returns>
    private List<MaterialAggregate> AggregateMaterials()
    {
        var aggregates = new Dictionary<int, MaterialAggregate>();
        
        System.Diagnostics.Debug.WriteLine($"[AggregateMaterials] START - RootItems.Count={RootItems.Count}");
        
        foreach (var root in RootItems)
        {
            System.Diagnostics.Debug.WriteLine($"[AggregateMaterials] Processing root: {root.Name} (ID:{root.ItemId}) Source={root.Source}, Children.Count={root.Children.Count}");
            AggregateNode(root, aggregates, depth: 0);
        }
        
        System.Diagnostics.Debug.WriteLine($"[AggregateMaterials] END - Aggregated {aggregates.Count} materials: [{string.Join(", ", aggregates.Values.Select(m => m.Name))}]");
        return aggregates.Values.OrderBy(m => m.Name).ToList();
    }
    
    private void AggregateNode(PlanNode node, Dictionary<int, MaterialAggregate> aggregates, int depth)
    {
        var indent = new string(' ', depth * 2);
        System.Diagnostics.Debug.WriteLine($"[AggregateNode] {indent}{node.Name} (ID:{node.ItemId}) Source={node.Source}, Children={node.Children.Count}");
        
        // If buying from market (NQ or HQ), this item goes to shopping list (not its children)
        if (node.Source == AcquisitionSource.MarketBuyNq || node.Source == AcquisitionSource.MarketBuyHq)
        {
            System.Diagnostics.Debug.WriteLine($"[AggregateNode] {indent}  -> ADDED (market buy)");
            AddToAggregation(node, aggregates, isCrafted: false, requiresHq: node.MustBeHq);
            // Don't recurse into children - we're buying the finished item
            return;
        }
        
        // If buying from vendor, add to aggregation
        if (node.Source == AcquisitionSource.VendorBuy)
        {
            System.Diagnostics.Debug.WriteLine($"[AggregateNode] {indent}  -> ADDED (vendor buy)");
            AddToAggregation(node, aggregates, isCrafted: false, requiresHq: node.MustBeHq);
            return;
        }
        
        // If leaf node (can't be crafted), add to aggregation
        if (!node.Children.Any())
        {
            System.Diagnostics.Debug.WriteLine($"[AggregateNode] {indent}  -> ADDED (leaf node)");
            AddToAggregation(node, aggregates, isCrafted: false, requiresHq: node.MustBeHq);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[AggregateNode] {indent}  -> Recursing into {node.Children.Count} children...");
        }
        
        // Recurse into children (sub-materials needed for crafting)
        foreach (var child in node.Children)
        {
            AggregateNode(child, aggregates, depth + 1);
        }
    }
    
    private void AddToAggregation(PlanNode node, Dictionary<int, MaterialAggregate> aggregates, bool isCrafted, bool requiresHq = false)
    {
        if (!aggregates.TryGetValue(node.ItemId, out var aggregate))
        {
            aggregate = new MaterialAggregate
            {
                ItemId = node.ItemId,
                Name = node.Name,
                IconId = node.IconId,
                UnitPrice = node.MarketPrice,
                RequiresHq = requiresHq
            };
            aggregates[node.ItemId] = aggregate;
        }
        aggregate.TotalQuantity += node.Quantity;
        aggregate.UnitPrice = node.MarketPrice;
        aggregate.RequiresHq = aggregate.RequiresHq || requiresHq; // If any source requires HQ, mark it
        aggregate.Sources.Add(new MaterialSource
        {
            ParentItemName = node.Parent?.Name ?? "Direct",
            Quantity = node.Quantity,
            IsCrafted = isCrafted
        });
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
    /// How to acquire this item (craft, buy, vendor, etc.)
    /// </summary>
    public AcquisitionSource Source { get; set; } = AcquisitionSource.Craft;
    
    /// <summary>
    /// If true, HQ version is required (for buying from market)
    /// </summary>
    [Obsolete("Use MustBeHq instead - this was conflated with acquisition source")]
    public bool RequiresHq { get; set; }
    
    /// <summary>
    /// If true, this item must be HQ quality (regardless of acquisition method).
    /// Applies to both crafted items (need to HQ the craft) and bought items (buy HQ listing).
    /// </summary>
    public bool MustBeHq { get; set; }
    
    /// <summary>
    /// If true, this item cannot be crafted (gathered, dropped, etc.)
    /// </summary>
    [Obsolete("Use Source instead")]
    public bool IsUncraftable { get; set; }
    
    /// <summary>
    /// Legacy property: true if buying from market (NQ or HQ) OR vendor
    /// </summary>
    [JsonIgnore]
    public bool IsBuy => Source == AcquisitionSource.MarketBuyNq || Source == AcquisitionSource.MarketBuyHq || Source == AcquisitionSource.VendorBuy;
    
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
    /// Current market price per unit (NQ) (fetched from Universalis)
    /// </summary>
    public decimal MarketPrice { get; set; }
    
    /// <summary>
    /// HQ market price per unit (fetched from Universalis)
    /// </summary>
    public decimal HqMarketPrice { get; set; }
    
    /// <summary>
    /// Vendor price per unit (if available, fetched from Garland)
    /// </summary>
    public decimal VendorPrice { get; set; }
    
    /// <summary>
    /// Whether this item can be HQ (crafted items and gathered materials can, crystals/clusters/aethersands cannot)
    /// </summary>
    public bool CanBeHq { get; set; }
    
    /// <summary>
    /// Source of the price (Vendor, Market, Untradeable)
    /// </summary>
    [JsonIgnore]
    public PriceSource PriceSource { get; set; }
    
    /// <summary>
    /// Details about the price source (vendor name, market location, etc.)
    /// </summary>
    [JsonIgnore]
    public string PriceSourceDetails { get; set; } = string.Empty;
    
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
    /// If true, this node represents a circular reference in the recipe tree.
    /// The item appears elsewhere in the crafting chain and cannot be expanded further.
    /// </summary>
    public bool IsCircularReference { get; set; }
    
    /// <summary>
    /// If true, this item can be bought from a vendor using gil.
    /// Set during FetchVendorPricesAsync based on Garland API data.
    /// 
    /// USAGE:
    /// - Used to show "Vendor" option in acquisition dropdown
    /// - Used for smart defaults (VendorBuy prioritized over MarketBuy/Craft)
    /// - Checked before displaying vendor-specific UI elements
    /// 
    /// NOTE: This only indicates availability of GIL vendors. Special currency vendors
    /// (tomestones, etc.) are tracked separately in VendorOptions but don't set this flag.
    /// </summary>
    public bool CanBuyFromVendor { get; set; }

    /// <summary>
    /// Full vendor options for this item (ALL vendors including special currency ones).
    /// 
    /// CONTENTS:
    /// - Gil vendors: Standard currency vendors (prioritized in UI)
    /// - Special currency vendors: Tomestones, beast tribe currency, etc.
    /// - Multiple locations: Vendors like "Material Supplier" in different zones
    /// 
    /// POPULATED BY:
    /// RecipeCalculationService.FetchVendorPricesAsync() extracts vendor data
    /// from Garland API and stores it here. Persists across save/load via
    /// PlanSerializationWrapper (Version 2+).
    /// 
    /// UI DISPLAY:
    /// - Gil vendors shown first in dropdown
    /// - Special currency vendors shown with currency icon
    /// - Alternate locations shown in tooltip
    /// - SelectedVendorIndex tracks user's choice
    /// </summary>
    public List<VendorInfo> VendorOptions { get; set; } = new();

    /// <summary>
    /// Gets the cheapest gil vendor option, or null if no gil vendors available.
    /// 
    /// Used for:
    /// - Cost calculations (vendor price vs market price comparisons)
    /// - Default vendor selection in procurement plans
    /// - Smart defaults during tree building (VendorBuy prioritized)
    /// 
    /// FILTERS:
    /// - Only vendors with IsGilVendor == true
    /// - Minimum price selected
    /// </summary>
    [JsonIgnore]
    public VendorInfo? CheapestGilVendor => VendorOptions
        .Where(v => v.IsGilVendor)
        .OrderBy(v => v.Price)
        .FirstOrDefault();

    /// <summary>
    /// Gets all gil vendors with the cheapest price.
    /// 
    /// USE CASE:
    /// Some items (like basic materials) are sold by multiple vendors at the same price.
    /// This property returns all vendors with the minimum price, allowing UI to:
    /// - Show "Available from: Material Supplier, Limsa/Gridania/Ul'dah"
    /// - Let user pick convenient location
    /// - Display all options when prices are identical
    /// 
    /// EXAMPLE:
    /// Iron Ore @ 18g from Material Suppliers in all three main cities.
    /// Returns all three locations so user can pick nearest.
    /// </summary>
    [JsonIgnore]
    public List<VendorInfo> CheapestGilVendors
    {
        get
        {
            var gilVendors = VendorOptions.Where(v => v.IsGilVendor).ToList();
            if (!gilVendors.Any()) return new List<VendorInfo>();
            var minPrice = gilVendors.Min(v => v.Price);
            return gilVendors.Where(v => v.Price == minPrice).ToList();
        }
    }

    /// <summary>
    /// Selected vendor index for procurement plan (which vendor to buy from).
    /// -1 means not selected (use CheapestGilVendor).
    /// 
    /// PERSISTENCE:
    /// Saved with plan via SerializablePlanNode. Restored on load.
    /// Allows users to specify preferred vendor location (e.g., "always buy from Limsa").
    /// 
    /// UI:
    /// Bound to vendor dropdown in recipe tree. Changing selection updates
    /// procurement plan display to show chosen vendor location.
    /// </summary>
    public int SelectedVendorIndex { get; set; } = -1;

    /// <summary>
    /// Gets the selected vendor or the cheapest gil vendor if none selected.
    /// 
    /// RESOLUTION ORDER:
    /// 1. If SelectedVendorIndex >= 0: Return VendorOptions[SelectedVendorIndex]
    /// 2. Else: Return CheapestGilVendor (first vendor with lowest price)
    /// 3. If no gil vendors: Return null
    /// 
    /// Used in procurement plan display to show final vendor selection.
    /// </summary>
    [JsonIgnore]
    public VendorInfo? SelectedVendor => SelectedVendorIndex >= 0 && SelectedVendorIndex < VendorOptions.Count
        ? VendorOptions[SelectedVendorIndex]
        : CheapestGilVendor;

    /// <summary>
    /// If true, this item has a craft recipe and can be crafted.
    /// </summary>
    public bool CanCraft { get; set; }
    
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
            Source = Source,
            MustBeHq = MustBeHq,
            CanBeHq = CanBeHq,
            HqMarketPrice = HqMarketPrice,
            RecipeLevel = RecipeLevel,
            Job = Job,
            Yield = Yield,
            MarketPrice = MarketPrice,
            PriceSource = PriceSource,
            PriceSourceDetails = PriceSourceDetails,
            NodeId = NodeId,
            ParentNodeId = ParentNodeId,
            Notes = Notes,
            CanBuyFromVendor = CanBuyFromVendor,
            CanCraft = CanCraft,
            VendorPrice = VendorPrice,
            SelectedVendorIndex = SelectedVendorIndex
        };

        // Clone vendor options
        clone.VendorOptions = VendorOptions.Select(v => new VendorInfo
        {
            Name = v.Name,
            Location = v.Location,
            Price = v.Price,
            Currency = v.Currency
        }).ToList();

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
        // Store original base quantity before mutation
        int originalQuantity = Quantity;
        
        // Recalculate based on parent's new quantity
        // Formula: newQuantity = ceil(parentQuantity * (originalQuantity / Yield))
        Quantity = Yield > 0 
            ? (int)Math.Ceiling(parentQuantity * (double)originalQuantity / Yield)
            : originalQuantity;
        
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
        Source = buy ? AcquisitionSource.MarketBuyNq : AcquisitionSource.Craft;
        
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
    
    /// <summary>
    /// If true, HQ version is required (for market purchases).
    /// </summary>
    public bool RequiresHq { get; set; }
    
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
    
    /// <summary>Legacy property for backward compatibility. Use Source instead.</summary>
    public bool IsBuy { get; set; }
    
    /// <summary>Acquisition source (new preferred property).</summary>
    public AcquisitionSource? Source { get; set; }
    
    /// <summary>Legacy: Use MustBeHq instead.</summary>
    public bool RequiresHq { get; set; }
    
    /// <summary>If true, this item must be HQ quality (for plan sharing).</summary>
    public bool MustBeHq { get; set; }
    
    /// <summary>If true, this item can be HQ (crafted/gathered items can, crystals/aethersands cannot).</summary>
    public bool CanBeHq { get; set; }
    
    public bool IsUncraftable { get; set; }
    public int RecipeLevel { get; set; }
    public string Job { get; set; } = string.Empty;
    public int Yield { get; set; } = 1;
    public decimal MarketPrice { get; set; }
    
    /// <summary>HQ market price per unit.</summary>
    public decimal HqMarketPrice { get; set; }
    
    /// <summary>Vendor price per unit (if available).</summary>
    public decimal VendorPrice { get; set; }
    
    /// <summary>If true, this item can be bought from a vendor.</summary>
    public bool CanBuyFromVendor { get; set; }
    
    /// <summary>If true, this item has a craft recipe and can be crafted.</summary>
    public bool CanCraft { get; set; }

    /// <summary>Full vendor options for this item.</summary>
    public List<VendorInfo> Vendors { get; set; } = new();

    /// <summary>Selected vendor index for procurement (-1 = use cheapest).</summary>
    public int SelectedVendorIndex { get; set; } = -1;

    public string NodeId { get; set; } = string.Empty;
    public string? ParentNodeId { get; set; }
    public string? Notes { get; set; }
    public List<string> ChildNodeIds { get; set; } = new();
}

/// <summary>
/// Helper class for JSON serialization
/// </summary>
public class PlanSerializationWrapper
{
    /// <summary>
    /// Plan format version for backward compatibility.
    /// Version 1: Initial format
    /// Version 2: Added full vendor data support
    /// </summary>
    public int Version { get; set; } = 2;

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string DataCenter { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public List<string> RootNodeIds { get; set; } = new();
    public List<SerializablePlanNode> Nodes { get; set; } = new();
}
