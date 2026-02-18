using System.Collections.Concurrent;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Core.Services;

#region Progress and Exception Types

/// <summary>
/// Progress information for tree building
/// </summary>
public class TreeBuildProgress
{
    public int CurrentLevel { get; set; }
    public int TotalLevels { get; set; }
    public int CurrentBatch { get; set; }
    public int TotalBatches { get; set; }
    public int ItemsFetched { get; set; }
    public int TotalItems { get; set; }
    public string CurrentItemName { get; set; } = "";
    public string Stage { get; set; } = "";  // "Discovering", "Fetching", "Building"
}

/// <summary>
/// Exception thrown when tree calculation fails at a specific level
/// </summary>
public class TreeCalculationException : Exception
{
    public int FailedLevel { get; }
    public List<int> FailedItemIds { get; }
    public TimeSpan ElapsedTime { get; }

    public TreeCalculationException(
        string message,
        int level,
        List<int> itemIds,
        TimeSpan elapsed,
        Exception? inner = null)
        : base(message, inner)
    {
        FailedLevel = level;
        FailedItemIds = itemIds;
        ElapsedTime = elapsed;
    }
}

#endregion

/// <summary>
/// Service for calculating hierarchical crafting recipes.
/// Builds a tree of items with their ingredients, handling circular dependencies and aggregation.
/// </summary>
/// <remarks>
/// DATA FLOW OVERVIEW:
/// 1. BuildPlanAsync: Entry point that orchestrates plan construction
///    - Takes target items (name, quantity, HQ requirement)
///    - Builds recursive ingredient tree for each item
///    - Fetches vendor prices for all items in parallel
///    - Returns populated CraftingPlan
/// 
/// 2. BuildNodeRecursive: Core algorithm for tree construction
///    - Fetches item data from Garland cache or API
///    - Determines if item is craftable (has recipe)
///    - Recursively builds children for each ingredient
///    - Applies smart defaults: VendorBuy > MarketBuy > Craft
///    - Calculates craft count: Ceiling(Quantity / Yield)
/// 
/// 3. AggregateMaterials: Flattens tree to shopping list
///    - Traverses tree depth-first
///    - Stops recursion when Source = MarketBuyNq/HQ or VendorBuy
///    - Aggregates quantities by ItemId
///    - Returns flat list of materials to acquire
/// </remarks>
public class RecipeCalculationService
{
    private readonly GarlandService _garlandService;
    private readonly ILogger<RecipeCalculationService>? _logger;
    
    // Cache to avoid fetching the same item multiple times during calculation
    private readonly ConcurrentDictionary<int, GarlandItem> _itemCache = new();
    
    // Maximum recursion depth to prevent stack overflow
    private const int MaxDepth = 20;

    #region Breadth-First Tree Building Types

    /// <summary>
    /// Represents a node pending tree construction
    /// </summary>
    private record PendingNode(
        int ItemId,
        string Name,
        int Quantity,
        int Depth,
        PlanNode? Parent);

    /// <summary>
    /// Information about one level of the tree
    /// </summary>
    private record LevelInfo(
        int Depth,
        List<int> ItemIds,
        Dictionary<int, int> QuantityByItemId);

    /// <summary>
    /// Result of tree discovery phase
    /// </summary>
    private class TreeDiscoveryResult
    {
        public List<LevelInfo> Levels { get; } = new();
        public HashSet<int> AllItemIds { get; } = new();
        public int MaxDepth { get; set; }
    }

    #endregion

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

        try
        {
            // Phase 1: Discover tree structure (breadth-first)
            _logger?.LogInformation("[RecipeCalc] Phase 1: Discovering tree structure");
            var discovery = await DiscoverTreeLevelsAsync(targetItems, ct);
            
            // Phase 2: Fetch all levels in batches
            _logger?.LogInformation("[RecipeCalc] Phase 2: Fetching {TotalItems} items across {Levels} levels", 
                discovery.AllItemIds.Count, discovery.Levels.Count);
            
            var itemCache = new Dictionary<int, GarlandItem>();
            
            // Default settings (can be made configurable)
            const int batchSize = 5;
            const int maxConcurrent = 2;
            const int timeoutSeconds = 120;
            
            for (int i = 0; i < discovery.Levels.Count; i++)
            {
                var level = discovery.Levels[i];
                _logger?.LogInformation("[RecipeCalc] Fetching level {Level} ({Current}/{Total})", 
                    level.Depth, i + 1, discovery.Levels.Count);
                
                var levelItems = await FetchLevelBatchesAsync(
                    level, batchSize, maxConcurrent, timeoutSeconds, null, ct);
                
                foreach (var kvp in levelItems)
                {
                    itemCache[kvp.Key] = kvp.Value;
                }
            }
            
            // Phase 3: Build tree from cache
            _logger?.LogInformation("[RecipeCalc] Phase 3: Building tree from cache");
            var nodeCache = new Dictionary<int, PlanNode>();
            
            foreach (var (itemId, name, quantity, isHqRequired) in targetItems)
            {
                try
                {
                    var node = BuildTreeFromCache(itemId, quantity, 0, isHqRequired, itemCache, nodeCache);
                    if (node != null)
                    {
                        plan.RootItems.Add(node);
                        _logger?.LogDebug("[RecipeCalc] Added root item: {Name} x{Qty} (HQ: {Hq})", 
                            node.Name, node.Quantity, isHqRequired);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[RecipeCalc] Failed to build tree from cache for item {ItemId}", itemId);
                    plan.RootItems.Add(new PlanNode
                    {
                        ItemId = itemId,
                        Name = $"{name} (Error: {ex.Message})",
                        Quantity = quantity,
                        Source = AcquisitionSource.MarketBuyNq,
                        MustBeHq = isHqRequired,
                        CanCraft = false
                    });
                }
            }
            
            // Apply vendor prices (data is already in cache from Phase 2)
            _logger?.LogInformation("[RecipeCalc] Applying vendor prices from cache");
            await ApplyVendorPricesFromCacheAsync(plan, itemCache, ct);
        }
        catch (TreeCalculationException tex)
        {
            _logger?.LogError(tex, "[RecipeCalc] Tree calculation failed at level {Level} after {Elapsed}", 
                tex.FailedLevel, tex.ElapsedTime);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[RecipeCalc] Unexpected error during tree building");
            throw;
        }

        _logger?.LogInformation("[RecipeCalc] Plan built with {Count} root items", plan.RootItems.Count);
        return plan;
    }

    /// <summary>
    /// Fetch vendor prices for all items in a plan using parallel batch fetching.
    /// 
    /// VENDOR ACQUISITION FLOW:
    /// 1. Collect all unique item IDs from the entire plan tree
    /// 2. Fetch item data from Garland API in parallel batches
    /// 3. Extract vendor information from each item:
    ///    - Gil vendors (primary): Standard currency, cheapest price used
    ///    - Special currency vendors: Tomestones, etc. (tracked but not used for price)
    /// 4. Build vendor cache: Dictionary{itemId → (price, vendors)}
    /// 5. Apply vendor data to all nodes recursively
    /// 
    /// GARLAND API VENDOR DATA FORMATS:
    /// - Full vendor objects: item.Vendors list with name, location, price
    /// - ID-only references: item.HasVendorReferences + item.Price (e.g., Ixali Vendor)
    /// - Partials resolution: Uses item.Partials to resolve vendor IDs to full info
    /// 
    /// VENDOR PRIORITIZATION:
    /// - Gil vendors are prioritized over special currency vendors
    /// - Cheapest gil vendor price is stored in VendorPrice
    /// - All vendors stored in VendorOptions for display
    /// - Special currency vendors shown in UI but not used for cost calculations
    /// </summary>
    public async Task FetchVendorPricesAsync(CraftingPlan plan, CancellationToken ct = default)
    {
        if (plan?.RootItems == null || !plan.RootItems.Any())
            return;

        _logger?.LogInformation("[RecipeCalc] Fetching vendor prices for plan items");
        
        // Collect all unique item IDs from the plan
        var allItemIds = new HashSet<int>();
        foreach (var root in plan.RootItems)
        {
            CollectItemIds(root, allItemIds);
        }
        
        _logger?.LogInformation("[RecipeCalc] Collected {Count} unique items to fetch", allItemIds.Count);
        
        // Fetch all items in parallel using batch method
        Dictionary<int, GarlandItem> fetchedItems;
        try
        {
            fetchedItems = await _garlandService.GetItemsAsync(allItemIds, useParallel: true, ct);
            _logger?.LogInformation("[RecipeCalc] Successfully fetched {FetchedCount} items", fetchedItems.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[RecipeCalc] Failed to batch fetch items, falling back to sequential");
            // Fallback to sequential fetching if batch fails
            fetchedItems = new Dictionary<int, GarlandItem>();
            foreach (var itemId in allItemIds)
            {
                try
                {
                    var item = await _garlandService.GetItemAsync(itemId, ct);
                    if (item != null)
                        fetchedItems[itemId] = item;
                }
                catch (Exception itemEx)
                {
                    _logger?.LogWarning(itemEx, "[RecipeCalc] Failed to fetch item {ItemId}", itemId);
                }
            }
        }
        
        // Build vendor cache from fetched items
        var vendorPriceCache = new Dictionary<int, (decimal price, List<VendorInfo> vendors)>();
        foreach (var kvp in fetchedItems)
        {
            var vendors = GetVendorOptions(kvp.Value);
            var gilVendors = vendors.Where(v => v.IsGilVendor).ToList();
            var cheapestPrice = gilVendors.Any() ? gilVendors.Min(v => v.Price) : 0;
            vendorPriceCache[kvp.Key] = (cheapestPrice, vendors);
        }
        
        int cachedCount = 0;
        int appliedCount = 0;
        
        // Apply vendor data to all nodes
        foreach (var root in plan.RootItems)
        {
            (cachedCount, appliedCount) = ApplyVendorPricesToNode(
                root, vendorPriceCache, cachedCount, appliedCount);
        }

        _logger?.LogInformation("[RecipeCalc] Vendor prices: {Fetched} fetched, {Cached} from cache, {Applied} applied", 
            fetchedItems.Count, cachedCount, appliedCount);
    }
    
    /// <summary>
    /// Apply vendor prices from already-fetched cache to plan nodes.
    /// Used after breadth-first tree building when items are already in cache.
    /// </summary>
    private async Task ApplyVendorPricesFromCacheAsync(
        CraftingPlan plan, 
        Dictionary<int, GarlandItem> itemCache, 
        CancellationToken ct)
    {
        if (plan?.RootItems == null || !plan.RootItems.Any())
            return;
        
        _logger?.LogInformation("[RecipeCalc] Applying vendor prices from cache to {Count} root items", 
            plan.RootItems.Count);
        
        // Build vendor cache from already-fetched items
        var vendorPriceCache = new Dictionary<int, (decimal price, List<VendorInfo> vendors)>();
        foreach (var kvp in itemCache)
        {
            var vendors = GetVendorOptions(kvp.Value);
            var gilVendors = vendors.Where(v => v.IsGilVendor).ToList();
            var cheapestPrice = gilVendors.Any() ? gilVendors.Min(v => v.Price) : 0;
            vendorPriceCache[kvp.Key] = (cheapestPrice, vendors);
        }
        
        int cachedCount = 0;
        int appliedCount = 0;
        
        // Apply vendor data to all nodes
        foreach (var root in plan.RootItems)
        {
            (cachedCount, appliedCount) = ApplyVendorPricesToNode(
                root, vendorPriceCache, cachedCount, appliedCount);
        }
        
        _logger?.LogInformation("[RecipeCalc] Vendor prices applied: {Applied} nodes, {Cached} with vendor data", 
            appliedCount, cachedCount);
    }
    
    /// <summary>
    /// Recursively collect all unique item IDs from a plan node and its children.
    /// </summary>
    private void CollectItemIds(PlanNode node, HashSet<int> itemIds)
    {
        itemIds.Add(node.ItemId);
        foreach (var child in node.Children)
        {
            CollectItemIds(child, itemIds);
        }
    }
    
    /// <summary>
    /// Apply cached vendor prices to a node and its children.
    /// </summary>
    private (int cached, int applied) ApplyVendorPricesToNode(
        PlanNode node,
        Dictionary<int, (decimal price, List<VendorInfo> vendors)> cache,
        int cachedCount,
        int appliedCount)
    {
        if (cache.TryGetValue(node.ItemId, out var cachedData))
        {
            node.VendorPrice = cachedData.price;
            node.VendorOptions = cachedData.vendors;
            node.CanBuyFromVendor = cachedData.vendors.Any(v => v.IsGilVendor);
            cachedCount++;
        }
        appliedCount++;

        // Recurse into children
        foreach (var child in node.Children)
        {
            (cachedCount, appliedCount) = ApplyVendorPricesToNode(child, cache, cachedCount, appliedCount);
        }

        return (cachedCount, appliedCount);
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
    /// 
    /// VENDOR DATA EXTRACTION LOGIC:
    /// Garland Tools API returns vendor data in multiple formats depending on the item:
    /// 
    /// FORMAT 1: Full vendor objects (most common for standard vendors)
    /// - item.Vendors contains list of GarlandVendor with name, location, price
    /// - Example: Material Supplier in Limsa Lominsa @ 5g
    /// - All vendor details available directly
    /// 
    /// FORMAT 2: ID-only references (special vendors like Ixali)
    /// - item.HasVendorReferences = true
    /// - item.Price set to root-level price
    /// - item.Vendors empty or missing
    /// - item.Partials contains NPC data that can be resolved
    /// 
    /// FORMAT 3: No vendor data (unavailable or unmapped)
    /// - item.HasVendorReferences = false
    /// - item.Vendors empty
    /// - Returns empty list
    /// 
    /// GIL VENDOR PRIORITIZATION:
    /// - Only gil vendors (Currency == "gil") are used for price calculations
    /// - Special currency vendors (tomestones, etc.) are tracked for display
    /// - Cheapest gil vendor price is used for VendorPrice
    /// - All vendors stored in VendorOptions for complete UI display
    /// 
    /// ALTERNATE LOCATIONS:
    /// - Some vendors (Material Supplier) appear in multiple zones
    /// - Resolved from item.Partials (NPC data with zone information)
    /// - Primary location shown first, alternates in tooltip/dropdown
    /// 
    /// ID-ONLY VENDOR RESOLUTION (Bug Fix):
    /// When vendors are listed as IDs only (not full objects), we must filter NPC partials
    /// by matching vendor IDs. The partials array may contain unrelated NPCs (quest givers, etc.)
    /// so we use item.VendorIds to extract the actual vendor IDs and only include matching NPCs.
    /// This prevents "hallucinated vendors" like Mogmul Mogbelly appearing for items he doesn't sell.
    /// 
    /// </summary>
    /// <param name="item">Garland item data from API</param>
    /// <returns>List of VendorInfo for all available vendors (both gil and special currency). 
    /// Returns empty list if no vendor data available.</returns>
    private List<VendorInfo> GetVendorOptions(GarlandItem? item)
    {
        if (item == null) return new List<VendorInfo>();

        var vendors = new List<VendorInfo>();

        // If we have full vendor objects with prices, convert them
        if (item.Vendors.Count > 0)
        {
            foreach (var garlandVendor in item.Vendors)
            {
                var vendorInfo = VendorInfo.FromGarlandVendor(garlandVendor);
                
                // Log warning if location resolution failed (still shows "Zone {id}")
                if (vendorInfo.Location.StartsWith("Zone ", StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning("[VendorInfo] Failed to resolve location for vendor '{VendorName}': RawLocation='{RawLocation}', LocationId={LocationId}, Result='{Result}'", 
                        garlandVendor.Name, garlandVendor.Location, garlandVendor.LocationId, vendorInfo.Location);
                }

                // If this vendor has a name, look up alternate locations from partials
                if (!string.IsNullOrEmpty(garlandVendor.Name) && item.Partials != null)
                {
                    var npcPartials = item.GetNpcPartialsByName(garlandVendor.Name);
                    var allLocations = npcPartials
                        .Select(npc => npc.LocationName)
                        .Where(loc => !string.IsNullOrEmpty(loc))
                        .Distinct()
                        .ToList();
                    
                    // Exclude the primary location from alternate locations
                    vendorInfo.AlternateLocations = allLocations
                        .Where(loc => loc != vendorInfo.Location)
                        .ToList();
                }

                vendors.Add(vendorInfo);
            }
        }
        // If vendors are listed as IDs only with root-level price, try to resolve via partials
        else if (item.HasVendorReferences && item.Price > 0 && item.Partials != null)
        {
            // Extract vendor IDs from VendorsRaw to filter NPC partials
            var vendorIds = item.VendorIds;
            
            // Look for NPC partials that match the vendor IDs
            // This ensures we only include actual vendors, not unrelated NPCs (quest givers, etc.)
            var vendorNpcs = item.Partials
                .Where(p => p.Type == "npc" && vendorIds.Contains(p.Id))
                .Select(p => p.GetNpcObject())
                .Where(npc => npc != null)
                .Cast<GarlandNpcPartial>()
                .ToList();

            if (vendorNpcs.Any())
            {
                // Group by vendor name to handle vendors with multiple locations
                var vendorGroups = vendorNpcs.GroupBy(npc => npc.Name);

                foreach (var group in vendorGroups)
                {
                    var npcList = group.ToList();
                    var primaryNpc = npcList.First();

                    var vendorInfo = new VendorInfo
                    {
                        Name = primaryNpc.Name,
                        Location = primaryNpc.LocationName,
                        Price = item.Price,
                        Currency = "gil",
                        AlternateLocations = npcList
                            .Select(npc => npc.LocationName)
                            .Where(loc => !string.IsNullOrEmpty(loc))
                            .Distinct()
                            .ToList()
                    };

                    vendors.Add(vendorInfo);
                }
            }
            else
            {
                // Fallback: create generic entry if no partials available
                vendors.Add(new VendorInfo
                {
                    Name = "Material Supplier",
                    Location = "Any",
                    Price = item.Price,
                    Currency = "gil"
                });
            }
        }
        else if (item.HasVendorReferences && item.Price > 0)
        {
            // Fallback when no partials available
            vendors.Add(new VendorInfo
            {
                Name = "Material Supplier",
                Location = "Any",
                Price = item.Price,
                Currency = "gil"
            });
        }

        return vendors;
    }

    /// <summary>
    /// Recursively builds a PlanNode tree representing an item and all its ingredients.
    /// 
    /// ALGORITHM:
    /// 1. Depth guard: Stops recursion at MaxDepth (20 levels) to prevent stack overflow
    /// 2. Data fetch: Retrieves item data from Garland API (with caching)
    /// 3. Craftability check: Determines if item has a recipe (Crafts or CompanyCrafts)
    /// 4. Leaf handling: Non-craftable items default to VendorBuy (if available) or MarketBuyNq
    /// 5. Recipe selection: Uses first available recipe (ordered by RecipeLevel)
    /// 6. Craft count calculation: Ceiling(Quantity / Yield) - accounts for recipe yield
    /// 7. Recursive children: Builds child nodes for each ingredient with scaled quantities
    /// 8. Smart defaults (for non-root items only):
    ///    - VendorBuy if vendor available (cheapest, most convenient)
    ///    - MarketBuyNq if low-level recipe with many ingredients (heuristic)
    ///    - Craft otherwise (default)
    /// 
    /// SMART DEFAULTS LOGIC:
    /// Root items (parent == null) always default to Craft - user wants to craft project items.
    /// For child items:
    /// - Vendor items are prioritized (free gil from vendors is cheapest)
    /// - Low-level items (RecipeLevel < 10) with many ingredients default to market buy
    /// - Everything else defaults to Craft
    /// </summary>
    /// <param name="itemId">Garland item ID</param>
    /// <param name="name">Item name (fallback if API fails)</param>
    /// <param name="quantity">Quantity needed (before yield calculation)</param>
    /// <param name="parent">Parent node (null for root items)</param>
    /// <param name="depth">Recursion depth (0 for root)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Populated PlanNode or null on error</returns>
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
    /// Accounts for recipe yield - returns cost per crafted item, not total ingredient cost.
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
            else if (child.Source == AcquisitionSource.VendorBuy && child.VendorPrice > 0)
            {
                // Fix: Use VendorPrice instead of MarketPrice for vendor purchases
                total += child.VendorPrice * child.Quantity;
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
        
        // Account for recipe yield: cost per item = total ingredient cost / yield
        if (node.Yield > 1)
        {
            return total / node.Yield;
        }
        
        return total;
    }

    #region Breadth-First Tree Building Methods

    /// <summary>
    /// Phase 1: Discover tree structure level by level.
    /// Fetches minimal data to discover ingredients, building a skeleton structure.
    /// </summary>
    private async Task<TreeDiscoveryResult> DiscoverTreeLevelsAsync(
        List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
        CancellationToken ct)
    {
        var result = new TreeDiscoveryResult();
        var currentLevel = new Queue<PendingNode>();
        var discoveredIds = new HashSet<int>();
        int depth = 0;

        // Initialize Level 0 with target items
        foreach (var (itemId, name, quantity, _) in targetItems)
        {
            if (discoveredIds.Add(itemId))
            {
                currentLevel.Enqueue(new PendingNode(itemId, name, quantity, 0, null));
            }
        }

        while (currentLevel.Count > 0 && depth < MaxDepth)
        {
            var levelItemIds = currentLevel.Select(n => n.ItemId).Distinct().ToList();
            var quantityByItemId = currentLevel
                .GroupBy(n => n.ItemId)
                .ToDictionary(g => g.Key, g => g.Sum(n => n.Quantity));

            result.Levels.Add(new LevelInfo(depth, levelItemIds, quantityByItemId));
            result.AllItemIds.UnionWith(levelItemIds);

            _logger?.LogDebug("[RecipeCalc] Discovered level {Depth}: {Count} items", depth, levelItemIds.Count);

            // Discover next level
            var nextLevel = new Queue<PendingNode>();
            var levelTasks = currentLevel.Select(async pendingNode =>
            {
                try
                {
                    var itemData = await _garlandService.GetItemAsync(pendingNode.ItemId, ct);
                    if (itemData?.Crafts?.Any() != true) return;

                    var recipe = itemData.Crafts.OrderBy(r => r.RecipeLevel).First();
                    var yield = Math.Max(1, recipe.Yield);
                    var craftCount = (int)Math.Ceiling((double)pendingNode.Quantity / yield);

                    foreach (var ingredient in recipe.Ingredients)
                    {
                        var ingredientQty = ingredient.Amount * craftCount;
                        if (discoveredIds.Add(ingredient.Id))
                        {
                            nextLevel.Enqueue(new PendingNode(
                                ingredient.Id,
                                ingredient.Name ?? $"Item_{ingredient.Id}",
                                ingredientQty,
                                depth + 1,
                                null));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[RecipeCalc] Failed to discover ingredients for item {ItemId}", pendingNode.ItemId);
                }
            }).ToList();

            await Task.WhenAll(levelTasks);
            currentLevel = nextLevel;
            depth++;
        }

        result.MaxDepth = depth;
        _logger?.LogInformation("[RecipeCalc] Tree discovery complete: {Levels} levels, {Items} unique items",
            result.Levels.Count, result.AllItemIds.Count);

        return result;
    }

    /// <summary>
    /// Phase 2: Fetch all items for a level in batches with retry logic.
    /// </summary>
    private async Task<Dictionary<int, GarlandItem>> FetchLevelBatchesAsync(
        LevelInfo level,
        int batchSize,
        int maxConcurrent,
        int timeoutSeconds,
        IProgress<TreeBuildProgress>? progress,
        CancellationToken ct)
    {
        var results = new Dictionary<int, GarlandItem>();
        var batches = level.ItemIds.Chunk(batchSize).ToList();
        var totalItems = level.ItemIds.Count;
        var fetchedCount = 0;

        _logger?.LogInformation("[RecipeCalc] Fetching level {Level}: {Items} items in {Batches} batches",
            level.Depth, totalItems, batches.Count);

        for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex].ToList();
            progress?.Report(new TreeBuildProgress
            {
                Stage = "Fetching",
                CurrentLevel = level.Depth,
                CurrentBatch = batchIndex + 1,
                TotalBatches = batches.Count,
                ItemsFetched = fetchedCount,
                TotalItems = totalItems
            });

            var batchResults = await FetchBatchWithRetryAsync(
                batch, level.Depth, batchIndex + 1, 10, 1000, 30000, timeoutSeconds, ct);

            foreach (var kvp in batchResults)
            {
                results[kvp.Key] = kvp.Value;
                _itemCache[kvp.Key] = kvp.Value;
                fetchedCount++;
            }
        }

        _logger?.LogInformation("[RecipeCalc] Level {Level} fetch complete: {Success}/{Total}",
            level.Depth, results.Count, totalItems);

        return results;
    }

    /// <summary>
    /// Fetch a single batch with exponential backoff retry.
    /// Only retries missing items on partial success.
    /// </summary>
    private async Task<Dictionary<int, GarlandItem>> FetchBatchWithRetryAsync(
        List<int> itemIds,
        int level,
        int batchNumber,
        int maxRetries,
        int baseDelayMs,
        int maxDelayMs,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var results = new Dictionary<int, GarlandItem>();
        var remainingIds = new List<int>(itemIds);
        var startTime = DateTime.UtcNow;
        var retryCount = 0;

        while (remainingIds.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            if ((DateTime.UtcNow - startTime).TotalSeconds > timeoutSeconds)
            {
                throw new TreeCalculationException(
                    $"Batch fetch timeout after {timeoutSeconds}s. Failed to fetch {remainingIds.Count} items: {string.Join(", ", remainingIds)}",
                    level, remainingIds, DateTime.UtcNow - startTime);
            }

            try
            {
                _logger?.LogDebug("[RecipeCalc] Level {Level} Batch {Batch}: Fetching {Count} items (attempt {Attempt})",
                    level, batchNumber, remainingIds.Count, retryCount + 1);

                var batchResults = await _garlandService.GetItemsAsync(remainingIds, useParallel: true, ct);

                foreach (var kvp in batchResults)
                {
                    results[kvp.Key] = kvp.Value;
                }

                var fetchedIds = batchResults.Keys.ToHashSet();
                var missingIds = remainingIds.Where(id => !fetchedIds.Contains(id)).ToList();

                if (missingIds.Count == 0)
                {
                    return results;
                }

                _logger?.LogWarning("[RecipeCalc] Level {Level} Batch {Batch}: Partial success - {Success}/{Total}, retrying {Missing}",
                    level, batchNumber, results.Count, itemIds.Count, missingIds.Count);

                remainingIds = missingIds;
                retryCount++;

                if (retryCount >= maxRetries)
                {
                    throw new TreeCalculationException(
                        $"Max retries ({maxRetries}) exceeded for batch. Failed items: {string.Join(", ", remainingIds)}",
                        level, remainingIds, DateTime.UtcNow - startTime);
                }

                var delayMs = (int)Math.Min(baseDelayMs * Math.Pow(2, retryCount - 1), maxDelayMs);
                await Task.Delay(delayMs, ct);
            }
            catch (TreeCalculationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                retryCount++;

                if (retryCount >= maxRetries)
                {
                    throw new TreeCalculationException(
                        $"Max retries ({maxRetries}) exceeded. Last error: {ex.Message}",
                        level, remainingIds, DateTime.UtcNow - startTime, ex);
                }

                var delayMs = (int)Math.Min(baseDelayMs * Math.Pow(2, retryCount - 1), maxDelayMs);
                _logger?.LogWarning(ex, "[RecipeCalc] Level {Level} Batch {Batch}: Error on attempt {Attempt}, retrying in {Delay}ms",
                    level, batchNumber, retryCount, delayMs);

                await Task.Delay(delayMs, ct);
            }
        }

        return results;
    }

    /// <summary>
    /// Phase 3: Build complete tree from cached item data.
    /// No API calls - everything comes from cache.
    /// </summary>
    private PlanNode? BuildTreeFromCache(
        int itemId,
        int quantity,
        int depth,
        bool isHqRequired,
        Dictionary<int, GarlandItem> itemCache,
        Dictionary<int, PlanNode> nodeCache)
    {
        // Prevent infinite recursion
        if (depth > MaxDepth)
        {
            _logger?.LogWarning("[RecipeCalc] Max depth reached for item {ItemId}", itemId);
            return new PlanNode
            {
                ItemId = itemId,
                Name = $"Item_{itemId} (Max Depth)",
                Quantity = quantity,
                Source = AcquisitionSource.MarketBuyNq,
                CanCraft = false
            };
        }

        // Return cached node if already built
        if (nodeCache.TryGetValue(itemId, out var cachedNode))
        {
            // Create a copy with updated quantity
            return new PlanNode
            {
                ItemId = cachedNode.ItemId,
                Name = cachedNode.Name,
                IconId = cachedNode.IconId,
                Quantity = quantity,
                Source = cachedNode.Source,
                MustBeHq = isHqRequired,
                CanBeHq = cachedNode.CanBeHq,
                CanBuyFromVendor = cachedNode.CanBuyFromVendor,
                CanCraft = cachedNode.CanCraft,
                RecipeLevel = cachedNode.RecipeLevel,
                Job = cachedNode.Job,
                Yield = cachedNode.Yield,
                VendorPrice = cachedNode.VendorPrice,
                VendorOptions = cachedNode.VendorOptions,
                Children = cachedNode.Children.Select(c => BuildTreeFromCache(
                    c.ItemId, c.Quantity, depth + 1, c.MustBeHq, itemCache, nodeCache)).ToList()
            };
        }

        if (!itemCache.TryGetValue(itemId, out var itemData))
        {
            _logger?.LogError("[RecipeCalc] Item {ItemId} not found in cache", itemId);
            return null;
        }

        var hasVendor = itemData?.HasVendorReferences == true;
        var hasCraft = itemData?.Crafts?.Any() == true;

        var node = new PlanNode
        {
            ItemId = itemId,
            Name = itemData?.Name ?? $"Item_{itemId}",
            IconId = itemData?.IconId ?? 0,
            Quantity = quantity,
            MustBeHq = isHqRequired,
            CanBeHq = DetermineCanBeHq(itemData, itemId),
            CanBuyFromVendor = hasVendor,
            CanCraft = hasCraft
        };

        if (!hasCraft)
        {
            node.Source = hasVendor ? AcquisitionSource.VendorBuy : AcquisitionSource.MarketBuyNq;
            nodeCache[itemId] = node;
            return node;
        }

        var recipe = itemData!.Crafts!.OrderBy(r => r.RecipeLevel).First();
        node.RecipeLevel = recipe.RecipeLevel;
        node.Job = JobHelper.GetJobName(recipe.JobId);
        node.Yield = Math.Max(1, recipe.Yield);

        var craftCount = (int)Math.Ceiling((double)quantity / node.Yield);

        foreach (var ingredient in recipe.Ingredients)
        {
            var childQuantity = ingredient.Amount * craftCount;
            var childNode = BuildTreeFromCache(ingredient.Id, childQuantity, depth + 1, false, itemCache, nodeCache);
            if (childNode != null)
            {
                node.Children.Add(childNode);
            }
        }

        // Apply smart defaults
        if (hasVendor)
        {
            node.Source = AcquisitionSource.VendorBuy;
        }
        else if (ShouldDefaultToBuy(node))
        {
            node.Source = AcquisitionSource.MarketBuyNq;
        }
        else
        {
            node.Source = AcquisitionSource.Craft;
        }

        nodeCache[itemId] = node;
        return node;
    }

    #endregion
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
