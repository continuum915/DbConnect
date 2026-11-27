using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests.Styles;

/// <summary>T5: ER diagram table boxes and notes are drawn where their model says, not stacked at the canvas origin.</summary>
public sealed class ErDiagramCanvasTests
{
    [AvaloniaFact]
    public void TableBoxes_AreDrawnAtTheirModelPosition()
    {
        var viewModel = new ErDiagramViewModel();
        viewModel.VisibleTables.Add(new ErTable { TableName = "customers", X = 300, Y = 200, Columns = [new ErColumn { Name = "id", DataType = "INTEGER" }] });
        viewModel.VisibleTables.Add(new ErTable { TableName = "orders", X = 700, Y = 450, Columns = [new ErColumn { Name = "id", DataType = "INTEGER" }] });
        var view = new ErDiagramView { DataContext = viewModel };
        using var host = ThemedHost.Show(view, "Light", height: 900, width: 1400);

        var canvas = view.FindControl<Canvas>("DiagramCanvas")!;
        AssertDrawnAt(canvas, "customers", new Point(300, 200));
        AssertDrawnAt(canvas, "orders", new Point(700, 450));
    }

    [AvaloniaFact]
    public void Notes_AreDrawnAtTheirModelPosition()
    {
        var viewModel = new ErDiagramViewModel();
        viewModel.Notes.Add(new DiagramNote { Text = "remember", X = 400, Y = 250 });
        var view = new ErDiagramView { DataContext = viewModel };
        using var host = ThemedHost.Show(view, "Light", height: 900, width: 1400);

        var canvas = view.FindControl<Canvas>("DiagramCanvas")!;
        var note = canvas.GetVisualDescendants().OfType<Border>().First(b => b.DataContext is DiagramNote);
        Assert.Equal(new Point(400, 250), note.TranslatePoint(default, canvas));
    }

    [AvaloniaFact]
    public void DraggingATableHeader_MovesTheBox()
    {
        var viewModel = new ErDiagramViewModel();
        var table = new ErTable { TableName = "customers", X = 100, Y = 100 };
        viewModel.VisibleTables.Add(table);
        var view = new ErDiagramView { DataContext = viewModel };
        using var host = ThemedHost.Show(view, "Light", height: 900, width: 1400);

        var canvas = view.FindControl<Canvas>("DiagramCanvas")!;
        var header = canvas.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TableHeaderBorder");
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var start = header.TranslatePoint(new Point(10, 5), host.Window)!.Value;
        host.Window.MouseDown(start, MouseButton.Left);
        host.Window.MouseMove(start + new Point(150, 80), RawInputModifiers.LeftMouseButton);
        host.Window.MouseUp(start + new Point(150, 80), MouseButton.Left);
        host.Window.UpdateLayout();

        AssertDrawnAt(canvas, "customers", new Point(250, 180));
    }

    [AvaloniaFact]
    public void EmptyDiagram_OffersToLoadIt_UntilATableShows()
    {
        var viewModel = new ErDiagramViewModel();
        var view = new ErDiagramView { DataContext = viewModel };
        using var host = ThemedHost.Show(view, "Light", height: 900, width: 1400);

        var empty = view.GetVisualDescendants().OfType<EmptyState>().Single(e => e.Title == "No tables to show");
        Assert.True(empty.IsVisible);
        Assert.Same(viewModel.LoadDiagramCommand, empty.ActionCommand);

        viewModel.VisibleTables.Add(new ErTable { TableName = "customers", X = 100, Y = 100 });
        Assert.False(empty.IsVisible);
    }

    private static void AssertDrawnAt(Canvas canvas, string tableName, Point expected)
    {
        var box = canvas.GetVisualDescendants().OfType<Border>()
            .First(b => b.DataContext is ErTable t && t.TableName == tableName && b.ContextMenu != null);
        Assert.True(box.Bounds.Width > 0 && box.Bounds.Height > 0, $"{tableName} has no size");
        Assert.Equal(expected, box.TranslatePoint(default, canvas));
    }
}
