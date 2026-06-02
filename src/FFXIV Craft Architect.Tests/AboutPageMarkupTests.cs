namespace FFXIV_Craft_Architect.Tests;

public class AboutPageMarkupTests
{
    [Fact]
    public void AboutPage_DescribesCurrentWebWorkflows()
    {
        var source = File.ReadAllText(GetAboutPagePath());

        Assert.Contains("market-aware crafting planner", source);
        Assert.Contains("recipe tree", source);
        Assert.Contains("acquisition choices", source);
        Assert.Contains("market evidence", source);
        Assert.Contains("shopping route", source);
        Assert.Contains("craft, vendor, or buy", source);
        Assert.DoesNotContain("Runs in your browser", source);
    }

    [Fact]
    public void AboutPage_DoesNotAdvertiseUnreleasedDesktopApp()
    {
        var source = File.ReadAllText(GetAboutPagePath());

        Assert.DoesNotContain("Desktop App", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WPF", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Download Desktop", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("packet capture", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/releases", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAboutPagePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Pages", "About.razor");
    }
}
