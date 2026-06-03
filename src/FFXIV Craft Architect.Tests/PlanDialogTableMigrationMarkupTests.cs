namespace FFXIV_Craft_Architect.Tests;

public class PlanDialogTableMigrationMarkupTests
{
    [Fact]
    public void PlanBuilderDialog_UsesGridTableForSearchAndTargetTables()
    {
        var source = File.ReadAllText(GetWebPath("Dialogs", "PlanBuilderDialog.razor"));
        var styles = File.ReadAllText(GetWebPath("Dialogs", "PlanBuilderDialog.razor.css"));

        Assert.Equal(2, CountOccurrences(source, "WebGridTable"));
        Assert.Contains("GetSearchResultColumns()", source);
        Assert.Contains("GetTargetItemColumns()", source);
        Assert.Contains("EmptyContent=\"RenderSearchEmptyContent\"", source);
        Assert.Contains("EmptyContent=\"RenderTargetEmptyContent\"", source);
        Assert.Contains("ToggleSearchSelection(result.Id, args)", source);
        Assert.Contains("SetSearchQuantity(result.Id, args)", source);
        Assert.Contains("ToggleQuality(item)", source);
        Assert.Contains("RemoveItem(item)", source);
        Assert.Contains("::deep .pb-result-table", styles);
        Assert.Contains("::deep .pb-target-table", styles);
    }

    [Fact]
    public void PlanEditorDialog_UsesGridTableWithoutSortingAndKeepsTreeSelectionHooks()
    {
        var source = File.ReadAllText(GetWebPath("Dialogs", "PlanEditorDialog.razor"));
        var styles = File.ReadAllText(GetWebPath("Dialogs", "PlanEditorDialog.razor.css"));

        Assert.Contains("WebGridTable", source);
        Assert.Contains("GetNodeColumns()", source);
        Assert.Contains("Sortable = false", source);
        Assert.Contains("RowActivated=\"ToggleNodeFromRow\"", source);
        Assert.Contains("SuppressRowActivation = true", source);
        Assert.Contains("row => row.Node.NodeId", source);
        Assert.Contains("row.Depth * 14", source);
        Assert.Contains("ToggleNode(row.Node.NodeId, args)", source);
        Assert.Contains("ToggleNodeFromRow(PlanNodeEditRow row)", source);
        Assert.Contains("PlanBulkEditService.FilterNodes(_rows, _filter)", source);
        Assert.Contains("::deep .pe-node-table", styles);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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
