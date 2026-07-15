using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class GitHubIssueReportServiceTests
{
    [Fact]
    public void CreateIssueUrl_PrefillsReproductionAndSafeDiagnosticContext()
    {
        var service = new GitHubIssueReportService();

        var url = service.CreateIssueUrl(new GitHubIssueReportContext(
            "2026.07.15.4",
            "local-dev",
            "abc1234",
            "North America",
            3,
            3,
            MarketEvidenceHydrationStatus.Published));

        var uri = new Uri(url);
        var decoded = Uri.UnescapeDataString(uri.Query);

        Assert.Equal("github.com", uri.Host);
        Assert.Equal("/FranFkntastic/XIV-Craft-Architect/issues/new", uri.AbsolutePath);
        Assert.Contains("[Bug] Describe the problem", decoded);
        Assert.Contains("How can we reproduce it?", decoded);
        Assert.Contains("2026.07.15.4", decoded);
        Assert.Contains("local-dev", decoded);
        Assert.Contains("Published", decoded);
        Assert.Contains("No diagnostic file was prepared", decoded);
        Assert.DoesNotContain("Drag the downloaded file", decoded);
    }

    [Fact]
    public void CreateIssueUrl_WhenAttachmentIsPrepared_NamesFileAndRequiresExplicitAttachment()
    {
        var service = new GitHubIssueReportService();

        var url = service.CreateIssueUrl(new GitHubIssueReportContext(
            "2026.07.15.4",
            "local-dev",
            "abc1234",
            "North America",
            3,
            3,
            MarketEvidenceHydrationStatus.Published,
            PrepareDiagnosticAttachment: true,
            DiagnosticFileName: "cobalt-rivets.ca-diagnostic.json"));

        var decoded = Uri.UnescapeDataString(new Uri(url).Query);

        Assert.Contains("cobalt-rivets.ca-diagnostic.json", decoded);
        Assert.Contains("reviewed and attached that file", decoded);
        Assert.Contains("Drag the downloaded file", decoded);
        Assert.DoesNotContain("No diagnostic file was prepared", decoded);
    }
}
