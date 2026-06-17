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

        Assert.Contains("indexedDB.js?v=6", source);
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
}
