namespace FFXIV_Craft_Architect.Tests;

public class TradePayrollMarkupTests
{
    [Fact]
    public void TradePayrollPage_IsPublicAndPayrollFocused()
    {
        var source = File.ReadAllText(GetTradePayrollPath());

        Assert.Contains("@page \"/trade\"", source);
        Assert.Contains("@page \"/trade/payroll\"", source);
        Assert.DoesNotContain("Returning to Craft Architect", source);
        Assert.DoesNotContain("!AppState.SecretDebugToolsEnabled", source);
        Assert.Contains("EnsureLoadedAsync", source);
        Assert.Contains("NavigationManager.NavigateTo(\"./\")", source);
        Assert.Contains("NavigationManager.NavigateTo(\"trade/orders\", replace: true)", source);
        Assert.Contains("Payroll Calculator", source);
        Assert.Contains("New payroll calculation from active craft plan", source);
        Assert.Contains("Copy Payroll Summary", source);
        Assert.Contains("title=\"@line.UnitCostExplanation\"", source);
        Assert.Contains("Responsibility", source);
        Assert.Contains("CommissionMaterialResponsibility.Crafter", source);
        Assert.Contains("CommissionMaterialResponsibility.Provided", source);
        Assert.Contains("Assigned Crafter", source);
        Assert.DoesNotContain("Commissioner name", source);
        Assert.Contains("TradeOperationsPersistenceService", source);
        Assert.Contains("TradePayrollPersistenceService", source);
        Assert.Contains("TradeOrderDraftFactory", source);
        Assert.DoesNotContain("TradeOrderCraftSnapshotService", source);
        Assert.Contains("FormatPayrollDate(_source.ImportedAtUtc)", source);
        Assert.Contains("Being made", source);
        Assert.Contains("_source.CraftedItems", source);
        Assert.Contains("Commission is @_commissionPercent.ToString(\"N0\")% of full estimated material cost", source);
        Assert.Contains("Crafter procures", source);
        Assert.Contains("Provided by commissioner", source);
        Assert.Contains("trade-assignment-actions", source);
        Assert.Contains("trade-assign-button", source);
        Assert.Contains("trade-open-orders-button", source);
        Assert.Contains("trade-meta-list", source);
        Assert.Contains("trade-crafted-table", source);
        Assert.Contains("trade-payroll-note", source);
        Assert.Contains("FormatProjectName(item)", source);
        Assert.Contains("TradeDisplayFormatter.FormatQuantity(item.Quantity)", source);
        Assert.DoesNotContain("<ul>\r\n                            @foreach (var item in _source.CraftedItems", source);
        Assert.DoesNotContain("Guild commission", source);
        Assert.DoesNotContain("MudNumericField", source);
        Assert.DoesNotContain("Handling", source);
    }

    [Fact]
    public void TradePayrollSummary_UsesAssignedCrafterAndMaterialResponsibilitySections()
    {
        var source = File.ReadAllText(GetTradePayrollPath());

        Assert.Contains("summary.AppendLine($\"Plan: {FormatPayrollDate(_source!.ImportedAtUtc)}\")", source);
        Assert.Contains("summary.AppendLine($\"Assigned crafter: {FormatAssignedCrafterName()}\")", source);
        Assert.Contains("AppendCraftedItemsSection(summary, _source.CraftedItems)", source);
        Assert.Contains("AppendMaterialSection(summary, \"Crafter procures\"", source);
        Assert.Contains("AppendMaterialSection(summary, \"Provided by commissioner\"", source);
        Assert.Contains("Materials reimbursement", source);
        Assert.DoesNotContain("summary.AppendLine($\"Plan: {_source!.SourcePlanName}\")", source);
        Assert.DoesNotContain("summary.AppendLine($\"Cost basis:", source);
        Assert.DoesNotContain("summary.AppendLine($\"Evidence:", source);
        Assert.DoesNotContain("summary.AppendLine($\"Name:", source);
        Assert.DoesNotContain("Provided materials excluded", source);
        Assert.DoesNotContain("Commission on full estimate", source);
    }

    [Fact]
    public void TradePayrollPage_PersistsResponsibilityWorkflowChanges()
    {
        var source = File.ReadAllText(GetTradePayrollPath());

        Assert.Contains("SetResponsibilityAsync", source);
        Assert.Contains("SaveWorkflowDraftAsync", source);
        Assert.Contains("TradePayrollResponsibilityLine", source);
        Assert.Contains("ApplyResponsibilities", source);
    }

    [Fact]
    public void TradePayrollPage_AssignsCraftJobsThroughOrders()
    {
        var source = File.ReadAllText(GetTradePayrollPath());

        Assert.Contains("Assign Craft Job", source);
        Assert.Contains("AssignCraftJobAsync", source);
        Assert.Contains("TradeOrderDraftFactory.CreateFromCurrentPlan", source);
        Assert.Contains("TradeOperationsPersistence.SaveOrderAsync(orderToSave)", source);
        Assert.DoesNotContain("DeleteSnapshotAsync(snapshot.Id)", source);
        Assert.Contains("_workflowDraft.OrderId = orderToSave.Id", source);
        Assert.Contains("TradeOrderHistoryEventKind.Assigned", source);
        Assert.Contains("TradeOrderHistoryEventKind.PayrollLinked", source);
        Assert.Contains("Open Orders", source);
        Assert.Contains("trade/orders?orderId=", source);
    }

    [Fact]
    public void TradePayrollPage_DoesNotMentionCustomerQuotes()
    {
        var source = File.ReadAllText(GetTradePayrollPath());

        Assert.DoesNotContain("Quote", source);
        Assert.DoesNotContain("Customer", source);
    }

    private static string GetTradePayrollPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Pages", "TradePayroll.razor");
    }
}
