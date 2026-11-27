using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using DbConnect.Models;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class QueryBuilderView : UserControl
{
    private const string JoinVisualTag = "JoinVisual";
    private readonly Dictionary<QueryBuilderTable, Border> _tableCards = new();
    private readonly Dictionary<(QueryBuilderTable Table, string ColumnName), Control> _columnAnchors = new();
    private readonly ScaleTransform _canvasScale = new(1, 1);
    private readonly TranslateTransform _canvasTranslate = new();

    private QueryBuilderViewModel? _viewModel;
    private QueryBuilderTable? _draggingTable;
    private Border? _draggingCard;
    private Point _dragStartPoint;
    private Point _cardStartPoint;
    private bool _isPanning;
    private Point _panStartPoint;
    private Vector _panTranslateStart;
    private (QueryBuilderTable Table, string ColumnName)? _joinStart;
    private Line? _previewJoinLine;

    public QueryBuilderView()
    {
        InitializeComponent();

        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(_canvasScale);
        transformGroup.Children.Add(_canvasTranslate);
        QueryCanvas.RenderTransform = transformGroup;

        DataContextChanged += OnQueryBuilderDataContextChanged;
    }

    private void OnQueryBuilderDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.Tables.CollectionChanged -= Tables_CollectionChanged;
            _viewModel.Joins.CollectionChanged -= Joins_CollectionChanged;
        }

        _viewModel = DataContext as QueryBuilderViewModel;
        if (_viewModel == null)
            return;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.Tables.CollectionChanged += Tables_CollectionChanged;
        _viewModel.Joins.CollectionChanged += Joins_CollectionChanged;
        _viewModel.OpenInEditorCallback = sql =>
        {
            var mainWindow = TopLevel.GetTopLevel(this) as MainWindow;
            if (mainWindow?.DataContext is MainWindowViewModel mainWindowViewModel)
                mainWindowViewModel.NewQueryTabWithContent(sql, _viewModel.Session);
        };

        UpdateViewModeVisibility();
        RebuildCanvas();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryBuilderViewModel.ShowCanvas))
            UpdateViewModeVisibility();
    }

    private void Tables_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildCanvas();

    private void Joins_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RedrawJoinLines();

    private async void AddTable_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var dialog = new AddTableDialog();
        var dialogVm = new AddTableDialogViewModel(_viewModel.Session);
        dialog.DataContext = dialogVm;

        var result = await dialog.ShowDialog<bool?>(owner);

        if (result == true && dialogVm.SelectedTable != null)
        {
            await _viewModel.AddTableToCanvasAsync(dialogVm.SelectedTable, dialogVm.Alias);
            RebuildCanvas();
        }
    }

    private void ShowCanvas_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
            _viewModel.ShowCanvas = true;
    }

    private void ShowForm_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
            _viewModel.ShowCanvas = false;
    }

    private void UpdateViewModeVisibility()
    {
        var showCanvas = _viewModel?.ShowCanvas ?? true;
        CanvasViewHost.IsVisible = showCanvas;
        FormViewHost.IsVisible = !showCanvas;
    }

    private void RebuildCanvas()
    {
        QueryCanvas.Children.Clear();
        _tableCards.Clear();
        _columnAnchors.Clear();
        _previewJoinLine = null;
        _joinStart = null;

        if (_viewModel == null)
            return;

        foreach (var table in _viewModel.TableCards)
        {
            var card = CreateTableCard(table);
            _tableCards[table] = card;
            card.ZIndex = 10;
            QueryCanvas.Children.Add(card);
            Canvas.SetLeft(card, table.X);
            Canvas.SetTop(card, table.Y);
        }

        RedrawJoinLines();
    }

    // The card's look is in QueryBuilderView.axaml (qb-card styles), so it follows the theme.
    private Border CreateTableCard(QueryBuilderTable table)
    {
        var border = new Border { Classes = { "qb-card" } };
        var panel = new StackPanel();

        var title = string.IsNullOrWhiteSpace(table.Alias)
            ? table.TableName
            : $"{table.TableName} ({table.Alias})";

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headerGrid.Children.Add(new TextBlock { Text = title, Classes = { "qb-card-title" } });

        var closeButton = new Button
        {
            Classes = { "toolbar", "icon" },
            Content = new FluentIcons.Avalonia.SymbolIcon { Symbol = FluentIcons.Common.Symbol.Dismiss },
            Tag = table
        };
        ToolTip.SetTip(closeButton, "Remove this table");
        closeButton.Click += RemoveTable_Click;
        Grid.SetColumn(closeButton, 1);
        headerGrid.Children.Add(closeButton);

        var header = new Border { Classes = { "qb-card-header" }, Child = headerGrid };
        header.PointerPressed += (_, e) => StartDraggingCard(border, table, e);
        header.PointerMoved += (_, e) => DragCard(e);
        header.PointerReleased += (_, e) => EndDraggingCard(e);
        panel.Children.Add(header);

        var body = new StackPanel { Classes = { "qb-card-body" } };
        panel.Children.Add(body);

        if (table.Columns.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "No columns loaded yet.", Classes = { "caption", "muted" } });
        }
        else
        {
            var columnCheckBoxes = new List<CheckBox>();
            var suppressSelectAll = false;

            var selectAllCheckBox = new CheckBox
            {
                IsChecked = table.Columns.Count > 0 && table.Columns.All(c => c.IsSelected),
                Content = new TextBlock { Text = "* (all columns)", Classes = { "caption", "muted" } }
            };
            body.Children.Add(selectAllCheckBox);

            foreach (var column in table.Columns)
            {
                // The name sits outside the CheckBox so pressing it starts a join instead of toggling the column.
                var checkBox = new CheckBox { IsChecked = column.IsSelected };
                checkBox.IsCheckedChanged += (_, _) =>
                {
                    column.IsSelected = checkBox.IsChecked == true;
                    suppressSelectAll = true;
                    selectAllCheckBox.IsChecked = table.Columns.All(c => c.IsSelected);
                    suppressSelectAll = false;
                    _viewModel?.GenerateSql();
                };
                columnCheckBoxes.Add(checkBox);

                var name = new TextBlock { Text = column.ColumnName };
                Grid.SetColumn(name, 1);
                var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { checkBox, name } };
                var row = new Border { Classes = { "qb-column" }, Tag = (table, column), Child = rowGrid };
                row.PointerPressed += (_, e) => BeginJoin(table, column, e);
                row.PointerReleased += (_, e) => CompleteJoin(table, column, e);
                body.Children.Add(row);
                _columnAnchors[(table, column.ColumnName)] = row;
            }

            selectAllCheckBox.IsCheckedChanged += (_, _) =>
            {
                if (suppressSelectAll) return;
                var all = selectAllCheckBox.IsChecked == true;
                for (var i = 0; i < table.Columns.Count; i++)
                {
                    table.Columns[i].IsSelected = all;
                    columnCheckBoxes[i].IsChecked = all;
                }
                _viewModel?.GenerateSql();
            };
        }

        border.Child = panel;
        return border;
    }

    private void StartDraggingCard(Border card, QueryBuilderTable table, PointerPressedEventArgs e)
    {
        if (_viewModel?.IsDrawJoinMode == true)
            return;

        _draggingTable = table;
        _draggingCard = card;
        _dragStartPoint = e.GetPosition(QueryCanvas);
        _cardStartPoint = new Point(table.X, table.Y);
        e.Pointer.Capture(card);
        e.Handled = true;
    }

    private void DragCard(PointerEventArgs e)
    {
        if (_draggingTable == null || _draggingCard == null)
            return;

        var position = e.GetPosition(QueryCanvas);
        var delta = position - _dragStartPoint;
        _draggingTable.X = Math.Max(0, _cardStartPoint.X + delta.X);
        _draggingTable.Y = Math.Max(0, _cardStartPoint.Y + delta.Y);
        Canvas.SetLeft(_draggingCard, _draggingTable.X);
        Canvas.SetTop(_draggingCard, _draggingTable.Y);
        RedrawJoinLines();
    }

    private void EndDraggingCard(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        _draggingCard = null;
        _draggingTable = null;
    }

    private void BeginJoin(QueryBuilderTable table, QueryBuilderColumn column, PointerPressedEventArgs e)
    {
        if (_viewModel?.IsDrawJoinMode != true)
            return;

        if (!TryGetAnchorPoint(table, column.ColumnName, true, out var startPoint))
            return;

        CancelJoinPreview();

        _joinStart = (table, column.ColumnName);
        _previewJoinLine = new Line
        {
            StartPoint = startPoint,
            EndPoint = startPoint,
            Classes = { "qb-join", "preview" },
            Tag = JoinVisualTag
        };
        _previewJoinLine.ZIndex = 5;
        QueryCanvas.Children.Add(_previewJoinLine);
        e.Handled = true;
    }

    private void CompleteJoin(QueryBuilderTable table, QueryBuilderColumn column, PointerReleasedEventArgs e)
    {
        if (_viewModel?.IsDrawJoinMode != true || _joinStart == null)
            return;

        var start = _joinStart.Value;
        if (!ReferenceEquals(start.Table, table) || !string.Equals(start.ColumnName, column.ColumnName, StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.AddJoin(new JoinDefinition
            {
                LeftTable = start.Table.TableName,
                LeftColumn = start.ColumnName,
                RightTable = table.TableName,
                RightColumn = column.ColumnName,
                JoinType = "INNER"
            });
        }

        CancelJoinPreview();
        RebuildCanvas();
        e.Handled = true;
    }

    private void CancelJoinPreview()
    {
        if (_previewJoinLine != null)
            QueryCanvas.Children.Remove(_previewJoinLine);

        _previewJoinLine = null;
        _joinStart = null;
    }

    private void RedrawJoinLines()
    {
        for (var index = QueryCanvas.Children.Count - 1; index >= 0; index--)
        {
            if (Equals(QueryCanvas.Children[index].Tag, JoinVisualTag))
                QueryCanvas.Children.RemoveAt(index);
        }

        if (_viewModel == null)
            return;

        foreach (var join in _viewModel.Joins)
        {
            var leftTable = FindTable(join.LeftTableAlias, join.LeftTableName);
            var rightTable = FindTable(join.RightTableAlias, join.RightTableName);
            if (leftTable == null || rightTable == null)
                continue;

            if (!TryGetAnchorPoint(leftTable, join.LeftColumn, true, out var startPoint) ||
                !TryGetAnchorPoint(rightTable, join.RightColumn, false, out var endPoint))
            {
                continue;
            }

            var line = new Line
            {
                StartPoint = startPoint,
                EndPoint = endPoint,
                Classes = { "qb-join", JoinClass(join.JoinType) },
                Tag = JoinVisualTag,
                ContextMenu = BuildJoinContextMenu(join)
            };
            line.ZIndex = 1;
            QueryCanvas.Children.Add(line);

            var label = new Border
            {
                Classes = { "qb-join-label" },
                Tag = JoinVisualTag,
                ContextMenu = BuildJoinContextMenu(join),
                Child = new TextBlock { Text = join.JoinType, Classes = { "caption" } }
            };
            label.ZIndex = 2;
            QueryCanvas.Children.Add(label);
            Canvas.SetLeft(label, (startPoint.X + endPoint.X) / 2);
            Canvas.SetTop(label, (startPoint.Y + endPoint.Y) / 2 - 10);
        }

        if (_previewJoinLine != null)
        {
            _previewJoinLine.ZIndex = 5;
            QueryCanvas.Children.Add(_previewJoinLine);
        }
    }

    private ContextMenu BuildJoinContextMenu(QueryBuilderJoin join)
    {
        var items = new List<MenuItem>();
        foreach (var joinType in new[] { "INNER", "LEFT", "RIGHT", "FULL OUTER", "CROSS" })
        {
            var menuItem = new MenuItem { Header = joinType };
            menuItem.Click += (_, _) =>
            {
                _viewModel?.SetJoinType(join, joinType);
                RedrawJoinLines();
            };
            items.Add(menuItem);
        }

        items.Add(new MenuItem());
        var removeItem = new MenuItem { Header = "Remove Join" };
        removeItem.Click += (_, _) =>
        {
            _viewModel?.RemoveJoinCommand.Execute(join);
            RedrawJoinLines();
        };
        items.Add(removeItem);

        return new ContextMenu { ItemsSource = items };
    }

    // Each join type has its own line colour (and CROSS a dash) in the qb-join styles.
    private static string JoinClass(string joinType) => joinType switch
    {
        "LEFT" => "left",
        "RIGHT" => "right",
        "FULL OUTER" => "full",
        "CROSS" => "cross",
        _ => "inner"
    };

    private QueryBuilderTable? FindTable(string alias, string tableName) =>
        _viewModel?.TableCards.FirstOrDefault(table =>
            string.Equals(table.Alias, alias, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(table.TableName, alias, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(table.TableName, tableName, StringComparison.OrdinalIgnoreCase));

    private bool TryGetAnchorPoint(QueryBuilderTable table, string columnName, bool rightEdge, out Point point)
    {
        if (_columnAnchors.TryGetValue((table, columnName), out var anchor))
        {
            var translated = anchor.TranslatePoint(
                new Point(rightEdge ? anchor.Bounds.Width : 0, anchor.Bounds.Height / 2),
                QueryCanvas);
            if (translated.HasValue)
            {
                point = translated.Value;
                return true;
            }
        }

        point = default;
        return false;
    }

    private void CanvasViewport_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
            return;

        _isPanning = true;
        _panStartPoint = e.GetPosition(this);
        _panTranslateStart = new Vector(_canvasTranslate.X, _canvasTranslate.Y);
        e.Pointer.Capture(CanvasViewport);
        e.Handled = true;
    }

    private void CanvasViewport_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isPanning)
        {
            var position = e.GetPosition(this);
            var delta = position - _panStartPoint;
            _canvasTranslate.X = _panTranslateStart.X + delta.X;
            _canvasTranslate.Y = _panTranslateStart.Y + delta.Y;
        }

        if (_previewJoinLine != null)
            _previewJoinLine.EndPoint = e.GetPosition(QueryCanvas);
    }

    private void CanvasViewport_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
        }

        if (_joinStart != null && _viewModel?.IsDrawJoinMode == true)
            CancelJoinPreview();
    }

    private void CanvasViewport_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        var factor = e.Delta.Y > 0 ? 1.1 : 0.9;
        var nextScale = Math.Clamp(_canvasScale.ScaleX * factor, 0.5, 2.5);
        _canvasScale.ScaleX = nextScale;
        _canvasScale.ScaleY = nextScale;
        e.Handled = true;
    }

    private void AddWhereCondition_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || string.IsNullOrWhiteSpace(WhereInput.Text))
            return;

        _viewModel.WhereConditions.Add(new WhereCondition { Expression = WhereInput.Text.Trim() });
        WhereInput.Text = string.Empty;
        _viewModel.GenerateSql();
    }

    private void RemoveWhereCondition_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not Button { Tag: WhereCondition condition })
            return;

        _viewModel.WhereConditions.Remove(condition);
        _viewModel.GenerateSql();
    }

    private void AddGroupBy_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || string.IsNullOrWhiteSpace(GroupByInput.Text))
            return;

        _viewModel.GroupByColumns.Add(new GroupByColumn { Column = GroupByInput.Text.Trim() });
        GroupByInput.Text = string.Empty;
        _viewModel.GenerateSql();
    }

    private void RemoveGroupBy_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not Button { Tag: GroupByColumn column })
            return;

        _viewModel.GroupByColumns.Remove(column);
        _viewModel.GenerateSql();
    }

    private void AddOrderBy_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || string.IsNullOrWhiteSpace(OrderByInput.Text))
            return;

        var expression = OrderByInput.Text.Trim();
        var descending = expression.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase);
        var normalizedColumn = expression.EndsWith(" ASC", StringComparison.OrdinalIgnoreCase) || descending
            ? expression[..expression.LastIndexOf(' ')].Trim()
            : expression;

        _viewModel.OrderByColumns.Add(new OrderByColumn { Column = normalizedColumn, Descending = descending });
        OrderByInput.Text = string.Empty;
        _viewModel.GenerateSql();
    }

    private void RemoveOrderBy_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not Button { Tag: OrderByColumn orderBy })
            return;

        _viewModel.OrderByColumns.Remove(orderBy);
        _viewModel.GenerateSql();
    }

    private void AddHavingCondition_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || string.IsNullOrWhiteSpace(HavingInput.Text))
            return;

        _viewModel.HavingConditions.Add(new HavingCondition { Expression = HavingInput.Text.Trim() });
        HavingInput.Text = string.Empty;
        _viewModel.GenerateSql();
    }

    private void RemoveHavingCondition_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not Button { Tag: HavingCondition condition })
            return;

        _viewModel.HavingConditions.Remove(condition);
        _viewModel.GenerateSql();
    }

    private void RemoveTable_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not Button { Tag: QueryBuilderTable table })
            return;

        _viewModel.RemoveTableCard(table);
        RebuildCanvas();
    }

    private void FormSelectAllColumns_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not Button { Tag: QueryBuilderTable table })
            return;

        var anyUnselected = table.Columns.Any(c => !c.IsSelected);
        foreach (var col in table.Columns)
            col.IsSelected = anyUnselected;

        _viewModel.GenerateSql();
    }

    private void FormAlias_LostFocus(object? sender, RoutedEventArgs e) => _viewModel?.GenerateSql();

    private void FormColumnSelectionChanged(object? sender, RoutedEventArgs e) => _viewModel?.GenerateSql();

    private void FormWhereExpression_LostFocus(object? sender, RoutedEventArgs e) => _viewModel?.GenerateSql();
}
