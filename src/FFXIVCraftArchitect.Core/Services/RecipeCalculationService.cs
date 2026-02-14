using System.Collections.Concurrent;
using System.Text.Json;
using FFXIVCraftArchitect.Core.Helpers;
using FFXIVCraftArchitect.Core.Models;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Core.Services;

/// <summary>
/// Service for calculating hierarchical crafting recipes.
/// Builds a tree of items with their ingredients, handling circular dependencies and aggregation.
/// </summary>
public class RecipeCalculationService
{
    private readonly GarlandService _garlandService;
    private readonly ILogger<RecipeCalculationService>? _logger;
    
    // Cache to avoid fetching the same item multiple times during calculation
    private readonly ConcurrentDictionary<int, GarlandItem> _itemCache = new();
    
    // Maximum recursion depth to prevent stack overflow
    private const int MaxDepth = 20;

    public RecipeCalculationService(GarlandService garlandService, ILogger<RecipeCalculationService>? logger = null)
    {
        _garlandService = garlandService;
        _logger = logger;
    }

    /// <summary>
    /// Build a complete crafting plan from a list of target items.
    /// </summary>
    public async Task<CraftingPlan> BuildPlanAsync(
        List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
        string dataCenter,
        string world,
        CancellationToken ct = default)
    {
        _logger?.LogInformation("[RecipeCalc] Building plan for {Count} target items", targetItems.Count);
        
        var plan = new CraftingPlan
        {
            Name = $"Plan {DateTime.Now:yyyy-MM-dd HH:mm}",
            DataCenter = dataCenter,
            World = world
        };

        _itemCache.Clear();

        foreach (var (itemId, name, quantity, isHqRequired) in targetItems)
        {
            try
            {
                var node = await BuildNodeRecursiveAsync(itemId, name, quantity, null, 0, ct);
                if (node != null)
                {
                    // Apply HQ requirement from project item settings
                    node.MustBeHq = isHqRequired;
                    plan.RootItems.Add(node);
                    _logger?.LogDebug("[RecipeCalc] Added root item: {Name} x{Qty} (HQ: {Hq})", node.Name, node.Quantity, isHqRequired);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[RecipeCalc] Failed to build tree for item {ItemId}", itemId);
                // Add a placeholder node so user knows something went wrong
                plan.RootItems.Add(new PlanNode
                {
                    ItemId = itemId,
                    Name = $"{name} (Error: {ex.Message})",
                    Quantity = quantity,
                    Source = AcquisitionSource.MarketBuyNq,
                    MustBeHq = isHqRequired,
                    CanCraft = false  // Error case, assume not craftable
                });
            }
        }

        _logger?.LogInformation("[RecipeCalc] Plan built with {Count} root items", plan.RootItems.Count);
        
        // Fetch vendor prices for all items in the plan
        await FetchVendorPricesAsync(plan, ct);
        
        return plan;
    }

    /// <summary>
    /// Fetch vendor prices for all items in a plan.
    /// Vendor prices are cached in-memory during the session.
    /// </summary>
    public async Task FetchVendorPricesAsync(CraftingPlan plan, CancellationToken ct = default)
    {
        if (plan?.RootItems == null || !plan.RootItems.Any())
            return;

        _logger?.LogInformation("[RecipeCalc] Fetching vendor prices for plan items");
        
        var vendorPriceCache = new Dictionary<int, (decimal price, List<VendorInfo> vendors)>();
        int fetchedCount = 0;
        int cachedCount = 0;

        foreach (var root in plan.RootItems)
        {
            (fetchedCount, cachedCount) = await FetchVendorPricesForNodeAsync(
                root, vendorPriceCache, fetchedCount, cachedCount, ct);
        }

        _logger?.LogInformation("[RecipeCalc] Vendor prices: {Fetched} fetched, {Cached} from cache", 
            fetchedCount, cachedCount);
    }

    private async Task<(int fetched, int cached)> FetchVendorPricesForNodeAsync(
        PlanNode node, 
        Dictionary<int, (decimal price, List<VendorInfo> vendors)> cache, 
        int fetchedCount, 
        int cachedCount,
        CancellationToken ct)
    {
        // Check in-memory cache first
        if (cache.TryGetValue(node.ItemId, out var cachedData))
        {
            node.VendorPrice = cachedData.price;
            node.VendorOptions = cachedData.vendors;
            node.CanBuyFromVendor = cachedData.vendors.Any(v => v.IsGilVendor);
            cachedCount++;
        }
        else
        {
            // Fetch from Garland
            try
            {
                var itemData = await _garlandService.GetItemAsync(node.ItemId, ct);
                if (itemData != null)
                {
                    var vendors = GetVendorOptions(itemData);
                    var gilVendors = vendors.Where(v => v.IsGilVendor).ToList();
                    var cheapestPrice = gilVendors.Any() ? gilVendors.Min(v => v.Price) : 0;
                    
                    node.VendorOptions = vendors;
                    node.VendorPrice = cheapestPrice;
                    node.CanBuyFromVendor = cheapestPrice > 0;
                    cache[node.ItemId] = (cheapestPrice, vendors);
                    fetchedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[RecipeCalc] Failed to fetch vendor price for {ItemId}", node.ItemId);
            }
        }

        // Recurse into children
        foreach (var child in node.Children)
        {
            (fetchedCount, cachedCount) = await FetchVendorPricesForNodeAsync(
                child, cache, fetchedCount, cachedCount, ct);
        }

        return (fetchedCount, cachedCount);
    }

    /// <summary>
    /// Extract vendor price from Garland item data.
    /// Handles both full vendor objects and ID-only references (e.g., Ixali Vendor).
    /// </summary>
    private static decimal GetVendorPrice(GarlandItem? item)
    {
        if (item == null) return 0;
        
        // If we have full vendor objects with prices, use the cheapest
        if (item.Vendors.Count > 0)
        {
            return item.Vendors.Min(v => v.Price);
        }
        
        // If vendors are listed as IDs only (e.g., Ixali Vendor), use root-level price
        if (item.HasVendorReferences && item.Price > 0)
        {
            return item.Price;
        }
        
        return 0;
    }

    /// <summary>
    /// Extract all vendor options from Garland item data.
    /// Returns a list of VendorInfo for all available vendors (both gil and special currency).
    /// </summary>
    private static List<VendorInfo> GetVendorOptions(GarlandItem? item)
    {
        if (item == null) return new List<VendorInfo>();

        var vendors = new List<VendorInfo>();

        // If we have full vendor objects with prices, convert them
        if (item.Vendors.Count > 0)
        {
            vendors = item.Vendors.Select(v => VendorInfo.FromGarlandVendor(v)).ToList();
        }
        // If vendors are listed as IDs only with root-level price, create a generic entry
        else if (item.HasVendorReferences && item.Price > 0)
        {
            vendors.Add(new VendorInfo
            {
                Name = "Material Supplier",
                Location = "Multiple Locations",
                Price = item.Price,
                Currency = "gil"
            });
        }

        return vendors;
    }

    /// <summary>
    /// Recursively build a node and its ingredient children.
    /// </summary>
    private async Task<PlanNode?> BuildNodeRecursiveAsync(
        int itemId, 
        string name, 
        int quantity, 
        PlanNode? parent, 
        int depth, 
        CancellationToken ct)
    {
        // Prevent infinite recursion (safety guard)
        if (depth > MaxDepth)
        {
            _logger?.LogWarning("[RecipeCalc] Max depth reached for item {ItemId}, treating as uncraftable", itemId);
            return new PlanNode
            {
                ItemId = itemId,
                Name = name,
                Quantity = quantity,
                Source = AcquisitionSource.MarketBuyNq,
                Parent = parent,
                CanCraft = false  // Max depth reached, can't craft
            };
        }

        // Fetch item data (with caching)
        if (!_itemCache.TryGetValue(itemId, out var itemData))
        {
            try
            {
                itemData = await _garlandService.GetItemAsync(itemId, ct);
                if (itemData != null)
                {
                    _itemCache[itemId] = itemData;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[RecipeCalc] Failed to fetch item {ItemId}", itemId);
            }
        }

        // Determine if item can be HQ
        var canBeHq = DetermineCanBeHq(itemData, itemId);
        
        // Check if item can be bought from vendor (has vendor objects OR vendor ID references)
        var hasVendor = itemData?.HasVendorReferences == true;
        
        // Create the node
        var node = new PlanNode
        {
            ItemId = itemId,
            Name = itemData?.Name ?? name,
            IconId = itemData?.IconId ?? 0,
            Quantity = quantity,
            Parent = parent,
            CanBeHq = canBeHq,
            CanBuyFromVendor = hasVendor
        };

        // Check for both traditional crafts and company crafts
        var hasCraft = itemData?.Crafts?.Any() == true;
        var hasCompanyCraft = itemData?.CompanyCrafts?.Any() == true;
        
        // Set craftability - company crafts are also considered "craftable"
        node.CanCraft = hasCraft || hasCompanyCraft;
        
        if (!hasCraft && !hasCompanyCraft)
        {
            // No craft recipe - prefer vendor if available, otherwise market
            node.Source = hasVendor ? AcquisitionSource.VendorBuy : AcquisitionSource.MarketBuyNq;
            _logger?.LogDebug("[RecipeCalc] Item {Name} has no recipe, marked as {Source}", node.Name, node.Source);
            return node;
        }

        // Handle company workshop recipes (airships, submarines, etc.)
        if (hasCompanyCraft)
        {
            return await BuildCompanyCraftNodeAsync(node, itemData!.CompanyCrafts!.First(), quantity, ct);
        }

        // Use the first available traditional recipe
        var recipe = itemData!.Crafts!.OrderBy(r => r.RecipeLevel).First();
        node.RecipeLevel = recipe.RecipeLevel;
        node.Job = JobHelper.GetJobName(recipe.JobId);
        node.Yield = Math.Max(1, recipe.Yield);

        // Calculate how many times we need to craft
        var craftCount = (int)Math.Ceiling((double)quantity / node.Yield);

        // Build child nodes for ingredients
        foreach (var ingredient in recipe.Ingredients)
        {
            var ingredientQuantity = ingredient.Amount * craftCount;
            var childNode = await BuildNodeRecursiveAsync(
                ingredient.Id, 
                ingredient.Name ?? $"Item_{ingredient.Id}", 
                ingredientQuantity, 
                node, 
                depth + 1, 
                ct);
            
            if (childNode != null)
            {
                node.Children.Add(childNode);
            }
        }

        // Smart default: determine best acquisition method
        // But NEVER default root items (parent == null) to buy - user wants to craft project items
        var isRootItem = parent == null;
        
        // Priority: Vendor > Buy > Craft (for non-root items)
        // Vendor is cheapest and most convenient, so default to it when available
        var shouldDefaultToVendor = !isRootItem && hasVendor;
        var shouldDefaultToBuy = !isRootItem && !shouldDefaultToVendor && ShouldDefaultToBuy(node);
        
        _logger?.LogInformation("[RecipeCalc] {Name}: RecipeLevel={Level}, Children={ChildCount}, IsRoot={IsRoot}, HasVendor={HasVendor}, ShouldDefaultToVendor={ShouldVendor}, ShouldDefaultToBuy={ShouldBuy}", 
            node.Name, node.RecipeLevel, node.Children.Count, isRootItem, hasVendor, shouldDefaultToVendor, shouldDefaultToBuy);
        
        if (shouldDefaultToVendor)
        {
            _logger?.LogInformation("[RecipeCalc] {Name}: Setting Source=VendorBuy (vendor available)", node.Name);
            node.Source = AcquisitionSource.VendorBuy;
        }
        else if (shouldDefaultToBuy)
        {
            _logger?.LogInformation("[RecipeCalc] {Name}: Setting Source=MarketBuyNq (heuristic)", node.Name);
            node.Source = AcquisitionSource.MarketBuyNq;
        }
        else
        {
            _logger?.LogInformation("[RecipeCalc] {Name}: Source remains default={Source} (crafting preferred)", node.Name, node.Source);
        }

        return node;
    }

    /// <summary>
    /// Determine if an item should default to "buy" mode based on its ingredients.
    /// </summary>
    private bool ShouldDefaultToBuy(PlanNode node)
    {
        if (!node.Children.Any())
            return true;

        if (node.RecipeLevel < 10 && node.Children.Count > 3)
            return true;
        
        return false;
    }

    /// <summary>
    /// Recalculate quantities when a root item quantity changes.
    /// </summary>
    public void RecalculateQuantities(PlanNode rootNode, int newQuantity)
    {
        if (rootNode.Quantity == 0 || newQuantity == 0)
        {
            ScaleNodeQuantities(rootNode, newQuantity > 0 ? 100 : 0);
            return;
        }

        var ratio = (double)newQuantity / rootNode.Quantity;
        ScaleNodeQuantities(rootNode, ratio);
    }

    private void ScaleNodeQuantities(PlanNode node, double ratio)
    {
        node.Quantity = Math.Max(1, (int)(node.Quantity * ratio));
        
        if (node.Children.Any() && node.Yield > 0)
        {
            foreach (var child in node.Children)
            {
                ScaleNodeQuantities(child, ratio);
            }
        }
    }

    /// <summary>
    /// Set the acquisition source for an item.
    /// </summary>
    public void SetAcquisitionSource(PlanNode node, AcquisitionSource source)
    {
        node.Source = source;
        
        if (source == AcquisitionSource.MarketBuyHq)
        {
            node.MustBeHq = true;
        }
        
        _logger?.LogInformation("[RecipeCalc] {ItemName} set to {Source} (MustBeHq={MustBeHq})", node.Name, source, node.MustBeHq);
    }

    /// <summary>
    /// Build a node for company workshop recipes (airships, submarines).
    /// </summary>
    private async Task<PlanNode> BuildCompanyCraftNodeAsync(
        PlanNode node, 
        GarlandCompanyCraft companyCraft, 
        int quantity,
        CancellationToken ct)
    {
        node.Job = "Company Workshop";
        node.RecipeLevel = 1;
        node.Yield = 1;
        
        _logger?.LogInformation("[RecipeCalc] Building company craft: {Name} x{Quantity} with {PhaseCount} phases", 
            node.Name, quantity, companyCraft.PhaseCount);

        foreach (var phase in companyCraft.Phases)
        {
            _logger?.LogDebug("[RecipeCalc] Processing phase {PhaseNumber} with {ItemCount} items",
                phase.PhaseNumber + 1, phase.Items.Count);
            
            foreach (var item in phase.Items)
            {
                var ingredientQuantity = item.Amount * quantity;
                _logger?.LogDebug("[RecipeCalc] Phase {PhaseNumber} item: {ItemName} x{Amount} = {TotalQuantity}",
                    phase.PhaseNumber + 1, item.Name, item.Amount, ingredientQuantity);
                
                var childNode = await BuildNodeRecursiveAsync(
                    item.Id,
                    item.Name ?? $"Item_{item.Id}",
                    ingredientQuantity,
                    node,  // Parent is the company craft node, not a phase node
                    0,
                    ct);
                
                if (childNode != null)
                {
                    node.Children.Add(childNode);
                    _logger?.LogDebug("[RecipeCalc] Added ingredient: {ItemName} x{Quantity}", 
                        childNode.Name, childNode.Quantity);
                }
                else
                {
                    _logger?.LogWarning("[RecipeCalc] Failed to build node for phase ingredient: {ItemId} ({ItemName})",
                        item.Id, item.Name);
                }
            }
        }

        _logger?.LogInformation("[RecipeCalc] Company craft {Name} complete: {ChildCount} total ingredients, Source={Source}",
            node.Name, node.Children.Count, node.Source);

        return node;
    }

    /// <summary>
    /// Determine if an item can be HQ based on its properties.
    /// </summary>
    private static bool DetermineCanBeHq(GarlandItem? itemData, int itemId)
    {
        // Crystals, shards, clusters cannot be HQ
        if (itemId >= 1 && itemId <= 19)
            return false;
        
        if (itemData?.Name != null)
        {
            var lowerName = itemData.Name.ToLowerInvariant();
            
            if (lowerName.Contains("crystal") || 
                lowerName.Contains("shard") || 
                lowerName.Contains("cluster"))
                return false;
            
            if (lowerName.Contains("aethersand"))
                return false;
        }
        
        // Only items with craft recipes can be HQ
        if (itemData?.Crafts?.Any() == true)
            return true;
        
        return false;
    }

    /// <summary>
    /// Serialize a plan to JSON for saving.
    /// </summary>
    public string SerializePlan(CraftingPlan plan)
    {
        var serializableNodes = new List<SerializablePlanNode>();
        
        foreach (var root in plan.RootItems)
        {
            SerializeNode(root, null, serializableNodes);
        }

        var wrapper = new PlanSerializationWrapper
        {
            Id = plan.Id,
            Name = plan.Name,
            CreatedAt = plan.CreatedAt,
            ModifiedAt = plan.ModifiedAt,
            DataCenter = plan.DataCenter,
            World = plan.World,
            RootNodeIds = plan.RootItems.Select(r => r.NodeId).ToList(),
            Nodes = serializableNodes
        };

        return JsonSerializer.Serialize(wrapper, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private void SerializeNode(PlanNode node, string? parentId, List<SerializablePlanNode> list)
    {
        var serializable = new SerializablePlanNode
        {
            ItemId = node.ItemId,
            Name = node.Name,
            IconId = node.IconId,
            Quantity = node.Quantity,
            Source = node.Source,
            MustBeHq = node.MustBeHq,
            CanBeHq = node.CanBeHq,
            CanBuyFromVendor = node.CanBuyFromVendor,
            CanCraft = node.CanCraft,
            RecipeLevel = node.RecipeLevel,
            Job = node.Job,
            Yield = node.Yield,
            VendorPrice = node.VendorPrice,
            Vendors = node.VendorOptions.ToList(),
            SelectedVendorIndex = node.SelectedVendorIndex,
            // Market prices intentionally NOT serialized - they bloat the file 
            // and will be refreshed from market data on load anyway
            NodeId = node.NodeId,
            ParentNodeId = parentId,
            Notes = node.Notes,
            ChildNodeIds = node.Children.Select(c => c.NodeId).ToList()
        };

        list.Add(serializable);

        foreach (var child in node.Children)
        {
            SerializeNode(child, node.NodeId, list);
        }
    }

    /// <summary>
    /// Deserialize a plan from JSON.
    /// </summary>
    public CraftingPlan? DeserializePlan(string json)
    {
        try
        {
            var wrapper = JsonSerializer.Deserialize<PlanSerializationWrapper>(json);
            if (wrapper == null) return null;

            var nodeLookup = new Dictionary<string, PlanNode>();
            foreach (var sNode in wrapper.Nodes)
            {
                var node = new PlanNode
                {
                    ItemId = sNode.ItemId,
                    Name = sNode.Name,
                    IconId = sNode.IconId,
                    Quantity = sNode.Quantity,
                    RecipeLevel = sNode.RecipeLevel,
                    Job = sNode.Job,
                    Yield = sNode.Yield,
                    MarketPrice = sNode.MarketPrice,
                    NodeId = sNode.NodeId,
                    ParentNodeId = sNode.ParentNodeId,
                    Notes = sNode.Notes
                };
                
                if (sNode.Source.HasValue)
                {
                    node.Source = sNode.Source.Value;
                }
                else
                {
                    node.Source = AcquisitionSource.Craft;
                }
                
                node.MustBeHq = sNode.MustBeHq;
                node.CanBeHq = sNode.CanBeHq;
                node.CanBuyFromVendor = sNode.CanBuyFromVendor;
                node.CanCraft = sNode.CanCraft;
                node.HqMarketPrice = sNode.HqMarketPrice;
                node.VendorPrice = sNode.VendorPrice;
                node.VendorOptions = sNode.Vendors?.ToList() ?? new List<VendorInfo>();
                node.SelectedVendorIndex = sNode.SelectedVendorIndex;

                nodeLookup[sNode.NodeId] = node;
            }

            foreach (var sNode in wrapper.Nodes)
            {
                if (!nodeLookup.TryGetValue(sNode.NodeId, out var node)) continue;
                
                if (sNode.ParentNodeId != null && nodeLookup.TryGetValue(sNode.ParentNodeId, out var parent))
                {
                    node.Parent = parent;
                }

                foreach (var childId in sNode.ChildNodeIds)
                {
                    if (nodeLookup.TryGetValue(childId, out var child))
                    {
                        node.Children.Add(child);
                    }
                }
            }

            var plan = new CraftingPlan
            {
                Id = wrapper.Id,
                Name = wrapper.Name,
                CreatedAt = wrapper.CreatedAt,
                ModifiedAt = wrapper.ModifiedAt,
                DataCenter = wrapper.DataCenter,
                World = wrapper.World
            };

            foreach (var rootId in wrapper.RootNodeIds)
            {
                if (nodeLookup.TryGetValue(rootId, out var rootNode))
                {
                    plan.RootItems.Add(rootNode);
                }
            }

            return plan;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[RecipeCalc] Failed to deserialize plan");
            return null;
        }
    }

    // ========================================================================
    // Price Manipulation Methods
    // ========================================================================

    /// <summary>
    /// Extracts all prices from a plan's nodes into a dictionary.
    /// </summary>
    public Dictionary<int, PriceInfo> ExtractPricesFromPlan(CraftingPlan plan)
    {
        var prices = new Dictionary<int, PriceInfo>();
        
        foreach (var root in plan.RootItems)
        {
            ExtractPricesFromNode(root, prices);
        }
        
        return prices;
    }
    
    private void ExtractPricesFromNode(PlanNode node, Dictionary<int, PriceInfo> prices)
    {
        if (node.MarketPrice > 0 || node.PriceSource != PriceSource.Unknown)
        {
            if (!prices.ContainsKey(node.ItemId))
            {
                prices[node.ItemId] = new PriceInfo
                {
                    ItemId = node.ItemId,
                    ItemName = node.Name,
                    UnitPrice = node.MarketPrice,
                    Source = node.PriceSource,
                    SourceDetails = node.PriceSourceDetails
                };
            }
        }
        
        foreach (var child in node.Children)
        {
            ExtractPricesFromNode(child, prices);
        }
    }

    /// <summary>
    /// Updates a single node's price information.
    /// </summary>
    public void UpdateSingleNodePrice(List<PlanNode> nodes, int itemId, PriceInfo priceInfo)
    {
        foreach (var node in nodes)
        {
            if (node.ItemId == itemId)
            {
                node.MarketPrice = priceInfo.UnitPrice;
                if (node.CanBeHq)
                {
                    node.HqMarketPrice = priceInfo.HqUnitPrice > 0 ? priceInfo.HqUnitPrice : 0;
                }
                node.PriceSource = priceInfo.Source;
                node.PriceSourceDetails = priceInfo.SourceDetails;
            }
            
            if (node.Children?.Any() == true)
            {
                UpdateSingleNodePrice(node.Children, itemId, priceInfo);
            }
        }
    }

    /// <summary>
    /// Updates all nodes in a plan with price information from a dictionary.
    /// </summary>
    public void UpdatePlanWithPrices(List<PlanNode> nodes, Dictionary<int, PriceInfo> prices)
    {
        foreach (var node in nodes)
        {
            if (prices.TryGetValue(node.ItemId, out var priceInfo))
            {
                node.MarketPrice = priceInfo.UnitPrice;
                if (node.CanBeHq)
                {
                    node.HqMarketPrice = priceInfo.HqUnitPrice > 0 ? priceInfo.HqUnitPrice : 0;
                }
                node.PriceSource = priceInfo.Source;
                node.PriceSourceDetails = priceInfo.SourceDetails;
            }

            if (node.Children?.Any() == true)
            {
                UpdatePlanWithPrices(node.Children, prices);
            }
        }
    }

    /// <summary>
    /// Collects all unique items with their quantities from a list of plan nodes.
    /// Skips children of items that are being bought (not crafted) to avoid unnecessary price fetches.
    /// </summary>
    public void CollectAllItemsWithQuantity(List<PlanNode> nodes, List<(int itemId, string name, int quantity)> items)
    {
        foreach (var node in nodes)
        {
            if (!items.Any(i => i.itemId == node.ItemId))
            {
                items.Add((node.ItemId, node.Name, node.Quantity));
            }

            // Only recurse into children if this item is being crafted
            // If it's being bought (VendorBuy/MarketBuy), its children aren't needed
            if (node.Children?.Any() == true && node.Source == AcquisitionSource.Craft)
            {
                CollectAllItemsWithQuantity(node.Children, items);
            }
        }
    }

    /// <summary>
    /// Calculates the total craft cost for a node by summing the costs of its children.
    /// Recursively calculates costs for child craft nodes.
    /// </summary>
    public decimal CalculateNodeCraftCost(PlanNode node)
    {
        if (!node.Children.Any())
            return 0;
        
        decimal total = 0;
        foreach (var child in node.Children)
        {
            if (child.Source == AcquisitionSource.MarketBuyNq && child.MarketPrice > 0)
            {
                total += child.MarketPrice * child.Quantity;
            }
            else if (child.Source == AcquisitionSource.MarketBuyHq && child.HqMarketPrice > 0)
            {
                total += child.HqMarketPrice * child.Quantity;
            }
            else if (child.Source == AcquisitionSource.VendorBuy && child.MarketPrice > 0)
            {
                total += child.MarketPrice * child.Quantity;
            }
            else if (child.Source == AcquisitionSource.Craft && child.Children.Any())
            {
                total += CalculateNodeCraftCost(child);
            }
            else if (child.MarketPrice > 0)
            {
                total += child.MarketPrice * child.Quantity;
            }
            else if (child.Children.Any())
            {
                total += CalculateNodeCraftCost(child);
            }
        }
        return total;
    }
}

/// <summary>
/// Helper class for JSON serialization
/// </summary>
public class PlanSerializationWrapper
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string DataCenter { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public List<string> RootNodeIds { get; set; } = new();
    public List<SerializablePlanNode> Nodes { get; set; } = new();
}
