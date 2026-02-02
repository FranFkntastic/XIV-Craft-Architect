using System.Collections.Concurrent;
using FFXIVCraftArchitect.Models;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for calculating hierarchical crafting recipes.
/// Builds a tree of items with their ingredients, handling circular dependencies and aggregation.
/// </summary>
public class RecipeCalculationService
{
    private readonly GarlandService _garlandService;
    private readonly ILogger<RecipeCalculationService> _logger;
    
    // Cache to avoid fetching the same item multiple times during calculation
    private readonly ConcurrentDictionary<int, GarlandItem> _itemCache = new();
    
    // Track visited items to prevent infinite recursion (circular recipes)
    private readonly HashSet<int> _visitedItems = new();
    
    // Maximum recursion depth to prevent stack overflow
    private const int MaxDepth = 20;

    public RecipeCalculationService(GarlandService garlandService, ILogger<RecipeCalculationService> logger)
    {
        _garlandService = garlandService;
        _logger = logger;
    }

    /// <summary>
    /// Build a complete crafting plan from a list of target items.
    /// </summary>
    public async Task<CraftingPlan> BuildPlanAsync(
        List<(int itemId, string name, int quantity)> targetItems,
        string dataCenter,
        string world,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[RecipeCalc] Building plan for {Count} target items", targetItems.Count);
        
        var plan = new CraftingPlan
        {
            Name = $"Plan {DateTime.Now:yyyy-MM-dd HH:mm}",
            DataCenter = dataCenter,
            World = world
        };

        _visitedItems.Clear();
        _itemCache.Clear();

        foreach (var (itemId, name, quantity) in targetItems)
        {
            try
            {
                var node = await BuildNodeRecursiveAsync(itemId, name, quantity, null, 0, ct);
                if (node != null)
                {
                    plan.RootItems.Add(node);
                    _logger.LogDebug("[RecipeCalc] Added root item: {Name} x{Qty}", node.Name, node.Quantity);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RecipeCalc] Failed to build tree for item {ItemId}", itemId);
                // Add a placeholder node so user knows something went wrong
                plan.RootItems.Add(new PlanNode
                {
                    ItemId = itemId,
                    Name = $"{name} (Error: {ex.Message})",
                    Quantity = quantity,
                    IsUncraftable = true,
                    Source = AcquisitionSource.MarketBuyNq
                });
            }
        }

        _logger.LogInformation("[RecipeCalc] Plan built with {Count} root items", plan.RootItems.Count);
        return plan;
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
        // Prevent infinite recursion
        if (depth > MaxDepth)
        {
            _logger.LogWarning("[RecipeCalc] Max depth reached for item {ItemId}, treating as uncraftable", itemId);
            return new PlanNode
            {
                ItemId = itemId,
                Name = name,
                Quantity = quantity,
                IsUncraftable = true,
                Source = AcquisitionSource.MarketBuyNq,
                Parent = parent
            };
        }

        // Check for circular dependency
        if (_visitedItems.Contains(itemId) && parent != null)
        {
            _logger.LogWarning("[RecipeCalc] Circular dependency detected for item {ItemId}", itemId);
            return new PlanNode
            {
                ItemId = itemId,
                Name = $"{name} (Circular)",
                Quantity = quantity,
                IsUncraftable = true,
                Source = AcquisitionSource.MarketBuyNq,
                Parent = parent
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
                _logger.LogError(ex, "[RecipeCalc] Failed to fetch item {ItemId}", itemId);
            }
        }

        // Create the node
        var node = new PlanNode
        {
            ItemId = itemId,
            Name = itemData?.Name ?? name,
            IconId = itemData?.IconId ?? 0,
            Quantity = quantity,
            Parent = parent
        };

        // Check for both traditional crafts and company crafts
        var hasCraft = itemData?.Crafts?.Any() == true;
        var hasCompanyCraft = itemData?.CompanyCrafts?.Any() == true;
        
        if (!hasCraft && !hasCompanyCraft)
        {
            node.IsUncraftable = true;
            node.Source = AcquisitionSource.MarketBuyNq;
            _logger.LogDebug("[RecipeCalc] Item {Name} has no recipe, marked as buy", node.Name);
            return node;
        }

        // Mark as visited for circular detection
        _visitedItems.Add(itemId);

        // Handle company workshop recipes (airships, submarines, etc.)
        if (hasCompanyCraft)
        {
            return await BuildCompanyCraftNodeAsync(node, itemData!.CompanyCrafts!.First(), quantity, ct);
        }

        // Use the first available traditional recipe (usually the lowest level/main recipe)
        var recipe = itemData!.Crafts!.OrderBy(r => r.RecipeLevel).First();
        node.RecipeLevel = recipe.RecipeLevel;
        node.Job = GetJobName(recipe.JobId);
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

        // Smart default: if this item's ingredients are expensive/hard to get, default to buy
        // This can be overridden by user later
        if (ShouldDefaultToBuy(node))
        {
            node.Source = AcquisitionSource.MarketBuyNq;
        }

        return node;
    }

    /// <summary>
    /// Determine if an item should default to "buy" mode based on its ingredients.
    /// </summary>
    private bool ShouldDefaultToBuy(PlanNode node)
    {
        // If no children, it must be bought
        if (!node.Children.Any())
            return true;

        // If recipe level is very low, might be easier to just buy
        // This is a heuristic - users can change later
        if (node.RecipeLevel < 10 && node.Children.Count > 3)
            return true;

        // If the item is cheap on market and has many complex ingredients, default to buy
        // (Would need market data for this decision)
        
        return false;
    }

    /// <summary>
    /// Recalculate quantities when a root item quantity changes.
    /// </summary>
    public void RecalculateQuantities(PlanNode rootNode, int newQuantity)
    {
        if (rootNode.Quantity == 0 || newQuantity == 0)
        {
            // Handle division by zero - just scale linearly
            ScaleNodeQuantities(rootNode, newQuantity > 0 ? 100 : 0);
            return;
        }

        var ratio = (double)newQuantity / rootNode.Quantity;
        ScaleNodeQuantities(rootNode, ratio);
    }

    private void ScaleNodeQuantities(PlanNode node, double ratio)
    {
        node.Quantity = Math.Max(1, (int)(node.Quantity * ratio));
        
        // Recalculate craft count and children
        if (node.Children.Any() && node.Yield > 0)
        {
            var newCraftCount = (int)Math.Ceiling((double)node.Quantity / node.Yield);
            
            // We need to refetch recipe data to recalculate children properly
            // For now, just scale children proportionally
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
        node.RequiresHq = source == AcquisitionSource.MarketBuyHq;
        
        _logger.LogInformation("[RecipeCalc] {ItemName} set to {Source}", node.Name, source);
    }

    /// <summary>
    /// Build a node for company workshop recipes (airships, submarines).
    /// These have phases instead of simple ingredients.
    /// </summary>
    private async Task<PlanNode> BuildCompanyCraftNodeAsync(
        PlanNode node, 
        GarlandCompanyCraft companyCraft, 
        int quantity, 
        CancellationToken ct)
    {
        node.Job = "Company Workshop";
        node.RecipeLevel = 1; // Company crafts don't have traditional levels
        node.Yield = 1; // Always yields 1
        
        _logger.LogDebug("[RecipeCalc] Building company craft node for {Name} with {PhaseCount} phases", 
            node.Name, companyCraft.PhaseCount);

        // Flatten all phase ingredients into children
        // Group by phase for display purposes
        foreach (var phase in companyCraft.Phases)
        {
            // Create a phase node (not a real item, just for organization)
            var phaseNode = new PlanNode
            {
                ItemId = 0, // No real item ID
                Name = $"Phase {phase.PhaseNumber + 1}",
                Quantity = 1,
                Source = AcquisitionSource.Craft,
                Parent = node,
                Job = "Phase"
            };
            
            foreach (var item in phase.Items)
            {
                var childNode = await BuildNodeRecursiveAsync(
                    item.Id,
                    item.Name ?? $"Item_{item.Id}",
                    item.Amount * quantity, // Scale by parent quantity
                    phaseNode,
                    0, // Reset depth for ingredients
                    ct);
                
                if (childNode != null)
                {
                    phaseNode.Children.Add(childNode);
                }
            }
            
            // Only add phase node if it has children
            if (phaseNode.Children.Any())
            {
                node.Children.Add(phaseNode);
            }
        }

        return node;
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

        return System.Text.Json.JsonSerializer.Serialize(wrapper, new System.Text.Json.JsonSerializerOptions
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
            IsBuy = node.IsBuy, // Backward compatibility
            Source = node.Source,
            RequiresHq = node.RequiresHq,
            IsUncraftable = node.IsUncraftable,
            RecipeLevel = node.RecipeLevel,
            Job = node.Job,
            Yield = node.Yield,
            MarketPrice = node.MarketPrice,
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
            var wrapper = System.Text.Json.JsonSerializer.Deserialize<PlanSerializationWrapper>(json);
            if (wrapper == null) return null;

            // Build node lookup
            var nodeLookup = new Dictionary<string, PlanNode>();
            foreach (var sNode in wrapper.Nodes)
            {
                var node = new PlanNode
                {
                    ItemId = sNode.ItemId,
                    Name = sNode.Name,
                    IconId = sNode.IconId,
                    Quantity = sNode.Quantity,
                    IsUncraftable = sNode.IsUncraftable,
                    RecipeLevel = sNode.RecipeLevel,
                    Job = sNode.Job,
                    Yield = sNode.Yield,
                    MarketPrice = sNode.MarketPrice,
                    NodeId = sNode.NodeId,
                    ParentNodeId = sNode.ParentNodeId,
                    Notes = sNode.Notes
                };
                
                // Handle backward compatibility
                if (sNode.Source.HasValue)
                {
                    node.Source = sNode.Source.Value;
                }
                else if (sNode.IsBuy)
                {
                    node.Source = AcquisitionSource.MarketBuyNq;
                }
                else
                {
                    node.Source = AcquisitionSource.Craft;
                }
                
                node.RequiresHq = sNode.RequiresHq;
                
                nodeLookup[sNode.NodeId] = node;
            }

            // Link parents and children
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

            // Build the plan
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
            _logger.LogError(ex, "[RecipeCalc] Failed to deserialize plan");
            return null;
        }
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
