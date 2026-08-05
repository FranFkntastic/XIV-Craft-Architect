using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// UI-shell state only. Canonical craft, market, acquisition, and procurement
/// state belongs to the Worker-owned session.
/// </summary>
public sealed class AppState
{
    private long _settingsVersion;
    private long _statusVersion;
    private long _viewVersion;
    private long _nextOperationId;
    private long? _currentOperationId;

    public string SelectedDataCenter { get; private set; } = "Aether";
    public string SelectedRegion { get; private set; } = "North America";
    public string? ComparisonRegion { get; private set; }
    public IReadOnlyList<string> AnalysisRegions =>
        MarketFetchScopeResolver.NormalizeSelectedRegions(
            SelectedRegion,
            string.IsNullOrWhiteSpace(ComparisonRegion)
                ? null
                : [ComparisonRegion]);
    public MarketFetchScope DefaultMarketFetchScope { get; private set; } =
        MarketFetchScope.EntireRegion;
    public bool ProcurementEnableSplitWorldPurchases { get; private set; } = true;
    public int ProcurementTravelTolerance { get; private set; }
    public bool ProcurementStartFromHomeDataCenter { get; private set; }
    public MarketTravelPriority ProcurementTravelPriority { get; private set; } =
        MarketTravelPriority.DataCenterTransfersFirst;
    public int TemporaryWorldBlacklistDurationMinutes { get; private set; } = 60;
    public bool SecretDebugToolsEnabled { get; private set; }
    public bool DeferAutomaticProcurementReconciliationForBenchmark { get; private set; }
    public IReadOnlyList<StoredPlanSummary> SavedPlans { get; private set; } =
        Array.AsReadOnly(Array.Empty<StoredPlanSummary>());
    public Guid? SelectedTradeOrderId { get; private set; }
    public WorldData? WorldData { get; private set; }

    public string StatusMessage { get; private set; } = "Ready";
    public bool IsBusy { get; private set; }
    public double ProgressPercent { get; private set; }
    public string? CurrentOperation { get; private set; }
    public DateTime LastStatusUpdate { get; private set; } = DateTime.Now;

    public event Action? OnSavedPlansChanged;
    public event Action? OnStatusChanged;
    public event Action<AppStateChange>? OnStateChanged;

    public AppStateVersionSnapshot CurrentVersions => new(
        0,
        0,
        0,
        0,
        0,
        0,
        _settingsVersion,
        _statusVersion);

    public void ReplaceSavedPlans(IEnumerable<StoredPlanSummary> summaries)
    {
        SavedPlans = Array.AsReadOnly(summaries.ToArray());
        OnSavedPlansChanged?.Invoke();
    }

    public void ClearSavedPlans()
    {
        SavedPlans = Array.AsReadOnly(Array.Empty<StoredPlanSummary>());
        OnSavedPlansChanged?.Invoke();
    }

    public void SetMarketEvidenceSettings(
        string dataCenter,
        string region,
        MarketFetchScope defaultFetchScope,
        string? comparisonRegion = null)
    {
        SelectedRegion = MarketFetchScopeResolver
            .NormalizeSelectedRegions(region, null)
            .Single();
        SelectedDataCenter = MarketFetchScopeResolver.ResolveValidDataCenter(
            SelectedRegion,
            dataCenter);
        ComparisonRegion = MarketFetchScopeResolver
            .NormalizeSelectedRegions(
                SelectedRegion,
                string.IsNullOrWhiteSpace(comparisonRegion)
                    ? null
                    : [comparisonRegion])
            .Skip(1)
            .FirstOrDefault();
        DefaultMarketFetchScope = defaultFetchScope;
        NotifySettingsChanged();
    }

    public void SetProcurementSettings(
        bool enableSplitWorldPurchases,
        int travelTolerance,
        int temporaryWorldBlacklistDurationMinutes)
    {
        ProcurementEnableSplitWorldPurchases = enableSplitWorldPurchases;
        ProcurementTravelTolerance = Math.Clamp(travelTolerance, 0, 11);
        TemporaryWorldBlacklistDurationMinutes =
            Math.Max(1, temporaryWorldBlacklistDurationMinutes);
        NotifySettingsChanged();
    }

    public bool SetProcurementHomeDataCenterOrigin(bool enabled)
    {
        if (ProcurementStartFromHomeDataCenter == enabled)
        {
            return false;
        }
        ProcurementStartFromHomeDataCenter = enabled;
        NotifySettingsChanged();
        return true;
    }

    public bool SetProcurementTravelPriority(MarketTravelPriority priority)
    {
        if (ProcurementTravelPriority == priority)
        {
            return false;
        }
        ProcurementTravelPriority = priority;
        NotifySettingsChanged();
        return true;
    }

    public bool SetSecretDebugToolsEnabled(bool enabled)
    {
        if (SecretDebugToolsEnabled == enabled)
        {
            return false;
        }
        SecretDebugToolsEnabled = enabled;
        NotifySettingsChanged();
        return true;
    }

    public void SetBenchmarkRouteDeferral(bool enabled)
    {
        DeferAutomaticProcurementReconciliationForBenchmark = enabled;
    }

    public void SelectTradeOrder(Guid? orderId)
    {
        if (SelectedTradeOrderId == orderId)
        {
            return;
        }
        SelectedTradeOrderId = orderId;
        PublishChange(AppStateChangeScope.TradeOperationsView);
    }

    public void NotifyTradeOperationsDataChanged() =>
        PublishChange(AppStateChangeScope.TradeOperationsData);

    public AppStateOperation BeginOperation(
        string operationName,
        string? message = null,
        bool announceImmediately = true)
    {
        var operation = new AppStateOperation(++_nextOperationId, operationName);
        _currentOperationId = operation.Id;
        CurrentOperation = operationName;
        if (announceImmediately)
        {
            SetStatus(message ?? $"{operationName}...", busy: true, progress: 0);
        }
        return operation;
    }

    public bool SetStatusForOperation(
        AppStateOperation operation,
        string message,
        bool busy = true,
        double? progress = null)
    {
        if (!IsCurrentOperation(operation))
        {
            return false;
        }
        SetStatus(message, busy, progress);
        return true;
    }

    public bool CancelOperation(
        AppStateOperation operation,
        string? message = null)
    {
        if (!IsCurrentOperation(operation))
        {
            return false;
        }
        _currentOperationId = null;
        CurrentOperation = null;
        SetStatus(message ?? "Ready");
        return true;
    }

    public bool EndOperation(
        AppStateOperation operation,
        string? message = null) =>
        CancelOperation(operation, message);

    public void SetStatus(
        string message,
        bool busy = false,
        double? progress = null)
    {
        StatusMessage = message;
        IsBusy = busy;
        ProgressPercent = progress.HasValue
            ? Math.Clamp(progress.Value, 0, 100)
            : busy ? ProgressPercent : 0;
        LastStatusUpdate = DateTime.Now;
        _statusVersion++;
        OnStatusChanged?.Invoke();
        OnStateChanged?.Invoke(new AppStateChange(
            AppStateChangeScope.Status,
            CurrentVersions));
    }

    public Task InitializeWorldDataAsync(
        PackagedWorldDirectoryService packagedWorldDirectory,
        UniversalisService universalisService)
    {
        if (WorldData == null)
        {
            WorldData = packagedWorldDirectory.LoadWorldData();
        }
        universalisService.SeedWorldData(WorldData);
        return Task.CompletedTask;
    }

    private bool IsCurrentOperation(AppStateOperation operation) =>
        _currentOperationId == operation.Id &&
        string.Equals(
            CurrentOperation,
            operation.Name,
            StringComparison.Ordinal);

    private void NotifySettingsChanged()
    {
        _settingsVersion++;
        PublishChange(AppStateChangeScope.Settings);
    }

    private void PublishChange(AppStateChangeScope scope)
    {
        _viewVersion++;
        OnStateChanged?.Invoke(new AppStateChange(scope, CurrentVersions));
    }
}

[Flags]
public enum AppStateChangeScope
{
    None = 0,
    Settings = 1 << 0,
    Status = 1 << 1,
    TradeOperationsView = 1 << 2,
    TradeOperationsData = 1 << 3
}

public sealed record AppStateVersionSnapshot(
    long PlanStructureVersion,
    long PlanDecisionVersion,
    long PlanPriceVersion,
    long PlanCoreVersion,
    long MarketAnalysisVersion,
    long ProcurementOverlayVersion,
    long SettingsVersion,
    long StatusVersion);

public sealed record AppStateChange(
    AppStateChangeScope Scopes,
    AppStateVersionSnapshot Versions)
{
    public bool HasScope(AppStateChangeScope scope) =>
        (Scopes & scope) == scope;
}

public sealed record AppStateOperation(long Id, string Name);

public enum MarketAnalysisGridSortColumn
{
    Item,
    Quantity,
    Coverage,
    Worlds,
    Total
}

public enum MarketAnalysisWorldGridSortColumn
{
    World,
    StockDepth,
    Coverage,
    PriceValue,
    Value,
    Data
}

public enum MarketAnalysisEvidenceOverlay
{
    CompetitivenessOverlay,
    PriceBandOverlay
}

public readonly record struct MarketAnalysisExpandedWorldKey(
    int ItemId,
    string DataCenter,
    string WorldName);

public readonly record struct MarketAnalysisGridSortState(
    MarketAnalysisGridSortColumn Column,
    bool Descending);

public readonly record struct MarketAnalysisWorldGridSortState(
    MarketAnalysisWorldGridSortColumn Column,
    bool Descending);

public sealed class StoredPlanSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime ModifiedAt { get; set; }
    public DateTime SavedAt { get; set; }
    public string DataCenter { get; set; } = string.Empty;
    public int ItemCount { get; set; }
}
