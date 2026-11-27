using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DbConnect.Tests.Styles;

/// <summary>Ticket 02: global styles for splitters, tab strips, row density and grid headers, in Light and Dark.</summary>
public sealed class GlobalStyleTests
{
    public static TheoryData<string> Variants => ThemedHost.Variants;

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void GridSplitter_IsAFivePixelBarWithSeparatorEdges(string variant)
    {
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, [Grid.ColumnProperty] = 1 };
        var grid = new Grid { ColumnDefinitions = new("*,5,*"), Children = { new Border(), splitter, new Border() } };
        using var host = ThemedHost.Show(grid, variant);

        var bar = splitter.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Bar");
        Assert.Equal(5, splitter.Bounds.Width);
        Assert.Equal(5, bar.Bounds.Width);
        Assert.Equal(host.Brush("DbConnect.Brush.Splitter"), ThemedHost.ColorOf(bar.Background));
        Assert.Equal(host.Brush("DbConnect.Brush.Separator"), ThemedHost.ColorOf(bar.BorderBrush));
        Assert.Equal(new Thickness(1, 0), bar.BorderThickness);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TabItem_UnselectedTabsHaveNoShadedBlock(string variant)
    {
        var selected = new TabItem { Header = "Results" };
        var unselected = new TabItem { Header = "Messages" };
        var tabs = new TabControl { Items = { selected, unselected }, SelectedIndex = 0 };
        using var host = ThemedHost.Show(tabs, variant);

        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(unselected.Background).Color);
        Assert.NotEqual(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(selected.Background).Color);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TreeListAndGridRows_AreTwentyTwoPixelsTall(string variant)
    {
        var tree = new TreeView { ItemsSource = new[] { "orders", "customers" } };
        var list = new ListBox { ItemsSource = new[] { "orders", "customers" } };
        var grid = new DataGrid
        {
            ItemsSource = new[] { new Row("orders"), new Row("customers") },
            Columns = { new DataGridTextColumn { Header = "Name", Binding = new Avalonia.Data.Binding(nameof(Row.Name)) } },
            Height = 120,
        };
        using var host = ThemedHost.Show(new StackPanel { Children = { tree, list, grid } }, variant, height: 500);

        Assert.All(tree.GetVisualDescendants().OfType<TreeViewItem>(), item => Assert.Equal(22, item.Bounds.Height));
        Assert.All(list.GetVisualDescendants().OfType<ListBoxItem>(), item => Assert.Equal(22, item.Bounds.Height));
        // A grid row is its 22 px of cells plus the 1 px horizontal grid line drawn below them.
        var cells = grid.GetVisualDescendants().OfType<DataGridCell>().ToList();
        Assert.NotEmpty(cells);
        Assert.All(cells, cell => Assert.Equal(22, cell.Bounds.Height));
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void DataGridColumnHeader_TrimsLongTextAndShowsItInATooltip(string variant)
    {
        const string header = "Not Null Constraint";
        var grid = new DataGrid
        {
            ItemsSource = new[] { new Row("orders") },
            Columns = { new DataGridTextColumn { Header = header, Width = new DataGridLength(48) } },
        };
        using var host = ThemedHost.Show(grid, variant);

        var cell = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .SelectMany(h => h.GetVisualDescendants().OfType<TextBlock>()).Single(t => t.Text == header);
        Assert.Equal(TextTrimming.CharacterEllipsis, cell.TextTrimming);
        Assert.Equal(header, ToolTip.GetTip(cell));
    }

    public sealed record Row(string Name);
}
