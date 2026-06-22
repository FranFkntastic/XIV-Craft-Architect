using System.Text.RegularExpressions;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradeOperationsFailureHandlingTests
{
    [Fact]
    public void PersistenceService_DefaultCompanyProfileSaveFailure_IsNotSilentlyIgnored()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Services", "TradeOperationsPersistenceService.cs"));

        Assert.Contains("if (!saved)", source);
        Assert.Contains("Failed to create the default Trade company profile.", source);
        Assert.Contains("GetTradeStoreDiagnosticsAsync", source);
        Assert.Contains("throw new InvalidOperationException", source);
    }

    [Fact]
    public void IndexedDbScript_ExposesTradeStoreDiagnostics()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "wwwroot", "indexedDB.js"));

        Assert.Contains("getTradeStoreDiagnostics", source);
        Assert.Contains("ensureTradeStores", source);
        Assert.Contains("openTradeStoreRepairUpgrade", source);
        Assert.Contains("database.version + 1", source);
        Assert.Contains("hasCompanyProfilesStore", source);
        Assert.Contains("hasCraftersStore", source);
        Assert.Contains("hasOrdersStore", source);
    }

    [Fact]
    public void IndexedDbAssetVersion_BustsTradeSchemaCache()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "wwwroot", "index.html"));

        Assert.Contains("indexedDB.js?v=7", source);
    }

    [Fact]
    public void TradePages_LoadFailures_RenderRecoverableErrorPanels()
    {
        var ordersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));
        var craftersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));

        Assert.Contains("_loadError", ordersSource);
        Assert.Contains("_loadError", craftersSource);
        Assert.Contains("Trade operations storage is unavailable.", ordersSource);
        Assert.Contains("Trade operations storage is unavailable.", craftersSource);
        Assert.Contains("catch (Exception ex)", ordersSource);
        Assert.Contains("catch (Exception ex)", craftersSource);
        Assert.Contains("OnClick=\"LoadAsync\"", ordersSource);
        Assert.Contains("OnClick=\"LoadAsync\"", craftersSource);
    }

    [Fact]
    public void TradeChrome_ShowsCompanyContextInStatusBarInsteadOfPageHeaders()
    {
        var statusBarSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Shared", "StatusBar.razor"));
        var mainLayoutSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Shared", "MainLayout.razor"));
        var ordersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));
        var craftersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));

        Assert.Contains("trade-company-badge", statusBarSource);
        Assert.Contains("TradeOperationsPersistence.GetOrCreateActiveCompanyProfileAsync", statusBarSource);
        Assert.Contains("AppState.SelectedRegion", statusBarSource);
        Assert.Contains("AppState.SelectedDataCenter", statusBarSource);
        Assert.Contains("GetTabStyle", mainLayoutSource);
        Assert.Contains("background-color: {accent}", mainLayoutSource);
        Assert.DoesNotContain("<MudText Typo=\"Typo.h5\">Orders</MudText>", ordersSource);
        Assert.DoesNotContain("<MudText Typo=\"Typo.h5\">Crafters</MudText>", craftersSource);
        Assert.DoesNotContain("@_companyProfile?.Name", ordersSource);
        Assert.DoesNotContain("@_companyProfile?.Name", craftersSource);
    }

    [Fact]
    public void CraftersPage_SaveFailures_ShowErrorsInsteadOfSuccess()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));

        Assert.Contains("var saved = await TradeOperationsPersistence.SaveCrafterAsync", source);
        Assert.Contains("Failed to save crafter.", source);
        Assert.Contains("if (!saved)", source);
        Assert.True(
            source.IndexOf("Failed to save crafter.", StringComparison.Ordinal) <
            source.IndexOf("Crafter created", StringComparison.Ordinal));
    }

    [Fact]
    public void CraftersPage_CreateButtonStaysVisuallyAvailable()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));
        var createPanel = GetMethodSource(source, "<MudText Typo=\"Typo.subtitle1\">Create Crafter</MudText>", "<section class=\"trade-crafters-panel\">");

        Assert.Contains("OnClick=\"CreateCrafterAsync\"", createPanel);
        Assert.DoesNotContain("Disabled=\"@string.IsNullOrWhiteSpace(_newCrafterName)\"", createPanel);
        Assert.Contains("Crafter display name is required.", source);
    }

    [Fact]
    public void OrdersPage_SaveFailures_ShowErrorsInsteadOfSuccess()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains("var saved = await TradeOperationsPersistence.SaveOrderAsync", source);
        Assert.Contains("Failed to save Trade order.", source);
        Assert.Contains("if (!saved)", source);
        Assert.True(
            source.IndexOf("Failed to save Trade order.", StringComparison.Ordinal) <
            source.IndexOf("Trade order created", StringComparison.Ordinal));
    }

    [Fact]
    public void OrdersPage_ArchiveTransitionsAreConfirmationGated()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains("DialogService.ShowAsync<TradeOrderCloseDialog>", source);
        Assert.DoesNotContain("TradeOrderStatusWorkflow.ActiveStatuses.Concat(TradeOrderStatusWorkflow.ArchiveStatuses)", source);
        Assert.DoesNotContain("OnClick=\"@(() => CloseSelectedOrderAsync", source);
    }

    [Fact]
    public void OrdersPage_TableShowsCrafterNameAndAssignmentHistory()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));
        var workflowSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Core", "Services", "TradeOrderWorkflow.cs"));

        Assert.Contains("FormatAssignedCrafter", source);
        Assert.Contains("AddHistoryIfAssignmentChanged", source);
        Assert.Contains("TradeOrderWorkflow.AppendAssignmentHistory", source);
        Assert.Contains("TradeOrderHistoryEventKind.Assigned", workflowSource);
    }

    [Fact]
    public void PayrollPage_LinksWorkflowDraftsBackToMatchingOrders()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradePayroll.razor"));

        Assert.Contains("LinkPayrollDraftToOrderAsync", source);
        Assert.Contains("_linkedOrder.PayrollDraftId", source);
        Assert.Contains("TradeOrderHistoryEventKind.PayrollLinked", source);
        Assert.Contains("Any(history => history.Kind == TradeOrderHistoryEventKind.PayrollLinked", source);
        Assert.Contains("TradeOperationsPersistence.SaveOrderAsync(orderToSave)", source);
    }

    [Fact]
    public void OrdersPage_RendersOnlyPopulatedStatusGroups()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains("ActiveOrderGroups", source);
        Assert.Contains(".Where(group => group.Orders.Count > 0)", source);
        Assert.Contains("No orders yet", source);
        Assert.Contains("ArchivedOrders.Count > 0", source);
    }

    [Fact]
    public void OrdersPage_OutputTablesShowRootQuantitiesInsteadOfLineCounts()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains(">Qty<", source);
        Assert.Contains("TradeDisplayFormatter.FormatQuantity(item.Quantity)", source);
        Assert.Contains("TradeDisplayFormatter.FormatQuantity(material.Quantity)", source);
        Assert.DoesNotContain("RootItems.Count.ToString(\"N0\")", source);
    }

    [Fact]
    public void OrdersPage_DetailShowsMaterialBreakdownFromSnapshot()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains("paymentSummary.Materials", source);
        Assert.Contains("TradeDisplayFormatter.FormatQuantity(item.Quantity)", source);
        Assert.Contains("TradeDisplayFormatter.FormatQuantity(material.Quantity)", source);
        Assert.Contains("Payment amount", source);
        Assert.Contains("Total estimated procurement", source);
        Assert.Contains("TradeCommissionPaymentSummary.FromOrder", source);
        Assert.Contains("TradeOrderWorkflow.GetProcurementEvidenceState(order).IsFullyPriced", source);
        Assert.Contains("Create a linked craft plan, then run market analysis to populate payment evidence.", source);
        Assert.Contains("Material lines are captured, but pricing evidence is missing.", source);
        Assert.Contains("SetOrderMaterialResponsibilityAsync", source);
        Assert.Contains("CopyGilAmountAsync", source);
        Assert.Contains("Disabled=\"@(paymentSummary.TotalPayment <= 0)\"", source);
        Assert.Contains("Disabled=\"@(paymentSummary.EstimatedProcurementTotal <= 0)\"", source);
        Assert.Contains("Reprice Order", source);
        Assert.Contains("TradeOrderPricingWorkflow.RepriceAsync", source);
    }

    [Fact]
    public void OrdersPage_PaymentResponsibilityDoesNotMutateLoadedDraftBeforeSave()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));
        var method = GetMethodSource(source, "private async Task SetOrderMaterialResponsibilityAsync", "private async Task<TradePayrollWorkflowDraft> GetOrCreatePayrollDraftForOrderAsync");

        Assert.Contains("var draftToSave = TradeOrderWorkflow.WithMaterialResponsibility", method);
        Assert.Contains("TradePayrollPersistence.SaveDraftAsync(draftToSave)", method);
        Assert.True(
            method.IndexOf("if (!savedDraft)", StringComparison.Ordinal) <
            method.IndexOf("_payrollDrafts = _payrollDrafts", StringComparison.Ordinal));
    }

    [Fact]
    public void OrdersPage_PaymentDraftLookupDoesNotFallbackByPlanSession()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.DoesNotContain("draft.PlanSessionVersion == order.SourceSnapshot.PlanSessionVersion", source);
        Assert.Contains("GetOrCreatePayrollDraftForOrderAsync", source);
        Assert.Contains("order.Id", source);
    }

    [Fact]
    public void OrdersPage_OpensOrderCraftPlanFromLinkedSavedPlan()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));
        var method = GetMethodSource(source, "private async Task OpenSelectedOrderCraftPlanAsync()", "private async Task<bool> ConfirmActiveCraftPlanCanBeReplacedAsync");

        Assert.Contains("Open Craft Plan", source);
        Assert.Contains("HasLinkedCraftPlan(_selectedOrder)", method);
        Assert.Contains("PlanPersistence.LoadPlanIntoSessionAsync(_selectedOrder.CraftPlanId!)", method);
        Assert.Contains("Create a linked craft plan before opening it.", method);
        Assert.Contains("NavigationManager.NavigateTo(\"./\")", method);
        Assert.DoesNotContain("RecipePlannerCommandService.ImportProjectItemsAsync", method);
        Assert.DoesNotContain("new ImportProjectItemsRequest", method);
        Assert.DoesNotContain("AppState.CurrentPlan =", method);
    }

    [Fact]
    public void TradeOrderPricingWorkflow_ReusesCraftArchitectMarketAndProcurementPipelines()
    {
        var serviceSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Services", "TradeOrderPricingWorkflowService.cs"));

        Assert.Contains("WebPlanPersistenceService", serviceSource);
        Assert.Contains("LoadPlanIntoSessionAsync", serviceSource);
        Assert.Contains("IRecipeLayerWorkflowService", serviceSource);
        Assert.Contains("MarketAnalysisWorkflowService", serviceSource);
        Assert.Contains("MarketAnalysisWorkflowRequest(forceRefreshMarketData)", serviceSource);
        Assert.Contains("ProcurementWorkflowService", serviceSource);
        Assert.Contains("ProcurementWorkflowRequest(() => operation.IsCurrent)", serviceSource);
        Assert.Contains("CancellableOperationWorkflow.TradeOrderPricing", serviceSource);
        Assert.Contains("BuildMarketRecommendationLines", serviceSource);
        Assert.Contains("TradeOrderMaterialEvidenceMapper.ToMaterialSnapshots(lines)", serviceSource);
        Assert.Contains("ActivateRecipePlan", serviceSource);
        Assert.Contains("SaveGeneratedOrderPlanAsync", serviceSource);
        Assert.Contains("_appState.TrackCurrentPlanIdentity(linkDraft.PlanId, linkDraft.PlanName)", serviceSource);
        Assert.Contains("trackStoredPlanIdentity: true", serviceSource);
        Assert.Contains("persistGeneratedPlan: true", serviceSource);
        Assert.Contains("_appState.MarkPersisted", serviceSource);
        Assert.Contains("PersistedStateBucket.PlanCore | PersistedStateBucket.MarketAnalysis", serviceSource);
    }

    [Fact]
    public void OrdersPage_NormalSaveProtectsOrderLifecycleInvariants()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains("Reopen archived orders before editing details.", source);
        Assert.Contains("Order title is required.", source);
        Assert.Contains("Use the close order controls for archive transitions.", source);
        Assert.Contains("TradeOrderWorkflow.ResolveStatusForAssignment(_detailStatus, _detailCrafterId)", source);
        Assert.DoesNotContain("Change status to Assigned Awaiting Payment before saving this assignment.", source);
        Assert.Contains("Change status to Ready to Assign before clearing this assignment.", source);
        Assert.Contains("Assign a crafter before using this status.", source);
    }

    [Fact]
    public void TradePages_ReselectPersistedRecordsAfterReload()
    {
        var ordersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));
        var craftersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));
        var appStateSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Services", "AppState.cs"));

        Assert.Contains("SelectOrderAfterReload", ordersSource);
        Assert.Contains("_orders.FirstOrDefault(order => order.Id == orderId)", ordersSource);
        Assert.Contains("TryGetOrderIdFromNavigation() ?? AppState.SelectedTradeOrderId", ordersSource);
        Assert.Contains("AppState.SelectTradeOrder(order.Id)", ordersSource);
        Assert.Contains("AppState.SelectTradeOrder(null)", ordersSource);
        Assert.Contains("public Guid? SelectedTradeOrderId { get; private set; }", appStateSource);
        Assert.Contains("public void SelectTradeOrder(Guid? orderId)", appStateSource);
        Assert.DoesNotContain("SelectOrder(_selectedOrder);", ordersSource);
        Assert.Contains("SelectCrafterAfterReload", craftersSource);
        Assert.Contains("_crafters.FirstOrDefault(crafter => crafter.Id == crafterId)", craftersSource);
        Assert.DoesNotContain("SelectCrafter(_selectedCrafter);", craftersSource);
    }

    [Fact]
    public void TradePages_DoNotMutateSelectedRecordsBeforePersistenceSucceeds()
    {
        var ordersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));
        var craftersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));

        Assert.Contains("var orderToSave = TradeOrderWorkflow.CopyOrder(_selectedOrder);", ordersSource);
        Assert.Contains("SaveOrderAndNotifyAsync(orderToSave)", ordersSource);
        Assert.DoesNotContain("SaveOrderAsync(_selectedOrder)", ordersSource);
        Assert.Contains("var crafterToSave = CopyCrafter(_selectedCrafter);", craftersSource);
        Assert.Contains("SaveCrafterAsync(crafterToSave)", craftersSource);
        Assert.DoesNotContain("SaveCrafterAsync(_selectedCrafter)", craftersSource);
    }

    [Fact]
    public void TradePages_GateSuccessSignalsAfterReload()
    {
        var ordersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));
        var craftersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));

        Assert.Contains("!string.IsNullOrWhiteSpace(_loadError)", ordersSource);
        Assert.Contains("!SelectOrderAfterReload", ordersSource);
        Assert.Contains("!string.IsNullOrWhiteSpace(_loadError)", craftersSource);
        Assert.Contains("!SelectCrafterAfterReload", craftersSource);
    }

    [Fact]
    public void OrdersPage_ManualNoteTextClearsOnlyAfterSuccessfulSave()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));
        var method = GetMethodSource(source, "private async Task AddManualNoteAsync()", "private async Task OpenCloseOrderDialogAsync");

        Assert.True(
            method.IndexOf("var saved = await TradeOperationsPersistence.SaveOrderAsync(orderToSave);", StringComparison.Ordinal) <
            method.IndexOf("_manualNote = string.Empty;", StringComparison.Ordinal));
        Assert.True(
            method.IndexOf("if (!saved)", StringComparison.Ordinal) <
            method.IndexOf("_manualNote = string.Empty;", StringComparison.Ordinal));
    }

    [Fact]
    public void TradeMutations_UpdateTimestampsAndUseReopenHistoryKind()
    {
        var ordersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));
        var craftersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));
        var workflowSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Core", "Services", "TradeOrderWorkflow.cs"));

        Assert.Contains("orderToSave.UpdatedAtUtc = DateTime.UtcNow;", ordersSource);
        Assert.Contains("crafterToSave.UpdatedAtUtc = DateTime.UtcNow;", craftersSource);
        Assert.Contains("TradeOrderWorkflow.AppendReopenedHistory", ordersSource);
        Assert.Contains("Kind = TradeOrderHistoryEventKind.Reopened", workflowSource);
        Assert.Contains("TradeOrderStatusWorkflow.IsArchived(newStatus)", workflowSource);
    }

    [Fact]
    public void TradeTables_SelectRowsForDetailPanelsWithoutActionColumns()
    {
        var tableSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Shared", "TablePrimitives", "WebDataTable.razor"));
        var ordersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));
        var craftersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));

        Assert.Contains("RowClicked", tableSource);
        Assert.Contains("@onclick=\"@(() => OnRowClickedAsync(item))\"", tableSource);
        Assert.Contains("trade-orders-rail-order", ordersSource);
        Assert.Contains("@onclick=\"() => SelectOrder(order)\"", ordersSource);
        Assert.Contains("RowClicked=\"SelectCrafter\"", craftersSource);
        Assert.Contains("GetRowClass=\"GetCrafterRowClass\"", craftersSource);
        Assert.DoesNotContain("Header = \"Details\"", ordersSource);
        Assert.DoesNotContain("Header = \"Details\"", craftersSource);
        Assert.DoesNotContain("TradeOrderColumn", ordersSource);
        Assert.DoesNotContain("TradeCrafterColumn.Action", craftersSource);
    }

    [Fact]
    public void TradeTables_HeaderCellsFillColumnsForTextAlignment()
    {
        var headerCss = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Shared", "TablePrimitives", "WebTableHeaderCell.razor.css"));

        Assert.Contains("width: 100%;", headerCss);
        Assert.Contains("justify-content: flex-start;", headerCss);
        Assert.Contains(".web-table-header-cell.is-align-end", headerCss);
    }

    [Fact]
    public void CraftersPage_UsesRegionDataCenterWorldSelectors()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));

        Assert.Contains("Value=\"@_newCrafterRegion\"", source);
        Assert.Contains("Value=\"@_newCrafterDataCenter\"", source);
        Assert.Contains("Value=\"@_newCrafterWorld\"", source);
        Assert.Contains("Value=\"@_detailRegion\"", source);
        Assert.Contains("Value=\"@_detailDataCenter\"", source);
        Assert.Contains("Value=\"@_detailWorld\"", source);
        Assert.Contains("GetDataCentersForRegion", source);
        Assert.Contains("GetWorldsForDataCenter", source);
        Assert.Contains("AppState.SelectedRegion", source);
        Assert.Contains("AppState.SelectedDataCenter", source);
        Assert.Contains("No world selected", source);
        Assert.DoesNotContain("Label=\"World\"\r\n                                      Variant=\"Variant.Outlined\"\r\n                                      Margin=\"Margin.Dense\" />", source);
    }

    [Fact]
    public void TradeRazorMarkup_DoesNotUseTextAdjacentQuantityExpressions()
    {
        var webProjectPath = GetWorkspacePath("src", "FFXIV Craft Architect.Web");
        var failures = Directory
            .EnumerateFiles(webProjectPath, "*.razor", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = Path.GetRelativePath(webProjectPath, path),
                Matches = Regex.Matches(File.ReadAllText(path), @"x@(?:\(|[A-Za-z_])")
                    .Select(match => match.Value)
                    .ToArray()
            })
            .Where(result => result.Matches.Length > 0)
            .Select(result => $"{result.Path}: {string.Join(", ", result.Matches)}")
            .ToArray();

        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void TradeDisplayFormatter_FormatsQuantitiesForMarkup()
    {
        Assert.Equal("x0", TradeDisplayFormatter.FormatQuantity(0));
        Assert.Equal("x4,995", TradeDisplayFormatter.FormatQuantity(4995));
    }

    [Fact]
    public void CraftersPage_UsesDenseRosterTableWithAssignmentColumn()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));

        Assert.Contains("WebDataTable TItem=\"TradeCrafterProfile\"", source);
        Assert.Contains("TradeCrafterColumn", source);
        Assert.Contains("Assignments", GetCrafterColumnsSource(source));
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

    private static string GetCrafterColumnsSource(string source)
    {
        var start = source.IndexOf("private IReadOnlyList<WebTableColumn<TradeCrafterProfile, TradeCrafterColumn>>", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private static string GetCrafterKey", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source[start..end];
    }

    private static string GetMethodSource(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source[start..end];
    }
}
