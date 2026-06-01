namespace FFXIV_Craft_Architect.Tests;

public class WebGitHubPagesDeploymentTests
{
    [Fact]
    public void DeployWorkflow_PublishesMainAndLocalDevArtifacts()
    {
        var workflow = ReadRepoFile(".github", "workflows", "deploy-web.yml");

        Assert.Contains("branches: [ main, master, local-dev ]", workflow);
        Assert.Contains("ref: main", workflow);
        Assert.Contains("ref: local-dev", workflow);
        Assert.Contains("dist/pages/local-dev", workflow);
        Assert.Contains("dist/web/local-dev/wwwroot/404.html dist/pages/404.html", workflow);
        Assert.Contains("path: dist/pages", workflow);
        Assert.Contains("if: github.ref == 'refs/heads/main' || github.ref == 'refs/heads/master'", workflow);
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
