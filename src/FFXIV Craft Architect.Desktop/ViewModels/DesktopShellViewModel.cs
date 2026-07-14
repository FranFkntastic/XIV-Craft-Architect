using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Desktop.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FFXIV_Craft_Architect.Desktop.ViewModels;

public sealed partial class DesktopShellViewModel : ObservableObject, IDisposable
{
    private static readonly SolidColorBrush ActiveTabBrush = new(global::Windows.UI.Color.FromArgb(255, 61, 50, 28));
    private static readonly SolidColorBrush InactiveTabBrush = new(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
    private static readonly SolidColorBrush ActiveSourceBrush = new(global::Windows.UI.Color.FromArgb(255, 80, 61, 25));
    private static readonly SolidColorBrush AvailableSourceBrush = new(global::Windows.UI.Color.FromArgb(255, 52, 48, 39));
    private static readonly SolidColorBrush ActiveActivityFilterBrush = new(global::Windows.UI.Color.FromArgb(255, 62, 51, 31));
    private static readonly SolidColorBrush InactiveActivityFilterBrush = new(global::Windows.UI.Color.FromArgb(255, 35, 35, 34));
    private const string ActivityFilterAll = "All";
    private const string ActivityFilterSession = "Session";
    private const string ActivityFilterJob = "Job";
    private const string ActivityFilterCache = "Cache";
    private const string ActivityFilterDesktop = "Desktop";

    private readonly CraftSessionState _session;
    private readonly CraftOperationState _operationState;
    private readonly ICraftOperationCoordinator _operationCoordinator;
    private readonly CoreSessionPersistenceService _persistence;
    private readonly CorePlanSessionLoadService _sessionLoadService;
    private readonly CoreAcquisitionDecisionService _acquisitionDecisionService;
    private readonly IDesktopClipboardService _clipboard;
    private readonly IDesktopFileDialogService _fileDialogs;
    private readonly DesktopSettingsStore _desktopSettings;
    private readonly DesktopLocalInfrastructureService _localInfrastructure;
    private readonly DesktopActivityLogStore _activityLogStore;
    private readonly DesktopLogStore _logStore;
    private readonly GarlandService _garlandService;
    private readonly IDesktopRecipePlanBuilder _recipePlanBuilder;
    private readonly DesktopMarketRefreshQueueService _marketRefreshQueue;
    private readonly DesktopProjectItemDraftService _projectItemDrafts;
    private readonly IDesktopLogViewerLauncher _logViewerLauncher;
    private readonly ILogger<DesktopShellViewModel> _logger;
    private string? _lastLoggedOperationStatus;
    private bool _disposed;

    [ObservableProperty]
    private string _planName = "New Plan";

    [ObservableProperty]
    private string _selectedDataCenter = "Aether";

    [ObservableProperty]
    private string _selectedWorld = "Data center scope";

    [ObservableProperty]
    private string _planGraphSummary = "No active plan";

    [ObservableProperty]
    private string _marketStatusText = "Market cache ready";

    [ObservableProperty]
    private string _localIntegrationStatusText = "Local integration offline";

    [ObservableProperty]
    private string _jobsStatusText = "Ready";

    [ObservableProperty]
    private string _operationStatusText = "Ready";

    [ObservableProperty]
    private int _operationProgressPercent;

    [ObservableProperty]
    private bool _isOperationBusy;

    [ObservableProperty]
    private string _persistenceStatusText = "Not saved";

    [ObservableProperty]
    private string _localSnapshotSummaryText = "Snapshots: unknown";

    [ObservableProperty]
    private bool _isPersistenceBusy;

    [ObservableProperty]
    private string _inspectorItemName = "No item selected";

    [ObservableProperty]
    private string _inspectorItemDetail = "Build or load a plan to inspect item details.";

    [ObservableProperty]
    private string _inspectorEstimatedCostText = "Pending";

    [ObservableProperty]
    private string _inspectorConfidenceText = "Needs data";

    [ObservableProperty]
    private string _targetItemCountText = "0";

    [ObservableProperty]
    private string _materialEntryCountText = "0";

    [ObservableProperty]
    private string _hqRequiredCountText = "0";

    [ObservableProperty]
    private string _pricedMaterialCountText = "0";

    [ObservableProperty]
    private string _missingPriceCountText = "0";

    [ObservableProperty]
    private string _marketUnavailableCountText = "0";

    [ObservableProperty]
    private string _inspectorActionStatusText = "No item action has run.";

    [ObservableProperty]
    private string _activeWorkflow = DesktopWorkflow.RecipePlanner;

    [ObservableProperty]
    private string _workbenchTitle = "Recipe Planner";

    [ObservableProperty]
    private string _workbenchSummary = "Build a recipe plan from draft target items.";

    [ObservableProperty]
    private string _primaryActionText = "Build Project Plan";

    [ObservableProperty]
    private string _marketRibbonSummary = "Market evidence not run";

    [ObservableProperty]
    private string _newItemName = "Cobalt Plate";

    [ObservableProperty]
    private string _newItemQuantityText = "999";

    [ObservableProperty]
    private bool _newItemMustBeHq;

    [ObservableProperty]
    private string _targetSearchStatusText = "No Garland search run.";

    [ObservableProperty]
    private Visibility _targetSearchStatusVisibility = Visibility.Visible;

    [ObservableProperty]
    private string _recipePlanEmptyText = "No recipe plan built.";

    [ObservableProperty]
    private Visibility _recipePlanRowsVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _recipePlanEmptyVisibility = Visibility.Visible;

    [ObservableProperty]
    private string _settingsDataCenterText = "Aether";

    [ObservableProperty]
    private string _settingsWorldText = string.Empty;

    [ObservableProperty]
    private string _sessionVersionSummaryText = "Session versions unavailable";

    [ObservableProperty]
    private string _dirtyBucketSummaryText = "No dirty buckets";

    [ObservableProperty]
    private string _changeLogSummaryText = "No changes recorded";

    [ObservableProperty]
    private string _marketEvidenceSummaryText = "Market evidence not run";

    [ObservableProperty]
    private string _desktopInfrastructureSummaryText = "Local services ready";

    [ObservableProperty]
    private string _desktopSettingsStorageSummaryText = "Desktop settings not loaded.";

    [ObservableProperty]
    private string _desktopLocalInfrastructureSummaryText = "Local data paths not inspected.";

    [ObservableProperty]
    private string _marketCacheHealthText = "Market cache not inspected.";

    [ObservableProperty]
    private string _marketCacheRepairText = "Refresh diagnostics to inspect cache repair actions.";

    [ObservableProperty]
    private string _marketCachePathText = "Market cache path unknown.";

    [ObservableProperty]
    private string _activityLogPathText = "Activity log path unknown.";

    [ObservableProperty]
    private string _diagnosticLogPathText = "Diagnostic log path unknown.";

    [ObservableProperty]
    private string _diagnosticLogSummaryText = "No diagnostic log entries loaded.";

    [ObservableProperty]
    private string _latestActivitySummaryText = "No activity recorded.";

    [ObservableProperty]
    private string _activityDrawerButtonText = "Activity";

    [ObservableProperty]
    private string _selectedActivityFilter = ActivityFilterAll;

    [ObservableProperty]
    private string _activityFilterSummaryText = "Showing all recent activity.";

    [ObservableProperty]
    private GridLength _leftPaneWidth = new(320);

    [ObservableProperty]
    private double _leftPaneMinWidth = 260;

    [ObservableProperty]
    private Visibility _leftPaneVisibility = Visibility.Visible;

    [ObservableProperty]
    private GridLength _rightPaneWidth = new(390);

    [ObservableProperty]
    private double _rightPaneMinWidth = 320;

    [ObservableProperty]
    private Visibility _rightPaneVisibility = Visibility.Visible;

    [ObservableProperty]
    private Visibility _recipePlannerPanelVisibility = Visibility.Visible;

    [ObservableProperty]
    private Visibility _marketAnalysisPanelVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _acquisitionEvaluationPanelVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _procurementPlanPanelVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _settingsPanelVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _diagnosticsPanelVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _activityDrawerVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _marketItemActionVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _sourceDecisionActionVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _procurementActionVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _planEditActionVisibility = Visibility.Visible;

    [ObservableProperty]
    private Visibility _craftSourceOptionVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _marketNqSourceOptionVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _marketHqSourceOptionVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Visibility _vendorSourceOptionVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private Brush _recipePlannerTabBackground = ActiveTabBrush;

    [ObservableProperty]
    private Brush _marketAnalysisTabBackground = InactiveTabBrush;

    [ObservableProperty]
    private Brush _acquisitionEvaluationTabBackground = InactiveTabBrush;

    [ObservableProperty]
    private Brush _procurementPlanTabBackground = InactiveTabBrush;

    [ObservableProperty]
    private Brush _craftSourceOptionBackground = AvailableSourceBrush;

    [ObservableProperty]
    private Brush _marketNqSourceOptionBackground = AvailableSourceBrush;

    [ObservableProperty]
    private Brush _marketHqSourceOptionBackground = AvailableSourceBrush;

    [ObservableProperty]
    private Brush _vendorSourceOptionBackground = AvailableSourceBrush;

    [ObservableProperty]
    private Brush _activityAllFilterBackground = ActiveActivityFilterBrush;

    [ObservableProperty]
    private Brush _activitySessionFilterBackground = InactiveActivityFilterBrush;

    [ObservableProperty]
    private Brush _activityJobFilterBackground = InactiveActivityFilterBrush;

    [ObservableProperty]
    private Brush _activityCacheFilterBackground = InactiveActivityFilterBrush;

    [ObservableProperty]
    private Brush _activityDesktopFilterBackground = InactiveActivityFilterBrush;

    [ObservableProperty]
    private DesktopPlanItemRow? _selectedPlanItem;

    [ObservableProperty]
    private DesktopMarketQueueRow? _selectedMarketQueueItem;

    [ObservableProperty]
    private DesktopStoredPlanRow? _selectedStoredPlan;

    [ObservableProperty]
    private DesktopLogRow? _selectedLogEntry;

    public ObservableCollection<DesktopPlanItemRow> PlanItems { get; } = new();

    public ObservableCollection<DesktopPlanItemRow> TargetItems { get; } = new();

    public ObservableCollection<DesktopMarketQueueRow> MarketQueue { get; } = new();

    public ObservableCollection<DesktopPlanSearchResultRow> SearchResults { get; } = new();

    public ObservableCollection<DesktopStoredPlanRow> SavedPlans { get; } = new();

    public ObservableCollection<DesktopActivityLogRow> ActivityLog { get; } = new();

    public ObservableCollection<DesktopActivityLogRow> FilteredActivityLog { get; } = new();

    public ObservableCollection<DesktopLogRow> DiagnosticLogEntries { get; } = new();

    public DesktopShellViewModel(
        CraftSessionState session,
        CraftOperationState operationState,
        ICraftOperationCoordinator operationCoordinator,
        CoreSessionPersistenceService persistence,
        CorePlanSessionLoadService sessionLoadService,
        CoreAcquisitionDecisionService acquisitionDecisionService,
        IDesktopClipboardService clipboard,
        IDesktopFileDialogService fileDialogs,
        DesktopSettingsStore desktopSettings,
        DesktopLocalInfrastructureService localInfrastructure,
        DesktopActivityLogStore activityLogStore,
        DesktopLogStore logStore,
        GarlandService garlandService,
        IDesktopRecipePlanBuilder recipePlanBuilder,
        DesktopMarketRefreshQueueService marketRefreshQueue,
        DesktopProjectItemDraftService projectItemDrafts,
        IDesktopLogViewerLauncher logViewerLauncher,
        ILogger<DesktopShellViewModel> logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _operationState = operationState ?? throw new ArgumentNullException(nameof(operationState));
        _operationCoordinator = operationCoordinator ?? throw new ArgumentNullException(nameof(operationCoordinator));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _sessionLoadService = sessionLoadService ?? throw new ArgumentNullException(nameof(sessionLoadService));
        _acquisitionDecisionService = acquisitionDecisionService ?? throw new ArgumentNullException(nameof(acquisitionDecisionService));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));
        _desktopSettings = desktopSettings ?? throw new ArgumentNullException(nameof(desktopSettings));
        _localInfrastructure = localInfrastructure ?? throw new ArgumentNullException(nameof(localInfrastructure));
        _activityLogStore = activityLogStore ?? throw new ArgumentNullException(nameof(activityLogStore));
        _logStore = logStore ?? throw new ArgumentNullException(nameof(logStore));
        _garlandService = garlandService ?? throw new ArgumentNullException(nameof(garlandService));
        _recipePlanBuilder = recipePlanBuilder ?? throw new ArgumentNullException(nameof(recipePlanBuilder));
        _marketRefreshQueue = marketRefreshQueue ?? throw new ArgumentNullException(nameof(marketRefreshQueue));
        _projectItemDrafts = projectItemDrafts ?? throw new ArgumentNullException(nameof(projectItemDrafts));
        _logViewerLauncher = logViewerLauncher ?? throw new ArgumentNullException(nameof(logViewerLauncher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _session.Changed += OnSessionChanged;
        _operationState.Changed += OnOperationChanged;

        LoadActivityHistory();
        RefreshDiagnosticLogEntries();
        ApplyStoredDesktopSettings(_desktopSettings.Load());
        ApplySessionState();
        ApplyOperationSnapshot(_operationState.Snapshot());
        ActivateWorkflow(DesktopWorkflow.RecipePlanner);
        AppendActivity("Shell", "Desktop workbench ready.");
        _logger.LogInformation(
            "Desktop shell initialized. PlanName={PlanName}; DataCenter={DataCenter}; World={World}; LogPath={LogPath}",
            PlanName,
            SelectedDataCenter,
            SelectedWorld,
            _logStore.LogPath);
    }

    [RelayCommand]
    private async Task RunEvaluationAsync()
    {
        _logger.LogDebug("Market evidence refresh requested. ActiveWorkflow={ActiveWorkflow}; DataCenter={DataCenter}", ActiveWorkflow, SelectedDataCenter);
        using var lease = _operationCoordinator.Start(
            CraftOperationWorkflow.ItemMarketRefresh,
            "Desktop Market Evidence Refresh",
            "Refreshing market evidence for active plan...");

        try
        {
            await Task.Delay(250, lease.Token);
            lease.ReportProgress(45, "Evaluating active plan items...");

            await Task.Delay(250, lease.Token);
            var result = await _marketRefreshQueue.RefreshPlanEvidenceAsync(_session, SelectedDataCenter, lease.Token);
            _logger.LogInformation(
                "Market evidence refresh completed. Status={Status}; ItemCount={ItemCount}; ItemName={ItemName}; Detail={Detail}",
                result.Status,
                result.ItemCount,
                result.ItemName,
                result.Detail);
            WorkbenchSummary = GetMarketWorkflowSummary();
            lease.ReportProgress(80, FormatRefreshQueueResult(result));

            await Task.Delay(150, lease.Token);
            lease.CompleteStatusIfCurrent(FormatRefreshQueueResult(result));
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
        {
            lease.CompleteStatusIfCurrent("Market evidence refresh cancelled.");
        }
    }

    [RelayCommand]
    private async Task RunPrimaryActionAsync()
    {
        _logger.LogTrace("Primary action invoked. ActiveWorkflow={ActiveWorkflow}", ActiveWorkflow);
        switch (ActiveWorkflow)
        {
            case DesktopWorkflow.RecipePlanner:
                await BuildProjectPlanAsync();
                break;
            case DesktopWorkflow.MarketAnalysis:
                await RunEvaluationAsync();
                break;
            case DesktopWorkflow.AcquisitionEvaluation:
                OperationStatusText = $"Acquisition evaluation ready for {GetCurrentInspectorItemName()}. Choose an available source.";
                OperationProgressPercent = 100;
                break;
            case DesktopWorkflow.ProcurementPlan:
                CopyProcurementPlan();
                break;
            case DesktopWorkflow.Settings:
                ApplyDesktopSettings();
                break;
            case DesktopWorkflow.Diagnostics:
                await RefreshDiagnosticsAsync();
                break;
        }
    }

    [RelayCommand]
    private void ShowRecipePlanner() =>
        ActivateWorkflow(DesktopWorkflow.RecipePlanner);

    [RelayCommand]
    private void ShowMarketAnalysis() =>
        ActivateWorkflow(DesktopWorkflow.MarketAnalysis);

    [RelayCommand]
    private void ShowAcquisitionEvaluation() =>
        ActivateWorkflow(DesktopWorkflow.AcquisitionEvaluation);

    [RelayCommand]
    private void ShowProcurementPlan() =>
        ActivateWorkflow(DesktopWorkflow.ProcurementPlan);

    [RelayCommand]
    private void ShowSettings() =>
        ActivateWorkflow(DesktopWorkflow.Settings);

    [RelayCommand]
    private async Task ShowDiagnosticsAsync()
    {
        ActivateWorkflow(DesktopWorkflow.Diagnostics);
        await RefreshDiagnosticsAsync();
    }

    [RelayCommand]
    private void ToggleActivityDrawer()
    {
        ActivityDrawerVisibility = ActivityDrawerVisibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        ActivityDrawerButtonText = ActivityDrawerVisibility == Visibility.Visible
            ? "Hide Activity"
            : "Activity";
        _logger.LogTrace("Activity drawer toggled. Visibility={Visibility}", ActivityDrawerVisibility);
    }

    [RelayCommand]
    private void SetActivityFilter(string? filter)
    {
        SelectedActivityFilter = NormalizeActivityFilter(filter);
        ApplyActivityFilter();
        _logger.LogTrace("Activity filter changed. Filter={Filter}; VisibleRows={VisibleRows}", SelectedActivityFilter, FilteredActivityLog.Count);
    }

    [RelayCommand]
    private async Task LoadSelectedSnapshotAsync()
    {
        _logger.LogDebug("Load selected snapshot requested. SelectedPlanId={SelectedPlanId}", SelectedStoredPlan?.Id);
        if (SelectedStoredPlan == null)
        {
            PersistenceStatusText = "Select a saved plan";
            OperationStatusText = "Select a saved plan before loading.";
            OperationProgressPercent = 0;
            return;
        }

        IsPersistenceBusy = true;
        PersistenceStatusText = "Loading...";
        try
        {
            var result = await _persistence.LoadPlanIntoSessionAsync(SelectedStoredPlan.Id);
            _logger.LogInformation(
                "Saved plan load completed. SelectedPlanId={SelectedPlanId}; CanLoad={CanLoad}; Warning={Warning}",
                SelectedStoredPlan.Id,
                result?.CanLoad,
                result?.Warning);
            if (result is not { CanLoad: true })
            {
                PersistenceStatusText = result?.Warning ?? "Could not load plan";
                OperationStatusText = PersistenceStatusText;
                OperationProgressPercent = 0;
                return;
            }

            PersistenceStatusText = string.IsNullOrWhiteSpace(result.Warning)
                ? $"Loaded {SelectedStoredPlan.Name}"
                : result.Warning;
            OperationStatusText = PersistenceStatusText;
            OperationProgressPercent = 100;
            await RefreshDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saved plan load failed. SelectedPlanId={SelectedPlanId}", SelectedStoredPlan.Id);
            PersistenceStatusText = $"Load failed: {ex.Message}";
            OperationStatusText = PersistenceStatusText;
            OperationProgressPercent = 0;
        }
        finally
        {
            IsPersistenceBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportSelectedSnapshotJsonAsync()
    {
        _logger.LogDebug("Export snapshot JSON requested. SelectedPlanId={SelectedPlanId}", SelectedStoredPlan?.Id);
        if (IsPersistenceBusy)
        {
            return;
        }

        IsPersistenceBusy = true;
        PersistenceStatusText = "Exporting...";

        try
        {
            var snapshot = SelectedStoredPlan == null
                ? BuildCurrentExportSnapshot()
                : await _persistence.LoadPlanPayloadAsync(SelectedStoredPlan.Id);

            if (snapshot == null)
            {
                PersistenceStatusText = "No saved plan selected";
                OperationStatusText = "Select a saved plan, or save the current plan before exporting.";
                OperationProgressPercent = 0;
                return;
            }

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            var exported = await _fileDialogs.SaveTextFileAsync(
                $"{SanitizeFileName(snapshot.Name)}.craftplan.json",
                json,
                "Craft Architect plan",
                new[] { ".json" });

            PersistenceStatusText = exported ? $"Exported {snapshot.Name}" : "Export cancelled";
            OperationStatusText = PersistenceStatusText;
            OperationProgressPercent = exported ? 100 : 0;
            _logger.LogInformation("Export snapshot JSON completed. SnapshotName={SnapshotName}; Exported={Exported}", snapshot.Name, exported);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export snapshot JSON failed.");
            PersistenceStatusText = $"Export failed: {ex.Message}";
            OperationStatusText = PersistenceStatusText;
            OperationProgressPercent = 0;
        }
        finally
        {
            IsPersistenceBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportSnapshotJsonAsync()
    {
        _logger.LogDebug("Import snapshot JSON requested.");
        if (IsPersistenceBusy)
        {
            return;
        }

        IsPersistenceBusy = true;
        PersistenceStatusText = "Importing...";

        try
        {
            var content = await _fileDialogs.OpenTextFileAsync(
                "Craft Architect plan",
                new[] { ".json" });
            if (content == null)
            {
                PersistenceStatusText = "Import cancelled";
                OperationStatusText = PersistenceStatusText;
                OperationProgressPercent = 0;
                return;
            }

            var snapshot = JsonSerializer.Deserialize<CoreStoredPlanSnapshot>(content);
            _logger.LogDebug("Import snapshot JSON parsed. HasSnapshot={HasSnapshot}", snapshot != null);
            if (snapshot == null)
            {
                PersistenceStatusText = "Import failed";
                OperationStatusText = "Selected file did not contain a Craft Architect plan snapshot.";
                OperationProgressPercent = 0;
                return;
            }

            var result = _sessionLoadService.Load(snapshot, trackStoredPlanIdentity: false);
            if (!result.CanLoad)
            {
                PersistenceStatusText = result.Warning ?? "Import failed";
                OperationStatusText = PersistenceStatusText;
                OperationProgressPercent = 0;
                return;
            }

            var importStatus = string.IsNullOrWhiteSpace(result.Warning)
                ? $"Imported {snapshot.Name}"
                : result.Warning;
            await RefreshDiagnosticsAsync();
            PersistenceStatusText = importStatus;
            OperationStatusText = importStatus;
            OperationProgressPercent = 100;
            _logger.LogInformation("Import snapshot JSON completed. SnapshotName={SnapshotName}; Status={Status}", snapshot.Name, importStatus);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Import snapshot JSON failed during parse.");
            PersistenceStatusText = $"Import failed: {ex.Message}";
            OperationStatusText = PersistenceStatusText;
            OperationProgressPercent = 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import snapshot JSON failed.");
            PersistenceStatusText = $"Import failed: {ex.Message}";
            OperationStatusText = PersistenceStatusText;
            OperationProgressPercent = 0;
        }
        finally
        {
            IsPersistenceBusy = false;
        }
    }

    [RelayCommand]
    private void OpenLocalDataFolder()
    {
        _logger.LogDebug("Open local data folder requested.");
        try
        {
            _localInfrastructure.OpenLocalDataRoot();
            OperationStatusText = "Opened local data folder.";
            InspectorActionStatusText = OperationStatusText;
            OperationProgressPercent = 100;
            AppendActivity("Desktop", OperationStatusText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open local data folder failed.");
            OperationStatusText = $"Could not open local data folder: {ex.Message}";
            InspectorActionStatusText = OperationStatusText;
            OperationProgressPercent = 0;
            AppendActivity("Desktop", OperationStatusText);
        }
    }

    [RelayCommand]
    private async Task CleanupStaleMarketCacheAsync()
    {
        _logger.LogDebug("Clear stale market cache requested.");
        try
        {
            var removed = await _localInfrastructure.CleanupStaleMarketCacheAsync(
                MarketEvidencePolicyDefaults.ReusableCacheMaxAge);
            OperationStatusText = removed == 0
                ? "No stale market cache entries to clear."
                : $"Cleared {removed:N0} stale market cache entr{(removed == 1 ? "y" : "ies")}.";
            InspectorActionStatusText = OperationStatusText;
            OperationProgressPercent = 100;
            AppendActivity("Cache", OperationStatusText);
            _logger.LogInformation("Clear stale market cache completed. Removed={Removed}", removed);
            await RefreshDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clear stale market cache failed.");
            OperationStatusText = $"Could not clear stale market cache entries: {ex.Message}";
            InspectorActionStatusText = OperationStatusText;
            OperationProgressPercent = 0;
            AppendActivity("Cache", OperationStatusText);
        }
    }

    [RelayCommand]
    private async Task ResetMarketCacheAsync()
    {
        _logger.LogWarning("Reset market cache requested.");
        try
        {
            var removed = await _localInfrastructure.ResetMarketCacheAsync();
            OperationStatusText = removed == 0
                ? "Market cache already empty."
                : $"Reset market cache and removed {removed:N0} entr{(removed == 1 ? "y" : "ies")}.";
            InspectorActionStatusText = OperationStatusText;
            OperationProgressPercent = 100;
            AppendActivity("Cache", OperationStatusText);
            _logger.LogInformation("Reset market cache completed. Removed={Removed}", removed);
            await RefreshDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reset market cache failed.");
            OperationStatusText = $"Could not reset market cache: {ex.Message}";
            InspectorActionStatusText = OperationStatusText;
            OperationProgressPercent = 0;
            AppendActivity("Cache", OperationStatusText);
        }
    }

    [RelayCommand]
    private void RefreshDiagnosticLog()
    {
        RefreshDiagnosticLogEntries();
        OperationStatusText = DiagnosticLogSummaryText;
        OperationProgressPercent = DiagnosticLogEntries.Count == 0 ? 0 : 100;
        _logger.LogTrace("Embedded diagnostic log count refreshed. Count={Count}", DiagnosticLogEntries.Count);
    }

    [RelayCommand]
    private void CopySelectedLogEntry()
    {
        if (SelectedLogEntry == null)
        {
            OperationStatusText = "Select a diagnostic log entry before copying.";
            OperationProgressPercent = 0;
            return;
        }

        _clipboard.SetText(SelectedLogEntry.CopyText);
        OperationStatusText = "Copied diagnostic log entry.";
        OperationProgressPercent = 100;
        _logger.LogTrace("Copied embedded diagnostic log entry. Category={Category}; Level={Level}", SelectedLogEntry.Category, SelectedLogEntry.Level);
    }

    [RelayCommand]
    private void OpenDiagnosticLogViewer()
    {
        _logger.LogInformation("Opening diagnostic log viewer from desktop shell.");
        _logViewerLauncher.Open();
        OperationStatusText = "Diagnostic log viewer opened.";
        OperationProgressPercent = 100;
    }

    [RelayCommand]
    private async Task SearchProjectItemsAsync()
    {
        _logger.LogDebug("Target search requested. Query={Query}", NewItemName);
        SearchResults.Clear();

        if (string.IsNullOrWhiteSpace(NewItemName))
        {
            OperationStatusText = "Enter an item name before searching.";
            SetTargetSearchStatus("Item name required for Garland search.");
            OperationProgressPercent = 0;
            return;
        }

        OperationStatusText = $"Searching Garland for '{NewItemName.Trim()}'...";
        SetTargetSearchStatus(OperationStatusText);
        OperationProgressPercent = 15;

        try
        {
            var results = await _garlandService.SearchAsync(NewItemName.Trim());
            foreach (var result in results
                .Where(result => result.Id > 0 && !string.IsNullOrWhiteSpace(result.Object?.Name))
                .Take(8))
            {
                SearchResults.Add(new DesktopPlanSearchResultRow(result.Id, result.Object.Name, "Garland"));
            }
            if (SearchResults.Count == 0)
            {
                var localMatches = AddLocalCatalogSearchResults(NewItemName.Trim());
                OperationStatusText = localMatches == 0
                    ? $"No Garland or local catalog matches for '{NewItemName.Trim()}'."
                    : $"No Garland matches; {localMatches:N0} local catalog match{(localMatches == 1 ? string.Empty : "es")} found.";
            }
            else
            {
                OperationStatusText = $"{SearchResults.Count:N0} Garland match{(SearchResults.Count == 1 ? string.Empty : "es")} found.";
            }

            SetTargetSearchStatus(OperationStatusText);
            OperationProgressPercent = SearchResults.Count == 0 ? 0 : 100;
            _logger.LogInformation(
                "Target search completed. Query={Query}; ResultCount={ResultCount}; Status={Status}",
                NewItemName.Trim(),
                SearchResults.Count,
                OperationStatusText);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Garland target search failed. Query={Query}", NewItemName.Trim());
            var localMatches = AddLocalCatalogSearchResults(NewItemName.Trim());
            OperationStatusText = localMatches == 0
                ? $"Garland search failed: {ex.Message}"
                : $"Garland search failed; {localMatches:N0} local catalog match{(localMatches == 1 ? string.Empty : "es")} shown.";
            SetTargetSearchStatus(OperationStatusText);
            OperationProgressPercent = localMatches == 0 ? 0 : 100;
        }
    }

    [RelayCommand]
    private async Task AddSearchResultAsync(DesktopPlanSearchResultRow? result)
    {
        if (result == null)
        {
            OperationStatusText = "Select a search result before adding a target.";
            SetTargetSearchStatus(OperationStatusText);
            OperationProgressPercent = 0;
            return;
        }

        var resolvedName = result.Name;
        if (result.Source.Equals("Garland", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var item = await _garlandService.GetItemAsync(result.ItemId);
                if (!string.IsNullOrWhiteSpace(item?.Name))
                {
                    resolvedName = item.Name.Trim();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Garland item name resolution failed before adding target. ItemId={ItemId}; SearchName={SearchName}",
                    result.ItemId,
                    result.Name);
            }
        }

        NewItemName = resolvedName;
        var mutation = _projectItemDrafts.AddTarget(_session, result.ItemId, resolvedName, NewItemQuantityText, NewItemMustBeHq);
        _logger.LogInformation(
            "Target added from search result. ItemId={ItemId}; Name={Name}; Quantity={Quantity}; MustBeHq={MustBeHq}; Changed={Changed}",
            result.ItemId,
            resolvedName,
            NewItemQuantityText,
            NewItemMustBeHq,
            mutation.Changed);
        SetTargetSearchStatus(mutation.Message);
        SearchResults.Clear();
        ApplyPlanMutation(mutation, DesktopWorkflow.RecipePlanner);
    }

    [RelayCommand]
    private void AddTargetItem()
    {
        var result = _projectItemDrafts.AddTarget(_session, NewItemName, NewItemQuantityText, NewItemMustBeHq);
        _logger.LogInformation(
            "Direct target add requested. Name={Name}; Quantity={Quantity}; MustBeHq={MustBeHq}; Changed={Changed}; Message={Message}",
            NewItemName,
            NewItemQuantityText,
            NewItemMustBeHq,
            result.Changed,
            result.Message);
        SearchResults.Clear();
        ApplyPlanMutation(result, DesktopWorkflow.RecipePlanner);
    }

    [RelayCommand]
    private void RemoveSelectedTarget()
    {
        var selectedItemId = SelectedPlanItem?.ItemId ?? SelectedMarketQueueItem?.ItemId;
        _logger.LogDebug("Remove selected target requested. ItemId={ItemId}", selectedItemId);
        if (selectedItemId == null)
        {
            OperationStatusText = "Select a root target before removing it.";
            OperationProgressPercent = 0;
            return;
        }

        ApplyPlanMutation(_projectItemDrafts.RemoveTarget(_session, selectedItemId.Value), DesktopWorkflow.RecipePlanner);
    }

    [RelayCommand]
    private void IncreaseSelectedQuantity() =>
        AdjustSelectedQuantity(100);

    [RelayCommand]
    private void DecreaseSelectedQuantity() =>
        AdjustSelectedQuantity(-100);

    [RelayCommand]
    private void ToggleSelectedHq()
    {
        var selectedItemId = SelectedPlanItem?.ItemId ?? SelectedMarketQueueItem?.ItemId;
        _logger.LogDebug("Toggle selected target HQ requested. ItemId={ItemId}", selectedItemId);
        if (selectedItemId == null)
        {
            OperationStatusText = "Select a root target before changing quality.";
            OperationProgressPercent = 0;
            return;
        }

        ApplyPlanMutation(_projectItemDrafts.ToggleRootHq(_session, selectedItemId.Value), DesktopWorkflow.RecipePlanner);
    }

    [RelayCommand]
    private async Task RefreshSelectedAsync()
    {
        var selectedItemId = SelectedPlanItem?.ItemId ?? SelectedMarketQueueItem?.ItemId;
        _logger.LogDebug("Refresh selected item requested. ItemId={ItemId}; DataCenter={DataCenter}", selectedItemId, SelectedDataCenter);
        if (selectedItemId == null)
        {
            InspectorActionStatusText = "Select an item before refreshing its market evidence.";
            OperationStatusText = "No item selected for refresh.";
            OperationProgressPercent = 0;
            return;
        }

        var result = await _marketRefreshQueue.RefreshSelectedItemAsync(_session, selectedItemId.Value, SelectedDataCenter);
        WorkbenchSummary = GetMarketWorkflowSummary();
        InspectorActionStatusText = FormatRefreshQueueResult(result);
        LocalIntegrationStatusText = result.Status == DesktopMarketRefreshQueueStatus.Processed
            ? "Item evidence refreshed"
            : "Item evidence needs review";
        OperationStatusText = FormatRefreshQueueResult(result);
        OperationProgressPercent = 100;
        _logger.LogInformation(
            "Selected item market refresh completed. ItemId={ItemId}; Status={Status}; Detail={Detail}",
            selectedItemId,
            result.Status,
            result.Detail);
    }

    [RelayCommand]
    private async Task RefreshSelectedItemAsync() =>
        await RefreshSelectedAsync();

    [RelayCommand]
    private void OpenSourceDecision()
    {
        _logger.LogTrace("Open source decision requested. InspectorItem={InspectorItem}", GetCurrentInspectorItemName());
        ActivateWorkflow(DesktopWorkflow.AcquisitionEvaluation);
        OperationStatusText = $"Acquisition evaluation ready for {GetCurrentInspectorItemName()}.";
        OperationProgressPercent = 100;
    }

    [RelayCommand]
    private void SetSelectedSourceToCraft() =>
        SetSelectedSource(AcquisitionSource.Craft);

    [RelayCommand]
    private void SetSelectedSourceToMarketNq() =>
        SetSelectedSource(AcquisitionSource.MarketBuyNq);

    [RelayCommand]
    private void SetSelectedSourceToMarketHq() =>
        SetSelectedSource(AcquisitionSource.MarketBuyHq);

    [RelayCommand]
    private void SetSelectedSourceToVendor() =>
        SetSelectedSource(AcquisitionSource.VendorBuy);

    private void SetSelectedSource(AcquisitionSource source)
    {
        var selectedItemId = SelectedPlanItem?.ItemId ?? SelectedMarketQueueItem?.ItemId;
        _logger.LogDebug("Set selected source requested. ItemId={ItemId}; Source={Source}", selectedItemId, source);
        if (selectedItemId == null)
        {
            InspectorActionStatusText = "Select an item before changing source.";
            OperationStatusText = "No item selected for source decision.";
            OperationProgressPercent = 0;
            return;
        }

        var itemName = GetCurrentInspectorItemName();
        var node = _session.ActivePlan?.FindNode(selectedItemId.Value);
        if (node == null || !AcquisitionPlanningService.GetAvailableSources(node).Contains(source))
        {
            InspectorActionStatusText = $"{FormatSource(source)} is not available for {itemName}.";
            OperationStatusText = $"{FormatSource(source)} is not available for {itemName}.";
            OperationProgressPercent = 0;
            return;
        }

        var result = _acquisitionDecisionService.ChangeSource(selectedItemId.Value, source);
        ActivateWorkflow(DesktopWorkflow.AcquisitionEvaluation);
        InspectorActionStatusText = result.Changed
            ? $"{itemName} source set to {FormatSource(source)}."
            : $"{itemName} source is already {FormatSource(source)}.";
        OperationStatusText = result.Changed
            ? $"{itemName} source set to {FormatSource(source)} across {result.NodesUpdated:N0} node{(result.NodesUpdated == 1 ? string.Empty : "s")}."
            : $"Source decision unchanged for {itemName}.";
        OperationProgressPercent = result.Changed ? 100 : 0;
        _logger.LogInformation(
            "Source decision applied. ItemId={ItemId}; ItemName={ItemName}; Source={Source}; Changed={Changed}; NodesUpdated={NodesUpdated}",
            selectedItemId,
            itemName,
            source,
            result.Changed,
            result.NodesUpdated);
    }

    [RelayCommand]
    private void CopyShoppingLine()
    {
        var itemName = GetCurrentInspectorItemName();
        var selectedItemId = SelectedPlanItem?.ItemId ?? SelectedMarketQueueItem?.ItemId;
        var source = selectedItemId is { } itemId ? GetSourceText(itemId) : "Unknown source";
        var line = $"{itemName}: {source}; {InspectorItemDetail}; estimate {InspectorEstimatedCostText}; confidence {InspectorConfidenceText}";
        _clipboard.SetText(line);
        InspectorActionStatusText = $"Copied shopping line for {itemName}.";
        OperationStatusText = $"Copied shopping line for {itemName}.";
        OperationProgressPercent = 100;
        _logger.LogTrace("Copied shopping line. ItemId={ItemId}; ItemName={ItemName}; Source={Source}", selectedItemId, itemName, source);
    }

    [RelayCommand]
    private void CopyProcurementPlanText() =>
        CopyProcurementPlan();

    [RelayCommand]
    private void CopyProcurementPlanCsv()
    {
        _logger.LogDebug("Copy procurement CSV requested. RowCount={RowCount}", MarketQueue.Count);
        if (MarketQueue.Count == 0)
        {
            OperationStatusText = "No procurement rows available to copy.";
            OperationProgressPercent = 0;
            return;
        }

        var lines = new List<string> { "Item,Need,Source,Estimate,Status" };
        lines.AddRange(MarketQueue.Select(row => string.Join(
            ",",
            EscapeCsv(row.ItemName),
            EscapeCsv(row.NeedText),
            EscapeCsv(row.SourceText),
            EscapeCsv(row.CostText),
            EscapeCsv(row.TrustText))));

        _clipboard.SetText(string.Join(Environment.NewLine, lines));
        InspectorActionStatusText = $"{MarketQueue.Count:N0} procurement row{(MarketQueue.Count == 1 ? string.Empty : "s")} copied as CSV.";
        OperationStatusText = InspectorActionStatusText;
        OperationProgressPercent = 100;
        _logger.LogTrace("Copied procurement CSV. RowCount={RowCount}", MarketQueue.Count);
    }

    [RelayCommand]
    private async Task SaveSnapshotAsync()
    {
        _logger.LogDebug("Save snapshot requested. PlanName={PlanName}; Busy={Busy}", PlanName, IsPersistenceBusy);
        if (IsPersistenceBusy)
        {
            return;
        }

        IsPersistenceBusy = true;
        PersistenceStatusText = "Saving...";

        try
        {
            var identity = _session.Identity;
            var planId = !string.IsNullOrWhiteSpace(identity.SourcePlanId)
                ? identity.SourcePlanId
                : $"desktop-{identity.SessionId:N}";
            var planName = !string.IsNullOrWhiteSpace(PlanName)
                ? PlanName
                : identity.Name;

            var saved = await _persistence.SaveCurrentPlanAsync(
                planId,
                planName,
                DateTime.UtcNow,
                includeSourcePlanIdentity: true);

            PersistenceStatusText = saved
                ? $"Saved {DateTime.Now:t}"
                : "Nothing to save";
            _logger.LogInformation("Save snapshot completed. PlanId={PlanId}; PlanName={PlanName}; Saved={Saved}", planId, planName, saved);
            await UpdateLocalSnapshotSummaryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save snapshot failed.");
            PersistenceStatusText = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsPersistenceBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadLatestSnapshotAsync()
    {
        if (IsPersistenceBusy)
        {
            return;
        }

        IsPersistenceBusy = true;
        PersistenceStatusText = "Loading...";

        try
        {
            var summaries = await _persistence.LoadPlanSummariesAsync();
            LocalSnapshotSummaryText = $"Snapshots: {summaries.Count}";
            var latest = summaries.FirstOrDefault();
            if (latest == null)
            {
                PersistenceStatusText = "No saved plans";
                return;
            }

            var result = await _persistence.LoadPlanIntoSessionAsync(latest.Id);
            if (result is not { CanLoad: true })
            {
                PersistenceStatusText = result?.Warning ?? "Could not load plan";
                return;
            }

            PersistenceStatusText = string.IsNullOrWhiteSpace(result.Warning)
                ? $"Loaded {latest.Name}"
                : result.Warning;
            await UpdateLocalSnapshotSummaryAsync();
        }
        catch (Exception ex)
        {
            PersistenceStatusText = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsPersistenceBusy = false;
        }
    }

    private void ApplyStoredDesktopSettings(DesktopSettingsProfile profile)
    {
        var normalized = profile.Normalize();
        SettingsDataCenterText = normalized.DataCenter;
        SettingsWorldText = normalized.World ?? string.Empty;
        DesktopSettingsStorageSummaryText = _desktopSettings.LastStatus;

        _session.ActivatePlan(
            _session.ActivePlan,
            _session.ProjectItems,
            new CraftSessionActiveContext(
                normalized.Region,
                normalized.DataCenter,
                normalized.World,
                MarketFetchScope.SelectedDataCenter),
            "desktop settings loaded",
            _session.Identity);
    }

    private void OnSessionChanged(object? sender, CraftSessionChange change)
    {
        AppendActivity("Session", change.Reason);
        ApplySessionState();
    }

    private void OnOperationChanged(CraftOperationSnapshot snapshot)
    {
        ApplyOperationSnapshot(snapshot);
        LogOperationActivity(snapshot);
    }

    private void ApplySessionState()
    {
        var activePlan = _session.ActivePlan;
        var activeContext = _session.ActiveContext;
        var projectItems = _session.ProjectItems;

        PlanName = !string.IsNullOrWhiteSpace(activePlan?.Name)
            ? activePlan.Name
            : _session.Identity.Name;

        SelectedDataCenter = activeContext.DataCenter
            ?? activePlan?.DataCenter
            ?? "Aether";

        SelectedWorld = !string.IsNullOrWhiteSpace(activeContext.World)
            ? activeContext.World!
            : !string.IsNullOrWhiteSpace(activePlan?.World)
                ? activePlan.World
                : "Data center scope";

        SettingsDataCenterText = SelectedDataCenter;
        SettingsWorldText = SelectedWorld == "Data center scope" ? string.Empty : SelectedWorld;

        PlanGraphSummary = projectItems.Count == 0
            ? "No project items"
            : $"{projectItems.Count} roots, {projectItems.Sum(item => item.Quantity):N0} total requested";

        var marketEvidence = _session.MarketEvidence;
        MarketStatusText = marketEvidence.PublishedAgainstVersion == null
            ? "Market evidence not run"
            : marketEvidence.ItemAnalyses.Any(analysis =>
                analysis.WorstDataQualityBucket == MarketDataQualityBucket.Missing)
                ? "Market evidence incomplete"
                : marketEvidence.ItemAnalyses.All(analysis =>
                    MarketEvidenceFreshness.IsRouteEligible(analysis.WorstDataQualityBucket))
                    ? "Market evidence recommendation-ready"
                    : "Market evidence needs refresh";
        MarketRibbonSummary = MarketStatusText;

        ReplacePlanItems(activePlan, projectItems);
        ReplaceMarketQueue(activePlan, marketEvidence, projectItems);
        ApplyProcurementSnapshot(activePlan, marketEvidence, projectItems);
        ApplyInspector(activePlan, projectItems, marketEvidence);
    }

    private void ApplyOperationSnapshot(CraftOperationSnapshot snapshot)
    {
        OperationStatusText = snapshot.StatusMessage;
        OperationProgressPercent = snapshot.ProgressPercent;
        IsOperationBusy = snapshot.IsBusy;
        JobsStatusText = snapshot.IsBusy
            ? $"{snapshot.OperationName} running"
            : snapshot.ProgressPercent == 100
                ? "Last job complete"
                : "Ready";
    }

    private void LogOperationActivity(CraftOperationSnapshot snapshot)
    {
        if (snapshot.IsBusy || string.IsNullOrWhiteSpace(snapshot.StatusMessage))
        {
            return;
        }

        var key = $"{snapshot.CurrentOperationId}:{snapshot.StatusMessage}:{snapshot.ProgressPercent}";
        if (key == _lastLoggedOperationStatus)
        {
            return;
        }

        _lastLoggedOperationStatus = key;
        AppendActivity("Job", snapshot.StatusMessage);
    }

    private void AppendActivity(string kind, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var row = new DesktopActivityLogRow(DateTime.Now, kind, message.Trim());
        _activityLogStore.Append(new DesktopActivityLogEntry(row.Timestamp.ToUniversalTime(), row.Kind, row.Message));
        ActivityLog.Insert(0, row);
        LatestActivitySummaryText = $"{row.TimeText} {row.Kind}: {row.Message}";
        while (ActivityLog.Count > 24)
        {
            ActivityLog.RemoveAt(ActivityLog.Count - 1);
        }

        ApplyActivityFilter();
    }

    private void LoadActivityHistory()
    {
        ActivityLog.Clear();
        foreach (var entry in _activityLogStore.LoadLatest(24))
        {
            ActivityLog.Add(new DesktopActivityLogRow(entry.Timestamp.ToLocalTime(), entry.Kind, entry.Message));
        }

        var latest = ActivityLog.FirstOrDefault();
        LatestActivitySummaryText = latest == null
            ? "No activity recorded."
            : $"{latest.TimeText} {latest.Kind}: {latest.Message}";
        ApplyActivityFilter();
    }

    private void RefreshDiagnosticLogEntries()
    {
        DiagnosticLogEntries.Clear();
        foreach (var entry in _logStore.LoadLatest(80))
        {
            DiagnosticLogEntries.Add(new DesktopLogRow(
                entry.Timestamp.ToLocalTime(),
                entry.Level,
                entry.Category,
                entry.EventId,
                entry.Message,
                entry.Exception,
                entry.StackTrace));
        }

        SelectedLogEntry = DiagnosticLogEntries.FirstOrDefault();
        DiagnosticLogSummaryText = DiagnosticLogEntries.Count == 0
            ? "No diagnostic log entries loaded."
            : $"{DiagnosticLogEntries.Count:N0} recent diagnostic log entr{(DiagnosticLogEntries.Count == 1 ? "y" : "ies")} loaded.";
    }

    private void ApplyActivityFilter()
    {
        FilteredActivityLog.Clear();
        foreach (var row in ActivityLog.Where(MatchesSelectedActivityFilter))
        {
            FilteredActivityLog.Add(row);
        }

        UpdateActivityFilterState();
    }

    private bool MatchesSelectedActivityFilter(DesktopActivityLogRow row) =>
        SelectedActivityFilter switch
        {
            ActivityFilterAll => true,
            ActivityFilterDesktop => row.Kind is "Desktop" or "Shell",
            _ => row.Kind == SelectedActivityFilter
        };

    private static string NormalizeActivityFilter(string? filter) =>
        filter switch
        {
            ActivityFilterSession => ActivityFilterSession,
            ActivityFilterJob => ActivityFilterJob,
            ActivityFilterCache => ActivityFilterCache,
            ActivityFilterDesktop => ActivityFilterDesktop,
            _ => ActivityFilterAll
        };

    private void UpdateActivityFilterState()
    {
        ActivityAllFilterBackground = SelectedActivityFilter == ActivityFilterAll
            ? ActiveActivityFilterBrush
            : InactiveActivityFilterBrush;
        ActivitySessionFilterBackground = SelectedActivityFilter == ActivityFilterSession
            ? ActiveActivityFilterBrush
            : InactiveActivityFilterBrush;
        ActivityJobFilterBackground = SelectedActivityFilter == ActivityFilterJob
            ? ActiveActivityFilterBrush
            : InactiveActivityFilterBrush;
        ActivityCacheFilterBackground = SelectedActivityFilter == ActivityFilterCache
            ? ActiveActivityFilterBrush
            : InactiveActivityFilterBrush;
        ActivityDesktopFilterBackground = SelectedActivityFilter == ActivityFilterDesktop
            ? ActiveActivityFilterBrush
            : InactiveActivityFilterBrush;

        var shown = FilteredActivityLog.Count;
        var total = ActivityLog.Count;
        ActivityFilterSummaryText = SelectedActivityFilter == ActivityFilterAll
            ? $"{shown:N0} recent event{(shown == 1 ? string.Empty : "s")} shown."
            : $"{shown:N0} of {total:N0} recent event{(total == 1 ? string.Empty : "s")} shown for {SelectedActivityFilter}.";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _session.Changed -= OnSessionChanged;
        _operationState.Changed -= OnOperationChanged;
        _disposed = true;
    }

    private void ReplacePlanItems(CraftingPlan? activePlan, IReadOnlyList<ProjectItem> projectItems)
    {
        var selectedItemId = SelectedPlanItem?.ItemId;
        PlanItems.Clear();
        TargetItems.Clear();

        foreach (var item in projectItems.Select(item => new DesktopPlanItemRow(
            item.Id,
            item.Name,
            item.Quantity,
            $"x{item.Quantity:N0}",
            item.MustBeHq ? "HQ target" : "NQ target",
            "Draft",
            EstimateCostText(item.Id, item.Quantity),
            0,
            true)))
        {
            TargetItems.Add(item);
        }

        if (activePlan != null)
        {
            foreach (var item in EnumeratePlanRows(activePlan.RootItems))
            {
                PlanItems.Add(item);
            }
        }

        RecipePlanRowsVisibility = PlanItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RecipePlanEmptyVisibility = PlanItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecipePlanEmptyText = projectItems.Count == 0
            ? "Add target items in Plan Builder before building a recipe plan."
            : "Draft targets are ready. Build the project plan to show recipe nodes.";

        SelectedPlanItem = PlanItems.FirstOrDefault(item => item.ItemId == selectedItemId)
            ?? TargetItems.FirstOrDefault()
            ?? PlanItems.FirstOrDefault();
    }

    private void ReplaceMarketQueue(
        CraftingPlan? activePlan,
        CraftSessionMarketEvidence marketEvidence,
        IReadOnlyList<ProjectItem> projectItems)
    {
        MarketQueue.Clear();

        if (marketEvidence.ShoppingPlans?.Count > 0)
        {
            foreach (var plan in marketEvidence.ShoppingPlans)
            {
                MarketQueue.Add(new DesktopMarketQueueRow(
                    plan.ItemId,
                    plan.Name,
                    plan.QuantityNeeded.ToString("N0"),
                    GetSourceText(plan.ItemId),
                    GetShoppingPlanTrust(plan),
                    FormatGil(GetShoppingPlanCostOrEstimate(plan))));
            }

            return;
        }

        if (marketEvidence.ItemAnalyses.Count > 0)
        {
            foreach (var analysis in marketEvidence.ItemAnalyses)
            {
                MarketQueue.Add(new DesktopMarketQueueRow(
                    analysis.ItemId,
                    analysis.Name,
                    analysis.QuantityNeeded.ToString("N0"),
                    GetSourceText(analysis.ItemId),
                    GetMarketAnalysisTrustText(analysis),
                    GetMarketAnalysisCostText(analysis)));
            }

            return;
        }

        var activeProcurementItems = activePlan == null
            ? projectItems.Select(item => new MaterialAggregate
            {
                ItemId = item.Id,
                Name = item.Name,
                TotalQuantity = item.Quantity,
                RequiresHq = item.MustBeHq
            })
            : AcquisitionPlanningService.GetActiveProcurementItems(activePlan)
                .Where(item => item.TotalQuantity > 0);
        foreach (var item in activeProcurementItems)
        {
            MarketQueue.Add(new DesktopMarketQueueRow(
                item.ItemId,
                item.Name,
                item.TotalQuantity.ToString("N0"),
                GetSourceText(item.ItemId),
                "Needs data",
                EstimateCostText(item.ItemId, item.TotalQuantity)));
        }
    }

    private void ApplyProcurementSnapshot(
        CraftingPlan? activePlan,
        CraftSessionMarketEvidence marketEvidence,
        IReadOnlyList<ProjectItem> projectItems)
    {
        var materials = activePlan == null
            ? projectItems.Select(item => new MaterialAggregate
            {
                ItemId = item.Id,
                Name = item.Name,
                TotalQuantity = item.Quantity,
                RequiresHq = item.MustBeHq
            }).ToList()
            : AcquisitionPlanningService.GetActiveProcurementItems(activePlan)
                .Where(item => item.TotalQuantity > 0)
                .ToList();
        var pricedIds = marketEvidence.ShoppingPlans?
            .Where(plan => PurchaseRecommendationCost.GetRecommendedCost(plan) > 0)
            .Select(plan => plan.ItemId)
            .ToHashSet()
            ?? new HashSet<int>();
        var unavailableIds = marketEvidence.UnavailableMarketItemIds;

        TargetItemCountText = activePlan?.RootItems.Count.ToString("N0")
            ?? projectItems.Count.ToString("N0");
        MaterialEntryCountText = materials.Count.ToString("N0");
        HqRequiredCountText = materials.Count(item => item.RequiresHq).ToString("N0");
        PricedMaterialCountText = materials.Count(item => pricedIds.Contains(item.ItemId)).ToString("N0");
        MissingPriceCountText = materials.Count(item => !pricedIds.Contains(item.ItemId) && !unavailableIds.Contains(item.ItemId)).ToString("N0");
        MarketUnavailableCountText = materials.Count(item => unavailableIds.Contains(item.ItemId)).ToString("N0");
    }

    private IEnumerable<DesktopPlanItemRow> EnumeratePlanRows(IEnumerable<PlanNode> roots)
    {
        foreach (var root in roots)
        {
            foreach (var row in EnumeratePlanRows(root, 0))
            {
                yield return row;
            }
        }
    }

    private IEnumerable<DesktopPlanItemRow> EnumeratePlanRows(PlanNode node, int depth)
    {
        yield return new DesktopPlanItemRow(
            node.ItemId,
            node.Name,
            node.Quantity,
            $"x{node.Quantity:N0}",
            $"{(node.MustBeHq ? "HQ" : "NQ")} {(depth == 0 ? "target" : "material")}",
            FormatSource(node.Source),
            EstimateCostText(node.ItemId, node.Quantity),
            depth,
            depth == 0);

        foreach (var child in node.Children)
        {
            foreach (var row in EnumeratePlanRows(child, depth + 1))
            {
                yield return row;
            }
        }
    }

    private static IEnumerable<PlanNode> EnumeratePlanNodes(CraftingPlan plan)
    {
        foreach (var root in plan.RootItems)
        {
            foreach (var node in EnumeratePlanNodes(root))
            {
                yield return node;
            }
        }
    }

    private static IEnumerable<PlanNode> EnumeratePlanNodes(PlanNode node)
    {
        yield return node;

        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumeratePlanNodes(child))
            {
                yield return descendant;
            }
        }
    }

    private static string GetShoppingPlanTrust(DetailedShoppingPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.Error))
        {
            return "Blocked";
        }

        if (!string.IsNullOrWhiteSpace(plan.MarketDataWarning))
        {
            return "Review";
        }

        return plan.HasSufficientStock ? "Ready" : "Thin";
    }

    private static string GetMarketAnalysisTrustText(MarketItemAnalysis analysis)
    {
        if (analysis.WorstDataQualityBucket == MarketDataQualityBucket.Missing)
        {
            return string.IsNullOrWhiteSpace(analysis.Warning) ? "Missing" : "No data";
        }

        return MarketEvidenceFreshness.IsRouteEligible(analysis.WorstDataQualityBucket)
            ? "Recommendation-ready"
            : analysis.WorstDataQualityBucket.ToString();
    }

    private static string GetMarketAnalysisCostText(MarketItemAnalysis analysis)
    {
        if (analysis.AnalysisScopeAverageUnitPrice <= 0)
        {
            return EstimateCostText(analysis.ItemId, analysis.QuantityNeeded);
        }

        return FormatGil((long)(analysis.AnalysisScopeAverageUnitPrice * analysis.QuantityNeeded));
    }

    private void ApplyInspector(
        CraftingPlan? activePlan,
        IReadOnlyList<ProjectItem> projectItems,
        CraftSessionMarketEvidence marketEvidence)
    {
        var selectedItemId = SelectedPlanItem?.ItemId ?? SelectedMarketQueueItem?.ItemId;
        var node = selectedItemId is { } selectedNodeId
            ? activePlan?.FindNode(selectedNodeId)
            : activePlan?.RootItems.FirstOrDefault();
        var projectItem = selectedItemId is { } fallbackId
            ? projectItems.FirstOrDefault(item => item.Id == fallbackId) ?? projectItems.FirstOrDefault()
            : projectItems.FirstOrDefault();
        if (node == null && projectItem == null)
        {
            InspectorItemName = "No item selected";
            InspectorItemDetail = "Build or load a plan to inspect item details.";
            InspectorEstimatedCostText = "Pending";
            InspectorConfidenceText = "Needs data";
            ApplySourceOptions(null);
            return;
        }

        var itemId = node?.ItemId ?? projectItem!.Id;
        var itemName = node?.Name ?? projectItem!.Name;
        var quantity = node?.Quantity ?? projectItem!.Quantity;
        var mustBeHq = node?.MustBeHq ?? projectItem!.MustBeHq;
        var sourceText = GetSourceText(itemId);

        InspectorItemName = itemName;
        InspectorItemDetail = $"{(mustBeHq ? "HQ" : "NQ")} {sourceText}; {quantity:N0} needed";

        var shoppingPlan = marketEvidence.ShoppingPlans?
            .FirstOrDefault(plan => plan.ItemId == itemId);
        var marketAnalysis = marketEvidence.ItemAnalyses
            .FirstOrDefault(analysis => analysis.ItemId == itemId);
        var shoppingPlanCost = shoppingPlan != null
            ? PurchaseRecommendationCost.GetRecommendedCost(shoppingPlan)
            : 0;
        InspectorEstimatedCostText = shoppingPlanCost > 0
            ? FormatGil(shoppingPlanCost)
            : marketAnalysis != null
                ? GetMarketAnalysisCostText(marketAnalysis)
            : EstimateCostText(itemId, quantity);

        InspectorConfidenceText = GetInspectorConfidenceText(itemId, marketEvidence, shoppingPlan);
        ApplySourceOptions(node);
    }

    private static string GetInspectorConfidenceText(
        int itemId,
        CraftSessionMarketEvidence marketEvidence,
        DetailedShoppingPlan? shoppingPlan)
    {
        if (marketEvidence.PublishedAgainstVersion == null)
        {
            return "Needs data";
        }

        var itemAnalysis = marketEvidence.ItemAnalyses
            .FirstOrDefault(analysis => analysis.ItemId == itemId);
        if (itemAnalysis == null)
        {
            return "Needs data";
        }

        if (itemAnalysis.WorstDataQualityBucket == MarketDataQualityBucket.Missing)
        {
            return "No data";
        }

        if (MarketEvidenceFreshness.IsRouteEligible(itemAnalysis.WorstDataQualityBucket))
        {
            return "Recommendation-ready";
        }

        return shoppingPlan == null ? "Review" : GetShoppingPlanTrust(shoppingPlan);
    }

    private long GetShoppingPlanCostOrEstimate(DetailedShoppingPlan plan)
    {
        var recommendedCost = PurchaseRecommendationCost.GetRecommendedCost(plan);
        return recommendedCost > 0
            ? recommendedCost
            : EstimateCost(plan.ItemId, plan.QuantityNeeded);
    }

    private static string FormatGil(long value) =>
        value switch
        {
            >= 1_000_000 => $"{value / 1_000_000m:0.#}m",
            >= 1_000 => $"{value / 1_000m:0.#}k",
            _ => value.ToString("N0")
        };

    private static string EstimateCostText(int itemId, int quantity) =>
        FormatGil(EstimateCost(itemId, quantity));

    private static long EstimateCost(int itemId, int quantity)
    {
        var unitPrice = 85 + Math.Abs(itemId * 37 % 4_250);
        return Math.Max(1, quantity) * unitPrice;
    }

    private void ApplySourceOptions(PlanNode? node)
    {
        if (node == null)
        {
            CraftSourceOptionVisibility = Visibility.Collapsed;
            MarketNqSourceOptionVisibility = Visibility.Collapsed;
            MarketHqSourceOptionVisibility = Visibility.Collapsed;
            VendorSourceOptionVisibility = Visibility.Collapsed;
            CraftSourceOptionBackground = AvailableSourceBrush;
            MarketNqSourceOptionBackground = AvailableSourceBrush;
            MarketHqSourceOptionBackground = AvailableSourceBrush;
            VendorSourceOptionBackground = AvailableSourceBrush;
            return;
        }

        var availableSources = AcquisitionPlanningService.GetAvailableSources(node);
        CraftSourceOptionVisibility = availableSources.Contains(AcquisitionSource.Craft) ? Visibility.Visible : Visibility.Collapsed;
        MarketNqSourceOptionVisibility = availableSources.Contains(AcquisitionSource.MarketBuyNq) ? Visibility.Visible : Visibility.Collapsed;
        MarketHqSourceOptionVisibility = availableSources.Contains(AcquisitionSource.MarketBuyHq) ? Visibility.Visible : Visibility.Collapsed;
        VendorSourceOptionVisibility = availableSources.Contains(AcquisitionSource.VendorBuy) ? Visibility.Visible : Visibility.Collapsed;

        CraftSourceOptionBackground = node.Source == AcquisitionSource.Craft ? ActiveSourceBrush : AvailableSourceBrush;
        MarketNqSourceOptionBackground = node.Source == AcquisitionSource.MarketBuyNq ? ActiveSourceBrush : AvailableSourceBrush;
        MarketHqSourceOptionBackground = node.Source == AcquisitionSource.MarketBuyHq ? ActiveSourceBrush : AvailableSourceBrush;
        VendorSourceOptionBackground = node.Source == AcquisitionSource.VendorBuy ? ActiveSourceBrush : AvailableSourceBrush;
    }

    private string GetSourceText(int itemId)
    {
        var source = _session.ActivePlan?.FindNode(itemId)?.Source;
        return source == null ? "Plan" : FormatSource(source.Value);
    }

    private static string FormatSource(AcquisitionSource source) =>
        source switch
        {
            AcquisitionSource.Craft => "Craft",
            AcquisitionSource.MarketBuyNq => "Buy NQ",
            AcquisitionSource.MarketBuyHq => "Buy HQ",
            AcquisitionSource.VendorBuy => "Vendor",
            AcquisitionSource.VendorSpecialCurrency => "Special Vendor",
            _ => "Unknown"
        };

    private void ActivateWorkflow(string workflow)
    {
        _logger.LogTrace("Activating workflow. PreviousWorkflow={PreviousWorkflow}; NextWorkflow={NextWorkflow}", ActiveWorkflow, workflow);
        ActiveWorkflow = workflow;
        RecipePlannerPanelVisibility = workflow == DesktopWorkflow.RecipePlanner ? Visibility.Visible : Visibility.Collapsed;
        MarketAnalysisPanelVisibility = workflow == DesktopWorkflow.MarketAnalysis ? Visibility.Visible : Visibility.Collapsed;
        AcquisitionEvaluationPanelVisibility = workflow == DesktopWorkflow.AcquisitionEvaluation ? Visibility.Visible : Visibility.Collapsed;
        ProcurementPlanPanelVisibility = workflow == DesktopWorkflow.ProcurementPlan ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanelVisibility = workflow == DesktopWorkflow.Settings ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanelVisibility = workflow == DesktopWorkflow.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
        MarketItemActionVisibility = workflow == DesktopWorkflow.MarketAnalysis ? Visibility.Visible : Visibility.Collapsed;
        SourceDecisionActionVisibility = workflow == DesktopWorkflow.AcquisitionEvaluation ? Visibility.Visible : Visibility.Collapsed;
        ProcurementActionVisibility = workflow == DesktopWorkflow.ProcurementPlan ? Visibility.Visible : Visibility.Collapsed;
        PlanEditActionVisibility = workflow == DesktopWorkflow.RecipePlanner ? Visibility.Visible : Visibility.Collapsed;
        RecipePlannerTabBackground = workflow == DesktopWorkflow.RecipePlanner ? ActiveTabBrush : InactiveTabBrush;
        MarketAnalysisTabBackground = workflow == DesktopWorkflow.MarketAnalysis ? ActiveTabBrush : InactiveTabBrush;
        AcquisitionEvaluationTabBackground = workflow == DesktopWorkflow.AcquisitionEvaluation ? ActiveTabBrush : InactiveTabBrush;
        ProcurementPlanTabBackground = workflow == DesktopWorkflow.ProcurementPlan ? ActiveTabBrush : InactiveTabBrush;
        ApplyWorkflowPaneLayout(workflow);

        (WorkbenchTitle, WorkbenchSummary, PrimaryActionText) = workflow switch
        {
            DesktopWorkflow.RecipePlanner => (
                "Recipe Planner",
                PlanGraphSummary,
                "Build Project Plan"),
            DesktopWorkflow.MarketAnalysis => (
                "Market Evidence",
                GetMarketWorkflowSummary(),
                "Refresh Market Evidence"),
            DesktopWorkflow.AcquisitionEvaluation => (
                "Acquisition Evaluation",
                $"Review source intent for {GetCurrentInspectorItemName()} before procurement.",
                "Review Source"),
            DesktopWorkflow.ProcurementPlan => (
                "Procurement Plan",
                $"{MarketQueue.Count:N0} procurement row{(MarketQueue.Count == 1 ? string.Empty : "s")} ready for local clipboard export.",
                "Copy Plan Lines"),
            DesktopWorkflow.Settings => (
                "Desktop Settings",
                "Apply market context used by desktop refresh and saved plans.",
                "Apply Settings"),
            DesktopWorkflow.Diagnostics => (
                "Diagnostics",
                "Inspect session versions, local snapshots, and desktop workflow state.",
                "Refresh Diagnostics"),
            _ => (
                "Desktop Workbench",
                "Choose a workflow to continue.",
                "Continue")
        };

        OperationStatusText = $"{workflow} selected.";
    }

    private void ApplyWorkflowPaneLayout(string workflow)
    {
        var showLeft = workflow == DesktopWorkflow.RecipePlanner;
        var showRight = workflow is DesktopWorkflow.RecipePlanner
            or DesktopWorkflow.MarketAnalysis
            or DesktopWorkflow.AcquisitionEvaluation
            or DesktopWorkflow.ProcurementPlan;

        LeftPaneVisibility = showLeft ? Visibility.Visible : Visibility.Collapsed;
        LeftPaneWidth = showLeft ? new GridLength(320) : new GridLength(0);
        LeftPaneMinWidth = showLeft ? 260 : 0;

        RightPaneVisibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        RightPaneWidth = showRight ? new GridLength(390) : new GridLength(0);
        RightPaneMinWidth = showRight ? 320 : 0;
    }

    private string GetCurrentInspectorItemName() =>
        InspectorItemName == "No item selected" ? "selected item" : InspectorItemName;

    private void CopyProcurementPlan()
    {
        _logger.LogDebug("Copy procurement plan requested. RowCount={RowCount}", MarketQueue.Count);
        if (MarketQueue.Count == 0)
        {
            OperationStatusText = "No procurement rows available to copy.";
            OperationProgressPercent = 0;
            return;
        }

        var lines = MarketQueue.Select(row => $"{row.ItemName}: {row.SourceText}; need {row.NeedText}; est. {row.CostText}; status {row.TrustText}");
        _clipboard.SetText(string.Join(Environment.NewLine, lines));
        InspectorActionStatusText = $"{MarketQueue.Count:N0} procurement line{(MarketQueue.Count == 1 ? string.Empty : "s")} copied.";
        OperationStatusText = $"{MarketQueue.Count:N0} procurement line{(MarketQueue.Count == 1 ? string.Empty : "s")} copied to clipboard.";
        OperationProgressPercent = 100;
        _logger.LogTrace("Copied procurement plan text. RowCount={RowCount}", MarketQueue.Count);
    }

    private CoreStoredPlanSnapshot? BuildCurrentExportSnapshot()
    {
        if (_session.ActivePlan == null && _session.ProjectItems.Count == 0)
        {
            return null;
        }

        var identity = _session.Identity;
        var planId = !string.IsNullOrWhiteSpace(identity.SourcePlanId)
            ? identity.SourcePlanId
            : $"desktop-{identity.SessionId:N}";
        var planName = !string.IsNullOrWhiteSpace(PlanName)
            ? PlanName
            : identity.Name;

        return _persistence.BuildSnapshot(
            planId,
            planName,
            DateTime.UtcNow,
            includeSourcePlanIdentity: true);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(character =>
            invalidCharacters.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "craft-architect-plan" : sanitized.Trim();
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private void AdjustSelectedQuantity(int delta)
    {
        var selectedItemId = SelectedPlanItem?.ItemId ?? SelectedMarketQueueItem?.ItemId;
        _logger.LogDebug("Adjust selected target quantity requested. ItemId={ItemId}; Delta={Delta}", selectedItemId, delta);
        if (selectedItemId == null)
        {
            OperationStatusText = "Select a root target before changing quantity.";
            OperationProgressPercent = 0;
            return;
        }

        ApplyPlanMutation(_projectItemDrafts.AdjustRootQuantity(_session, selectedItemId.Value, delta), DesktopWorkflow.RecipePlanner);
    }

    private void ApplyPlanMutation(DesktopPlanMutationResult result, string workflow)
    {
        _logger.LogDebug("Applying plan mutation. Changed={Changed}; Workflow={Workflow}; Message={Message}", result.Changed, workflow, result.Message);
        if (result.Changed)
        {
            ActivateWorkflow(workflow);
            OperationProgressPercent = 100;
        }
        else
        {
            OperationProgressPercent = 0;
        }

        OperationStatusText = result.Message;
        InspectorActionStatusText = result.Message;
    }

    private void SetTargetSearchStatus(string message)
    {
        TargetSearchStatusText = string.IsNullOrWhiteSpace(message)
            ? "No Garland search run."
            : message;
        TargetSearchStatusVisibility = Visibility.Visible;
    }

    private int AddLocalCatalogSearchResults(string query)
    {
        var existingIds = SearchResults
            .Select(result => result.ItemId)
            .ToHashSet();
        var localMatches = _projectItemDrafts.SearchKnownItems(query)
            .Where(item => !existingIds.Contains(item.ItemId))
            .Take(8 - SearchResults.Count)
            .ToList();

        foreach (var item in localMatches)
        {
            SearchResults.Add(new DesktopPlanSearchResultRow(item.ItemId, item.Name, "Local catalog"));
        }

        return localMatches.Count;
    }

    partial void OnNewItemNameChanged(string value)
    {
        SearchResults.Clear();
        if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 2)
        {
            AddLocalCatalogSearchResults(value.Trim());
            if (SearchResults.Count > 0)
            {
                SetTargetSearchStatus($"{SearchResults.Count:N0} local catalog suggestion{(SearchResults.Count == 1 ? string.Empty : "s")}.");
                return;
            }
        }
    }

    private async Task BuildProjectPlanAsync()
    {
        var projectItems = _session.ProjectItems.ToList();
        _logger.LogDebug(
            "Recipe build requested. TargetCount={TargetCount}; Targets={Targets}",
            projectItems.Count,
            string.Join("; ", projectItems.Select(item => $"{item.Name} ({item.Id}) x{item.Quantity:N0}{(item.MustBeHq ? " HQ" : string.Empty)}")));
        if (projectItems.Count == 0)
        {
            OperationStatusText = "Add at least one target item before building a recipe plan.";
            OperationProgressPercent = 0;
            return;
        }

        using var lease = _operationCoordinator.Start(
            CraftOperationWorkflow.RecipeBuild,
            "Desktop Recipe Build",
            "Fetching recipe data from Garland...");

        try
        {
            var dataCenter = string.IsNullOrWhiteSpace(SettingsDataCenterText)
                ? SelectedDataCenter
                : SettingsDataCenterText.Trim();
            var world = string.IsNullOrWhiteSpace(SettingsWorldText)
                ? string.Empty
                : SettingsWorldText.Trim();

            lease.ReportProgress(20, "Building recipe tree...");
            var targetItems = projectItems
                .Select(item => (item.Id, item.Name, item.Quantity, item.MustBeHq))
                .ToList();
            var builtPlan = await _recipePlanBuilder.BuildPlanAsync(
                targetItems,
                dataCenter,
                world,
                lease.Token);

            builtPlan.Name = string.IsNullOrWhiteSpace(PlanName)
                ? builtPlan.Name
                : PlanName;
            builtPlan.DataCenter = dataCenter;
            builtPlan.World = world;

            lease.ReportProgress(85, "Publishing recipe plan...");
            var published = lease.CompleteIfCurrent(
                () => _session.ActivatePlan(
                    builtPlan,
                    projectItems,
                    new CraftSessionActiveContext(
                        "North America",
                        dataCenter,
                        string.IsNullOrWhiteSpace(world) ? null : world,
                        MarketFetchScope.SelectedDataCenter),
                    "desktop recipe plan built",
                    _session.Identity),
                "Recipe plan built.");

            if (published)
            {
                _logger.LogInformation(
                    "Recipe plan published. TargetCount={TargetCount}; DataCenter={DataCenter}; World={World}",
                    projectItems.Count,
                    dataCenter,
                    world);
                OperationStatusText = $"Recipe plan built: {projectItems.Count:N0} target item{(projectItems.Count == 1 ? string.Empty : "s")}.";
                OperationProgressPercent = 100;
                InspectorActionStatusText = OperationStatusText;
            }
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
        {
            lease.CompleteStatusIfCurrent("Recipe build cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Recipe build failed for {TargetCount} target item(s): {Targets}",
                projectItems.Count,
                string.Join(
                    "; ",
                    projectItems.Select(item => $"{item.Name} ({item.Id}) x{item.Quantity:N0}{(item.MustBeHq ? " HQ" : string.Empty)}")));
            lease.CompleteStatusIfCurrent($"Recipe build failed: {ex.Message}");
            OperationStatusText = $"Recipe build failed: {ex.Message}";
            OperationProgressPercent = 0;
            InspectorActionStatusText = OperationStatusText;
            RefreshDiagnosticLogEntries();
        }
    }

    private void ApplyDesktopSettings()
    {
        _logger.LogDebug("Apply desktop settings requested. DataCenterText={DataCenter}; WorldText={World}", SettingsDataCenterText, SettingsWorldText);
        var dataCenter = string.IsNullOrWhiteSpace(SettingsDataCenterText)
            ? "Aether"
            : SettingsDataCenterText.Trim();
        var world = string.IsNullOrWhiteSpace(SettingsWorldText)
            ? null
            : SettingsWorldText.Trim();
        var profile = new DesktopSettingsProfile
        {
            Region = "North America",
            DataCenter = dataCenter,
            World = world
        }.Normalize();

        var activePlan = _session.ActivePlan;
        if (activePlan != null)
        {
            activePlan.DataCenter = profile.DataCenter;
            activePlan.World = profile.World ?? string.Empty;
        }

        _session.ActivatePlan(
            activePlan,
            _session.ProjectItems,
            new CraftSessionActiveContext(profile.Region, profile.DataCenter, profile.World, MarketFetchScope.SelectedDataCenter),
            "desktop settings context applied",
            _session.Identity);

        try
        {
            _desktopSettings.Save(profile);
            DesktopSettingsStorageSummaryText = _desktopSettings.LastStatus;
            PersistenceStatusText = "Settings saved";
            OperationStatusText = $"Desktop settings applied: {profile.FormatContext()}. Market evidence was cleared.";
            InspectorActionStatusText = OperationStatusText;
            OperationProgressPercent = 100;
            _logger.LogInformation("Desktop settings saved. DataCenter={DataCenter}; World={World}", profile.DataCenter, profile.World);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Desktop settings save failed.");
            DesktopSettingsStorageSummaryText = $"Desktop settings could not be saved. {ex.Message}";
            PersistenceStatusText = "Settings save failed";
            OperationStatusText = DesktopSettingsStorageSummaryText;
            InspectorActionStatusText = OperationStatusText;
            OperationProgressPercent = 0;
        }
    }

    private async Task RefreshDiagnosticsAsync()
    {
        _logger.LogDebug("Diagnostics refresh requested.");
        var summaries = await _persistence.LoadPlanSummariesAsync();
        var versions = _session.Versions;
        var dirtyBuckets = _session.DirtyBuckets;
        var changes = _session.Changes;
        var evidence = _session.MarketEvidence;
        var shoppingPlanCount = evidence.ShoppingPlans?.Count ?? 0;

        LocalSnapshotSummaryText = $"Snapshots: {summaries.Count}";
        SavedPlans.Clear();
        foreach (var summary in summaries)
        {
            SavedPlans.Add(new DesktopStoredPlanRow(
                summary.Id,
                summary.Name,
                summary.DataCenter,
                summary.ProjectItemCount,
                summary.SavedAt));
        }

        SelectedStoredPlan ??= SavedPlans.FirstOrDefault();
        SessionVersionSummaryText =
            $"plan {versions.PlanCore}, decisions {versions.PlanDecision}, prices {versions.PlanPrice}, market {versions.MarketAnalysis}, procurement {versions.Procurement}, settings {versions.SettingsContext}";
        DirtyBucketSummaryText = dirtyBuckets.Count == 0
            ? "No dirty buckets"
            : string.Join(", ", dirtyBuckets);
        ChangeLogSummaryText = changes.Count == 0
            ? "No changes recorded"
            : changes[^1].Reason;
        MarketEvidenceSummaryText = evidence.PublishedAgainstVersion == null
            ? "Market evidence not run"
            : $"{evidence.ItemAnalyses.Count:N0} market row{(evidence.ItemAnalyses.Count == 1 ? string.Empty : "s")}, {shoppingPlanCount:N0} shopping plan{(shoppingPlanCount == 1 ? string.Empty : "s")}";
        var localInfrastructure = await _localInfrastructure.InspectAsync();
        DesktopLocalInfrastructureSummaryText = localInfrastructure.Summary;
        MarketCacheHealthText = $"{localInfrastructure.MarketCacheHealthText} {localInfrastructure.MarketCacheAgeText}.";
        MarketCacheRepairText = localInfrastructure.MarketCacheRecommendedAction;
        MarketCachePathText = localInfrastructure.MarketCachePath;
        ActivityLogPathText = localInfrastructure.ActivityLogPath;
        DiagnosticLogPathText = localInfrastructure.DiagnosticLogPath;
        RefreshDiagnosticLogEntries();
        DesktopSettingsStorageSummaryText = $"{_desktopSettings.LastStatus} Path: {_desktopSettings.SettingsPath}";
        DesktopInfrastructureSummaryText =
            $"{TargetItems.Count:N0} target rows, {PlanItems.Count:N0} recipe rows, {MarketQueue.Count:N0} procurement rows, {summaries.Count:N0} local snapshots";
        OperationStatusText = $"Diagnostics refreshed: {DesktopInfrastructureSummaryText}.";
        OperationProgressPercent = 100;
        _logger.LogInformation(
            "Diagnostics refreshed. SavedPlans={SavedPlans}; Targets={Targets}; RecipeRows={RecipeRows}; ProcurementRows={ProcurementRows}; LogEntries={LogEntries}",
            summaries.Count,
            TargetItems.Count,
            PlanItems.Count,
            MarketQueue.Count,
            DiagnosticLogEntries.Count);
    }

    private string GetMarketWorkflowSummary()
    {
        var analyses = _session.MarketEvidence.ItemAnalyses;
        if (analyses.Count == 0)
        {
            return "Refresh market evidence for every active procurement item in the plan.";
        }

        var eligibleCount = analyses.Count(analysis =>
            MarketEvidenceFreshness.IsRouteEligible(analysis.WorstDataQualityBucket));
        var reviewCount = analyses.Count - eligibleCount;
        return reviewCount == 0
            ? $"{eligibleCount:N0} procurement item{(eligibleCount == 1 ? string.Empty : "s")} have recommendation-ready market evidence."
            : $"{eligibleCount:N0} recommendation-ready, {reviewCount:N0} need refresh.";
    }

    private static string FormatRefreshQueueResult(DesktopMarketRefreshQueueResult result) =>
        result.Status switch
        {
            DesktopMarketRefreshQueueStatus.Processed => result.ItemCount == 1
                ? $"{result.ItemName ?? "Selected item"} market evidence is fresh."
                : $"Market evidence refreshed for {result.ItemCount:N0} items.",
            DesktopMarketRefreshQueueStatus.NoData => result.ItemCount == 1
                ? $"{result.ItemName ?? "Selected item"} returned no Universalis listings."
                : $"{result.ItemCount:N0} item{(result.ItemCount == 1 ? string.Empty : "s")} refreshed with missing listings.",
            DesktopMarketRefreshQueueStatus.Failed => result.Detail ?? "Market evidence refresh failed.",
            DesktopMarketRefreshQueueStatus.NoQueuedItems => "No market evidence refresh is pending.",
            DesktopMarketRefreshQueueStatus.NoPlanItems => "No plan items are available for market analysis.",
            DesktopMarketRefreshQueueStatus.NotFound => "Selected item is not in the active plan.",
            _ => "Market evidence updated."
        };

    private async Task UpdateLocalSnapshotSummaryAsync()
    {
        var summaries = await _persistence.LoadPlanSummariesAsync();
        LocalSnapshotSummaryText = $"Snapshots: {summaries.Count}";
    }

    partial void OnSelectedPlanItemChanged(DesktopPlanItemRow? value)
    {
        if (value == null)
        {
            return;
        }

        var matchingQueueItem = MarketQueue.FirstOrDefault(item => item.ItemId == value.ItemId);
        if (matchingQueueItem != null && !Equals(SelectedMarketQueueItem, matchingQueueItem))
        {
            SelectedMarketQueueItem = matchingQueueItem;
        }

        ApplyInspector(_session.ActivePlan, _session.ProjectItems, _session.MarketEvidence);
    }

    partial void OnSelectedMarketQueueItemChanged(DesktopMarketQueueRow? value)
    {
        if (value == null)
        {
            return;
        }

        var matchingPlanItem = PlanItems.FirstOrDefault(item => item.ItemId == value.ItemId);
        if (matchingPlanItem != null && !Equals(SelectedPlanItem, matchingPlanItem))
        {
            SelectedPlanItem = matchingPlanItem;
            return;
        }

        ApplyInspector(_session.ActivePlan, _session.ProjectItems, _session.MarketEvidence);
    }
}

public static class DesktopWorkflow
{
    public const string RecipePlanner = "Recipe Planner";
    public const string MarketAnalysis = "Market Analysis";
    public const string AcquisitionEvaluation = "Acquisition Evaluation";
    public const string ProcurementPlan = "Procurement Plan";
    public const string Settings = "Settings";
    public const string Diagnostics = "Diagnostics";
}

public sealed record DesktopPlanItemRow(
    int ItemId,
    string Name,
    int Quantity,
    string QuantityText,
    string Role,
    string SourceText,
    string CostText,
    int Depth,
    bool IsRoot)
{
    public string DisplayName => Depth == 0 ? Name : $"{new string(' ', Depth * 2)}- {Name}";
    public string QualityText => Role.StartsWith("HQ", StringComparison.Ordinal) ? "HQ" : "NQ";
}

public sealed record DesktopMarketQueueRow(
    int ItemId,
    string ItemName,
    string NeedText,
    string SourceText,
    string TrustText,
    string CostText);

public sealed record DesktopPlanSearchResultRow(int ItemId, string Name, string Source)
{
    public string MetadataText => $"{Source} item";
}

public sealed record DesktopStoredPlanRow(
    string Id,
    string Name,
    string DataCenter,
    int ProjectItemCount,
    DateTime SavedAt)
{
    public string MetadataText => $"{ProjectItemCount:N0} item{(ProjectItemCount == 1 ? string.Empty : "s")}";
    public string SavedAtText => SavedAt.ToLocalTime().ToString("g");
}

public sealed record DesktopActivityLogRow(DateTime Timestamp, string Kind, string Message)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("T");
}

public sealed record DesktopLogRow(
    DateTime Timestamp,
    string Level,
    string Category,
    int EventId,
    string Message,
    string? Exception,
    string? StackTrace)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("T");
    public string CategoryText => Category.Split('.').LastOrDefault() ?? Category;
    public string SummaryText => string.IsNullOrWhiteSpace(Exception)
        ? Message
        : $"{Message} | {Exception.Split(Environment.NewLine).FirstOrDefault()}";
    public string CopyText =>
        $"{Timestamp:O} [{Level}] {Category} ({EventId}){Environment.NewLine}{Message}{Environment.NewLine}{Exception}";
}
