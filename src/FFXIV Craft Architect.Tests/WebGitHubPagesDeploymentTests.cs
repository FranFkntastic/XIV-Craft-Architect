namespace FFXIV_Craft_Architect.Tests;

public class WebGitHubPagesDeploymentTests
{
    [Fact]
    public void DeployWorkflow_PublishesManualMovedNoticeOnly()
    {
        var workflow = ReadRepoFile(".github", "workflows", "deploy-web.yml");

        Assert.Contains("name: Manual GitHub Pages Moved Notice Deploy", workflow);
        Assert.Contains("workflow_dispatch:", workflow);
        Assert.DoesNotContain("push:", workflow);
        Assert.DoesNotContain("actions/setup-dotnet", workflow);
        Assert.DoesNotContain("dotnet publish", workflow);
        Assert.DoesNotContain("dist/web", workflow);
        Assert.Contains("dist/pages/local-dev", workflow);
        Assert.Contains("FFXIV Craft Architect has moved", workflow);
        Assert.Contains("https://xivcraftarchitect.com", workflow);
        Assert.Contains("If you're here, you know where to find the dev build.", workflow);
        Assert.DoesNotContain("dev.xivcraftarchitect.com", workflow);
        Assert.Contains("path: dist/pages", workflow);
        Assert.Contains("uses: actions/deploy-pages@v4", workflow);
    }

    [Fact]
    public void IndexHtml_DetectsLocalDevGitHubPagesBasePath()
    {
        var indexHtml = ReadRepoFile("src", "FFXIV Craft Architect.Web", "wwwroot", "index.html");

        Assert.Contains("const githubPagesLocalDevBase = '/XIV-Craft-Architect/local-dev/';", indexHtml);
        Assert.Contains("pathname.startsWith(githubPagesLocalDevBase)", indexHtml);
        Assert.Contains("baseHref = githubPagesLocalDevBase;", indexHtml);
    }

    [Fact]
    public void NotFoundHtml_RedirectsLocalDevRoutesToLocalDevBase()
    {
        var notFoundHtml = ReadRepoFile("src", "FFXIV Craft Architect.Web", "wwwroot", "404.html");

        Assert.Contains("const localDevBase = repoName + '/local-dev';", notFoundHtml);
        Assert.Contains("path === localDevBase || path.startsWith(localDevBase + '/')", notFoundHtml);
        Assert.Contains("const baseUrl = window.location.origin + activeBase + '/';", notFoundHtml);
    }

    [Fact]
    public void IndexedDbScript_RetriesWithoutVersionWhenExistingDatabaseIsNewer()
    {
        var indexedDbScript = ReadRepoFile("src", "FFXIV Craft Architect.Web", "wwwroot", "indexedDB.js");

        Assert.Contains("request.error?.name === 'VersionError'", indexedDbScript);
        Assert.Contains("openExistingDatabaseVersion", indexedDbScript);
        Assert.Contains("indexedDB.open(DB_NAME)", indexedDbScript);
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
