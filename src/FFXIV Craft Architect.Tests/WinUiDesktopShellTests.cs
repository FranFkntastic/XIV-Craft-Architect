namespace FFXIV_Craft_Architect.Tests;

public class WinUiDesktopShellTests
{
    [Fact]
    public void MainWindow_WorkflowRibbonMatchesApprovedOrder()
    {
        var source = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "MainWindow.xaml"));

        var recipeIndex = source.IndexOf("Recipe Planner", StringComparison.Ordinal);
        var marketIndex = source.IndexOf("Market Analysis", StringComparison.Ordinal);
        var acquisitionIndex = source.IndexOf("Acquisition Evaluation", StringComparison.Ordinal);
        var procurementIndex = source.IndexOf("Procurement Plan", StringComparison.Ordinal);

        Assert.True(recipeIndex >= 0);
        Assert.True(marketIndex > recipeIndex);
        Assert.True(acquisitionIndex > marketIndex);
        Assert.True(procurementIndex > acquisitionIndex);
    }

    [Fact]
    public void DesktopShell_BindsPersistentStateAndCoreOperationStatus()
    {
        var mainWindowSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "MainWindow.xaml"));
        var viewModelSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "ViewModels",
            "DesktopShellViewModel.cs"));
        var appSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "App.xaml.cs"));
        var applicationServicesSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "Services",
            "DesktopApplicationServices.cs"));
        var serviceRegistrationSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "Services",
            "DesktopServiceCollectionExtensions.cs"));
        var startupSmokeSource = File.ReadAllText(GetWorkspacePath(
            "scripts",
            "smoke-winui-desktop.ps1"));

        Assert.Contains("CraftSessionState", viewModelSource);
        Assert.Contains("CraftOperationState", viewModelSource);
        Assert.Contains("ICraftOperationCoordinator", viewModelSource);
        Assert.Contains("CoreSessionPersistenceService", viewModelSource);
        Assert.Contains("CoreAcquisitionDecisionService", viewModelSource);
        Assert.Contains("CraftOperationWorkflow.ItemMarketRefresh", viewModelSource);
        Assert.Contains("DesktopApplicationServices.BuildServiceProvider()", appSource);
        Assert.Contains("services.AddSingleton<CraftSessionState>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<CraftOperationState>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<CorePlanSessionLoadService>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<ICoreStoredPlanStore, FileCoreStoredPlanStore>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<CoreSessionPersistenceService>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<CoreAcquisitionDecisionService>", serviceRegistrationSource);
        Assert.Contains("new UniversalisService(new HttpClient())", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<DesktopJsonMarketCacheService>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<IMarketCacheService>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<GarlandService>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<RecipeCalculationService>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<IDesktopRecipePlanBuilder>", serviceRegistrationSource);
        Assert.Contains("DesktopRecipeCalculationPlanBuilder", serviceRegistrationSource);
        Assert.Contains("DesktopSmokeRecipePlanBuilder", serviceRegistrationSource);
        Assert.Contains("FFXIV_CRAFT_ARCHITECT_DESKTOP_SMOKE_BUILD", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<IDesktopClipboardService, WinUiClipboardService>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<IDesktopFileDialogService, WinUiFileDialogService>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<DesktopSettingsStore>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<DesktopLocalInfrastructureService>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<DesktopActivityLogStore>", serviceRegistrationSource);
        Assert.Contains("DesktopLogStore", applicationServicesSource);
        Assert.Contains("DesktopLogProvider", applicationServicesSource);
        Assert.Contains("LogLevel.Trace", applicationServicesSource);
        Assert.Contains("services.AddSingleton<DesktopMarketRefreshQueueService>", serviceRegistrationSource);
        Assert.Contains("services.AddSingleton<DesktopProjectItemDraftService>", serviceRegistrationSource);
        Assert.Contains("IDesktopLogViewerLauncher", serviceRegistrationSource);
        Assert.Contains("DesktopLogViewerWindow", serviceRegistrationSource);
        Assert.Contains("Start-Process", startupSmokeSource);
        Assert.Contains("UIAutomationClient", startupSmokeSource);
        Assert.Contains("Find-ElementByAutomationId", startupSmokeSource);
        Assert.Contains("Invoke-ElementByAutomationId", startupSmokeSource);
        Assert.Contains("Wait-ElementName", startupSmokeSource);
        Assert.Contains("Wait-ElementNameContains", startupSmokeSource);
        Assert.Contains("ExpectedWindowTitle", startupSmokeSource);
        Assert.Contains("MainWindowHandle", startupSmokeSource);
        Assert.Contains("MainWindowTitle", startupSmokeSource);
        Assert.Contains("Responding", startupSmokeSource);
        Assert.Contains("RecipePlannerTabButton", startupSmokeSource);
        Assert.Contains("MarketAnalysisTabButton", startupSmokeSource);
        Assert.Contains("AcquisitionEvaluationTabButton", startupSmokeSource);
        Assert.Contains("ProcurementPlanTabButton", startupSmokeSource);
        Assert.Contains("SettingsButton", startupSmokeSource);
        Assert.Contains("DiagnosticsButton", startupSmokeSource);
        Assert.Contains("ActivityDrawerToggleButton", startupSmokeSource);
        Assert.Contains("ActivityFilterAllButton", startupSmokeSource);
        Assert.Contains("ActivityFilterJobButton", startupSmokeSource);
        Assert.Contains("ActivityFilterCacheButton", startupSmokeSource);
        Assert.Contains("ActivityFilterSummaryText", startupSmokeSource);
        Assert.Contains("AddTargetButton", startupSmokeSource);
        Assert.Contains("Cobalt Plate added", startupSmokeSource);
        Assert.Contains("IncreaseSelectedQuantityButton", startupSmokeSource);
        Assert.Contains("DecreaseSelectedQuantityButton", startupSmokeSource);
        Assert.Contains("ToggleSelectedHqButton", startupSmokeSource);
        Assert.Contains("quantity is now", startupSmokeSource);
        Assert.Contains("quality set to HQ", startupSmokeSource);
        Assert.Contains("FFXIV_CRAFT_ARCHITECT_DESKTOP_SMOKE_BUILD", startupSmokeSource);
        Assert.Contains("PrimaryActionButton", startupSmokeSource);
        Assert.Contains("Recipe plan built", startupSmokeSource);
        Assert.Contains("deterministic build", startupSmokeSource);
        Assert.Contains("CopyProcurementPlanTextButton", startupSmokeSource);
        Assert.Contains("procurement line", startupSmokeSource);
        Assert.Contains("procurement export", startupSmokeSource);
        Assert.Contains("Desktop settings applied", startupSmokeSource);
        Assert.Contains("RefreshDiagnosticLogButton", startupSmokeSource);
        Assert.Contains("OpenDiagnosticLogViewerButton", startupSmokeSource);
        Assert.Contains("FFXIV Craft Architect Diagnostic Logs", startupSmokeSource);
        Assert.Contains("LogViewerSearchTextBox", startupSmokeSource);
        Assert.Contains("diagnostic log viewer", startupSmokeSource);
        Assert.Contains("OperationStatusText", startupSmokeSource);
        Assert.Contains("WorkbenchTitleText", startupSmokeSource);
        Assert.Contains("Desktop smoke passed", startupSmokeSource);
        Assert.Contains("PlanName", mainWindowSource);
        Assert.Contains("SelectedDataCenter", mainWindowSource);
        Assert.Contains("SelectedWorld", mainWindowSource);
        Assert.Contains("OperationStatusText", mainWindowSource);
        Assert.Contains("OperationProgressPercent", mainWindowSource);
        Assert.Contains("RunPrimaryActionCommand", mainWindowSource);
        Assert.Contains("ShowRecipePlannerCommand", mainWindowSource);
        Assert.Contains("ShowMarketAnalysisCommand", mainWindowSource);
        Assert.Contains("ShowAcquisitionEvaluationCommand", mainWindowSource);
        Assert.Contains("ShowProcurementPlanCommand", mainWindowSource);
        Assert.Contains("ShowSettingsCommand", mainWindowSource);
        Assert.Contains("ShowDiagnosticsCommand", mainWindowSource);
        Assert.Contains("SaveSnapshotCommand", mainWindowSource);
        Assert.Contains("LoadLatestSnapshotCommand", mainWindowSource);
        Assert.Contains("LoadSelectedSnapshotCommand", mainWindowSource);
        Assert.Contains("ExportSelectedSnapshotJsonCommand", mainWindowSource);
        Assert.Contains("ImportSnapshotJsonCommand", mainWindowSource);
        Assert.Contains("OpenLocalDataFolderCommand", mainWindowSource);
        Assert.Contains("CleanupStaleMarketCacheCommand", mainWindowSource);
        Assert.Contains("ResetMarketCacheCommand", mainWindowSource);
        Assert.Contains("SearchProjectItemsCommand", mainWindowSource);
        Assert.Contains("AddSearchResultCommand", File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "MainWindow.xaml.cs")));
        Assert.Contains("RefreshSelectedItemCommand", mainWindowSource);
        Assert.Contains("OpenSourceDecisionCommand", mainWindowSource);
        Assert.Contains("SetSelectedSourceToCraftCommand", mainWindowSource);
        Assert.Contains("SetSelectedSourceToMarketNqCommand", mainWindowSource);
        Assert.Contains("SetSelectedSourceToMarketHqCommand", mainWindowSource);
        Assert.Contains("SetSelectedSourceToVendorCommand", mainWindowSource);
        Assert.Contains("CopyShoppingLineCommand", mainWindowSource);
        Assert.Contains("CopyProcurementPlanTextCommand", mainWindowSource);
        Assert.Contains("CopyProcurementPlanCsvCommand", mainWindowSource);
        Assert.Contains("AddTargetItemCommand", mainWindowSource);
        Assert.Contains("RemoveSelectedTargetCommand", mainWindowSource);
        Assert.Contains("IncreaseSelectedQuantityCommand", mainWindowSource);
        Assert.Contains("DecreaseSelectedQuantityCommand", mainWindowSource);
        Assert.Contains("ToggleSelectedHqCommand", mainWindowSource);
        Assert.Contains("KeyboardAccelerator", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"RecipePlannerTabButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"MarketAnalysisTabButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"AcquisitionEvaluationTabButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"ProcurementPlanTabButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"SettingsButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"DiagnosticsButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"PrimaryActionButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"ActivityDrawerToggleButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"TargetSearchBox\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"AddTargetButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"TargetSearchStatusText\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"IncreaseSelectedQuantityButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"DecreaseSelectedQuantityButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"ToggleSelectedHqButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"RemoveSelectedTargetButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"CopyProcurementPlanTextButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"OperationStatusText\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"ActivityFilterAllButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"ActivityFilterJobButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"ActivityFilterCacheButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"ActivityFilterSummaryText\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"WorkbenchTitleText\"", mainWindowSource);
        Assert.Contains("AutomationProperties.AutomationId=\"RefreshDiagnosticLogButton\"", mainWindowSource);
        Assert.Contains("AutomationProperties.Name=\"{Binding WorkbenchTitle}\"", mainWindowSource);
        Assert.Contains("AutomationProperties.Name=\"{Binding OperationStatusText}\"", mainWindowSource);
        Assert.Contains("AutomationProperties.Name=\"{Binding TargetSearchStatusText}\"", mainWindowSource);
        Assert.Contains("Key=\"Number1\"", mainWindowSource);
        Assert.Contains("Key=\"Number2\"", mainWindowSource);
        Assert.Contains("Key=\"Number3\"", mainWindowSource);
        Assert.Contains("Key=\"Number4\"", mainWindowSource);
        Assert.Contains("Key=\"S\" Modifiers=\"Control\"", mainWindowSource);
        Assert.Contains("Key=\"O\" Modifiers=\"Control\"", mainWindowSource);
        Assert.Contains("Key=\"Enter\" Modifiers=\"Control\"", mainWindowSource);
        Assert.Contains("PersistenceStatusText", mainWindowSource);
        Assert.Contains("LocalSnapshotSummaryText", mainWindowSource);
        Assert.Contains("InspectorItemName", mainWindowSource);
        Assert.Contains("InspectorEstimatedCostText", mainWindowSource);
        Assert.Contains("InspectorActionStatusText", mainWindowSource);
        Assert.Contains("WorkbenchTitle", mainWindowSource);
        Assert.Contains("PrimaryActionText", mainWindowSource);
        Assert.Contains("RecipePlannerPanelVisibility", mainWindowSource);
        Assert.Contains("MarketAnalysisPanelVisibility", mainWindowSource);
        Assert.Contains("AcquisitionEvaluationPanelVisibility", mainWindowSource);
        Assert.Contains("ProcurementPlanPanelVisibility", mainWindowSource);
        Assert.Contains("SettingsPanelVisibility", mainWindowSource);
        Assert.Contains("DiagnosticsPanelVisibility", mainWindowSource);
        Assert.Contains("LeftPaneWidth", mainWindowSource);
        Assert.Contains("LeftPaneVisibility", mainWindowSource);
        Assert.Contains("RightPaneWidth", mainWindowSource);
        Assert.Contains("RightPaneVisibility", mainWindowSource);
        Assert.Contains("ApplyWorkflowPaneLayout", viewModelSource);
        Assert.Contains("MarketItemActionVisibility", mainWindowSource);
        Assert.Contains("SourceDecisionActionVisibility", mainWindowSource);
        Assert.Contains("ProcurementActionVisibility", mainWindowSource);
        Assert.Contains("CraftSourceOptionVisibility", mainWindowSource);
        Assert.Contains("MarketNqSourceOptionVisibility", mainWindowSource);
        Assert.Contains("MarketHqSourceOptionVisibility", mainWindowSource);
        Assert.Contains("VendorSourceOptionVisibility", mainWindowSource);
        Assert.Contains("PlanEditActionVisibility", mainWindowSource);
        Assert.Contains("RecipePlannerTabBackground", mainWindowSource);
        Assert.Contains("ProcurementPlanTabBackground", mainWindowSource);
        Assert.Contains("ObservableCollection<DesktopPlanItemRow>", viewModelSource);
        Assert.Contains("ObservableCollection<DesktopPlanSearchResultRow>", viewModelSource);
        Assert.Contains("ObservableCollection<DesktopMarketQueueRow>", viewModelSource);
        Assert.Contains("ObservableCollection<DesktopStoredPlanRow>", viewModelSource);
        Assert.Contains("ObservableCollection<DesktopActivityLogRow>", viewModelSource);
        Assert.Contains("FilteredActivityLog", viewModelSource);
        Assert.Contains("ActivityFilterSummaryText", mainWindowSource);
        Assert.Contains("SetActivityFilterCommand", mainWindowSource);
        Assert.Contains("ActivityAllFilterBackground", mainWindowSource);
        Assert.Contains("ActivitySessionFilterBackground", mainWindowSource);
        Assert.Contains("ActivityJobFilterBackground", mainWindowSource);
        Assert.Contains("ActivityCacheFilterBackground", mainWindowSource);
        Assert.Contains("ActivityDesktopFilterBackground", mainWindowSource);
        Assert.Contains("LatestActivitySummaryText", mainWindowSource);
        Assert.Contains("ActivityDrawerVisibility", mainWindowSource);
        Assert.Contains("ActivityDrawerButtonText", mainWindowSource);
        Assert.Contains("ActivityLogPathText", mainWindowSource);
        Assert.Contains("DiagnosticLogPathText", mainWindowSource);
        Assert.Contains("DiagnosticLogEntries", viewModelSource);
        Assert.Contains("SelectedLogEntry", viewModelSource);
        Assert.Contains("RefreshDiagnosticLogCommand", mainWindowSource);
        Assert.Contains("OpenDiagnosticLogViewerCommand", mainWindowSource);
        Assert.Contains("RefreshDiagnosticLogEntries", viewModelSource);
        Assert.Contains("DesktopLogStore", viewModelSource);
        Assert.Contains("ILogger<DesktopShellViewModel>", viewModelSource);
        Assert.Contains("_logger.LogError", viewModelSource);
        Assert.Contains("ToggleActivityDrawerCommand", mainWindowSource);
        Assert.Contains("ToggleActivityDrawer", viewModelSource);
        Assert.Contains("SetActivityFilter", viewModelSource);
        Assert.Contains("ApplyActivityFilter", viewModelSource);
        Assert.Contains("MatchesSelectedActivityFilter", viewModelSource);
        Assert.Contains("DesktopActivityLogStore", viewModelSource);
        Assert.Contains("LoadActivityHistory", viewModelSource);
        Assert.Contains("DesktopProjectItemDraftService", viewModelSource);
        Assert.Contains("GarlandService", viewModelSource);
        Assert.Contains("IDesktopRecipePlanBuilder", viewModelSource);
        Assert.Contains("_recipePlanBuilder", viewModelSource);
        Assert.Contains("ItemsSource=\"{Binding TargetItems}\"", mainWindowSource);
        Assert.Contains("ItemsSource=\"{Binding PlanItems}\"", mainWindowSource);
        Assert.Contains("ItemsSource=\"{Binding MarketQueue}\"", mainWindowSource);
        Assert.Contains("ItemsSource=\"{Binding SearchResults}\"", mainWindowSource);
        Assert.Contains("SelectedItem=\"{Binding SelectedPlanItem, Mode=TwoWay}\"", mainWindowSource);
        Assert.Contains("SelectedItem=\"{Binding SelectedMarketQueueItem, Mode=TwoWay}\"", mainWindowSource);
    }

    [Fact]
    public void DesktopShell_WiresMarketEvidenceRefresh()
    {
        var viewModelSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "ViewModels",
            "DesktopShellViewModel.cs"));
        var logViewerSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "Windows",
            "DesktopLogViewerWindow.xaml"));
        var logViewerViewModelSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "ViewModels",
            "DesktopLogViewerViewModel.cs"));
        var queueServiceSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "Services",
            "DesktopMarketRefreshQueueService.cs"));
        var cacheServiceSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "Services",
            "DesktopJsonMarketCacheService.cs"));

        Assert.Contains("DesktopMarketRefreshQueueService", viewModelSource);
        Assert.Contains("RefreshSelectedItemAsync", viewModelSource);
        Assert.Contains("RefreshPlanEvidenceAsync", viewModelSource);
        Assert.Contains("IMarketCacheService", queueServiceSource);
        Assert.Contains("RefreshRequestedAsync", queueServiceSource);
        Assert.Contains("UniversalisService", cacheServiceSource);
        Assert.Contains("UniversalisMarketDataMapper.ToCachedMarketData", cacheServiceSource);
        Assert.Contains("NoListingsWarning", queueServiceSource);
        Assert.Contains("FetchFailedWarningPrefix", queueServiceSource);
        Assert.Contains("session.PublishMarketAnalysis", queueServiceSource);
        Assert.Contains("Market evidence fresh", viewModelSource);
        Assert.Contains("Market evidence partial", viewModelSource);
    }

    [Fact]
    public void DesktopShell_WiresSettingsAndDiagnosticsSurfaces()
    {
        var mainWindowSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "MainWindow.xaml"));
        var viewModelSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "ViewModels",
            "DesktopShellViewModel.cs"));
        var logViewerSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "Windows",
            "DesktopLogViewerWindow.xaml"));
        var logViewerViewModelSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "ViewModels",
            "DesktopLogViewerViewModel.cs"));

        Assert.Contains("Desktop Settings", mainWindowSource);
        Assert.Contains("SettingsDataCenterText", mainWindowSource);
        Assert.Contains("SettingsWorldText", mainWindowSource);
        Assert.Contains("ApplyDesktopSettings", viewModelSource);
        Assert.Contains("desktop settings context applied", viewModelSource);
        Assert.Contains("Diagnostics", mainWindowSource);
        Assert.Contains("SessionVersionSummaryText", mainWindowSource);
        Assert.Contains("DirtyBucketSummaryText", mainWindowSource);
        Assert.Contains("ChangeLogSummaryText", mainWindowSource);
        Assert.Contains("MarketEvidenceSummaryText", mainWindowSource);
        Assert.Contains("DesktopInfrastructureSummaryText", mainWindowSource);
        Assert.Contains("DesktopSettingsStorageSummaryText", mainWindowSource);
        Assert.Contains("DesktopLocalInfrastructureSummaryText", mainWindowSource);
        Assert.Contains("MarketCacheHealthText", mainWindowSource);
        Assert.Contains("MarketCacheRepairText", mainWindowSource);
        Assert.Contains("MarketCachePathText", mainWindowSource);
        Assert.Contains("Cache Health", mainWindowSource);
        Assert.Contains("Cache Repair", mainWindowSource);
        Assert.Contains("Cache File", mainWindowSource);
        Assert.Contains("Activity Log", mainWindowSource);
        Assert.Contains("Diagnostic Logs", mainWindowSource);
        Assert.Contains("OpenDiagnosticLogViewerCommand", mainWindowSource);
        Assert.Contains("OpenDiagnosticLogViewer", viewModelSource);
        Assert.Contains("Diagnostic Log Viewer", logViewerSource);
        Assert.Contains("LogViewerSearchTextBox", logViewerSource);
        Assert.Contains("LogViewerLevelFilter", logViewerSource);
        Assert.Contains("LogViewerCategoryFilter", logViewerSource);
        Assert.Contains("LogViewerEntriesList", logViewerSource);
        Assert.Contains("OpenSelectedLogFileWithCommand", logViewerSource);
        Assert.Contains("SelectedLogEntry.CopyText", logViewerSource);
        Assert.Contains("OpenDifferentLogAsync", logViewerViewModelSource);
        Assert.Contains("ApplyFilters", logViewerViewModelSource);
        Assert.Contains("MatchesSearch", logViewerViewModelSource);
        Assert.Contains("RefreshLogFiles", logViewerViewModelSource);
        Assert.Contains("Grid.Row=\"3\"", mainWindowSource);
        Assert.Contains("Open Local Data", mainWindowSource);
        Assert.Contains("Clear Stale Cache", mainWindowSource);
        Assert.Contains("Reset Market Cache", mainWindowSource);
        Assert.Contains("ItemsSource=\"{Binding FilteredActivityLog}\"", mainWindowSource);
        Assert.Contains("Activity History", mainWindowSource);
        Assert.Contains("Recent Activity", mainWindowSource);
        Assert.Contains("AppendActivity", viewModelSource);
        Assert.Contains("LogOperationActivity", viewModelSource);
        Assert.Contains("DesktopSettingsStore", viewModelSource);
        Assert.Contains("DesktopLocalInfrastructureService", viewModelSource);
        Assert.Contains("OpenLocalDataFolder", viewModelSource);
        Assert.Contains("CleanupStaleMarketCacheAsync", viewModelSource);
        Assert.Contains("ResetMarketCacheAsync", viewModelSource);
        Assert.Contains("MarketCacheRecommendedAction", viewModelSource);
        Assert.Contains("MarketCachePath", viewModelSource);
        Assert.Contains("ApplyStoredDesktopSettings", viewModelSource);
        Assert.Contains("ItemsSource=\"{Binding SavedPlans}\"", mainWindowSource);
        Assert.Contains("SelectedItem=\"{Binding SelectedStoredPlan, Mode=TwoWay}\"", mainWindowSource);
        Assert.Contains("Load Selected Plan", mainWindowSource);
        Assert.Contains("Export JSON", mainWindowSource);
        Assert.Contains("Import JSON", mainWindowSource);
        Assert.Contains("LoadSelectedSnapshotAsync", viewModelSource);
        Assert.Contains("ExportSelectedSnapshotJsonAsync", viewModelSource);
        Assert.Contains("ImportSnapshotJsonAsync", viewModelSource);
        Assert.Contains("SaveTextFileAsync", viewModelSource);
        Assert.Contains("OpenTextFileAsync", viewModelSource);
        Assert.Contains("_sessionLoadService.Load", viewModelSource);
        Assert.Contains("RefreshDiagnosticsAsync", viewModelSource);
    }

    [Fact]
    public void DesktopShell_WiresAcquisitionDecisionAndProcurementExport()
    {
        var viewModelSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "ViewModels",
            "DesktopShellViewModel.cs"));
        Assert.Contains("_acquisitionDecisionService.ChangeSource", viewModelSource);
        Assert.Contains("SetSelectedSourceToCraft", viewModelSource);
        Assert.Contains("SetSelectedSourceToMarketNq", viewModelSource);
        Assert.Contains("SetSelectedSourceToMarketHq", viewModelSource);
        Assert.Contains("SetSelectedSourceToVendor", viewModelSource);
        Assert.Contains("AcquisitionPlanningService.GetAvailableSources", viewModelSource);
        Assert.Contains("GetSourceText", viewModelSource);
        Assert.Contains("CopyProcurementPlan", viewModelSource);
        Assert.Contains("CopyProcurementPlanText", viewModelSource);
        Assert.Contains("CopyProcurementPlanCsv", viewModelSource);
        Assert.Contains("Copy Plan Lines", viewModelSource);
    }

    [Fact]
    public void DesktopShell_WiresProjectItemDraftEditingSurface()
    {
        var mainWindowSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "MainWindow.xaml"));
        var viewModelSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "ViewModels",
            "DesktopShellViewModel.cs"));
        var draftServiceSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "Services",
            "DesktopProjectItemDraftService.cs"));

        Assert.Contains("Plan Builder", mainWindowSource);
        Assert.Contains("Project Items", mainWindowSource);
        Assert.Contains("Search", mainWindowSource);
        var mainWindowCodeBehindSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "MainWindow.xaml.cs"));
        Assert.Contains("<AutoSuggestBox", mainWindowSource);
        Assert.Contains("ItemsSource=\"{Binding SearchResults}\"", mainWindowSource);
        Assert.Contains("AutoSuggestBoxSuggestionsListBackground", mainWindowSource);
        Assert.Contains("AutoSuggestBoxSuggestionsListBorderBrush", mainWindowSource);
        Assert.Contains("ListViewItemForeground", mainWindowSource);
        Assert.Contains("QuerySubmitted=\"TargetSearchBox_QuerySubmitted\"", mainWindowSource);
        Assert.Contains("SuggestionChosen=\"TargetSearchBox_SuggestionChosen\"", mainWindowSource);
        Assert.Contains("TargetSearchBox_QuerySubmitted", mainWindowCodeBehindSource);
        Assert.Contains("TargetSearchBox_SuggestionChosen", mainWindowCodeBehindSource);
        Assert.DoesNotContain("TargetSearchBox_KeyDown", mainWindowCodeBehindSource);
        Assert.DoesNotContain("SearchResultsOverlayVisibility", mainWindowSource);
        Assert.Contains("RecipePlanEmptyText", mainWindowSource);
        Assert.Contains("RecipePlanRowsVisibility", mainWindowSource);
        Assert.Contains("RecipePlanEmptyVisibility", mainWindowSource);
        Assert.DoesNotContain("Canvas.ZIndex=\"12\"", mainWindowSource);
        Assert.DoesNotContain("<ItemsControl ItemsSource=\"{Binding SearchResults}\"", mainWindowSource);
        Assert.Contains("Build Project Plan", viewModelSource);
        Assert.Contains("NewItemName", mainWindowSource);
        Assert.Contains("NewItemQuantityText", mainWindowSource);
        Assert.Contains("NewItemMustBeHq", mainWindowSource);
        Assert.Contains("OnNewItemNameChanged", viewModelSource);
        Assert.Contains("TargetSearchStatusText", mainWindowSource);
        Assert.Contains("TargetSearchStatusVisibility", mainWindowSource);
        Assert.DoesNotContain("SearchResultsOverlayVisibility", viewModelSource);
        Assert.Contains("RecipePlanRowsVisibility", viewModelSource);
        Assert.Contains("RecipePlanEmptyVisibility", viewModelSource);
        Assert.Contains("No recipe plan built.", viewModelSource);
        Assert.Contains("DisplayName", mainWindowSource);
        Assert.Contains("CostText", mainWindowSource);
        Assert.Contains("AddTarget(", draftServiceSource);
        Assert.Contains("RemoveTarget(", draftServiceSource);
        Assert.Contains("AdjustRootQuantity(", draftServiceSource);
        Assert.Contains("ToggleRootHq(", draftServiceSource);
        Assert.Contains("ActivateDraft", draftServiceSource);
        Assert.Contains("session.ActivatePlan(", draftServiceSource);
        Assert.Contains("EnumeratePlanRows", viewModelSource);
        Assert.Contains("EstimateCostText", viewModelSource);
        Assert.Contains("SearchProjectItemsAsync", viewModelSource);
        Assert.Contains("SetTargetSearchStatus", viewModelSource);
        Assert.Contains("AddLocalCatalogSearchResults", viewModelSource);
        Assert.Contains("SearchKnownItems", draftServiceSource);
        Assert.Contains("_garlandService.GetItemAsync(result.ItemId)", viewModelSource);
        Assert.Contains("Garland item", viewModelSource);
        Assert.DoesNotContain("Garland ID", viewModelSource);
        Assert.Contains("Local catalog", viewModelSource);
        Assert.Contains("No Garland or local catalog matches", viewModelSource);
        Assert.Contains("No Garland search run.", viewModelSource);
        Assert.Contains("Item name required for Garland search.", viewModelSource);
        Assert.Contains("Garland search failed", viewModelSource);
        Assert.Contains("AddSearchResult", viewModelSource);
        Assert.Contains("_garlandService.SearchAsync", viewModelSource);
        Assert.Contains("_recipePlanBuilder.BuildPlanAsync", viewModelSource);
        Assert.Contains("desktop recipe plan built", viewModelSource);
        Assert.DoesNotContain("EnsureInitialSession", viewModelSource);
        Assert.DoesNotContain("LoadSample", draftServiceSource);
        Assert.DoesNotContain("LoadFixture", draftServiceSource);
        Assert.DoesNotContain("FindFixtureItem", draftServiceSource);
        Assert.DoesNotContain("Prototype Fixtures", mainWindowSource);
        Assert.DoesNotContain("ToggleDevFixtures", viewModelSource);
        Assert.DoesNotContain("CobaltDevFixtureButton", mainWindowSource);
        Assert.DoesNotContain("ToggleDevFixturesButton", mainWindowSource);
        Assert.DoesNotContain("CreateNode(", draftServiceSource);
        Assert.DoesNotContain("RootItems =", draftServiceSource);
    }

    [Fact]
    public void DesktopShell_UsesWebUtilityProcurementSemantics()
    {
        var mainWindowSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "MainWindow.xaml"));
        var viewModelSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "ViewModels",
            "DesktopShellViewModel.cs"));

        Assert.Contains("Procurement Details", mainWindowSource);
        Assert.Contains("TargetItemCountText", mainWindowSource);
        Assert.Contains("MaterialEntryCountText", mainWindowSource);
        Assert.Contains("HqRequiredCountText", mainWindowSource);
        Assert.Contains("PricedMaterialCountText", mainWindowSource);
        Assert.Contains("MissingPriceCountText", mainWindowSource);
        Assert.Contains("MarketUnavailableCountText", mainWindowSource);
        Assert.Contains("AcquisitionPlanningService.GetActiveProcurementItems", viewModelSource);
        Assert.Contains("ApplyProcurementSnapshot", viewModelSource);
    }

    [Fact]
    public void DesktopShell_DoesNotExposePrototypeHarnessAsWorkflowLanguage()
    {
        var mainWindowSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "MainWindow.xaml"));
        var viewModelSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "ViewModels",
            "DesktopShellViewModel.cs"));

        Assert.Contains("Refresh Market Evidence", viewModelSource);
        Assert.Contains("Refresh This Item", mainWindowSource);
        Assert.Contains("Review Source Options", mainWindowSource);
        Assert.Contains("Buy NQ", mainWindowSource);
        Assert.Contains("Buy HQ", mainWindowSource);
        Assert.DoesNotContain("Refresh Queue", mainWindowSource);
        Assert.DoesNotContain("Run Refresh Queue", mainWindowSource);
        Assert.DoesNotContain("Pin Item For Refresh", mainWindowSource);
        Assert.DoesNotContain("Pin Market Refresh", mainWindowSource);
        Assert.DoesNotContain("Cycle Source", mainWindowSource);
        Assert.DoesNotContain("Change Acquisition Source", mainWindowSource);
        Assert.DoesNotContain("Apply Source Decision", viewModelSource);
        Assert.DoesNotContain("GetNextSourceForSelectedItem", viewModelSource);
        Assert.DoesNotContain("Pin items", viewModelSource);
        Assert.DoesNotContain("queue row", viewModelSource);
    }

    [Fact]
    public void DesktopShell_AvoidsInstructionalCalloutCardTreatment()
    {
        var mainWindowSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "MainWindow.xaml"));
        var viewModelSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Desktop",
            "ViewModels",
            "DesktopShellViewModel.cs"));

        Assert.DoesNotContain("Next Step:", viewModelSource);
        Assert.DoesNotContain("Review the active roots", viewModelSource);
        Assert.DoesNotContain("Background=\"#2D291F\"", mainWindowSource);
        Assert.DoesNotContain("RecipePlannerAccentVisibility", mainWindowSource);
        Assert.DoesNotContain("ProcurementPlanAccentVisibility", mainWindowSource);
    }

    [Fact]
    public void DesktopProject_DoesNotImportWpfShellTypes()
    {
        var desktopRoot = GetWorkspacePath("src", "FFXIV Craft Architect.Desktop");
        var sourceFiles = Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(desktopRoot, "*.xaml", SearchOption.AllDirectories));

        foreach (var sourceFile in sourceFiles)
        {
            var source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("System.Windows", source);
            Assert.DoesNotContain("Wpf.Ui", source);
            Assert.DoesNotContain("FFXIV_Craft_Architect.ViewModels", source);
            Assert.DoesNotContain("FFXIV_Craft_Architect.Views", source);
            Assert.DoesNotContain("UIBuilders", source);
        }
    }

    [Fact]
    public void Solution_UsesWinUiDesktopAsOnlyActiveDesktopShell()
    {
        var solutionSource = File.ReadAllText(GetWorkspacePath("FFXIV Craft Architect.sln"));

        Assert.Contains("FFXIV Craft Architect.Desktop.csproj", solutionSource);
        Assert.DoesNotContain(@"src\FFXIV Craft Architect\FFXIV Craft Architect.csproj", solutionSource);
    }

    [Fact]
    public void TestProject_DoesNotReferenceLegacyWpfShell()
    {
        var testProjectSource = File.ReadAllText(GetWorkspacePath(
            "src",
            "FFXIV Craft Architect.Tests",
            "FFXIV Craft Architect.Tests.csproj"));

        Assert.Contains("FFXIV Craft Architect.Desktop.csproj", testProjectSource);
        Assert.DoesNotContain(@"..\FFXIV Craft Architect\FFXIV Craft Architect.csproj", testProjectSource);
    }

    private static string GetWorkspacePath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
    }
}
