using Bunit;
using FFXIV_Craft_Architect.Web.Shared.TablePrimitives;
using Microsoft.AspNetCore.Components;

namespace FFXIV_Craft_Architect.Tests;

public class WebTableComponentTests : BunitContext
{
    [Fact]
    public void HeaderCell_RendersSortStateAndEmitsToggle()
    {
        WebTableSortState<TestColumn>? received = null;

        var rendered = Render<WebTableHeaderCell<TestColumn>>(parameters => parameters
            .Add(parameter => parameter.ColumnId, TestColumn.Name)
            .Add(parameter => parameter.Label, "Name")
            .Add(parameter => parameter.Tooltip, "Sort by name")
            .Add(parameter => parameter.SortState, new WebTableSortState<TestColumn>(TestColumn.Name, Descending: false))
            .Add(parameter => parameter.SortChanged, state => received = state));

        var button = rendered.Find("button.web-table-sort-button");
        Assert.Equal("ascending", rendered.Find("[role='columnheader']").GetAttribute("aria-sort"));
        Assert.Equal("Sort by name", rendered.Find("[role='columnheader']").GetAttribute("title"));

        button.Click();

        Assert.Equal(TestColumn.Name, received?.Column);
        Assert.True(received?.Descending);
    }

    [Fact]
    public void DataTable_RendersSemanticTableRowsAndExpandedContent()
    {
        var rows = new[]
        {
            new TestRow("ore", "Ore", 4, 100),
            new TestRow("ingot", "Ingot", 2, 250)
        };

        var rendered = Render<WebDataTable<TestRow, TestColumn>>(parameters => parameters
            .Add(parameter => parameter.Items, rows)
            .Add(parameter => parameter.Columns, CreateColumns())
            .Add(parameter => parameter.GetRowKey, row => row.Key)
            .Add(parameter => parameter.IsRowExpanded, row => row.Key == "ore")
            .Add(parameter => parameter.ExpandedContent, row => builder =>
            {
                builder.OpenElement(0, "span");
                builder.AddContent(1, $"expanded {row.Name}");
                builder.CloseElement();
            }));

        Assert.Equal("table", rendered.Find("table").GetAttribute("role"));
        Assert.Equal(2, rendered.FindAll("tbody tr.web-data-table-row").Count);
        Assert.Equal("3", rendered.Find(".web-data-table-expanded-cell").GetAttribute("colspan"));
        Assert.Contains("expanded Ore", rendered.Markup);
    }

    [Fact]
    public void DataTable_RendersEmptyContentWhenNoRowsExist()
    {
        RenderFragment emptyContent = builder =>
        {
            builder.OpenElement(0, "span");
            builder.AddContent(1, "No rows");
            builder.CloseElement();
        };

        var rendered = Render<WebDataTable<TestRow, TestColumn>>(parameters => parameters
            .Add(parameter => parameter.Items, Array.Empty<TestRow>())
            .Add(parameter => parameter.Columns, CreateColumns())
            .Add(parameter => parameter.EmptyContent, emptyContent));

        Assert.Empty(rendered.FindAll("tbody tr.web-data-table-row"));
        Assert.Contains("No rows", rendered.Find(".web-data-table-empty-cell").TextContent);
        Assert.Equal("3", rendered.Find(".web-data-table-empty-cell").GetAttribute("colspan"));
    }

    [Fact]
    public void DataTable_RendersColumnSizingAndCssHooks()
    {
        var columns = new[]
        {
            new WebTableColumn<TestRow, TestColumn>
            {
                Id = TestColumn.Name,
                Header = "Name",
                Size = WebTableColumnSize.Percent(22m),
                ColCssClass = "name-col",
                HeaderCssClass = "name-header",
                CellCssClass = "name-cell",
                CellTemplate = row => builder => builder.AddContent(0, row.Name)
            }
        };

        var rendered = Render<WebDataTable<TestRow, TestColumn>>(parameters => parameters
            .Add(parameter => parameter.Items, new[] { new TestRow("ore", "Ore", 4, 100) })
            .Add(parameter => parameter.Columns, columns));

        Assert.Contains("name-col", rendered.Find("col").GetAttribute("class"));
        Assert.Contains("width: 22%", rendered.Find("col").GetAttribute("style"));
        Assert.Contains("name-header", rendered.Find("th .web-table-header-cell").GetAttribute("class"));
        Assert.Contains("name-cell", rendered.Find("td").GetAttribute("class"));
    }

    [Fact]
    public void GridTable_RendersAriaTableAndActivatesRows()
    {
        var rows = new[]
        {
            new TestRow("ore", "Ore", 4, 100),
            new TestRow("ingot", "Ingot", 2, 250)
        };
        string? activatedKey = null;

        var rendered = Render<WebGridTable<TestRow, TestColumn>>(parameters => parameters
            .Add(parameter => parameter.Items, rows)
            .Add(parameter => parameter.Columns, CreateColumns())
            .Add(parameter => parameter.GetRowKey, row => row.Key)
            .Add(parameter => parameter.Selection, WebTableSelection<string>.Single("ingot"))
            .Add(parameter => parameter.GetRowClass, row => row.Key == "ingot" ? "selected-row" : string.Empty)
            .Add(parameter => parameter.RowActionLabel, row => $"Select {row.Name}")
            .Add(parameter => parameter.RowActivated, row => activatedKey = row.Key));

        Assert.Equal("table", rendered.Find("[role='table']").GetAttribute("role"));
        Assert.Equal("true", rendered.Find(".web-grid-table-row.is-selected").GetAttribute("aria-selected"));
        Assert.Contains("selected-row", rendered.Find(".web-grid-table-row.is-selected").GetAttribute("class"));

        rendered.Find(".web-grid-table-row[aria-label='Select Ore']").Click();

        Assert.Equal("ore", activatedKey);
    }

    [Fact]
    public void GridTable_RowActivationWorksFromNonFirstCells()
    {
        var rows = new[]
        {
            new TestRow("ore", "Ore", 4, 100)
        };
        string? activatedKey = null;

        var rendered = Render<WebGridTable<TestRow, TestColumn>>(parameters => parameters
            .Add(parameter => parameter.Items, rows)
            .Add(parameter => parameter.Columns, CreateColumns())
            .Add(parameter => parameter.GetRowKey, row => row.Key)
            .Add(parameter => parameter.RowActivated, row => activatedKey = row.Key));

        rendered.FindAll("[role='cell']")[1].Click();

        Assert.Equal("ore", activatedKey);
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData(" ")]
    public void GridTable_RowActivationWorksFromKeyboard(string key)
    {
        var rows = new[]
        {
            new TestRow("ore", "Ore", 4, 100)
        };
        string? activatedKey = null;

        var rendered = Render<WebGridTable<TestRow, TestColumn>>(parameters => parameters
            .Add(parameter => parameter.Items, rows)
            .Add(parameter => parameter.Columns, CreateColumns())
            .Add(parameter => parameter.GetRowKey, row => row.Key)
            .Add(parameter => parameter.RowActivated, row => activatedKey = row.Key));

        var row = rendered.Find(".web-grid-table-row[aria-label='Select row']");

        Assert.Equal("0", row.GetAttribute("tabindex"));

        row.KeyDown(key);

        Assert.Equal("ore", activatedKey);
    }

    [Fact]
    public void GridTable_InteractiveCellsCanSuppressRowActivation()
    {
        var rows = new[]
        {
            new TestRow("ore", "Ore", 4, 100)
        };
        var columns = new[]
        {
            WebTableColumn<TestRow, TestColumn>.Text(TestColumn.Name, "Name", row => row.Name, widthPx: 160),
            new WebTableColumn<TestRow, TestColumn>
            {
                Id = TestColumn.Quantity,
                Header = "Qty",
                Size = new WebTableColumnSize(80),
                SuppressRowActivation = true,
                CellTemplate = row => builder => builder.AddContent(0, row.Quantity.ToString("N0"))
            }
        };
        string? activatedKey = null;

        var rendered = Render<WebGridTable<TestRow, TestColumn>>(parameters => parameters
            .Add(parameter => parameter.Items, rows)
            .Add(parameter => parameter.Columns, columns)
            .Add(parameter => parameter.GetRowKey, row => row.Key)
            .Add(parameter => parameter.RowActivated, row => activatedKey = row.Key));

        rendered.FindAll("[role='cell']")[1].Click();

        Assert.Null(activatedKey);
    }

    [Fact]
    public void GridTable_RendersEmptyContentWhenNoRowsExist()
    {
        RenderFragment emptyContent = builder =>
        {
            builder.OpenElement(0, "span");
            builder.AddContent(1, "Nothing here yet");
            builder.CloseElement();
        };

        var rendered = Render<WebGridTable<TestRow, TestColumn>>(parameters => parameters
            .Add(parameter => parameter.Items, Array.Empty<TestRow>())
            .Add(parameter => parameter.Columns, CreateColumns())
            .Add(parameter => parameter.EmptyContent, emptyContent));

        Assert.Empty(rendered.FindAll(".web-grid-table-row"));
        Assert.Contains("Nothing here yet", rendered.Find(".web-grid-table-empty").TextContent);
    }

    [Fact]
    public void GridTable_RejectsNonPixelColumnSizes()
    {
        var columns = new[]
        {
            new WebTableColumn<TestRow, TestColumn>
            {
                Id = TestColumn.Name,
                Header = "Name",
                Size = WebTableColumnSize.Percent(50m),
                CellTemplate = row => builder => builder.AddContent(0, row.Name)
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Render<WebGridTable<TestRow, TestColumn>>(parameters => parameters
                .Add(parameter => parameter.Items, new[] { new TestRow("ore", "Ore", 4, 100) })
                .Add(parameter => parameter.Columns, columns)));

        Assert.Contains("WebGridTable requires pixel column sizes", exception.Message);
    }

    private static IReadOnlyList<WebTableColumn<TestRow, TestColumn>> CreateColumns()
    {
        return
        [
            WebTableColumn<TestRow, TestColumn>.Text(TestColumn.Name, "Name", row => row.Name, widthPx: 160),
            WebTableColumn<TestRow, TestColumn>.Text(TestColumn.Quantity, "Qty", row => row.Quantity.ToString("N0"), widthPx: 80, alignEnd: true),
            WebTableColumn<TestRow, TestColumn>.Text(TestColumn.Cost, "Cost", row => row.Cost.ToString("N0"), widthPx: 120, alignEnd: true)
        ];
    }

    private enum TestColumn
    {
        Name,
        Quantity,
        Cost
    }

    private sealed record TestRow(string Key, string Name, int Quantity, long Cost);
}
