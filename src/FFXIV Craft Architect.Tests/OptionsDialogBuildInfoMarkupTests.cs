namespace FFXIV_Craft_Architect.Tests;

public class OptionsDialogBuildInfoMarkupTests
{
    [Fact]
    public void OptionsDialog_AboutTabShowsBuildVersionAndBranch()
    {
        var source = File.ReadAllText(GetWebPath("Dialogs", "OptionsDialog.razor"));

        Assert.Contains("Build @WebBuildInfo.BuildVersion", source);
        Assert.Contains("Branch @WebBuildInfo.BranchName", source);
    }

    [Fact]
    public void OptionsDialog_AboutTabShowsCommitDetailsOnlyWhenDebugToolsEnabled()
    {
        var source = File.ReadAllText(GetWebPath("Dialogs", "OptionsDialog.razor"));

        Assert.Contains("@if (AppState.SecretDebugToolsEnabled)", source);
        Assert.Contains("Commit @WebBuildInfo.CommitSha", source);
        Assert.Contains("Dirty @WebBuildInfo.IsDirty", source);
    }

    [Fact]
    public void WebProject_GeneratesBuildInfoDuringCompile()
    {
        var project = File.ReadAllText(GetWebPath("FFXIV Craft Architect.Web.csproj"));
        var script = File.ReadAllText(GetWebPath("Build", "GenerateBuildInfo.ps1"));

        Assert.Contains("GenerateWebBuildInfo", project);
        Assert.Contains("GeneratedBuildInfo.g.cs", project);
        Assert.Contains("GenerateBuildInfo.ps1", project);
        Assert.Contains("git rev-list --count", script);
        Assert.Contains("WebBuildInfo", script);
    }

    private static string GetWebPath(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(new[] { directory.FullName, "src", "FFXIV Craft Architect.Web" }.Concat(segments).ToArray());
    }
}
