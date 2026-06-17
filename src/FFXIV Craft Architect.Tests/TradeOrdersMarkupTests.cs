namespace FFXIV_Craft_Architect.Tests;

public class TradeOrdersMarkupTests
{
    [Fact]
    public void TradeOrdersPage_UsesDenseGroupedTableAndDetailPanelMutation()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains("@page \"/trade/orders\"", source);
        Assert.Contains("WebDataTable", source);
        Assert.Contains("Ready to Assign", source);
        Assert.Contains("Awaiting Delivery", source);
        Assert.Contains("Order Details", source);
        Assert.Contains("Change status", source);
        Assert.Contains("Close Order", source);
        Assert.Contains("Reopen Order", source);
        Assert.Contains("Add Note", source);
        Assert.DoesNotContain("Manual order", source);
        Assert.DoesNotContain("DragDrop", source);
    }

    [Fact]
    public void TradeOrdersPage_ImportsFromActiveCraftPlanWithTitleConfirmation()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeOrders.razor"));

        Assert.Contains("Create Order from Active Craft Plan", source);
        Assert.Contains("_newOrderTitle", source);
        Assert.Contains("Suggested title", source);
        Assert.Contains("Assigned crafter", source);
        Assert.Contains("TradeOrderDraftFactory", source);
        Assert.Contains("TradeOperationsPersistenceService", source);
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
