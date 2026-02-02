using System.Globalization;
using System.IO;
using CsvHelper;
using CsvHelper.Configuration;
using FFXIVCraftArchitect.Models;
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
    /// </summary>
    public async Task<bool> SaveRecommendationsAsync(string planFileName, List<DetailedShoppingPlan> recommendations)
    {
        try
        {
            var csvPath = GetCsvPath(planFileName);
            var records = recommendations.Select(RecommendationCsvRecord.FromShoppingPlan).ToList();

            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true
            };

            using var writer = new StreamWriter(csvPath, false);
            using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
            csv.Context.RegisterClassMap<RecommendationCsvMap>();
            await csv.WriteRecordsAsync(records);

            _logger.LogInformation("[RecommendationCsv] Saved {Count} recommendations to {Path}", 
                records.Count, csvPath);
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
            var plans = records.Select(r => r.ToShoppingPlan()).ToList();

            _logger.LogInformation("[RecommendationCsv] Loaded {Count} recommendations from {Path}", 
                plans.Count, csvPath);
            return plans;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RecommendationCsv] Failed to load recommendations for {PlanFile}", planFileName);
            return new List<DetailedShoppingPlan>();
        }
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
