namespace FFXIV_Craft_Architect.Tests;

public sealed class GitHubActionsWorkflowLintTests
{
    [Fact]
    public void WorkflowLint_RunsActionlintForWorkflowChanges()
    {
        var workflow = ReadRepoFile(".github", "workflows", "lint-workflows.yml");

        Assert.Contains("name: Lint GitHub Actions Workflows", workflow);
        Assert.Contains("paths:", workflow);
        Assert.Contains("'.github/workflows/**'", workflow);
        Assert.Contains("workflow_dispatch:", workflow);
        Assert.Contains("ACTIONLINT_VERSION: 1.7.12", workflow);
        Assert.Contains("github.com/rhysd/actionlint/releases/download/v${ACTIONLINT_VERSION}/actionlint_${ACTIONLINT_VERSION}_linux_amd64.tar.gz", workflow);
        Assert.Contains("actionlint", workflow);
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
