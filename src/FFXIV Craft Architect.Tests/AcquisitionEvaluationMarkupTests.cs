namespace FFXIV_Craft_Architect.Tests;

public class AcquisitionEvaluationMarkupTests
{
    [Fact]
    public void AcquisitionEvaluation_UsesCalculatedTotalWordingAndTooltipHooks()
    {
        var markup = File.ReadAllText(GetWebPath("Pages", "AcquisitionEvaluation.razor"));
        var styles = File.ReadAllText(GetWebPath("Pages", "AcquisitionEvaluation.razor.css"));

        Assert.Contains("\"Calculated Total\"", markup);
        Assert.DoesNotContain("\"Estimate\"", markup);
        Assert.Contains("GetColumnHeaderTooltip(ColumnHeaders[i])", markup);
        Assert.Contains("GetCalculatedTotalClass(row)", markup);
        Assert.Contains("GetCalculatedTotalTooltip(row)", markup);
        Assert.Contains("ae-cost-cell.projected-unsupported", styles);
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
