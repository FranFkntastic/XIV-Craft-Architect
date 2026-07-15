using System.Text;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class GitHubIssueReportService
{
    private const string NewIssueUrl =
        "https://github.com/FranFkntastic/XIV-Craft-Architect/issues/new";

    public string CreateIssueUrl(GitHubIssueReportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var title = "[Bug] Describe the problem";
        var body = new StringBuilder()
            .AppendLine("## What happened?")
            .AppendLine("<!-- Replace this line with what you observed. -->")
            .AppendLine()
            .AppendLine("## What did you expect?")
            .AppendLine("<!-- Replace this line with the expected behavior. -->")
            .AppendLine()
            .AppendLine("## How can we reproduce it?")
            .AppendLine("1. ")
            .AppendLine()
            .AppendLine("## Craft Architect context")
            .AppendLine($"- Build: `{context.BuildVersion}`")
            .AppendLine($"- Branch / commit: `{context.Branch}` / `{context.Commit}`")
            .AppendLine($"- Market scope: `{context.MarketScope}`")
            .AppendLine($"- Loaded evidence: {context.ShoppingPlanCount:N0} shopping plans, {context.MarketAnalysisCount:N0} analyses")
            .AppendLine($"- Automatic refresh: `{context.AutomaticRefreshStatus}`")
            .AppendLine()
            .AppendLine("## Diagnostic attachment");

        if (context.PrepareDiagnosticAttachment)
        {
            body
                .AppendLine($"Craft Architect prepared `{context.DiagnosticFileName}` alongside this draft.")
                .AppendLine()
                .AppendLine("- [ ] I reviewed and attached that file before submitting this issue.")
                .AppendLine()
                .AppendLine("> GitHub does not allow another site to insert a local file into its editor. Drag the downloaded file into this issue before submitting.");
        }
        else
        {
            body.AppendLine("> No diagnostic file was prepared for this report.");
        }

        return $"{NewIssueUrl}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body.ToString())}";
    }
}

public sealed record GitHubIssueReportContext(
    string BuildVersion,
    string Branch,
    string Commit,
    string MarketScope,
    int ShoppingPlanCount,
    int MarketAnalysisCount,
    MarketEvidenceHydrationStatus AutomaticRefreshStatus,
    bool PrepareDiagnosticAttachment = false,
    string? DiagnosticFileName = null);
