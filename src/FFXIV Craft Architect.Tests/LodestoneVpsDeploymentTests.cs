namespace FFXIV_Craft_Architect.Tests;

public sealed class LodestoneVpsDeploymentTests
{
    [Fact]
    public void LodestoneDeployWorkflow_IsManualOnly()
    {
        var workflow = ReadRepoFile(".github", "workflows", "deploy-vps-lodestone.yml");

        Assert.Contains("name: Deploy Lodestone Helper to VPS", workflow);
        Assert.Contains("workflow_dispatch:", workflow);
        Assert.DoesNotContain("push:", workflow);
    }

    [Fact]
    public void LodestoneDeployWorkflow_PublishesSelfContainedLinuxHelper()
    {
        var workflow = ReadRepoFile(".github", "workflows", "deploy-vps-lodestone.yml");

        Assert.Contains("FFXIV Craft Architect.LodestoneLookup.csproj", workflow);
        Assert.Contains("-r linux-x64", workflow);
        Assert.Contains("--self-contained true", workflow);
        Assert.Contains("tar -czf \"$archive\" -C dist/lodestone .", workflow);
    }

    [Fact]
    public void LodestoneDeployWorkflow_RunsFullTestSuiteBeforePublishing()
    {
        var workflow = ReadRepoFile(".github", "workflows", "deploy-vps-lodestone.yml");

        Assert.Contains("name: Run full test suite", workflow);
        Assert.Contains("dotnet test \"src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj\"", workflow);
        Assert.True(
            workflow.IndexOf("name: Run full test suite", StringComparison.Ordinal) <
            workflow.IndexOf("name: Publish Lodestone helper", StringComparison.Ordinal));
    }

    [Fact]
    public void LodestoneDeployWorkflow_ActivatesSystemdRelease()
    {
        var workflow = ReadRepoFile(".github", "workflows", "deploy-vps-lodestone.yml");

        Assert.Contains("root=\"/srv/craftarchitect/services/lodestone\"", workflow);
        Assert.Contains("release_dir=\"$root/releases/$release\"", workflow);
        Assert.Contains("sudo ln -sfn \"$release_dir\" \"$root/current\"", workflow);
        Assert.Contains("sudo systemctl restart craftarchitect-lodestone", workflow);
        Assert.Contains("systemctl is-active craftarchitect-lodestone", workflow);
        Assert.Contains("for attempt in {1..30}", workflow);
        Assert.Contains("https://dev.xivcraftarchitect.com/api/lodestone/crafters/search", workflow);
    }

    private static string ReadRepoFile(params string[] segments)
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine([repoRoot.FullName, .. segments]);

        return File.ReadAllText(path);
    }

    private static DirectoryInfo FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test output directory.");
    }
}
