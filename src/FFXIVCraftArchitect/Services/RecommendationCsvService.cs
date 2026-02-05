using System.Globalization;
using System.IO;
using CsvHelper;
using CsvHelper.Configuration;
using FFXIVCraftArchitect.Models;
using FFXIVCraftArchitect.Core.Models;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for saving and loading plan-specific shopping recommendations as CSV files.
/// These are companion files to the main plan JSON.
/// </summary>
public class RecommendationCsvService
{
    private readonly ILogger<RecommendationCsvService> _logger;
    private readonly string _plansDirectory;

    public RecommendationCsvService(ILogger<RecommendationCsvService> logger)
    {
        _logger = logger;
        _plansDirectory = Path.Combine(AppContext.BaseDirectory, "Plans");
    }

    /// <summary>
    /// Save recommendations to a CSV file companion to the plan.
    /// Writes one row per world option to preserve multi-world data.
    /// </summary>
    public async Task<bool> SaveRecommendationsAsync(string planFileName, List<DetailedShoppingPlan> recommendations)
    {
        try
        {
            var csvPath = GetCsvPath(planFileName);
            // Flatten all world options into records
            var records = recommendations
                .SelectMany(RecommendationCsvRecord.FromShoppingPlanToRecords)
                .ToList();

            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true
            };

            using var writer = new StreamWriter(csvPath, false);
            using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
            csv.Context.RegisterClassMap<RecommendationCsvMap>();
            await csv.WriteRecordsAsync(records);

            _logger.LogInformation("[RecommendationCsv] Saved {RecordCount} records ({PlanCount} plans) to {Path}", 
                records.Count, recommendations.Count, csvPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RecommendationCsv] Failed to save recommendations for {PlanFile}", planFileName);
            return false;
        }
    }

    /// <summary>
    /// Load recommendations from a CSV file.
    /// Groups rows by ItemId to reconstruct multi-world options.
    /// Returns empty list if file doesn't exist.
    /// </summary>
    public async Task<List<DetailedShoppingPlan>> LoadRecommendationsAsync(string planFileName)
    {
        try
        {
            var csvPath = GetCsvPath(planFileName);
            
            if (!File.Exists(csvPath))
            {
                _logger.LogDebug("[RecommendationCsv] No CSV file found at {Path}", csvPath);
                return new List<DetailedShoppingPlan>();
            }

            using var reader = new StreamReader(csvPath);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            csv.Context.RegisterClassMap<RecommendationCsvMap>();
            
            var records = new List<RecommendationCsvRecord>();
            await foreach (var record in csv.GetRecordsAsync<RecommendationCsvRecord>())
            {
                records.Add(record);
            }

            // Group records by ItemId to reconstruct multi-world plans
            var plans = records
                .GroupBy(r => r.ItemId)
                .Select(g => MergeRecordsToPlan(g.ToList()))
                .ToList();

            _logger.LogInformation("[RecommendationCsv] Loaded {PlanCount} plans ({RecordCount} records) from {Path}", 
                plans.Count, records.Count, csvPath);
            return plans;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RecommendationCsv] Failed to load recommendations for {PlanFile}", planFileName);
            return new List<DetailedShoppingPlan>();
        }
    }

    /// <summary>
    /// Merge multiple CSV records (one per world) into a single DetailedShoppingPlan.
    /// </summary>
    private DetailedShoppingPlan MergeRecordsToPlan(List<RecommendationCsvRecord> records)
    {
        if (records.Count == 0)
            return new DetailedShoppingPlan();

        if (records.Count == 1)
            return records[0].ToShoppingPlan();

        // Multiple records = multiple worlds for same item
        var first = records.First();
        var worldOptions = records.Select(r => r.ToWorldSummary()).ToList();
        var recommended = records.FirstOrDefault(r => r.IsRecommended)?.ToWorldSummary() 
            ?? worldOptions.First();

        return new DetailedShoppingPlan
        {
            ItemId = first.ItemId,
            Name = first.Name,
            QuantityNeeded = first.QuantityNeeded,
            DCAveragePrice = first.DCAveragePrice,
            RecommendedWorld = recommended,
            WorldOptions = worldOptions
        };
    }

    /// <summary>
    /// Check if a recommendations CSV exists for a plan.
    /// </summary>
    public bool RecommendationsExist(string planFileName)
    {
        var csvPath = GetCsvPath(planFileName);
        return File.Exists(csvPath);
    }

    /// <summary>
    /// Delete the recommendations CSV for a plan.
    /// </summary>
    public bool DeleteRecommendations(string planFileName)
    {
        try
        {
            var csvPath = GetCsvPath(planFileName);
            if (File.Exists(csvPath))
            {
                File.Delete(csvPath);
                _logger.LogInformation("[RecommendationCsv] Deleted {Path}", csvPath);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RecommendationCsv] Failed to delete recommendations for {PlanFile}", planFileName);
            return false;
        }
    }

    /// <summary>
    /// Get the CSV file path for a given plan file name.
    /// </summary>
    private string GetCsvPath(string planFileName)
    {
        // Replace .json extension with .recommendations.csv
        var baseName = Path.GetFileNameWithoutExtension(planFileName);
        return Path.Combine(_plansDirectory, $"{baseName}.recommendations.csv");
    }
}
