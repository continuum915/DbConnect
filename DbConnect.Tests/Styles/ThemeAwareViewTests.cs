using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit;
using DbConnect.Models;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests.Styles;

/// <summary>Ticket 03: views that build brushes in code-behind resolve them for their own theme variant.</summary>
public sealed class ThemeAwareViewTests
{
    public static TheoryData<string> Variants => ThemedHost.Variants;

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void SupportBundleDialog_OpensWithTheThemesBorderAndFollowsASwitch(string variant)
    {
        var dialog = new SupportBundleDialog { RequestedThemeVariant = ThemedHost.VariantOf(variant) };
        dialog.Show();
        try
        {
            var framed = dialog.GetVisualDescendants().OfType<Border>().Single(border => border.Child is ScrollViewer { Content: TextBox });
            Assert.Equal(Border(dialog), ThemedHost.ColorOf(framed.BorderBrush));

            dialog.RequestedThemeVariant = ThemedHost.VariantOf(variant == "Dark" ? "Light" : "Dark");
            Assert.Equal(Border(dialog), ThemedHost.ColorOf(framed.BorderBrush));
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void SwitchingTheme_RecoloursAnOpenQueryTabsEditorAndResults()
    {
        var vm = new QueryTabViewModel();
        vm.SetExecutionResults([new QueryResult
        {
            ColumnInfos = [new ColumnInfo { Name = "name", Ordinal = 0 }],
            ObjectRows = [new object?[] { "alice" }],
        }]);
        var view = new QueryTabView { DataContext = vm };
        var editor = view.FindControl<TextEditor>("QueryEditor")!;
        using var host = ThemedHost.Show(view, "Light", height: 700);
        // Typed once shown: headless, text a tab opens with is not always tokenized after earlier tabs have closed.
        editor.Document.Insert(0, "SELECT 1");
        var lightKeyword = EditorKeyword.Colour(editor);

        host.Window.RequestedThemeVariant = ThemeVariant.Dark;
        host.Window.UpdateLayout();

        Assert.NotEqual(lightKeyword, EditorKeyword.Colour(editor, notEqualTo: lightKeyword));
        var grid = view.FindControl<DataGrid>("ResultsGrid")!;
        var row = grid.GetVisualDescendants().OfType<DataGridRow>().First();
        var cell = row.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "alice");
        Assert.Equal((Color)host.Resource("DbConnect.Color.Surface"), ThemedHost.ColorOf(row.Background));
        Contrast.AssertReadable(cell.Foreground, row.Background, "result cell after switching to Dark");
    }

    private static Color Border(Window window) =>
        window.TryFindResource("DbConnect.Color.Border", window.ActualThemeVariant, out var colour) ? (Color)colour! : default;
}
