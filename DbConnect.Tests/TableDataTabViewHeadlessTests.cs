using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;
using static DbConnect.Tests.TableDataTestFixtures;

namespace DbConnect.Tests;

/// <summary>
/// revert-grid-fixups — table cells are built once from <see cref="EditableRow.Values"/> rather than
/// bound, and the context menu and cell editors are wired up in the view's code-behind. These tests
/// render the real view in a window and drive it through its editors, pointer and menu, asserting on
/// the cell text the grid shows.
/// </summary>
public class TableDataTabViewHeadlessTests
{
    [AvaloniaFact]
    public async Task CommittingATextEdit_WritesTheTypedValue()
    {
        using var ui = await GridHarness.ShowAsync(CreatePage([1]));
        var row = ui.ViewModel.Rows[0];

        ui.OpenTextEditor(row, "Name").Text = "Typed";
        ui.Grid.CommitEdit();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Typed", ui.CellText(row, "Name"));
        Assert.Equal(EditState.Modified, row.State);
    }

    [AvaloniaFact]
    public async Task CancellingATextEdit_DiscardsTheTypedValue()
    {
        using var ui = await GridHarness.ShowAsync(CreatePage([1]));
        var row = ui.ViewModel.Rows[0];

        ui.OpenTextEditor(row, "Name").Text = "Typed";
        ui.Grid.CancelEdit();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Name 1", ui.CellText(row, "Name"));
        Assert.Equal("Name 1", row.Values["Name"]);
    }

    [AvaloniaFact]
    public async Task CancellingABooleanEdit_RestoresThePreEditValue()
    {
        using var ui = await GridHarness.ShowAsync(CreateTypedPage());
        var row = ui.ViewModel.Rows[0];

        ui.OpenEditor<CheckBox>(row, "IsActive").IsChecked = false;
        ui.Grid.CancelEdit();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(true, row.Values["IsActive"]);
        Assert.Equal(EditState.Unchanged, row.State);
    }

    [AvaloniaFact]
    public async Task ClickingAToolbarButtonMidEdit_CommitsTheTypedValue()
    {
        using var ui = await GridHarness.ShowAsync(CreatePage([1]));
        var row = ui.ViewModel.Rows[0];

        ui.OpenTextEditor(row, "Name").Text = "Typed";
        ui.Click(ui.ToolbarButton(ui.ViewModel.AddRowCommand));

        Assert.Equal("Typed", row.Values["Name"]);
        Assert.Equal(EditState.Modified, row.State);
    }

    [AvaloniaFact]
    public async Task RevertAll_RedrawsRevertedCells()
    {
        using var ui = await GridHarness.ShowAsync(CreatePage([1, 2]));
        var row = ui.ViewModel.Rows[0];
        ui.EditText(row, "Name", "Updated");
        Assert.Equal("Updated", ui.CellText(row, "Name"));

        ui.ViewModel.RevertCommand.Execute(null);

        Assert.Equal("Name 1", ui.CellText(row, "Name"));
    }

    [AvaloniaFact]
    public async Task RevertCellFromMenu_TargetsTheRightClickedRowAndSelectsIt()
    {
        using var ui = await GridHarness.ShowAsync(CreatePage([1, 2]));
        var (first, second) = (ui.ViewModel.Rows[0], ui.ViewModel.Rows[1]);
        ui.EditText(first, "Name", "Edited 1");
        ui.EditText(second, "Name", "Edited 2");

        ui.Grid.SelectedItem = first;
        ui.RightClickCell(second, "Name");
        ui.InvokeMenuItem("RevertCellMenuItem");

        Assert.Same(second, ui.Grid.SelectedItem);
        Assert.Equal("Name 2", ui.CellText(second, "Name"));
        Assert.Equal("Edited 1", ui.CellText(first, "Name"));
        Assert.Equal(EditState.Modified, first.State);
    }

    [AvaloniaFact]
    public async Task RevertCellFromMenu_InsideMultiRowSelection_KeepsSelectionAndTargetsTheClickedRow()
    {
        using var ui = await GridHarness.ShowAsync(CreatePage([1, 2, 3]));
        var (first, second) = (ui.ViewModel.Rows[0], ui.ViewModel.Rows[1]);
        ui.EditText(first, "Name", "Edited 1");
        ui.EditText(second, "Name", "Edited 2");

        ui.Grid.SelectedItems.Clear();
        ui.Grid.SelectedItems.Add(first);
        ui.Grid.SelectedItems.Add(second);
        ui.RightClickCell(second, "Name");
        ui.InvokeMenuItem("RevertCellMenuItem");

        Assert.Equal(2, ui.Grid.SelectedItems.Count);
        Assert.Contains(first, ui.Grid.SelectedItems.Cast<EditableRow>());
        Assert.Contains(second, ui.Grid.SelectedItems.Cast<EditableRow>());
        Assert.Equal("Name 2", ui.CellText(second, "Name"));
        Assert.Equal("Edited 1", ui.CellText(first, "Name"));
    }

    [AvaloniaFact]
    public async Task RevertRowFromMenu_RevertsTheRightClickedRowOnly()
    {
        using var ui = await GridHarness.ShowAsync(CreatePage([1, 2]));
        var (first, second) = (ui.ViewModel.Rows[0], ui.ViewModel.Rows[1]);
        ui.EditText(first, "Name", "Edited 1");
        ui.EditText(second, "Name", "Edited 2");

        ui.RightClickCell(second, "Id");
        ui.InvokeMenuItem("RevertRowMenuItem");

        Assert.Equal("Name 2", ui.CellText(second, "Name"));
        Assert.Equal(EditState.Unchanged, second.State);
        Assert.Equal("Edited 1", ui.CellText(first, "Name"));
    }

    [AvaloniaFact]
    public async Task MenuOpenedAfterSelectionMovedByKeyboard_TargetsTheSelectedRowAndCurrentColumn()
    {
        using var ui = await GridHarness.ShowAsync(CreatePage([1, 2]));
        var (first, second) = (ui.ViewModel.Rows[0], ui.ViewModel.Rows[1]);
        ui.EditText(first, "Name", "Edited 1");
        ui.EditText(second, "Name", "Edited 2");
        ui.RightClickCell(first, "Name");
        ui.Grid.ContextMenu!.Close();

        // Selection and current cell move without a pointer press, as arrow keys would.
        ui.Grid.SelectedItem = second;
        ui.Grid.CurrentColumn = ui.Column("Name");
        ui.InvokeMenuItem("RevertCellMenuItem");

        Assert.Equal("Name 2", ui.CellText(second, "Name"));
        Assert.Equal("Edited 1", ui.CellText(first, "Name"));
    }

    [AvaloniaFact]
    public async Task RevertAll_WithACellEditorOpen_DiscardsTheEditorValue()
    {
        using var ui = await GridHarness.ShowAsync(CreatePage([1]));
        var row = ui.ViewModel.Rows[0];
        ui.EditText(row, "Name", "Edited");

        ui.OpenTextEditor(row, "Name").Text = "Typed";
        ui.ViewModel.RevertCommand.Execute(null);
        ui.Grid.CommitEdit();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Name 1", row.Values["Name"]);
        Assert.Equal(EditState.Unchanged, row.State);
        Assert.Equal("Name 1", ui.CellText(row, "Name"));
    }

    [AvaloniaFact]
    public async Task RevertCell_WithAnotherCellsTextEditorOpen_DoesNotLeakTheEditorValue()
    {
        using var ui = await GridHarness.ShowAsync(CreatePage([1]));
        var row = ui.ViewModel.Rows[0];
        ui.EditText(row, "Id", "99");

        ui.OpenTextEditor(row, "Name").Text = "Typed";
        ui.ViewModel.RevertCell(row, "Id");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Name 1", row.Values["Name"]);
        Assert.Equal(EditState.Unchanged, row.State);
        Assert.Equal("Name 1", ui.CellText(row, "Name"));
    }

    [AvaloniaFact]
    public async Task RevertCell_WithAnotherCellsBooleanEditorOpen_DoesNotLeakTheEditorValue()
    {
        using var ui = await GridHarness.ShowAsync(CreateTypedPage());
        var row = ui.ViewModel.Rows[0];
        ui.EditText(row, "Name", "Edited");

        ui.OpenEditor<CheckBox>(row, "IsActive").IsChecked = false;
        ui.ViewModel.RevertCell(row, "Name");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(true, row.Values["IsActive"]);
        Assert.Equal(EditState.Unchanged, row.State);
    }

    [AvaloniaFact]
    public async Task RevertRow_WithAnotherRowsBooleanEditorOpen_DoesNotLeakTheEditorValue()
    {
        var page = CreateTypedPage();
        page.Result.ObjectRows.Add([2L, "Gadget", true, new DateTime(2021, 1, 1), new DateTime(2026, 9, 27), 1.5m]);
        using var ui = await GridHarness.ShowAsync(page);
        var (first, second) = (ui.ViewModel.Rows[0], ui.ViewModel.Rows[1]);
        ui.EditText(first, "Name", "Edited");

        ui.OpenEditor<CheckBox>(second, "IsActive").IsChecked = false;
        ui.ViewModel.RevertRowCommand.Execute(first);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(true, second.Values["IsActive"]);
        Assert.Equal(EditState.Unchanged, second.State);
    }

    [AvaloniaFact]
    public async Task RejectedEditWithoutRowIdentity_RedrawsTheOriginalValue()
    {
        using var ui = await GridHarness.ShowAsync(CreatePage([1]), hasPrimaryKey: false);
        var row = ui.ViewModel.Rows[0];

        ui.EditText(row, "Name", "Updated");

        Assert.Equal("Name 1", row.Values["Name"]);
        Assert.Equal(EditState.Unchanged, row.State);
        Assert.Equal("Name 1", ui.CellText(row, "Name"));
    }

    [AvaloniaFact]
    public async Task RevertRowFromMenu_ResetsTheRowColour()
    {
        using var ui = await GridHarness.ShowAsync(CreatePage([1]));
        var row = ui.ViewModel.Rows[0];
        ui.EditText(row, "Name", "Edited");
        Assert.Equal("Modified", ui.RowTag(row));

        ui.RightClickCell(row, "Name");
        ui.InvokeMenuItem("RevertRowMenuItem");

        Assert.Equal("Unchanged", ui.RowTag(row));
    }

    [AvaloniaFact]
    public async Task ColumnLayoutChanges_RebuildTheGridColumns()
    {
        using var ui = await GridHarness.ShowAsync(CreateTypedPage());

        await ui.ViewModel.HideColumnAsync("Name");
        await ui.ViewModel.MoveColumnAsync("Price", 0);
        await ui.ViewModel.FreezeColumnsThroughAsync("Price");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["State", "Price", "Id", "IsActive", "BirthDate", "CreatedAt"], ui.DisplayedHeaders());
        Assert.Equal(2, ui.Grid.FrozenColumnCount);

        await ui.ViewModel.UnfreezeColumnsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, ui.Grid.FrozenColumnCount);
    }

    [AvaloniaFact]
    public async Task ColumnMenuItems_HideAndFreezeTheClickedColumn()
    {
        using var ui = await GridHarness.ShowAsync(CreateTypedPage());
        var row = ui.ViewModel.Rows[0];

        ui.RightClickCell(row, "Name");
        ui.InvokeMenuItem("FreezeColumnsMenuItem");
        ui.RightClickCell(row, "IsActive");
        ui.InvokeMenuItem("HideColumnMenuItem");

        Assert.Equal(["State", "Id", "Name", "BirthDate", "CreatedAt", "Price"], ui.DisplayedHeaders());
        Assert.Equal(3, ui.Grid.FrozenColumnCount);
    }

    [AvaloniaFact]
    public async Task ViewValue_IsOfferedForTextCells_ButNotForNullOnes()
    {
        using var ui = await GridHarness.ShowAsync(CreatePage([1, 2]));
        var (first, second) = (ui.ViewModel.Rows[0], ui.ViewModel.Rows[1]);
        second.Values["Name"] = null;

        MenuItem Item(string name) => ui.Grid.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == name);

        ui.RightClickCell(first, "Name");
        Assert.True(Item("ViewValueMenuItem").IsVisible);
        Assert.False(Item("ViewBinaryMenuItem").IsVisible);
        ui.Grid.ContextMenu!.Close();

        ui.RightClickCell(second, "Name");
        Assert.False(Item("ViewValueMenuItem").IsVisible);
        ui.Grid.ContextMenu!.Close();
    }

    [AvaloniaFact]
    public async Task DraggingAColumnHeader_ReordersTheLayout()
    {
        using var ui = await GridHarness.ShowAsync(CreateTypedPage());

        ui.DragColumn("Price", displayIndex: 1);

        Assert.Equal(["Price", "Id", "Name", "IsActive", "BirthDate", "CreatedAt"], ui.ViewModel.DisplayedColumns);
        Assert.Equal(["State", "Price", "Id", "Name", "IsActive", "BirthDate", "CreatedAt"], ui.DisplayedHeaders());
    }

    [AvaloniaFact]
    public async Task ColumnLayoutChanges_KeepResizedColumnWidths()
    {
        using var ui = await GridHarness.ShowAsync(CreateTypedPage());
        ui.Column("Name").Width = new DataGridLength(222);

        await ui.ViewModel.HideColumnAsync("Price");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(222, ui.Column("Name").Width.Value);
    }

    [AvaloniaTheory]
    [InlineData("BirthDate")]
    [InlineData("CreatedAt")]
    public async Task DateEditors_CommitValuesAndSynchronizeTheirNullToggle(string column)
    {
        using var ui = await GridHarness.ShowAsync(CreateTypedPage());
        var row = ui.ViewModel.Rows[0];
        var date = ui.OpenEditor<DatePicker>(row, column);
        date.SelectedDate = new DateTimeOffset(2030, 2, 3, 0, 0, 0, TimeSpan.Zero);
        if (column == "CreatedAt")
            ui.Cell(row, column).GetVisualDescendants().OfType<TimePicker>().Single().SelectedTime = new TimeSpan(12, 34, 0);
        Assert.True(ui.Grid.CommitEdit());
        Assert.Equal(column == "BirthDate" ? new DateTime(2030, 2, 3) : new DateTime(2030, 2, 3, 12, 34, 0), row.Values[column]);

        date = ui.OpenEditor<DatePicker>(row, column);
        var nullToggle = ui.Cell(row, column).GetVisualDescendants().OfType<CheckBox>().Single();
        nullToggle.IsChecked = true;
        Assert.Null(row.Values[column]);
        Assert.Null(date.SelectedDate);
        Assert.False(date.IsEnabled);
        if (column == "CreatedAt")
            Assert.False(ui.Cell(row, column).GetVisualDescendants().OfType<TimePicker>().Single().IsEnabled);
        nullToggle.IsChecked = false;
        Assert.IsType<DateTime>(row.Values[column]);
        Assert.NotNull(date.SelectedDate);
        Assert.True(date.IsEnabled);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnumAndSetEditors_WriteProviderMembersAndSynchronizeNull(bool multiple)
    {
        var page = new TablePageResult
        {
            Result = new QueryResult
            {
                ColumnInfos = [new ColumnInfo { Name = "Id", Ordinal = 0 }, new ColumnInfo { Name = "Status", Ordinal = 1 }],
                ObjectRows = [new object?[] { 1L, "red" }]
            }
        };
        using var ui = await GridHarness.ShowAsync(page,
            enumColumns: new() { ["Status"] = new EnumColumnInfo(["red", "blue"], multiple) });
        var row = ui.ViewModel.Rows[0];
        Control editor;
        if (multiple)
        {
            var button = ui.OpenEditor<Button>(row, "Status");
            var checklist = Assert.IsType<StackPanel>(Assert.IsType<Flyout>(button.Flyout).Content);
            checklist.Children.OfType<CheckBox>().Single(c => Equals(c.Content, "blue")).IsChecked = true;
            Assert.Equal("red,blue", row.Values["Status"]);
            Assert.Equal("red,blue", button.Content);
            editor = button;
        }
        else
        {
            var combo = ui.OpenEditor<ComboBox>(row, "Status");
            Assert.Equal(new[] { "red", "blue" }, combo.Items.Cast<string>());
            combo.SelectedItem = "blue";
            Assert.Equal("blue", row.Values["Status"]);
            editor = combo;
        }
        var nullToggle = ui.Cell(row, "Status").GetVisualDescendants().OfType<CheckBox>().Single();
        nullToggle.IsChecked = true;
        Assert.Null(row.Values["Status"]);
        Assert.False(editor.IsEnabled);
        nullToggle.IsChecked = false;
        Assert.True(editor.IsEnabled);
        Assert.Equal(multiple ? "" : "red", row.Values["Status"]);
        if (editor is ComboBox comboBox) Assert.Equal("red", comboBox.SelectedItem);
        else Assert.Equal("(none)", ((Button)editor).Content);
    }

    /// <summary>The real view in a shown window; disposing closes the window.</summary>
    private sealed class GridHarness(Window window, TableDataTabViewModel viewModel, DataGrid grid) : IDisposable
    {
        public TableDataTabViewModel ViewModel => viewModel;
        public DataGrid Grid => grid;

        public static async Task<GridHarness> ShowAsync(TablePageResult page, bool hasPrimaryKey = true,
            Dictionary<string, EnumColumnInfo>? enumColumns = null)
        {
            var provider = new TablePagingProvider(page, enumColumns: enumColumns);
            var viewModel = new TableDataTabViewModel("Customers", CreateSession(provider), CreateTableNode(hasPrimaryKey));
            await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);

            var view = new TableDataTabView { DataContext = viewModel };
            var window = new Window { Content = view, Width = 1000, Height = 600 };
            window.Show();
            window.UpdateLayout();

            return new GridHarness(window, viewModel, view.FindControl<DataGrid>("DataGrid")!);
        }

        public DataGridColumn Column(string column) =>
            grid.Columns.Single(c => Equals(c.Header, column));

        public DataGridCell Cell(EditableRow row, string column) =>
            (DataGridCell)Column(column).GetCellContent(row)!.Parent!;

        public string? CellText(EditableRow row, string column) =>
            Assert.IsType<TextBlock>(Column(column).GetCellContent(row)).Text;

        // The row's state colour comes from DataGridRow.Tag (see the view's row styles).
        public object? RowTag(EditableRow row) =>
            Cell(row, "Name").GetVisualAncestors().OfType<DataGridRow>().First().Tag;

        public Button ToolbarButton(System.Windows.Input.ICommand command) =>
            window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, command));

        /// <summary>Edits a cell the way a user does: open its editor, type, commit.</summary>
        public void EditText(EditableRow row, string column, string text)
        {
            OpenTextEditor(row, column).Text = text;
            Assert.True(grid.CommitEdit(), "the edit should commit");
            Dispatcher.UIThread.RunJobs();
        }

        public TextBox OpenTextEditor(EditableRow row, string column) => OpenEditor<TextBox>(row, column);

        // Opens the cell's editing template and focuses its editor, as double-clicking the cell would.
        public TEditor OpenEditor<TEditor>(EditableRow row, string column) where TEditor : Control
        {
            grid.SelectedItem = row;
            grid.CurrentColumn = Column(column);
            Assert.True(grid.BeginEdit(), "the cell should enter edit mode");
            Dispatcher.UIThread.RunJobs();

            var editor = Cell(row, column).GetVisualDescendants().OfType<TEditor>().First();
            editor.Focus();
            Dispatcher.UIThread.RunJobs();
            return editor;
        }

        public void RightClickCell(EditableRow row, string column) => Click(Cell(row, column), MouseButton.Right);

        public void Click(Control control, MouseButton button = MouseButton.Left)
        {
            var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, button);
            window.MouseUp(center, button);
            Dispatcher.UIThread.RunJobs();
        }

        // Opens the grid's context menu (running its Opening handler) and clicks the named item.
        public void InvokeMenuItem(string itemName)
        {
            var menu = grid.ContextMenu!;
            if (!menu.IsOpen)
                menu.Open(grid);
            var item = menu.Items.OfType<MenuItem>().Single(i => i.Name == itemName);
            Assert.True(item.IsEnabled, $"{itemName} should be enabled");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
            Dispatcher.UIThread.RunJobs();
        }

        public IReadOnlyList<string> DisplayedHeaders() =>
            grid.Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayIndex).Select(c => (string)c.Header!).ToList();

        // A header drag moves the column's DisplayIndex, then the grid raises ColumnReordered.
        // The drag gesture itself can't be driven headlessly, so this reproduces its two steps.
        public void DragColumn(string column, int displayIndex)
        {
            var dragged = Column(column);
            dragged.DisplayIndex = displayIndex;
            typeof(DataGrid)
                .GetMethod("OnColumnReordered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
                .Invoke(grid, [new DataGridColumnEventArgs(dragged)]);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => window.Close();
    }
}
