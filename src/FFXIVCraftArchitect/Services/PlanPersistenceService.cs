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
    public async Task<bool> SavePlanAsync(CraftingPlan plan, string? customName = null)
    {
        try
        {
            EnsureDirectoryExists();
            
            plan.MarkModified();
            
            // Generate filename from plan name or custom name
            var baseName = !string.IsNullOrWhiteSpace(customName) 
                ? customName 
                : plan.Name;
            
            // Sanitize filename
            var safeName = string.Concat(baseName.Split(Path.GetInvalidFileNameChars()))
                .Replace(" ", "_")
                .Replace(".", "_");
            
            // Add timestamp to avoid collisions
            var fileName = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            var filePath = Path.Combine(_plansDirectory, fileName);

            // Create serializable data
            var data = new PlanFileData
            {
                Version = 1,
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
            
            _logger.LogInformation("[PlanPersistence] Saved plan '{PlanName}' ({FileName})", plan.Name, fileName);
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
                RootItems = plan.RootItems.Select(ConvertToFileNode).ToList()
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
            IsBuy = node.IsBuy,
            IsUncraftable = node.IsUncraftable,
            RecipeLevel = node.RecipeLevel,
            Job = node.Job,
            Yield = node.Yield,
            MarketPrice = node.MarketPrice,
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
            IsBuy = fileNode.IsBuy,
            IsUncraftable = fileNode.IsUncraftable,
            RecipeLevel = fileNode.RecipeLevel,
            Job = fileNode.Job ?? string.Empty,
            Yield = fileNode.Yield,
            MarketPrice = fileNode.MarketPrice,
            Notes = fileNode.Notes,
            Children = fileNode.Children?.Select(ConvertFromFileNode).ToList() ?? new List<PlanNode>()
        };
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
    public bool IsBuy { get; set; }
    public bool IsUncraftable { get; set; }
    public int RecipeLevel { get; set; }
    public string? Job { get; set; }
    public int Yield { get; set; } = 1;
    public decimal MarketPrice { get; set; }
    public string? Notes { get; set; }
    public List<PlanFileNode> Children { get; set; } = new();
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
