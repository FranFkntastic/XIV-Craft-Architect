using FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

namespace FFXIV_Craft_Architect.Tests;

public class WebTablePrimitiveTests
{
    [Fact]
    public void SortState_Toggle_UsesAscendingThenDescendingCycle()
    {
        var first = WebTableSortState<TestColumn>.Unsorted.Toggle(TestColumn.Name);
        var second = first.Toggle(TestColumn.Name);
        var third = second.Toggle(TestColumn.Name);
        var switched = third.Toggle(TestColumn.Quantity);

        Assert.Equal(TestColumn.Name, first.Column);
        Assert.False(first.Descending);
        Assert.Equal(TestColumn.Name, second.Column);
        Assert.True(second.Descending);
        Assert.Equal(TestColumn.Name, third.Column);
        Assert.False(third.Descending);
        Assert.Equal(TestColumn.Quantity, switched.Column);
        Assert.False(switched.Descending);
    }

    [Fact]
    public void Ordering_AppliesTypedSortRuleWithStableTieBreaker()
    {
        var rows = new[]
        {
            new TestRow("b", "Bronze", 10, 200),
            new TestRow("a", "Adamantite", 10, 300),
            new TestRow("c", "Copper", 5, 100)
        };
        var rules = new[]
        {
            WebTableSortRule<TestRow, TestColumn>.Create(TestColumn.Quantity, row => row.Quantity)
        };

        var ordered = WebTableOrdering.Apply(
            rows,
            new WebTableSortState<TestColumn>(TestColumn.Quantity, Descending: true),
            rules,
            items => items.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            orderedItems => orderedItems.ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["Adamantite", "Bronze", "Copper"], ordered.Select(row => row.Name));
    }

    [Fact]
    public void Ordering_UsesCustomSortRuleWhenColumnNeedsDomainLogic()
    {
        var rows = new[]
        {
            new TestRow("b", "HQ Copper", 1, 10),
            new TestRow("a", "NQ Adamantite", 1, 20),
            new TestRow("c", "NQ Bronze", 1, 30)
        };
        var rules = new[]
        {
            WebTableSortRule<TestRow, TestColumn>.CreateCustom(
                TestColumn.Name,
                (items, descending) => descending
                    ? items.OrderByDescending(row => row.Name.StartsWith("HQ ", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                    : items.OrderBy(row => row.Name.StartsWith("HQ ", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase))
        };

        var ordered = WebTableOrdering.Apply(
            rows,
            new WebTableSortState<TestColumn>(TestColumn.Name, Descending: false),
            rules,
            items => items.OrderBy(row => row.Key));

        Assert.Equal(["NQ Adamantite", "NQ Bronze", "HQ Copper"], ordered.Select(row => row.Name));
    }

    [Fact]
    public void ColumnSize_ClampsInitialWidthToMinimum()
    {
        var size = new WebTableColumnSize(widthPx: 80, minWidthPx: 120);

        Assert.Equal(120, size.WidthPx);
        Assert.Equal(120, size.MinWidthPx);
        Assert.Equal("120px", size.ToCssWidth());
    }

    [Fact]
    public void Selection_ReportsSelectedKeyOnlyWhenActive()
    {
        var selection = WebTableSelection<string>.Single("node-2");

        Assert.True(selection.IsSelected("node-2"));
        Assert.False(selection.IsSelected("node-1"));
        Assert.False(WebTableSelection<string>.None.IsSelected("node-2"));
    }

    private enum TestColumn
    {
        Name,
        Quantity
    }

    private sealed record TestRow(string Key, string Name, int Quantity, long Cost);
}
