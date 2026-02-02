using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFXIVCraftArchitect.Models;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for saving and loading crafting plans to/from disk.
/// Uses simple JSON serialization for maximum compatibility.
/// </summary>
public class PlanPersistenceService
{
    private readonly ILogger<PlanPersistenceService> _logger;
    private readonly string _plansDirectory;
    private readonly JsonSerializerOptions _jsonOptions;

    public PlanPersistenceService(ILogger<PlanPersistenceService> logger)
    {
        _logger = logger;
        _plansDirectory = Path.Combine(AppContext.BaseDirectory, "Plans");
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        
        // Ensure directory exists
        EnsureDirectoryExists();
    }

    private void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_plansDirectory))
            {
                Directory.CreateDirectory(_plansDirectory);
                _logger.LogInformation("[PlanPersistence] Created plans directory: {Path}", _plansDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlanPersistence] Failed to create plans directory: {Path}", _plansDirectory);
        }
    }

    /// <summary>
    /// Save a plan to disk.
    /// </summary>
    /// <param name="plan">The plan to save.</param>
    /// <param name="customName">Optional custom name for the plan.</param>
    /// <param name="overwritePath">Optional path to an existing plan file to overwrite.</param>
    public async Task<bool> SavePlanAsync(CraftingPlan plan, string? customName = null, string? overwritePath = null)
    {
        try
        {
            EnsureDirectoryExists();
            
            plan.MarkModified();
            
            string filePath;
            string fileName;
            
            if (!string.IsNullOrEmpty(overwritePath) && File.Exists(overwritePath))
            {
                // Overwrite existing file
                filePath = overwritePath;
                fileName = Path.GetFileName(filePath);
                _logger.LogInformation("[PlanPersistence] Overwriting plan at {FilePath}", filePath);
            }
            else
            {
                // Generate filename from plan name or custom name
                var baseName = !string.IsNullOrWhiteSpace(customName) 
                    ? customName 
                    : plan.Name;
                
                // Sanitize filename
                var safeName = string.Concat(baseName.Split(Path.GetInvalidFileNameChars()))
                    .Replace(" ", "_")
                    .Replace(".", "_");
                
                // Use name only (no timestamp) to allow natural overwriting
                fileName = $"{safeName}.json";
                filePath = Path.Combine(_plansDirectory, fileName);
                
                // If file exists, we'll overwrite it (user has already confirmed)
                _logger.LogInformation("[PlanPersistence] Saving new plan '{PlanName}' ({FileName})", plan.Name, fileName);
            }

            // Create serializable data
            var data = new PlanFileData
            {
                Version = 1,
                Id = plan.Id,
                Name = !string.IsNullOrWhiteSpace(customName) ? customName : plan.Name,
                CreatedAt = plan.CreatedAt,
                ModifiedAt = plan.ModifiedAt,
                DataCenter = plan.DataCenter,
                World = plan.World,
                RootItems = plan.RootItems.Select(ConvertToFileNode).ToList(),
                MarketPlans = plan.SavedMarketPlans?.Select(ConvertToMarketPlanData).ToList() ?? new List<MarketShoppingPlanData>()
            };

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
            
            _logger.LogInformation("[PlanPersistence] Saved plan '{PlanName}' ({FileName})", data.Name, fileName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlanPersistence] Failed to save plan '{PlanName}'", plan.Name);
            return false;
        }
    }

    /// <summary>
    /// Load a plan from disk.
    /// </summary>
    public async Task<CraftingPlan?> LoadPlanAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("[PlanPersistence] Plan file not found: {Path}", filePath);
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath);
            var data = JsonSerializer.Deserialize<PlanFileData>(json, _jsonOptions);
            
            if (data == null)
            {
                _logger.LogWarning("[PlanPersistence] Failed to deserialize plan: {Path}", filePath);
                return null;
            }

            // Convert back to CraftingPlan
            var plan = new CraftingPlan
            {
                Id = data.Id,
                Name = data.Name,
                CreatedAt = data.CreatedAt,
                ModifiedAt = data.ModifiedAt,
                DataCenter = data.DataCenter,
                World = data.World,
                RootItems = data.RootItems?.Select(ConvertFromFileNode).ToList() ?? new List<PlanNode>(),
                SavedMarketPlans = data.MarketPlans?.Select(ConvertFromMarketPlanData).ToList() ?? new List<DetailedShoppingPlan>()
            };

            // Re-link parent references
            foreach (var root in plan.RootItems)
            {
                LinkParents(root, null);
            }

            _logger.LogInformation("[PlanPersistence] Loaded plan '{PlanName}' from {Path}", plan.Name, filePath);
            return plan;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlanPersistence] Failed to load plan from {Path}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Get a list of all saved plans.
    /// </summary>
    public List<PlanInfo> ListSavedPlans()
    {
        var plans = new List<PlanInfo>();
        
        try
        {
            EnsureDirectoryExists();
            
            if (!Directory.Exists(_plansDirectory))
                return plans;

            foreach (var file in Directory.GetFiles(_plansDirectory, "*.json").OrderByDescending(f => File.GetLastWriteTime(f)))
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    
                    // Try to read just the header to get metadata
                    var json = File.ReadAllText(file);
                    var data = JsonSerializer.Deserialize<PlanFileData>(json, _jsonOptions);
                    
                    if (data != null)
                    {
                        plans.Add(new PlanInfo
                        {
                            Id = data.Id,
                            Name = data.Name,
                            FilePath = file,
                            CreatedAt = data.CreatedAt,
                            ModifiedAt = fileInfo.LastWriteTimeUtc,
                            ItemCount = data.RootItems?.Count ?? 0
                        });
                    }
                    else
                    {
                        // If we can't parse it, still show it with filename
                        plans.Add(new PlanInfo
                        {
                            Id = Guid.NewGuid(),
                            Name = Path.GetFileNameWithoutExtension(file),
                            FilePath = file,
                            CreatedAt = fileInfo.CreationTimeUtc,
                            ModifiedAt = fileInfo.LastWriteTimeUtc,
                            ItemCount = 0
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PlanPersistence] Failed to read plan info from {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlanPersistence] Failed to list saved plans");
        }

        return plans.OrderByDescending(p => p.ModifiedAt).ToList();
    }

    /// <summary>
    /// Delete a saved plan.
    /// </summary>
    public bool DeletePlan(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("[PlanPersistence] Deleted plan: {Path}", filePath);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlanPersistence] Failed to delete plan: {Path}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Export a plan to a specific location (for sharing).
    /// </summary>
    public async Task<bool> ExportPlanAsync(CraftingPlan plan, string filePath)
    {
        try
        {
            var data = new PlanFileData
            {
                Version = 1,
                Id = plan.Id,
                Name = plan.Name,
                CreatedAt = plan.CreatedAt,
                ModifiedAt = plan.ModifiedAt,
                DataCenter = plan.DataCenter,
                World = plan.World,
                RootItems = plan.RootItems.Select(ConvertToFileNode).ToList(),
                MarketPlans = plan.SavedMarketPlans?.Select(ConvertToMarketPlanData).ToList() ?? new List<MarketShoppingPlanData>()
            };

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
            
            _logger.LogInformation("[PlanPersistence] Exported plan '{PlanName}' to {Path}", plan.Name, filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlanPersistence] Failed to export plan '{PlanName}'", plan.Name);
            return false;
        }
    }

    /// <summary>
    /// Import a plan from a specific location.
    /// </summary>
    public async Task<CraftingPlan?> ImportPlanAsync(string filePath)
    {
        var plan = await LoadPlanAsync(filePath);
        
        if (plan != null)
        {
            // Generate new ID to avoid conflicts
            plan.Id = Guid.NewGuid();
            plan.Name = $"{plan.Name} (Imported)";
            plan.CreatedAt = DateTime.UtcNow;
            plan.ModifiedAt = DateTime.UtcNow;
            
            // Save to default location
            await SavePlanAsync(plan);
        }
        
        return plan;
    }

    /// <summary>
    /// Get the default directory for saving plans.
    /// </summary>
    public string GetPlansDirectory() => _plansDirectory;

    #region Helper Methods

    private PlanFileNode ConvertToFileNode(PlanNode node)
    {
        return new PlanFileNode
        {
            ItemId = node.ItemId,
            Name = node.Name,
            IconId = node.IconId,
            Quantity = node.Quantity,
            IsBuy = node.IsBuy,  // For backward compatibility
            Source = node.Source,
            RequiresHq = node.RequiresHq,
            IsUncraftable = node.IsUncraftable,
            RecipeLevel = node.RecipeLevel,
            Job = node.Job,
            Yield = node.Yield,
            MarketPrice = node.MarketPrice,
            PriceSource = node.PriceSource,
            PriceSourceDetails = node.PriceSourceDetails,
            Notes = node.Notes,
            Children = node.Children.Select(ConvertToFileNode).ToList()
        };
    }

    private PlanNode ConvertFromFileNode(PlanFileNode fileNode)
    {
        var node = new PlanNode
        {
            ItemId = fileNode.ItemId,
            Name = fileNode.Name ?? string.Empty,
            IconId = fileNode.IconId,
            Quantity = fileNode.Quantity,
            IsUncraftable = fileNode.IsUncraftable,
            RecipeLevel = fileNode.RecipeLevel,
            Job = fileNode.Job ?? string.Empty,
            Yield = fileNode.Yield,
            MarketPrice = fileNode.MarketPrice,
            PriceSource = fileNode.PriceSource,
            PriceSourceDetails = fileNode.PriceSourceDetails ?? string.Empty,
            Notes = fileNode.Notes,
            Children = fileNode.Children?.Select(ConvertFromFileNode).ToList() ?? new List<PlanNode>()
        };
        
        // Handle backward compatibility: old plans used IsBuy, new plans use Source
        if (fileNode.Source.HasValue)
        {
            node.Source = fileNode.Source.Value;
        }
        else if (fileNode.IsBuy)
        {
            // Legacy: IsBuy=true meant market purchase (default to NQ)
            node.Source = AcquisitionSource.MarketBuyNq;
        }
        else
        {
            node.Source = AcquisitionSource.Craft;
        }
        
        node.RequiresHq = fileNode.RequiresHq;
        
        return node;
    }

    private void LinkParents(PlanNode node, PlanNode? parent)
    {
        node.Parent = parent;
        foreach (var child in node.Children)
        {
            LinkParents(child, node);
        }
    }

    #region Market Plan Conversion

    private MarketShoppingPlanData ConvertToMarketPlanData(DetailedShoppingPlan plan)
    {
        return new MarketShoppingPlanData
        {
            ItemId = plan.ItemId,
            Name = plan.Name,
            QuantityNeeded = plan.QuantityNeeded,
            DCAveragePrice = plan.DCAveragePrice,
            HQAveragePrice = plan.HQAveragePrice,
            Error = plan.Error,
            WorldOptions = plan.WorldOptions.Select(ConvertToWorldSummaryData).ToList(),
            RecommendedWorld = plan.RecommendedWorld != null ? ConvertToWorldSummaryData(plan.RecommendedWorld) : null
        };
    }

    private WorldShoppingSummaryData ConvertToWorldSummaryData(WorldShoppingSummary world)
    {
        return new WorldShoppingSummaryData
        {
            WorldName = world.WorldName,
            TotalCost = world.TotalCost,
            AveragePricePerUnit = world.AveragePricePerUnit,
            ListingsUsed = world.ListingsUsed,
            IsFullyUnderAverage = world.IsFullyUnderAverage,
            TotalQuantityPurchased = world.TotalQuantityPurchased,
            ExcessQuantity = world.ExcessQuantity,
            Listings = world.Listings.Select(ConvertToListingEntryData).ToList(),
            BestSingleListing = world.BestSingleListing != null ? ConvertToListingEntryData(world.BestSingleListing) : null
        };
    }

    private ShoppingListingEntryData ConvertToListingEntryData(ShoppingListingEntry entry)
    {
        return new ShoppingListingEntryData
        {
            Quantity = entry.Quantity,
            PricePerUnit = entry.PricePerUnit,
            RetainerName = entry.RetainerName,
            IsUnderAverage = entry.IsUnderAverage,
            IsHq = entry.IsHq,
            NeededFromStack = entry.NeededFromStack,
            ExcessQuantity = entry.ExcessQuantity,
            IsAdditionalOption = entry.IsAdditionalOption
        };
    }

    private DetailedShoppingPlan ConvertFromMarketPlanData(MarketShoppingPlanData data)
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = data.ItemId,
            Name = data.Name,
            QuantityNeeded = data.QuantityNeeded,
            DCAveragePrice = data.DCAveragePrice,
            HQAveragePrice = data.HQAveragePrice,
            Error = data.Error,
            WorldOptions = data.WorldOptions?.Select(ConvertFromWorldSummaryData).ToList() ?? new List<WorldShoppingSummary>()
        };
        
        if (data.RecommendedWorld != null)
        {
            plan.RecommendedWorld = plan.WorldOptions.FirstOrDefault(w => w.WorldName == data.RecommendedWorld.WorldName)
                ?? ConvertFromWorldSummaryData(data.RecommendedWorld);
        }
        
        return plan;
    }

    private WorldShoppingSummary ConvertFromWorldSummaryData(WorldShoppingSummaryData data)
    {
        var summary = new WorldShoppingSummary
        {
            WorldName = data.WorldName,
            TotalCost = data.TotalCost,
            AveragePricePerUnit = data.AveragePricePerUnit,
            ListingsUsed = data.ListingsUsed,
            IsFullyUnderAverage = data.IsFullyUnderAverage,
            TotalQuantityPurchased = data.TotalQuantityPurchased,
            ExcessQuantity = data.ExcessQuantity,
            Listings = data.Listings?.Select(ConvertFromListingEntryData).ToList() ?? new List<ShoppingListingEntry>()
        };
        
        if (data.BestSingleListing != null)
        {
            summary.BestSingleListing = ConvertFromListingEntryData(data.BestSingleListing);
        }
        
        return summary;
    }

    private ShoppingListingEntry ConvertFromListingEntryData(ShoppingListingEntryData data)
    {
        return new ShoppingListingEntry
        {
            Quantity = data.Quantity,
            PricePerUnit = data.PricePerUnit,
            RetainerName = data.RetainerName,
            IsUnderAverage = data.IsUnderAverage,
            IsHq = data.IsHq,
            NeededFromStack = data.NeededFromStack,
            ExcessQuantity = data.ExcessQuantity,
            IsAdditionalOption = data.IsAdditionalOption
        };
    }

    #endregion

    #endregion
}

#region File Data Models

/// <summary>
/// Simple DTO for plan file serialization.
/// Flat structure without circular references.
/// </summary>
public class PlanFileData
{
    public int Version { get; set; } = 1;
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string DataCenter { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public List<PlanFileNode> RootItems { get; set; } = new();
    
    /// <summary>
    /// Saved market shopping plans with recommended worlds and listings.
    /// </summary>
    public List<MarketShoppingPlanData> MarketPlans { get; set; } = new();
}

/// <summary>
/// Simple DTO for plan node serialization.
/// </summary>
public class PlanFileNode
{
    public int ItemId { get; set; }
    public string? Name { get; set; }
    public int IconId { get; set; }
    public int Quantity { get; set; }
    
    /// <summary>
    /// Legacy property for backward compatibility. Use Source instead.
    /// </summary>
    public bool IsBuy { get; set; }
    
    /// <summary>
    /// Acquisition source - new preferred property.
    /// </summary>
    public AcquisitionSource? Source { get; set; }
    
    /// <summary>
    /// If true, HQ version is required (for market purchases).
    /// </summary>
    public bool RequiresHq { get; set; }
    
    public bool IsUncraftable { get; set; }
    public int RecipeLevel { get; set; }
    public string? Job { get; set; }
    public int Yield { get; set; } = 1;
    public decimal MarketPrice { get; set; }
    public PriceSource PriceSource { get; set; }
    public string? PriceSourceDetails { get; set; }
    public string? Notes { get; set; }
    public List<PlanFileNode> Children { get; set; } = new();
}

/// <summary>
/// Saved market shopping plan data with recommended worlds and listings.
/// </summary>
public class MarketShoppingPlanData
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int QuantityNeeded { get; set; }
    public decimal DCAveragePrice { get; set; }
    public decimal? HQAveragePrice { get; set; }
    public string? Error { get; set; }
    public List<WorldShoppingSummaryData> WorldOptions { get; set; } = new();
    public WorldShoppingSummaryData? RecommendedWorld { get; set; }
}

/// <summary>
/// Saved world shopping summary data.
/// </summary>
public class WorldShoppingSummaryData
{
    public string WorldName { get; set; } = string.Empty;
    public long TotalCost { get; set; }
    public decimal AveragePricePerUnit { get; set; }
    public int ListingsUsed { get; set; }
    public bool IsFullyUnderAverage { get; set; }
    public int TotalQuantityPurchased { get; set; }
    public int ExcessQuantity { get; set; }
    public List<ShoppingListingEntryData> Listings { get; set; } = new();
    public ShoppingListingEntryData? BestSingleListing { get; set; }
}

/// <summary>
/// Saved shopping listing entry data.
/// </summary>
public class ShoppingListingEntryData
{
    public int Quantity { get; set; }
    public long PricePerUnit { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public bool IsUnderAverage { get; set; }
    public bool IsHq { get; set; }
    public int NeededFromStack { get; set; }
    public int ExcessQuantity { get; set; }
    public bool IsAdditionalOption { get; set; }
}

#endregion

/// <summary>
/// Metadata about a saved plan (without loading the full plan).
/// </summary>
public class PlanInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public int ItemCount { get; set; }
    
    public override string ToString() => $"{Name} ({ItemCount} items) - {ModifiedAt:yyyy-MM-dd HH:mm}";
}
