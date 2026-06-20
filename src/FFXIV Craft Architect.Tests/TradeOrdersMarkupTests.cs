namespace FFXIV_Craft_Architect.Tests;

public class TradeOrdersMarkupTests
{
    [Fact]
    public void TradeOrdersPage_UsesThreeColumnOperationsBoardAndRightPanelTabs()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains("@page \"/trade/orders\"", source);
        Assert.Contains("trade-orders-board", source);
        Assert.Contains("trade-orders-rail", source);
        Assert.Contains("trade-orders-workspace", source);
        Assert.Contains("trade-orders-ops", source);
        Assert.Contains("MudTabs", source);
        Assert.Contains("Payment", source);
        Assert.Contains("Procurement", source);
        Assert.Contains("History", source);
        Assert.Contains("Ready to Assign", source);
        Assert.Contains("Assigned Awaiting Payment", source);
        Assert.Contains("Awaiting Delivery", source);
        Assert.Contains("Order status", source);
        Assert.Contains("Close Order", source);
        Assert.Contains("Reopen Order", source);
        Assert.Contains("Add Note", source);
        Assert.Contains("Linked Craft Plan", source);
        Assert.DoesNotContain("Plan Snapshot", source);
        Assert.DoesNotContain("Refresh Snapshot", source);
        Assert.DoesNotContain("WebDataTable", source);
        Assert.DoesNotContain("DragDrop", source);
    }

    [Fact]
    public void TradeOrdersPage_ImportsFromActiveCraftPlanWithTitleConfirmation()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains("Create From Active Plan", source);
        Assert.Contains("_newOrderTitle", source);
        Assert.Contains("Active Craft Plan", source);
        Assert.Contains("Assigned crafter", source);
        Assert.Contains("TradeOrderDraftFactory", source);
        Assert.Contains("TradeOperationsPersistenceService", source);
    }

    [Fact]
    public void TradeOrdersPage_CreatesTradeNativeOrdersThroughCraftPlanBoundary()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains("New Order", source);
        Assert.Contains("Add output item", source);
        Assert.Contains("SearchRequestedOrderItemsAsync", source);
        Assert.Contains("TradeOrderCraftPlanBuildService.BuildAsync", source);
        Assert.Contains("PlanPersistence.SaveGeneratedOrderPlanAsync", source);
        Assert.Contains("PlanPersistence.LoadPlanIntoSessionAsync", source);
        Assert.Contains("TradeOrderCraftPlanLinkKind.OrderGenerated", source);
        Assert.Contains("CreateFromRequestedOutputs", source);
        Assert.Contains("replaceExistingPlan: false", source);
        Assert.Contains("replaceExistingPlan: true", source);
        Assert.DoesNotContain("TradeOrderCraftSnapshot", source);
        Assert.DoesNotContain("RecipePlannerCommandService", source);
        Assert.DoesNotContain("ImportProjectItemsAsync", source);
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
