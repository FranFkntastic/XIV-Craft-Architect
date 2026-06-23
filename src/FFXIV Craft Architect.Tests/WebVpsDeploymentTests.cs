namespace FFXIV_Craft_Architect.Tests;

public sealed class WebVpsDeploymentTests
{
    [Fact]
    public void VpsDeployWorkflow_DeploysMainAndLocalDevSlots()
    {
        var workflow = ReadRepoFile(".github", "workflows", "deploy-vps-web.yml");

        Assert.Contains("branches: [ main, master, local-dev ]", workflow);
        Assert.Contains("slot=\"local-dev\"", workflow);
        Assert.Contains("domain=\"dev.xivcraftarchitect.com\"", workflow);
        Assert.Contains("slot=\"main\"", workflow);
        Assert.Contains("domain=\"xivcraftarchitect.com\"", workflow);
        Assert.Contains("-p:BuildInfoBranchName=${{ steps.deploy-slot.outputs.ref }}", workflow);
    }

    [Fact]
    public void VpsDeployWorkflow_WritesHostedApiConfiguration()
    {
        var workflow = ReadRepoFile(".github", "workflows", "deploy-vps-web.yml");

        Assert.Contains("dist/web/wwwroot/appsettings.json", workflow);
        Assert.Contains("\"BaseAddress\": \"https://${{ steps.deploy-slot.outputs.domain }}/api/\"", workflow);
    }

    [Fact]
    public void VpsDeployWorkflow_RunsFullTestSuiteBeforePublishing()
    {
        var workflow = ReadRepoFile(".github", "workflows", "deploy-vps-web.yml");

        Assert.Contains("name: Run full test suite", workflow);
        Assert.Contains("dotnet test \"src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj\"", workflow);
        Assert.Contains("-p:EnableWindowsTargeting=true", workflow);
        Assert.True(
            workflow.IndexOf("name: Run full test suite", StringComparison.Ordinal) <
            workflow.IndexOf("name: Publish web app", StringComparison.Ordinal));
    }

    [Fact]
    public void VpsDeployWorkflow_ActivatesReleaseBySymlink()
    {
        var workflow = ReadRepoFile(".github", "workflows", "deploy-vps-web.yml");

        Assert.Contains("VPS_HOST", workflow);
        Assert.Contains("VPS_USER", workflow);
        Assert.Contains("VPS_SSH_PRIVATE_KEY", workflow);
        Assert.Contains("root=\"/srv/craftarchitect/web/$slot\"", workflow);
        Assert.Contains("release_dir=\"$root/releases/$release\"", workflow);
        Assert.Contains("sudo ln -sfn \"$release_dir\" \"$root/current\"", workflow);
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
