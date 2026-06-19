namespace FFXIV_Craft_Architect.Tests;

public class TradeCraftersMarkupTests
{
    [Fact]
    public void TradeCraftersPage_UsesNameOnlyCreationAndCraftingJobLevels()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));

        Assert.Contains("@page \"/trade/crafters\"", source);
        Assert.Contains("Display name", source);
        Assert.Contains("Create Crafter", source);
        Assert.Contains("TradeCraftingJob", source);
        Assert.Contains("Carpenter", source);
        Assert.Contains("Culinarian", source);
        Assert.DoesNotContain("Miner", source);
        Assert.DoesNotContain("Botanist", source);
        Assert.DoesNotContain("Fisher", source);
    }

    [Fact]
    public void TradeCraftersPage_ShowsAssignmentsInRosterAndDetailPanel()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Pages", "TradeCrafters.razor"));

        Assert.Contains("Crafter Details", source);
        Assert.Contains("Current Assignments", source);
        Assert.Contains("TradeCrafterColumn.Assignments", source);
        Assert.Contains("OpenOrderAssignment", source);
        Assert.Contains("trade/orders?orderId=", source);
        Assert.Contains("_selectedCrafter", source);
    }

    [Fact]
    public void MainLayout_TradeModeShowsOrdersAndCraftersTabs()
    {
        var source = File.ReadAllText(GetWorkspacePath("src", "FFXIV Craft Architect.Web", "Shared", "MainLayout.razor"));

        Assert.Contains("NavigateTo(\"trade/orders\")", source);
        Assert.Contains("NavigateTo(\"trade/crafters\")", source);
        Assert.Contains("Orders", source);
        Assert.Contains("Crafters", source);
        Assert.DoesNotContain(">Payroll", source);
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
