using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DbConnect.Tests.Styles;

/// <summary>Ticket 03: text stays readable on the controls' resolved backgrounds in Light and Dark.</summary>
public sealed class ThemeContrastTests
{
    public static TheoryData<string> Variants => ThemedHost.Variants;

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void ThemeBrushes_UseTheColoursOfTheWindowsVariant(string variant)
    {
        var button = new Button { Content = "Run" };
        using var host = ThemedHost.Show(button, variant);

        Assert.Equal((Color)host.Resource("DbConnect.Color.Text"), ThemedHost.ColorOf(button.Foreground));
        Assert.Equal((Color)host.Resource("DbConnect.Color.Surface"), host.Brush("DbConnect.Brush.Surface"));
        Assert.Equal((Color)host.Resource("DbConnect.Color.Button"), ThemedHost.ColorOf(button.Background));
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void ButtonText_IsReadableOnTheButtonFace(string variant)
    {
        var button = new Button { Content = "Run" };
        using var host = ThemedHost.Show(button, variant);

        Contrast.AssertReadable(button.Foreground, button.Background, "button");
        foreach (var face in new[] { "DbConnect.Brush.HoverBg", "DbConnect.Brush.PressedBg", "DbConnect.Brush.Chrome" })
            Contrast.AssertReadable(button.Foreground, (IBrush)host.Resource(face), face);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void GridCellText_IsReadableOnTheRowBackground(string variant)
    {
        var grid = new DataGrid
        {
            ItemsSource = new[] { new { Name = "alice" } },
            Columns = { new DataGridTextColumn { Header = "Name", Binding = new Binding("Name") } },
        };
        using var host = ThemedHost.Show(grid, variant);

        var row = grid.GetVisualDescendants().OfType<DataGridRow>().First();
        var text = row.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == "alice");
        Contrast.AssertReadable(text.Foreground, row.Background, "grid cell");
        Assert.Equal((Color)host.Resource("DbConnect.Color.GridLine"), ThemedHost.ColorOf(grid.HorizontalGridLinesBrush));
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TabText_IsReadableOnTheStripWhetherSelectedOrNot(string variant)
    {
        var selected = new TabItem { Header = "Results", Content = new TextBlock() };
        var unselected = new TabItem { Header = "Messages", Content = new TextBlock() };
        var tabs = new TabControl { Items = { selected, unselected }, SelectedIndex = 0 };
        using var host = ThemedHost.Show(tabs, variant);

        Contrast.AssertReadable(selected.Foreground, OpaqueBackground(selected, tabs), "selected tab");
        Contrast.AssertReadable(unselected.Foreground, OpaqueBackground(unselected, tabs), "unselected tab");
    }

    /// <summary>A tab's own background, or the strip's (the template's PART_Strip) where the tab is transparent.</summary>
    private static IBrush? OpaqueBackground(TabItem tab, TabControl tabs) =>
        tab.Background is ISolidColorBrush { Color.A: 0 }
            ? tabs.GetVisualDescendants().OfType<Border>().First(border => border.Name == "PART_Strip").Background
            : tab.Background;
}
