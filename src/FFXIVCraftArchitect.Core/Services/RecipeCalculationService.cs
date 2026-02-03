using System.Collections.Concurrent;
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
                var visitedItems = new HashSet<int>();
                var node = await BuildNodeRecursiveAsync(itemId, name, quantity, null, 0, visitedItems, ct);
                if (node != null)
                {
                    node.MustBeHq = isHqRequired;
                    plan.RootItems.Add(node);
                    _logger?.LogDebug("[RecipeCalc] Added root item: {Name} x{Qty} (HQ: {Hq})", node.Name, node.Quantity, isHqRequired);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[RecipeCalc] Failed to build tree for item {ItemId}", itemId);
                plan.RootItems.Add(new PlanNode
                {
                    ItemId = itemId,
                    Name = $"{name} (Error: {ex.Message})",
                    Quantity = quantity,
                    IsUncraftable = true,
                    Source = AcquisitionSource.MarketBuyNq,
                    MustBeHq = isHqRequired
                });
            }
        }

        _logger?.LogInformation("[RecipeCalc] Plan built with {Count} root items", plan.RootItems.Count);
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
        HashSet<int> visitedItems,
        CancellationToken ct)
    {
        if (depth > MaxDepth)
        {
            _logger?.LogWarning("[RecipeCalc] Max depth reached for item {ItemId}, treating as uncraftable", itemId);
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

        if (visitedItems.Contains(itemId) && parent != null)
        {
            _logger?.LogWarning("[RecipeCalc] Circular dependency detected for item {ItemId}", itemId);
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
                _logger?.LogError(ex, "[RecipeCalc] Failed to fetch item {ItemId}", itemId);
            }
        }

        // Determine if item can be HQ
        var canBeHq = DetermineCanBeHq(itemData, itemId);
        
        var node = new PlanNode
        {
            ItemId = itemId,
            Name = itemData?.Name ?? name,
            IconId = itemData?.IconId ?? 0,
            Quantity = quantity,
            Parent = parent,
            CanBeHq = canBeHq
        };

        var hasCraft = itemData?.Crafts?.Any() == true;
        var hasCompanyCraft = itemData?.CompanyCrafts?.Any() == true;
        
        if (!hasCraft && !hasCompanyCraft)
        {
            node.IsUncraftable = true;
            node.Source = AcquisitionSource.MarketBuyNq;
            _logger?.LogDebug("[RecipeCalc] Item {Name} has no recipe, marked as buy", node.Name);
            return node;
        }

        visitedItems.Add(itemId);

        if (hasCompanyCraft)
        {
            return await BuildCompanyCraftNodeAsync(node, itemData!.CompanyCrafts!.First(), quantity, visitedItems, ct);
        }

        var recipe = itemData!.Crafts!.OrderBy(r => r.RecipeLevel).First();
        node.RecipeLevel = recipe.RecipeLevel;
        node.Job = JobHelper.GetJobName(recipe.JobId);
        node.Yield = Math.Max(1, recipe.Yield);

        var craftCount = (int)Math.Ceiling((double)quantity / node.Yield);

        foreach (var ingredient in recipe.Ingredients)
        {
            var ingredientQuantity = ingredient.Amount * craftCount;
            var childNode = await BuildNodeRecursiveAsync(
                ingredient.Id, 
                ingredient.Name ?? $"Item_{ingredient.Id}", 
                ingredientQuantity, 
                node, 
                depth + 1, 
                visitedItems,
                ct);
            
            if (childNode != null)
            {
                node.Children.Add(childNode);
            }
        }

        if (ShouldDefaultToBuy(node))
        {
            node.Source = AcquisitionSource.MarketBuyNq;
        }

        return node;
    }

    private bool ShouldDefaultToBuy(PlanNode node)
    {
        if (!node.Children.Any())
            return true;

        if (node.RecipeLevel < 10 && node.Children.Count > 3)
            return true;

        return false;
    }

    private async Task<PlanNode> BuildCompanyCraftNodeAsync(
        PlanNode node, 
        GarlandCompanyCraft companyCraft, 
        int quantity,
        HashSet<int> visitedItems,
        CancellationToken ct)
    {
        node.Job = "Company Workshop";
        node.RecipeLevel = 1;
        node.Yield = 1;
        
        _logger?.LogDebug("[RecipeCalc] Building company craft node for {Name} with {PhaseCount} phases", 
            node.Name, companyCraft.PhaseCount);

        foreach (var phase in companyCraft.Phases)
        {
            var phaseNode = new PlanNode
            {
                ItemId = 0,
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
                    item.Amount * quantity,
                    phaseNode,
                    0,
                    visitedItems,
                    ct);
                
                if (childNode != null)
                {
                    phaseNode.Children.Add(childNode);
                }
            }
            
            if (phaseNode.Children.Any())
            {
                node.Children.Add(phaseNode);
            }
        }

        return node;
    }

    private static bool DetermineCanBeHq(GarlandItem? itemData, int itemId)
    {
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
            IsBuy = node.IsBuy,
            Source = node.Source,
            RequiresHq = node.RequiresHq,
            MustBeHq = node.MustBeHq,
            CanBeHq = node.CanBeHq,
            IsUncraftable = node.IsUncraftable,
            RecipeLevel = node.RecipeLevel,
            Job = node.Job,
            Yield = node.Yield,
            MarketPrice = node.MarketPrice,
            HqMarketPrice = node.HqMarketPrice,
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
                node.MustBeHq = sNode.MustBeHq;
                node.CanBeHq = sNode.CanBeHq;
                node.HqMarketPrice = sNode.HqMarketPrice;
                
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
}
