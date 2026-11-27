using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.ViewModels;
using SkiaSharp;

namespace DbConnect.Views;

public partial class ErDiagramView : UserControl
{
    private static readonly IEnumerable<double> VirtualDashPattern = [6, 4];

    private Point _lastCanvasContextPoint = new(100, 100);
    private Point _dragOffset;
    private Control? _draggedControl;
    private ErTable? _draggedTable;
    private DiagramNote? _draggedNote;
    private ErDiagramViewModel? _subscribedViewModel;

    public ErDiagramView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ActualThemeVariantChanged += (_, _) => QueueVisualRefresh();
    }

    private ErDiagramViewModel? ViewModel => DataContext as ErDiagramViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnsubscribeViewModel(_subscribedViewModel);
        _subscribedViewModel = ViewModel;
        SubscribeViewModel(_subscribedViewModel);
        QueueVisualRefresh();
    }

    private void SubscribeViewModel(ErDiagramViewModel? viewModel)
    {
        if (viewModel == null)
            return;

        viewModel.LiveRelationshipVisuals.CollectionChanged += RelationshipVisuals_CollectionChanged;
        viewModel.VirtualRelationshipVisuals.CollectionChanged += RelationshipVisuals_CollectionChanged;
        viewModel.VisibleTables.CollectionChanged += VisibleTables_CollectionChanged;
    }

    private void UnsubscribeViewModel(ErDiagramViewModel? viewModel)
    {
        if (viewModel == null)
            return;

        viewModel.LiveRelationshipVisuals.CollectionChanged -= RelationshipVisuals_CollectionChanged;
        viewModel.VirtualRelationshipVisuals.CollectionChanged -= RelationshipVisuals_CollectionChanged;
        viewModel.VisibleTables.CollectionChanged -= VisibleTables_CollectionChanged;
    }

    private void RelationshipVisuals_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RenderRelationshipLayers();

    private void VisibleTables_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        QueueVisualRefresh();

    private void QueueVisualRefresh() => Dispatcher.UIThread.Post(() =>
    {
        ApplyTableHeaderColors();
        RenderRelationshipLayers();
    }, DispatcherPriority.Background);

    private void DiagramCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Canvas canvas && e.GetCurrentPoint(canvas).Properties.IsRightButtonPressed)
            _lastCanvasContextPoint = e.GetPosition(canvas);
    }

    private void DiagramCanvas_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Canvas canvas || ViewModel?.IsDesignMode != true || !ReferenceEquals(e.Source, canvas))
            return;

        var position = e.GetPosition(canvas);
        ViewModel.AddVirtualTableAt(position.X, position.Y, true);
        e.Handled = true;
    }

    private async void TableNode_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not ErTable table || ViewModel?.IsDesignMode != true)
            return;

        await ViewModel.OpenColumnEditorAsync(table);
        e.Handled = true;
    }

    private void ColumnLink_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not ErColumn column || ViewModel?.IsDesignMode != true || ViewModel.IsRelationshipMode != true)
            return;

        var table = GetOwningTable(control);
        if (table == null)
            return;

        ViewModel.BeginRelationship(table.TableName, column.Name);
        e.Handled = true;
    }

    private void ColumnLink_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not ErColumn column || ViewModel?.IsDesignMode != true || ViewModel.IsRelationshipMode != true)
            return;

        var table = GetOwningTable(control);
        if (table == null)
            return;

        ViewModel.CompleteRelationship(table.TableName, column.Name);
        e.Handled = true;
    }

    private void HideTable_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is ErTable table)
            ViewModel?.HideTable(table.TableName);
    }

    private void ShowHiddenTable_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tableName)
            ViewModel?.ShowTable(tableName);
    }

    private void AddNote_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.AddNoteAt(Math.Max(0, _lastCanvasContextPoint.X), Math.Max(0, _lastCanvasContextPoint.Y));
    }

    private void SetTableColorGroup_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.DataContext is not ErTable table || menuItem.Tag is not string colorGroup)
            return;

        ViewModel?.SetTableColorGroup((table.TableName, string.Equals(colorGroup, "None", StringComparison.OrdinalIgnoreCase) ? null : colorGroup));
    }

    private void TableNode_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Border border && border.DataContext is ErTable table)
            ApplyTableHeaderColor(border, table);
    }

    private void TableHeader_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle || handle.DataContext is not ErTable table || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;

        _draggedTable = table;
        _draggedNote = null;
        _draggedControl = CanvasItemOf(handle);
        _dragOffset = _draggedControl == null ? default : e.GetPosition(_draggedControl);
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void NoteHeader_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle || handle.DataContext is not DiagramNote note || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;

        _draggedNote = note;
        _draggedTable = null;
        _draggedControl = CanvasItemOf(handle);
        _dragOffset = _draggedControl == null ? default : e.GetPosition(_draggedControl);
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    // The Canvas positions the item container that wraps a table or note template, so drags move the container.
    private static Control? CanvasItemOf(Control handle) =>
        handle.FindAncestorOfType<Border>() is { } root ? root.GetVisualParent() as Control ?? root : null;

    private void DragHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control handle || _draggedControl == null || this.FindControl<Canvas>("DiagramCanvas") is not Canvas canvas)
            return;

        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;

        var pointerPosition = e.GetPosition(canvas);
        var x = Math.Max(0, pointerPosition.X - _dragOffset.X);
        var y = Math.Max(0, pointerPosition.Y - _dragOffset.Y);

        Canvas.SetLeft(_draggedControl, x);
        Canvas.SetTop(_draggedControl, y);

        if (_draggedTable != null)
            ViewModel?.UpdateTablePosition(_draggedTable, x, y);
        else if (_draggedNote != null)
            ViewModel?.UpdateNotePosition(_draggedNote, x, y);

        e.Handled = true;
    }

    private void DragHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control)
            e.Pointer.Capture(null);

        _draggedControl = null;
        _draggedTable = null;
        _draggedNote = null;
    }

    private async void SaveModel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not TopLevel topLevel || ViewModel == null)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save DbConnect Model",
            SuggestedFileName = string.IsNullOrWhiteSpace(ViewModel.VirtualSchema.Name) ? "model.dbcmodel" : $"{ViewModel.VirtualSchema.Name}.dbcmodel",
            DefaultExtension = "dbcmodel",
            FileTypeChoices =
            [
                new FilePickerFileType("DbConnect Model") { Patterns = ["*.dbcmodel"], MimeTypes = ["application/json"] }
            ]
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        await ViewModel.SaveModelAsync(path);
    }

    private async void OpenModel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not TopLevel topLevel || ViewModel == null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open DbConnect Model",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("DbConnect Model") { Patterns = ["*.dbcmodel"], MimeTypes = ["application/json"] }
            ]
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        await ViewModel.OpenModelAsync(path);
    }

    private async void ExportDiagram_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not TopLevel topLevel || this.FindControl<Canvas>("DiagramCanvas") is not Canvas canvas)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Diagram",
            SuggestedFileName = "diagram",
            DefaultExtension = "png",
            FileTypeChoices =
            [
                new FilePickerFileType("PNG Image") { Patterns = ["*.png"], MimeTypes = ["image/png"] },
                new FilePickerFileType("PDF Document") { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] }
            ]
        });

        if (file == null)
            return;

        using var bitmap = RenderDiagramBitmap(canvas);
        var path = file.TryGetLocalPath();
        var extension = System.IO.Path.GetExtension(path ?? file.Name).ToLowerInvariant();

        if (extension == ".pdf")
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                ViewModel!.StatusMessage = "PDF export requires a local file path.";
                return;
            }

            SaveBitmapAsPdf(bitmap, path);
            ViewModel!.StatusMessage = $"Exported diagram to {path}.";
            return;
        }

        await using (var stream = await file.OpenWriteAsync())
            bitmap.Save(stream);

        ViewModel!.StatusMessage = $"Exported diagram to {path ?? file.Name}.";
    }

    private RenderTargetBitmap RenderDiagramBitmap(Canvas canvas)
    {
        var width = Math.Max(1, (int)Math.Ceiling(canvas.Bounds.Width > 0 ? canvas.Bounds.Width : canvas.Width));
        var height = Math.Max(1, (int)Math.Ceiling(canvas.Bounds.Height > 0 ? canvas.Bounds.Height : canvas.Height));
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        bitmap.Render(canvas);
        return bitmap;
    }

    private static void SaveBitmapAsPdf(RenderTargetBitmap bitmap, string path)
    {
        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream);
        var imageBytes = pngStream.ToArray();

        using var data = SKData.CreateCopy(imageBytes);
        using var image = SKImage.FromEncodedData(data);
        if (image == null)
            throw new InvalidOperationException("Unable to encode ER diagram for PDF export.");

        using var document = SKDocument.CreatePdf(path);
        using var pdfCanvas = document.BeginPage(image.Width, image.Height);
        pdfCanvas.Clear(SKColors.White);
        pdfCanvas.DrawImage(image, SKRect.Create(image.Width, image.Height));
        document.EndPage();
        document.Close();
    }

    // Stroke and fill come from the relationship canvas styles in ErDiagramView.axaml, so they follow the theme.
    private void RenderRelationshipLayers()
    {
        RenderRelationships(this.FindControl<Canvas>("LiveRelationshipCanvas"), ViewModel?.LiveRelationshipVisuals, null);
        RenderRelationships(this.FindControl<Canvas>("VirtualRelationshipCanvas"), ViewModel?.VirtualRelationshipVisuals, VirtualDashPattern);
    }

    private static void RenderRelationships(Canvas? canvas, IEnumerable<ErRelationshipVisual>? relationships, IEnumerable<double>? dashPattern)
    {
        if (canvas == null)
            return;

        canvas.Children.Clear();
        if (relationships == null)
            return;

        foreach (var relationship in relationships)
            DrawRelationship(canvas, relationship, dashPattern);
    }

    private static void DrawRelationship(Canvas canvas, ErRelationshipVisual relationship, IEnumerable<double>? dashPattern)
    {
        var start = relationship.StartPoint;
        var end = relationship.EndPoint;
        var vector = end - start;
        var length = Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y));
        if (length < 1)
            return;

        var direction = new Point(vector.X / length, vector.Y / length);
        var normal = new Point(-direction.Y, direction.X);
        var startDepth = GetCardinalityDepth(relationship.StartCardinality);
        var endDepth = GetCardinalityDepth(relationship.EndCardinality);
        var lineStart = Offset(start, direction, startDepth);
        var lineEnd = Offset(end, new Point(-direction.X, -direction.Y), endDepth);

        canvas.Children.Add(CreateLine(lineStart, lineEnd, dashPattern));
        DrawEndpointMarker(canvas, start, direction, normal, relationship.StartCardinality);
        DrawEndpointMarker(canvas, end, new Point(-direction.X, -direction.Y), new Point(-normal.X, -normal.Y), relationship.EndCardinality);
    }

    private static void DrawEndpointMarker(Canvas canvas, Point endpoint, Point direction, Point normal, ErRelationshipCardinality cardinality)
    {
        Point Along(double distance) => Offset(endpoint, direction, distance);

        switch (cardinality)
        {
            case ErRelationshipCardinality.ZeroOrOne:
                canvas.Children.Add(CreateCircle(Along(5), 4));
                canvas.Children.Add(CreateTick(Along(16), normal));
                break;
            case ErRelationshipCardinality.ZeroOrMany:
                canvas.Children.Add(CreateCircle(Along(5), 4));
                AddCrowFoot(canvas, Along(12), Along(26), normal);
                break;
            case ErRelationshipCardinality.OneAndOnlyOne:
                canvas.Children.Add(CreateTick(Along(5), normal));
                canvas.Children.Add(CreateTick(Along(12), normal));
                break;
            case ErRelationshipCardinality.OneOrMany:
                canvas.Children.Add(CreateTick(Along(6), normal));
                AddCrowFoot(canvas, Along(12), Along(26), normal);
                break;
            case ErRelationshipCardinality.Many:
                AddCrowFoot(canvas, endpoint, Along(14), normal);
                break;
            default:
                canvas.Children.Add(CreateTick(Along(6), normal));
                break;
        }
    }

    private static double GetCardinalityDepth(ErRelationshipCardinality cardinality) => cardinality switch
    {
        ErRelationshipCardinality.Many => 14,
        ErRelationshipCardinality.ZeroOrOne => 20,
        ErRelationshipCardinality.ZeroOrMany => 28,
        ErRelationshipCardinality.OneAndOnlyOne => 16,
        ErRelationshipCardinality.OneOrMany => 28,
        _ => 12
    };

    private static void AddCrowFoot(Canvas canvas, Point apex, Point fanCenter, Point normal)
    {
        canvas.Children.Add(CreateLine(apex, fanCenter, null));
        canvas.Children.Add(CreateLine(apex, Offset(fanCenter, normal, 7), null));
        canvas.Children.Add(CreateLine(apex, Offset(fanCenter, normal, -7), null));
    }

    private static Line CreateLine(Point start, Point end, IEnumerable<double>? dashPattern)
    {
        var line = new Line
        {
            StartPoint = start,
            EndPoint = end,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round
        };

        if (dashPattern != null)
            line.StrokeDashArray = new AvaloniaList<double>(dashPattern);

        return line;
    }

    private static Line CreateTick(Point center, Point normal)
    {
        const double tickHalfLength = 6;
        return CreateLine(Offset(center, normal, -tickHalfLength), Offset(center, normal, tickHalfLength), null);
    }

    private static Ellipse CreateCircle(Point center, double radius)
    {
        var ellipse = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            StrokeThickness = 2
        };

        Canvas.SetLeft(ellipse, center.X - radius);
        Canvas.SetTop(ellipse, center.Y - radius);
        return ellipse;
    }

    private void ApplyTableHeaderColors()
    {
        if (this.FindControl<Canvas>("DiagramCanvas") is not Canvas canvas)
            return;

        foreach (var border in canvas.GetVisualDescendants().OfType<Border>().Where(border => border.DataContext is ErTable))
        {
            if (border.DataContext is ErTable table)
                ApplyTableHeaderColor(border, table);
        }
    }

    private void ApplyTableHeaderColor(Border border, ErTable table)
    {
        if (border.Child is not StackPanel stackPanel || stackPanel.Children.FirstOrDefault() is not Border headerBorder)
            return;

        var groupedBrush = ViewModel?.ResolveColorGroupHex(table.ColorGroup ?? ViewModel.ResolveTableColorGroup(table.TableName));
        if (!string.IsNullOrWhiteSpace(groupedBrush))
        {
            headerBorder.Background = Brush.Parse(groupedBrush);
            return;
        }

        if (!string.IsNullOrWhiteSpace(table.ColorHex) && !string.Equals(table.ColorHex, "#FFFFFF", StringComparison.OrdinalIgnoreCase))
        {
            headerBorder.Background = Brush.Parse(table.ColorHex);
            return;
        }

        if (this.TryFindResource("DbConnectToolbarBrush", ActualThemeVariant, out var toolbarBrush)
            && toolbarBrush is IBrush brush)
        {
            headerBorder.Background = brush;
        }
    }

    private static Point Offset(Point origin, Point direction, double distance) =>
        new(origin.X + (direction.X * distance), origin.Y + (direction.Y * distance));

    private static ErTable? GetOwningTable(Control control) =>
        control.GetSelfAndVisualAncestors().OfType<Control>().Select(item => item.DataContext).OfType<ErTable>().FirstOrDefault();
}
