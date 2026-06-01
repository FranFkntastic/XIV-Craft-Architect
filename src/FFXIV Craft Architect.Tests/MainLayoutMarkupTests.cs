using System.Text.RegularExpressions;

namespace FFXIV_Craft_Architect.Tests;

public class MainLayoutMarkupTests
{
    [Fact]
    public void MainLayout_DebugMenu_UsesClickableExpandableToolsSection()
    {
        var source = File.ReadAllText(GetMainLayoutPath());
        var debugActivator = Regex.Match(
            source,
            "<MudMenuItem\\s+OnClick=\"ToggleDebugToolsMenu\"[\\s\\S]*?</MudMenuItem>",
            RegexOptions.CultureInvariant);

        Assert.True(debugActivator.Success);
        Assert.Contains("Icon=\"@Icons.Material.Filled.BugReport\"", debugActivator.Value);
        Assert.Contains("AutoClose=\"false\"", debugActivator.Value);
        Assert.Contains("<span>Debug</span>", debugActivator.Value);
        Assert.Contains("_debugToolsMenuOpen", source);
        Assert.Contains("Dump Selected Market Analysis Item", source);
        Assert.DoesNotContain("ActivationEvent=\"MouseEvent.MouseOver\"", debugActivator.Value);
        Assert.DoesNotContain("Label=\"Debug\"", source);
        Assert.DoesNotContain("<ActivatorContent>", debugActivator.Value);
    }

    private static string GetMainLayoutPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Shared", "MainLayout.razor");
    }
}
