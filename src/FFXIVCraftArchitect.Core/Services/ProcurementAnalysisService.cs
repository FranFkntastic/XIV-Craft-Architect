using FFXIVCraftArchitect.Core.Models;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Core.Services;

/// <summary>
/// Service for analyzing procurement options for a crafting plan.
/// Determines optimal acquisition strategy (craft vs buy) for each item
/// and calculates the best worlds to purchase from.
/// </summary>
public class ProcurementAnalysisService
{
    private readonly UniversalisService _universalisService;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly ILogger<ProcurementAnalysisService>? _logger;

    public ProcurementAnalysisService(
        UniversalisService universalisService,
        MarketShoppingService marketShoppingService,
        ILogger<ProcurementAnalysisService>? logger = null)
    {
        _universalisService = universalisService;
        _marketShoppingService = marketShoppingService;
        _logger = logger;
    }

    /// <summary>
    /// Analyze procurement options for a complete crafting plan.
    /// Returns detailed recommendations for each material.
    /// </summary>
    public async Task<ProcurementAnalysis> AnalyzePlanAsync(
        CraftingPlan plan,
        string dataCenter,
        RecommendationMode mode = RecommendationMode.MinimizeTotalCost,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        _logger?.LogInformation("[Procurement] Starting analysis for plan with {Count} root items", 
            plan.RootItems.Count);
        
        var analysis = new ProcurementAnalysis
        {
            DataCenter = dataCenter,
            AnalysisMode = mode,
            AnalyzedAt = DateTime.UtcNow
        };

        // Step 1: Aggregate all materials needed (flatten the recipe tree)
        progress?.Report("Aggregating materials...");
        var allMaterials = AggregateMaterials(plan.RootItems);
        analysis.TotalMaterials = allMaterials.Count;
        
        _logger?.LogInformation("[Procurement] Aggregated {Count} unique materials", allMaterials.Count);

        // Step 2: Fetch market data for all materials
        progress?.Report("Fetching market data...");
        var marketData = await FetchMarketDataAsync(allMaterials, dataCenter, progress, ct);
        
        // Step 3: Analyze each material's procurement options
        progress?.Report("Analyzing procurement options...");
        foreach (var material in allMaterials)
        {
            ct.ThrowIfCancellationRequested();
            
            var itemAnalysis = AnalyzeItemProcurement(material, marketData, plan, mode);
            analysis.ItemAnalyses.Add(itemAnalysis);
            
            progress?.Report($"Analyzed {material.Name}");
        }

        // Step 4: Calculate totals
        CalculateTotals(analysis);
        
        _logger?.LogInformation("[Procurement] Analysis complete. Optimal cost: {Cost:F0}g", 
            analysis.OptimalTotalCost);
        
        return analysis;
    }

    /// <summary>
    /// Aggregate all materials from the recipe tree, including sub-craftables.
    /// </summary>
    private List<MaterialAggregate> AggregateMaterials(List<PlanNode> rootItems)
    {
        var materials = new Dictionary<int, MaterialAggregate>();
        
        foreach (var root in rootItems)
        {
            AggregateNodeMaterials(root, materials);
        }
        
        return materials.Values.ToList();
    }

    private void AggregateNodeMaterials(PlanNode node, Dictionary<int, MaterialAggregate> materials)
    {
        // If this node has children (craftable), recursively aggregate them
        if (node.Children?.Any() == true)
        {
            foreach (var child in node.Children)
            {
                AggregateNodeMaterials(child, materials);
            }
        }
        else
        {
            // Leaf node - add to materials list
            if (!materials.TryGetValue(node.ItemId, out var aggregate))
            {
                aggregate = new MaterialAggregate
                {
                    ItemId = node.ItemId,
                    Name = node.Name,
                    IconId = node.IconId,
                    TotalQuantity = 0,
                    UnitPrice = 0
                };
                materials[node.ItemId] = aggregate;
            }
            
            aggregate.TotalQuantity += node.Quantity;
        }
    }

    /// <summary>
    /// Fetch market data for all materials in bulk.
    /// </summary>
    private async Task<Dictionary<int, UniversalisResponse>> FetchMarketDataAsync(
        List<MaterialAggregate> materials,
        string dataCenter,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var results = new Dictionary<int, UniversalisResponse>();
        var itemIds = materials.Select(m => m.ItemId).ToList();
        
        try
        {
            progress?.Report($"Fetching prices for {itemIds.Count} items...");
            var bulkData = await _universalisService.GetMarketDataBulkAsync(dataCenter, itemIds);
            
            foreach (var kvp in bulkData)
            {
                results[kvp.Key] = kvp.Value;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Procurement] Failed to fetch bulk market data");
            
            // Fallback: fetch individually
            foreach (var material in materials)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var data = await _universalisService.GetMarketDataAsync(dataCenter, material.ItemId, ct: ct);
                    if (data != null)
                    {
                        results[material.ItemId] = data;
                    }
                }
                catch (Exception itemEx)
                {
                    _logger?.LogWarning(itemEx, "[Procurement] Failed to fetch data for {ItemName}", material.Name);
                }
            }
        }
        
        return results;
    }

    /// <summary>
    /// Analyze procurement options for a single item.
    /// </summary>
    private ItemProcurementAnalysis AnalyzeItemProcurement(
        MaterialAggregate material,
        Dictionary<int, UniversalisResponse> marketData,
        CraftingPlan plan,
        RecommendationMode mode)
    {
        var analysis = new ItemProcurementAnalysis
        {
            ItemId = material.ItemId,
            Name = material.Name,
            IconId = material.IconId,
            QuantityNeeded = material.TotalQuantity
        };

        // Find the corresponding node in the plan to check if craftable
        var node = FindNodeInPlan(plan.RootItems, material.ItemId);
        analysis.IsCraftable = node?.Children?.Any() == true;
        
        // Get market data for this item
        if (marketData.TryGetValue(material.ItemId, out var market))
        {
            analysis.MarketPriceNq = (decimal)market.AveragePrice;
            analysis.MarketPriceHq = (decimal)market.AveragePriceHq;
            analysis.HasMarketData = market.Listings.Count > 0;
            
            if (analysis.HasMarketData)
            {
                // Calculate best world to buy from
                var worldAnalysis = CalculateBestWorldPurchase(material, market, mode);
                analysis.BestWorldPurchase = worldAnalysis;
                analysis.TotalMarketCost = worldAnalysis?.TotalCost ?? 
                    (analysis.MarketPriceNq * material.TotalQuantity);
            }
        }

        // If craftable, calculate craft cost
        if (analysis.IsCraftable && node != null)
        {
            analysis.CraftCost = CalculateCraftCost(node, marketData);
            analysis.CraftComponents = GetCraftComponents(node);
        }

        // Determine recommendation
        analysis.Recommendation = DetermineRecommendation(analysis, mode);
        
        return analysis;
    }

    private PlanNode? FindNodeInPlan(List<PlanNode> nodes, int itemId)
    {
        foreach (var node in nodes)
        {
            if (node.ItemId == itemId)
                return node;
            
            if (node.Children?.Any() == true)
            {
                var found = FindNodeInPlan(node.Children.ToList(), itemId);
                if (found != null)
                    return found;
            }
        }
        return null;
    }

    private decimal CalculateCraftCost(PlanNode node, Dictionary<int, UniversalisResponse> marketData)
    {
        if (node.Children?.Any() != true)
            return 0;

        decimal totalCost = 0;
        foreach (var child in node.Children)
        {
            // Cost is the market price of each component
            if (marketData.TryGetValue(child.ItemId, out var market))
            {
                totalCost += (decimal)market.AveragePrice * child.Quantity;
            }
        }
        return totalCost;
    }

    private List<CraftComponent> GetCraftComponents(PlanNode node)
    {
        if (node.Children?.Any() != true)
            return new List<CraftComponent>();

        return node.Children.Select(child => new CraftComponent
        {
            ItemId = child.ItemId,
            Name = child.Name,
            IconId = child.IconId,
            Quantity = child.Quantity
        }).ToList();
    }

    private WorldPurchaseAnalysis? CalculateBestWorldPurchase(
        MaterialAggregate material,
        UniversalisResponse marketData,
        RecommendationMode mode)
    {
        var worldGroups = marketData.Listings
            .GroupBy(l => l.WorldName)
            .ToList();

        WorldPurchaseAnalysis? best = null;
        
        foreach (var worldGroup in worldGroups)
        {
            var worldName = worldGroup.Key;
            var listings = worldGroup.OrderBy(l => l.PricePerUnit).ToList();
            
            int remainingQty = material.TotalQuantity;
            decimal totalCost = 0;
            int listingsNeeded = 0;
            
            foreach (var listing in listings)
            {
                if (remainingQty <= 0) break;
                
                int qtyToBuy = Math.Min(remainingQty, listing.Quantity);
                totalCost += qtyToBuy * (decimal)listing.PricePerUnit;
                remainingQty -= qtyToBuy;
                listingsNeeded++;
            }
            
            if (remainingQty > 0) continue; // Can't fulfill quantity on this world
            
            var analysis = new WorldPurchaseAnalysis
            {
                WorldName = worldName,
                TotalCost = totalCost,
                UnitPrice = totalCost / material.TotalQuantity,
                ListingsNeeded = listingsNeeded
            };
            
            // Compare based on mode
            bool isBetter = mode switch
            {
                RecommendationMode.MinimizeTotalCost => best == null || totalCost < best.TotalCost,
                RecommendationMode.BestUnitPrice => best == null || analysis.UnitPrice < best.UnitPrice,
                _ => best == null || totalCost < best.TotalCost
            };
            
            if (isBetter)
            {
                best = analysis;
            }
        }
        
        return best;
    }

    private ProcurementRecommendation DetermineRecommendation(ItemProcurementAnalysis analysis, RecommendationMode mode)
    {
        // If not craftable, must buy
        if (!analysis.IsCraftable)
        {
            return analysis.HasMarketData 
                ? ProcurementRecommendation.BuyFromMarket 
                : ProcurementRecommendation.NoDataAvailable;
        }

        // If no market data, must craft
        if (!analysis.HasMarketData)
        {
            return ProcurementRecommendation.Craft;
        }

        // Compare craft cost vs buy cost
        var craftCost = analysis.CraftCost;
        var buyCost = analysis.TotalMarketCost;

        // If craft cost is significantly lower (10%+ savings), recommend craft
        if (craftCost > 0 && craftCost < buyCost * 0.9m)
        {
            return ProcurementRecommendation.Craft;
        }

        // Otherwise recommend buy (faster, less hassle)
        return ProcurementRecommendation.BuyFromMarket;
    }

    private void CalculateTotals(ProcurementAnalysis analysis)
    {
        analysis.TotalCraftCost = analysis.ItemAnalyses
            .Where(i => i.Recommendation == ProcurementRecommendation.Craft)
            .Sum(i => i.CraftCost);

        analysis.TotalPurchaseCost = analysis.ItemAnalyses
            .Where(i => i.Recommendation == ProcurementRecommendation.BuyFromMarket)
            .Sum(i => i.TotalMarketCost);

        analysis.OptimalTotalCost = analysis.TotalCraftCost + analysis.TotalPurchaseCost;

        analysis.ItemsToCraft = analysis.ItemAnalyses
            .Count(i => i.Recommendation == ProcurementRecommendation.Craft);

        analysis.ItemsToBuy = analysis.ItemAnalyses
            .Count(i => i.Recommendation == ProcurementRecommendation.BuyFromMarket);
    }
}

/// <summary>
/// Complete procurement analysis for a crafting plan.
/// </summary>
public class ProcurementAnalysis
{
    public string DataCenter { get; set; } = string.Empty;
    public RecommendationMode AnalysisMode { get; set; }
    public DateTime AnalyzedAt { get; set; }
    
    public List<ItemProcurementAnalysis> ItemAnalyses { get; set; } = new();
    
    public int TotalMaterials { get; set; }
    public int ItemsToCraft { get; set; }
    public int ItemsToBuy { get; set; }
    
    public decimal TotalCraftCost { get; set; }
    public decimal TotalPurchaseCost { get; set; }
    public decimal OptimalTotalCost { get; set; }
}

/// <summary>
/// Procurement analysis for a single item.
/// </summary>
public class ItemProcurementAnalysis
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    public int QuantityNeeded { get; set; }
    
    public bool IsCraftable { get; set; }
    public bool HasMarketData { get; set; }
    
    public decimal MarketPriceNq { get; set; }
    public decimal MarketPriceHq { get; set; }
    public decimal TotalMarketCost { get; set; }
    
    public decimal CraftCost { get; set; }
    public List<CraftComponent> CraftComponents { get; set; } = new();
    
    public WorldPurchaseAnalysis? BestWorldPurchase { get; set; }
    public ProcurementRecommendation Recommendation { get; set; }
}

/// <summary>
/// Analysis of purchasing from a specific world.
/// </summary>
public class WorldPurchaseAnalysis
{
    public string WorldName { get; set; } = string.Empty;
    public decimal TotalCost { get; set; }
    public decimal UnitPrice { get; set; }
    public int ListingsNeeded { get; set; }
}

/// <summary>
/// Component needed to craft an item.
/// </summary>
public class CraftComponent
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    public int Quantity { get; set; }
}

public enum ProcurementRecommendation
{
    BuyFromMarket,
    Craft,
    NoDataAvailable
}
