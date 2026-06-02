using System.Text.RegularExpressions;

namespace FFXIV_Craft_Architect.Tests;

public class OptionsDialogMarkupTests
{
    [Fact]
    public void OptionsDialog_AboutTab_HasQuietBottomLeftDebugToggle()
    {
        var source = File.ReadAllText(GetOptionsDialogPath());
        var aboutTab = Regex.Match(
            source,
            "<MudTabPanel Text=\"About\">[\\s\\S]*?</MudTabPanel>",
            RegexOptions.CultureInvariant);

        Assert.True(aboutTab.Success);
        Assert.Contains("Icon=\"@Icons.Material.Filled.BugReport\"", aboutTab.Value);
        Assert.Contains("OnClick=\"ToggleSecretDebugToolsAsync\"", aboutTab.Value);
        Assert.Contains("position: absolute", aboutTab.Value);
        Assert.Contains("bottom: 8px", aboutTab.Value);
        Assert.Contains("left: 8px", aboutTab.Value);
        Assert.Contains("Size=\"Size.Small\"", aboutTab.Value);
        Assert.DoesNotContain("Tooltip", aboutTab.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptionsDialog_DebugToggle_PersistsSecretDebugToolsSetting()
    {
        var source = File.ReadAllText(GetOptionsDialogPath());

        Assert.Contains("debug.secret_tools_enabled", source);
        Assert.Contains("AppState.SetSecretDebugToolsEnabled", source);
        Assert.Contains("Settings.SetAsync", source);
    }

    private static string GetOptionsDialogPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Dialogs", "OptionsDialog.razor");
    }
}
