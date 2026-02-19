using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Service for saving and loading crafting plans to/from disk.
/// Uses a three-tier architecture:
/// 1. plan.json - Minimal recipe tree
/// 2. plan.recommendations.csv - Plan-specific shopping strategy
/// 3. market_cache.json - Global raw market data (shared across plans)
/// </summary>
public class PlanPersistenceService : IPlanPersistenceService
{
    private readonly ILogger<PlanPersistenceService> _logger;
    private readonly RecommendationCsvService _recommendationService;
    private readonly string _plansDirectory;
    private readonly JsonSerializerOptions _jsonOptions;

    public PlanPersistenceService(
        ILogger<PlanPersistenceService> logger,
        RecommendationCsvService recommendationService)
    {
        _logger = logger;
        _recommendationService = recommendationService;
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

            // Create serializable data (minimal - just recipe tree)
            var data = new PlanFileData
            {
                Version = 2, // Version 2 = new minimal format
                Id = plan.Id,
                Name = !string.IsNullOrWhiteSpace(customName) ? customName : plan.Name,
                CreatedAt = plan.CreatedAt,
                ModifiedAt = plan.ModifiedAt,
                DataCenter = plan.DataCenter,
                World = plan.World,
                RootItems = plan.RootItems.Select(ConvertToFileNode).ToList()
                // Note: MarketPlans no longer stored in JSON
            };

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
            
            // Save recommendations to CSV companion file
            if (plan.SavedMarketPlans?.Count > 0)
            {
                await _recommendationService.SaveRecommendationsAsync(fileName, plan.SavedMarketPlans);
            }
            
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
                RootItems = data.RootItems?.Select(ConvertFromFileNode).ToList() ?? new List<PlanNode>()
                // Note: SavedMarketPlans loaded from CSV below
            };

            // Re-link parent references
            foreach (var root in plan.RootItems)
            {
                LinkParents(root, null);
            }

            // Load recommendations from CSV companion file (if exists)
            var fileName = Path.GetFileName(filePath);
            plan.SavedMarketPlans = await _recommendationService.LoadRecommendationsAsync(fileName);

            _logger.LogInformation("[PlanPersistence] Loaded plan '{PlanName}' from {Path} ({MarketPlans} recommendations)", 
                plan.Name, filePath, plan.SavedMarketPlans.Count);
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
    /// Includes both JSON and CSV files.
    /// </summary>
    public async Task<bool> ExportPlanAsync(CraftingPlan plan, string filePath)
    {
        try
        {
            // Save JSON (minimal - just recipe tree)
            var data = new PlanFileData
            {
                Version = 2,
                Id = plan.Id,
                Name = plan.Name,
                CreatedAt = plan.CreatedAt,
                ModifiedAt = plan.ModifiedAt,
                DataCenter = plan.DataCenter,
                World = plan.World,
                RootItems = plan.RootItems.Select(ConvertToFileNode).ToList()
            };

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
            
            // Save recommendations CSV alongside
            if (plan.SavedMarketPlans?.Count > 0)
            {
                var csvPath = Path.ChangeExtension(filePath, ".recommendations.csv");
                await _recommendationService.SaveRecommendationsAsync(Path.GetFileName(filePath), plan.SavedMarketPlans);
            }
            
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
            Vendors = node.VendorOptions.Select(v => new VendorInfoData
            {
                Name = v.Name,
                Location = v.Location,
                Price = v.Price,
                Currency = v.Currency
            }).ToList(),
            SelectedVendorIndex = node.SelectedVendorIndex,
            PriceSource = node.PriceSource,
            PriceSourceDetails = node.PriceSourceDetails,
            Notes = node.Notes,
            IsCircularReference = node.IsCircularReference,
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
            RecipeLevel = fileNode.RecipeLevel,
            Job = fileNode.Job ?? string.Empty,
            Yield = fileNode.Yield,
            MarketPrice = fileNode.MarketPrice,
            HqMarketPrice = fileNode.HqMarketPrice,
            VendorPrice = fileNode.VendorPrice,
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
        
        node.MustBeHq = fileNode.MustBeHq;
        node.CanBeHq = fileNode.CanBeHq;
        node.CanBuyFromVendor = fileNode.CanBuyFromVendor;
        node.CanCraft = fileNode.CanCraft;
        node.IsCircularReference = fileNode.IsCircularReference;

        // Restore vendor options (backward compatible - may be null in old saves)
        node.VendorOptions = fileNode.Vendors?.Select(v => new VendorInfo
        {
            Name = v.Name,
            Location = v.Location,
            Price = v.Price,
            Currency = v.Currency
        }).ToList() ?? new List<VendorInfo>();
        node.SelectedVendorIndex = fileNode.SelectedVendorIndex;

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

    #endregion
}

