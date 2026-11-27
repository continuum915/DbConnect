using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbConnect.ViewModels;
using DbConnect.Views;
using static DbConnect.Tests.TableDataTestFixtures;

namespace DbConnect.Tests.Styles;

/// <summary>Ticket 05: the table-data toolbar fits a narrow window and the table designer's headers are readable.</summary>
public sealed class ObjectTabLayoutTests
{
    [AvaloniaFact]
    public async Task TableDataToolbar_FitsAt1100PxAndTheRowCountIsShownOnce()
    {
        var viewModel = new TableDataTabViewModel("Customers", CreateSession(new TablePagingProvider(CreatePage([1, 2, 3]))),
            CreateTableNode(hasPrimaryKey: true));
        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var view = new TableDataTabView { DataContext = viewModel };
        using var host = ThemedHost.Show(view, "Light", height: 700, width: 1100);
        var window = host.Window;

        var toolbar = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("toolbar"));
        var clipped = ((Panel)toolbar.Child!).Children
            .Where(child => child.IsVisible && RightEdge(child, window) > RightEdge(toolbar, window) + 0.5)
            .Select(child => child.GetType().Name + " " + (child as TextBlock)?.Text)
            .ToList();
        Assert.True(clipped.Count == 0, "Clipped toolbar items: " + string.Join(", ", clipped));

        var rowCounts = view.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && t.Text?.Contains("row(s)", StringComparison.Ordinal) == true)
            .ToList();
        Assert.Single(rowCounts);
    }

    [AvaloniaFact]
    public void TableDesignerColumnHeaders_AreNotTrimmed()
    {
        var view = new TableDesignerView { DataContext = new TableDesignerViewModel() };
        using var host = ThemedHost.Show(view, "Light", height: 700, width: 1100);

        AssertHeadersFit(view.FindControl<DataGrid>("ColumnGrid")!, ["Name", "Type", "Length", "Precision", "Scale", "Collation", "Not Null", "PK",
            "Default", "Description", "Identity", "Seed", "Increment", "Generated", "Storage", "Generator", "Properties"]);
    }

    [AvaloniaFact]
    public void TableDesignerRenameNotice_IsAnInfoBannerBelowTheToolbar()
    {
        var viewModel = new TableDesignerViewModel();
        var view = new TableDesignerView { DataContext = viewModel };
        using var host = ThemedHost.Show(view, "Light", height: 700, width: 1100);

        var notice = view.FindControl<Border>("RenameNotice")!;
        Assert.Equal(!viewModel.SupportsTableDesignerRenameColumn, notice.IsVisible);
        Assert.Contains("banner", notice.Classes);
        Assert.Contains("info", notice.Classes);
        Assert.DoesNotContain(notice.GetVisualAncestors().OfType<Border>(), b => b.Classes.Contains("toolbar"));
    }

    internal static void AssertHeadersFit(DataGrid grid, IReadOnlyCollection<string> expected)
    {
        var headers = DataGridHeaders.Texts(grid).ToList();

        Assert.Equal(expected.Order(), headers.Select(h => h.Text!).Order());
        var trimmed = headers.Where(DataGridHeaders.IsTrimmed).Select(h => h.Text).ToList();
        Assert.True(trimmed.Count == 0, "Trimmed headers: " + string.Join(", ", trimmed));
    }

    private static double RightEdge(Visual visual, Visual root) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width, 0), root)!.Value.X;
}
