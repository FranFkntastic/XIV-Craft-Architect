namespace FFXIV_Craft_Architect.Tests;

public class AcquisitionDetailsPanelMarkupTests
{
    [Fact]
    public void AcquisitionDetailsPanel_StylesUnsupportedProjectedOptions()
    {
        var markup = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "FFXIV Craft Architect.Web",
                "Shared",
                "AcquisitionDetailsPanel.razor"));
        var styles = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "FFXIV Craft Architect.Web",
                "Shared",
                "AcquisitionDetailsPanel.razor.css"));

        Assert.Contains("option.IsProjectedUnsupported", markup);
        Assert.Contains("projected-unsupported", markup);
        Assert.Contains("rp-method-row.projected-unsupported", styles);
        Assert.Contains("rp-method-cost.projected-unsupported", styles);
    }
}
