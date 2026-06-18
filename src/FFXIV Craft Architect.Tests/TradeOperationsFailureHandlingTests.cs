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

        Assert.Contains("FormatAssignedCrafter", source);
        Assert.Contains("AddHistoryIfAssignmentChanged", source);
        Assert.Contains("TradeOrderHistoryEventKind.Assigned", source);
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
    public void OrdersPage_TableShowsRootQuantityInsteadOfLineCount()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains("TradeOrderColumn.Quantity", source);
        Assert.Contains("\"Qty\"", source);
        Assert.Contains("GetRootQuantity(order).ToString(\"N0\")", source);
        Assert.Contains("RootItems.Sum(item => item.Quantity)", source);
        Assert.DoesNotContain("RootItems.Count.ToString(\"N0\")", source);
    }

    [Fact]
    public void OrdersPage_DetailShowsMaterialBreakdownFromSnapshot()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains("_selectedOrder.SourceSnapshot.Materials", source);
        Assert.Contains("TradeDisplayFormatter.FormatQuantity(item.Quantity)", source);
        Assert.Contains("TradeDisplayFormatter.FormatQuantity(material.Quantity)", source);
        Assert.Contains("FormatMaterialCost(material)", source);
        Assert.Contains("Payment amount", source);
        Assert.Contains("Total estimated procurement", source);
        Assert.Contains("GetSelectedOrderPaymentSummary", source);
        Assert.Contains("TradeCommissionPaymentSummary.FromOrder", source);
        Assert.Contains("FormatResponsibility", source);
        Assert.Contains("Not priced", source);
    }

    [Fact]
    public void OrdersPage_NormalSaveProtectsOrderLifecycleInvariants()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains("Reopen archived orders before editing details.", source);
        Assert.Contains("Order title is required.", source);
        Assert.Contains("Use the close order controls for archive transitions.", source);
        Assert.Contains("Change status to Assigned before saving this assignment.", source);
        Assert.Contains("Change status to Ready to Assign before clearing this assignment.", source);
        Assert.Contains("Assign a crafter before using this status.", source);
    }

    [Fact]
    public void TradePages_ReselectPersistedRecordsAfterReload()
    {
        var ordersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));
        var craftersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));

        Assert.Contains("SelectOrderAfterReload", ordersSource);
        Assert.Contains("_orders.FirstOrDefault(order => order.Id == orderId)", ordersSource);
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

        Assert.Contains("var orderToSave = CopyOrder(_selectedOrder);", ordersSource);
        Assert.Contains("SaveOrderAsync(orderToSave)", ordersSource);
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

        Assert.Contains("orderToSave.UpdatedAtUtc = DateTime.UtcNow;", ordersSource);
        Assert.Contains("crafterToSave.UpdatedAtUtc = DateTime.UtcNow;", craftersSource);
        Assert.Contains("AddReopenedHistory", ordersSource);
        Assert.Contains("Kind = TradeOrderHistoryEventKind.Reopened", ordersSource);
        Assert.Contains("Kind = TradeOrderStatusWorkflow.IsArchived(newStatus) ? TradeOrderHistoryEventKind.Closed : TradeOrderHistoryEventKind.StatusChanged", ordersSource);
    }

    [Fact]
    public void TradeTables_SelectRowsForDetailPanelsWithoutActionColumns()
    {
        var tableSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Shared", "TablePrimitives", "WebDataTable.razor"));
        var ordersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));
        var craftersSource = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));

        Assert.Contains("RowClicked", tableSource);
        Assert.Contains("@onclick=\"@(() => OnRowClickedAsync(item))\"", tableSource);
        Assert.Contains("RowClicked=\"SelectOrder\"", ordersSource);
        Assert.Contains("GetRowClass=\"GetOrderRowClass\"", ordersSource);
        Assert.Contains("RowClicked=\"SelectCrafter\"", craftersSource);
        Assert.Contains("GetRowClass=\"GetCrafterRowClass\"", craftersSource);
        Assert.DoesNotContain("Header = \"Details\"", ordersSource);
        Assert.DoesNotContain("Header = \"Details\"", craftersSource);
        Assert.DoesNotContain("TradeOrderColumn.Action", ordersSource);
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
    public void CraftersPage_UsesDenseRosterTableWithoutAssignmentColumn()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));

        Assert.Contains("WebDataTable TItem=\"TradeCrafterProfile\"", source);
        Assert.Contains("TradeCrafterColumn", source);
        Assert.DoesNotContain("Assignment", GetCrafterColumnsSource(source));
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
