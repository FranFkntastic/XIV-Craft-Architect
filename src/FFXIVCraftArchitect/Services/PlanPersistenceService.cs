using System.IO;
using FFXIVCraftArchitect.Models;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for saving and loading crafting plans to/from disk.
/// Plans are stored as JSON files in the application directory.
/// </summary>
public class PlanPersistenceService
{
    private readonly RecipeCalculationService _calculationService;
    private readonly ILogger<PlanPersistenceService> _logger;
    private readonly string _plansDirectory;

    public PlanPersistenceService(RecipeCalculationService calculationService, ILogger<PlanPersistenceService> logger)
    {
        _calculationService = calculationService;
        _logger = logger;
        _plansDirectory = Path.Combine(AppContext.BaseDirectory, "Plans");
        
        // Ensure directory exists
        try
        {
            Directory.CreateDirectory(_plansDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlanPersistence] Failed to create plans directory");
        }
    }

    /// <summary>
    /// Save a plan to disk.
    /// </summary>
    public async Task<bool> SavePlanAsync(CraftingPlan plan, string? fileName = null)
    {
        try
        {
            plan.MarkModified();
            
            var name = fileName ?? $"{plan.Name.Replace(" ", "_")}_{plan.Id:N}.json";
            var filePath = Path.Combine(_plansDirectory, name);
            
            // Ensure .json extension
            if (!filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                filePath += ".json";
            }

            var json = _calculationService.SerializePlan(plan);
            await File.WriteAllTextAsync(filePath, json);
            
            _logger.LogInformation("[PlanPersistence] Saved plan '{PlanName}' to {Path}", plan.Name, filePath);
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
            var plan = _calculationService.DeserializePlan(json);
            
            if (plan != null)
            {
                _logger.LogInformation("[PlanPersistence] Loaded plan '{PlanName}' from {Path}", plan.Name, filePath);
            }
            
            return plan;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlanPersistence] Failed to load plan from {Path}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Load a plan by name from the default plans directory.
    /// </summary>
    public async Task<CraftingPlan?> LoadPlanByNameAsync(string planName)
    {
        var filePath = Path.Combine(_plansDirectory, $"{planName}.json");
        return await LoadPlanAsync(filePath);
    }

    /// <summary>
    /// Get a list of all saved plans.
    /// </summary>
    public List<PlanInfo> ListSavedPlans()
    {
        var plans = new List<PlanInfo>();
        
        try
        {
            if (!Directory.Exists(_plansDirectory))
                return plans;

            foreach (var file in Directory.GetFiles(_plansDirectory, "*.json"))
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    var json = File.ReadAllText(file);
                    var wrapper = System.Text.Json.JsonSerializer.Deserialize<PlanSerializationWrapper>(json);
                    
                    if (wrapper != null)
                    {
                        plans.Add(new PlanInfo
                        {
                            Id = wrapper.Id,
                            Name = wrapper.Name,
                            FilePath = file,
                            CreatedAt = wrapper.CreatedAt,
                            ModifiedAt = wrapper.ModifiedAt,
                            ItemCount = wrapper.RootNodeIds?.Count ?? 0
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
            var json = _calculationService.SerializePlan(plan);
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
}

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
