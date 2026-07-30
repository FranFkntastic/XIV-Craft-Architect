using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Narrow browser-storage gateway for named plans, UI settings, and Trade data.
/// The Worker owns active-session persistence and autosave revisions directly.
/// </summary>
public sealed class IndexedDbService
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger<IndexedDbService>? _logger;

    public IndexedDbService(
        IJSRuntime jsRuntime,
        ILogger<IndexedDbService>? logger = null)
    {
        _jsRuntime = jsRuntime;
        _logger = logger;
    }

    public async Task<bool> SavePlanAsync(StoredPlan plan)
    {
        try
        {
            if (plan.SavedAt == default)
            {
                plan.SavedAt = DateTime.UtcNow;
            }
            return await _jsRuntime.InvokeAsync<bool>("IndexedDB.savePlan", plan);
        }
        catch (OutOfMemoryException)
        {
            _logger?.LogError(
                "Failed to save plan '{PlanName}' because browser memory was exhausted",
                plan.Name);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save plan '{PlanName}'", plan.Name);
            return false;
        }
    }

    public Task<StoredPlan?> LoadPlanAsync(string planId) =>
        InvokeOrDefaultAsync<StoredPlan?>(
            "IndexedDB.loadPlan",
            null,
            $"load plan {planId}",
            planId);

    public Task<List<StoredPlan>> LoadAllPlansAsync() =>
        InvokeOrDefaultAsync(
            "IndexedDB.loadAllPlans",
            new List<StoredPlan>(),
            "load plans");

    public Task<List<StoredPlan>> LoadAllPlansRequiredAsync() =>
        InvokeRequiredAsync<List<StoredPlan>>(
            "IndexedDB.loadAllPlans",
            "load plans for hosted profile sync");

    public Task<List<StoredPlanSummary>> LoadPlanSummariesAsync() =>
        InvokeOrDefaultAsync(
            "IndexedDB.loadPlanSummaries",
            new List<StoredPlanSummary>(),
            "load plan summaries");

    public Task<SpecializedBrowserStorageDiagnostics> EnsureSpecializedStorageAsync() =>
        InvokeRequiredAsync<SpecializedBrowserStorageDiagnostics>(
            "IndexedDB.getSpecializedStorageDiagnostics",
            "initialize specialized browser storage");

    public Task<bool> DeletePlanAsync(string planId) =>
        InvokeOrDefaultAsync(
            "IndexedDB.deletePlan",
            false,
            $"delete plan {planId}",
            planId);

    public Task<bool> ClearAllPlansAsync() =>
        InvokeOrDefaultAsync(
            "IndexedDB.clearAllPlans",
            false,
            "clear plans");

    public async Task<bool> SaveSettingAsync<T>(string key, T value) =>
        await InvokeOrDefaultAsync(
            "IndexedDB.saveSetting",
            false,
            $"save setting {key}",
            key,
            JsonSerializer.Serialize(value));

    public async Task<T?> LoadSettingAsync<T>(
        string key,
        T? defaultValue = default)
    {
        var serialized = await InvokeOrDefaultAsync<string?>(
            "IndexedDB.loadSetting",
            null,
            $"load setting {key}",
            key);
        if (string.IsNullOrEmpty(serialized))
        {
            return defaultValue;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(serialized);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Ignoring invalid setting {Key}", key);
            return defaultValue;
        }
    }

    public async Task<T?> LoadRequiredSettingAsync<T>(
        string key,
        T? defaultValue = default)
    {
        var serialized = await InvokeRequiredAsync<string?>(
            "IndexedDB.loadSetting",
            $"load required setting {key}",
            key);
        if (string.IsNullOrEmpty(serialized))
        {
            return defaultValue;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(serialized);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Required browser setting '{key}' is invalid.",
                ex);
        }
    }

    public Task<Dictionary<string, string>> LoadAllSettingsAsync() =>
        InvokeOrDefaultAsync(
            "IndexedDB.loadAllSettings",
            new Dictionary<string, string>(),
            "load settings");

    public Task<Dictionary<string, string>> LoadAllSettingsRequiredAsync() =>
        InvokeRequiredAsync<Dictionary<string, string>>(
            "IndexedDB.loadAllSettings",
            "load settings for hosted profile sync");

    public Task<bool> SaveSettingsBatchAsync(
        Dictionary<string, string> settings) =>
        InvokeOrDefaultAsync(
            "IndexedDB.saveSettingsBatch",
            false,
            "save settings",
            settings);

    public Task<bool> SavePlansBatchAsync(IReadOnlyList<StoredPlan> plans) =>
        InvokeOrDefaultAsync(
            "IndexedDB.savePlansBatch",
            false,
            "save plans batch",
            plans);

    public Task<bool> SaveTradeCompanyProfileAsync(
        TradeCompanyProfile profile) =>
        InvokeOrDefaultAsync(
            "IndexedDB.saveTradeCompanyProfile",
            false,
            $"save Trade company {profile.Id}",
            profile);

    public Task<TradeIndexedDbDiagnostics> GetTradeStoreDiagnosticsAsync() =>
        InvokeOrDefaultAsync(
            "IndexedDB.getTradeStoreDiagnostics",
            new TradeIndexedDbDiagnostics
            {
                ErrorMessage = "Trade storage diagnostics were unavailable."
            },
            "read Trade storage diagnostics");

    public Task<List<TradeCompanyProfile>> LoadTradeCompanyProfilesAsync() =>
        InvokeRequiredAsync<List<TradeCompanyProfile>>(
            "IndexedDB.loadTradeCompanyProfiles",
            "load Trade company profiles");

    public Task<bool> DeleteTradeCompanyProfileAsync(Guid companyProfileId) =>
        InvokeOrDefaultAsync(
            "IndexedDB.deleteTradeCompanyProfile",
            false,
            $"delete Trade company {companyProfileId}",
            companyProfileId);

    public Task<bool> SaveTradeCrafterAsync(TradeCrafterProfile crafter) =>
        InvokeOrDefaultAsync(
            "IndexedDB.saveTradeCrafter",
            false,
            $"save Trade crafter {crafter.Id}",
            crafter);

    public Task<List<TradeCrafterProfile>> LoadTradeCraftersAsync(
        Guid companyProfileId) =>
        InvokeRequiredAsync<List<TradeCrafterProfile>>(
            "IndexedDB.loadTradeCrafters",
            "load Trade crafters",
            companyProfileId);

    public Task<bool> DeleteTradeCrafterAsync(Guid crafterId) =>
        InvokeOrDefaultAsync(
            "IndexedDB.deleteTradeCrafter",
            false,
            $"delete Trade crafter {crafterId}",
            crafterId);

    public Task<bool> SaveTradeOrderAsync(TradeOrder order) =>
        InvokeOrDefaultAsync(
            "IndexedDB.saveTradeOrder",
            false,
            $"save Trade order {order.Id}",
            order);

    public Task<List<TradeOrder>> LoadTradeOrdersAsync(Guid companyProfileId) =>
        InvokeRequiredAsync<List<TradeOrder>>(
            "IndexedDB.loadTradeOrders",
            "load Trade orders",
            companyProfileId);

    public Task<bool> DeleteTradeOrderAsync(Guid orderId) =>
        InvokeOrDefaultAsync(
            "IndexedDB.deleteTradeOrder",
            false,
            $"delete Trade order {orderId}",
            orderId);

    public Task<bool> SaveTradePayrollDraftAsync(
        TradePayrollWorkflowDraft draft) =>
        InvokeOrDefaultAsync(
            "IndexedDB.saveTradePayrollDraft",
            false,
            $"save Trade payroll draft {draft.Id}",
            draft);

    public Task<List<TradePayrollWorkflowDraft>> LoadTradePayrollDraftsAsync(
        Guid companyProfileId) =>
        InvokeRequiredAsync<List<TradePayrollWorkflowDraft>>(
            "IndexedDB.loadTradePayrollDrafts",
            "load Trade payroll drafts",
            companyProfileId);

    public Task<bool> DeleteTradePayrollDraftAsync(string draftId) =>
        InvokeOrDefaultAsync(
            "IndexedDB.deleteTradePayrollDraft",
            false,
            $"delete Trade payroll draft {draftId}",
            draftId);

    public Task<StoredPlan?> LoadAutoSaveAsync() =>
        LoadPlanAsync("autosave");

    private async Task<T> InvokeOrDefaultAsync<T>(
        string identifier,
        T fallback,
        string operation,
        params object?[] args)
    {
        try
        {
            return await _jsRuntime.InvokeAsync<T>(identifier, args);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to {Operation}", operation);
            return fallback;
        }
    }

    private async Task<T> InvokeRequiredAsync<T>(
        string identifier,
        string operation,
        params object?[] args)
    {
        try
        {
            return await _jsRuntime.InvokeAsync<T>(identifier, args);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to {Operation}", operation);
            throw new InvalidOperationException(
                $"Failed to {operation} from browser storage.",
                ex);
        }
    }
}

public sealed class TradeIndexedDbDiagnostics
{
    public int DatabaseVersion { get; set; }
    public bool HasCompanyProfilesStore { get; set; }
    public bool HasCraftersStore { get; set; }
    public bool HasOrdersStore { get; set; }
    public bool HasPayrollDraftsStore { get; set; }
    public string? ErrorMessage { get; set; }

    public bool IsReady =>
        string.IsNullOrWhiteSpace(ErrorMessage) &&
        HasCompanyProfilesStore &&
        HasCraftersStore &&
        HasOrdersStore &&
        HasPayrollDraftsStore;

    public string ToDisplayMessage()
    {
        var details =
            $"Trade storage diagnostics: database v{DatabaseVersion}; " +
            $"stores company={HasCompanyProfilesStore}, crafters={HasCraftersStore}, " +
            $"orders={HasOrdersStore}, payrollDrafts={HasPayrollDraftsStore}.";
        if (!string.IsNullOrWhiteSpace(ErrorMessage))
        {
            return $"{details} {ErrorMessage}";
        }
        return IsReady
            ? details
            : $"{details} Reload after closing other Craft Architect tabs so the browser can finish the IndexedDB upgrade.";
    }
}

public sealed class SpecializedBrowserStorageDiagnostics
{
    public Dictionary<string, string> DatabaseNames { get; set; } = [];
    public Dictionary<string, int> Versions { get; set; } = [];
    public bool EngineDatabasePresent { get; set; }
    public Dictionary<string, SpecializedBrowserStorageMigration> Migrations { get; set; } = [];
}

public sealed class SpecializedBrowserStorageMigration
{
    public string State { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string? SourceDatabase { get; set; }
    public int? SourceSchemaVersion { get; set; }
    public Dictionary<string, int> Counts { get; set; } = [];
}

public sealed class StoredPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Plan";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
    public string DataCenter { get; set; } = "Aether";
    public List<StoredProjectItem> ProjectItems { get; set; } = [];
    public string? PlanJson { get; set; }
    public string? PlanStateJson { get; set; }
    public string? MarketPlansJson { get; set; }
    public string? MarketIntelligenceJson { get; set; }
    public string? ProcurementRouteJson { get; set; }
    public int? ProcurementTravelTolerance { get; set; }
    public string? MarketItemAnalysesJson { get; set; }
    public string? MarketAnalysisRecipeBasisJson { get; set; }
    public string? MarketAnalysisScopeSnapshotJson { get; set; }
    public RecommendationMode SavedRecommendationMode { get; set; } =
        RecommendationMode.MinimizeTotalCost;
    public MarketAcquisitionLens SavedMarketAnalysisLens { get; set; } =
        MarketAcquisitionLens.MinimumUpfrontCost;
    public string? SourcePlanId { get; set; }
    public string? SourcePlanName { get; set; }
}

public sealed record StoredProcurementRoute(
    int SchemaVersion,
    string OptimizerVersion,
    IReadOnlyList<DetailedShoppingPlan>? ShoppingPlans,
    MarketRouteDecision? Decision,
    ProcurementRoutePublicationBasis? Basis,
    string PlanHash,
    string? MarketEvidenceHash,
    string? PayloadHash);

public sealed class StoredProjectItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    public int Quantity { get; set; }
    public bool MustBeHq { get; set; }
}
