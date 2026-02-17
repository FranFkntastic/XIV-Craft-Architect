using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Services;

namespace FFXIV_Craft_Architect.Models;

/// <summary>
/// Serializable state for hot reload watch mode.
/// Persisted when app receives reload signal, restored on next startup.
/// Uses PlanFileData/PlanFileNode DTOs for proper serialization (same as PlanPersistenceService).
/// </summary>
public class WatchState
{

    /// <summary>
    /// The current crafting plan being worked on.
    /// Note: This is converted to/from PlanFileData for serialization.
    /// </summary>
    public CraftingPlan? CurrentPlan { get; set; }
    
    /// <summary>
    /// Timestamp when state was saved.
    /// </summary>
    public DateTimeOffset SavedAt { get; set; }
    
    /// <summary>
    /// Data center selection.
    /// </summary>
    public string? DataCenter { get; set; }
    
    /// <summary>
    /// World selection (optional).
    /// </summary>
    public string? World { get; set; }
    

    /// <summary>
    /// Gets the path to the watch state file.
    /// Located in temp directory for automatic cleanup.
    /// </summary>
    private static string StateFilePath => Path.Combine(Path.GetTempPath(), "FFXIV_Craft_Architect_watch_state.json");
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
    
    /// <summary>
    /// Save watch state to disk.
    /// Converts CraftingPlan to PlanFileData for proper serialization.
    /// </summary>
    public void Save()
    {
        try
        {
            SavedAt = DateTimeOffset.UtcNow;
            
            // Convert to serializable DTO format (same as PlanPersistenceService)
            var stateData = new WatchStateData
            {
                SavedAt = SavedAt,
                DataCenter = DataCenter,
                World = World,
                PlanData = CurrentPlan != null ? ConvertPlanToData(CurrentPlan) : null
            };
            
            var json = JsonSerializer.Serialize(stateData, JsonOptions);
            File.WriteAllText(StateFilePath, json);
        }
        catch (Exception ex)
        {
            App.LogMessage($"[WatchState] Failed to save watch state to {StateFilePath}: {ex.GetType().Name}: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Load watch state from disk if it exists and is recent.
    /// Converts PlanFileData back to CraftingPlan after deserialization.
    /// </summary>
    /// <param name="maxAge">Maximum age of state to consider valid (default: 5 minutes)</param>
    /// <returns>The loaded state, or null if not found or too old.</returns>
    public static WatchState? Load(TimeSpan? maxAge = null)
    {
        maxAge ??= TimeSpan.FromMinutes(5);
        
        if (!File.Exists(StateFilePath))
            return null;
        
        try
        {
            var json = File.ReadAllText(StateFilePath);
            var stateData = JsonSerializer.Deserialize<WatchStateData>(json, JsonOptions);
            
            if (stateData == null)
                return null;
            
            // Check if state is too old
            if (DateTimeOffset.UtcNow - stateData.SavedAt > maxAge)
            {
                // Clean up stale file
                try { File.Delete(StateFilePath); } 
                catch (Exception ex) 
                { 
                    App.LogMessage($"[WatchState] Failed to delete stale watch state file at {StateFilePath}: {ex.GetType().Name}: {ex.Message}");
                }
                return null;
            }
            
            // Convert back to WatchState with proper CraftingPlan
            var state = new WatchState
            {
                SavedAt = stateData.SavedAt,
                DataCenter = stateData.DataCenter,
                World = stateData.World,
                CurrentPlan = stateData.PlanData != null ? ConvertDataToPlan(stateData.PlanData) : null
            };
            
            return state;
        }
        catch (Exception ex)
        {
            App.LogMessage($"[WatchState] Failed to load watch state from {StateFilePath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Clear the saved watch state file.
    /// </summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(StateFilePath))
                File.Delete(StateFilePath);
        }
        catch (Exception ex)
        {
            App.LogMessage($"[WatchState] Failed to clear watch state file at {StateFilePath}: {ex.GetType().Name}: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Clears the saved watch state file.
    /// </summary>
    
    #region Serialization Helpers
    
    /// <summary>
    /// Converts a CraftingPlan to PlanFileData for serialization.
    /// </summary>
    private static PlanFileData ConvertPlanToData(CraftingPlan plan)
    {
        return new PlanFileData
        {
            Version = 2,
            Id = plan.Id,
            Name = plan.Name,
            CreatedAt = plan.CreatedAt,
            ModifiedAt = plan.ModifiedAt,
            DataCenter = plan.DataCenter,
            World = plan.World,
            RootItems = plan.RootItems.Select(ConvertNodeToData).ToList()
        };
    }
    
    /// <summary>
    /// Converts a PlanNode to PlanFileNode for serialization.
    /// </summary>
    private static PlanFileNode ConvertNodeToData(PlanNode node)
    {
        return new PlanFileNode
        {
            ItemId = node.ItemId,
            Name = node.Name,
            IconId = node.IconId,
            Quantity = node.Quantity,
            IsBuy = node.Source == AcquisitionSource.MarketBuyNq || node.Source == AcquisitionSource.MarketBuyHq,
            Source = node.Source,
            MustBeHq = node.MustBeHq,
            CanBeHq = node.CanBeHq,
            CanBuyFromVendor = node.CanBuyFromVendor,
            CanCraft = node.CanCraft,
            RecipeLevel = node.RecipeLevel,
            Job = node.Job,
            Yield = node.Yield,
            MarketPrice = node.MarketPrice,
            HqMarketPrice = node.HqMarketPrice,
            VendorPrice = node.VendorPrice,
            PriceSource = node.PriceSource,
            PriceSourceDetails = node.PriceSourceDetails,
            Notes = node.Notes,
            Children = node.Children.Select(ConvertNodeToData).ToList()
        };
    }
    
    /// <summary>
    /// Converts PlanFileData back to CraftingPlan.
    /// Re-links parent references after deserialization.
    /// </summary>
    private static CraftingPlan ConvertDataToPlan(PlanFileData data)
    {
        var plan = new CraftingPlan
        {
            Id = data.Id,
            Name = data.Name,
            CreatedAt = data.CreatedAt,
            ModifiedAt = data.ModifiedAt,
            DataCenter = data.DataCenter,
            World = data.World,
            RootItems = data.RootItems?.Select(ConvertDataToNode).ToList() ?? new List<PlanNode>()
        };
        
        // Re-link parent references
        foreach (var root in plan.RootItems)
        {
            LinkParents(root, null);
        }
        
        return plan;
    }
    
    /// <summary>
    /// Converts PlanFileNode back to PlanNode.
    /// </summary>
    private static PlanNode ConvertDataToNode(PlanFileNode fileNode)
    {
        var node = new PlanNode
        {
            ItemId = fileNode.ItemId,
            Name = fileNode.Name ?? string.Empty,
            IconId = fileNode.IconId,
            Quantity = fileNode.Quantity,
            RecipeLevel = fileNode.RecipeLevel,
            Job = fileNode.Job ?? string.Empty,
            Yield = fileNode.Yield,
            MarketPrice = fileNode.MarketPrice,
            HqMarketPrice = fileNode.HqMarketPrice,
            VendorPrice = fileNode.VendorPrice,
            PriceSource = fileNode.PriceSource,
            PriceSourceDetails = fileNode.PriceSourceDetails ?? string.Empty,
            Notes = fileNode.Notes,
            Children = fileNode.Children?.Select(ConvertDataToNode).ToList() ?? new List<PlanNode>()
        };
        
        // Handle backward compatibility: old plans used IsBuy, new plans use Source
        if (fileNode.Source.HasValue)
        {
            node.Source = fileNode.Source.Value;
        }
        else if (fileNode.IsBuy)
        {
            node.Source = AcquisitionSource.MarketBuyNq;
        }
        else
        {
            node.Source = AcquisitionSource.Craft;
        }
        
        node.MustBeHq = fileNode.MustBeHq;
        node.CanBeHq = fileNode.CanBeHq;
        node.CanBuyFromVendor = fileNode.CanBuyFromVendor;
        node.CanCraft = fileNode.CanCraft;
        
        return node;
    }
    
    /// <summary>
    /// Re-links parent references in the node tree after deserialization.
    /// </summary>
    private static void LinkParents(PlanNode node, PlanNode? parent)
    {
        node.Parent = parent;
        foreach (var child in node.Children)
        {
            LinkParents(child, node);
        }
    }
    
    #endregion
}

/// <summary>
/// DTO for serializing watch state to JSON.
/// Uses PlanFileData for the plan to ensure proper serialization.
/// </summary>
public class WatchStateData
{
    public DateTimeOffset SavedAt { get; set; }
    public string? DataCenter { get; set; }
    public string? World { get; set; }
    public PlanFileData? PlanData { get; set; }
}
