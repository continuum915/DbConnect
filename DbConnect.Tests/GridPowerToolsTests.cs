using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

/// <summary>Set Value, Fill Down, Form View and editing without a primary key (Tier 2 §2.9).</summary>
public sealed class GridPowerToolsTests : MainWindowTestBase, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "rain-grid-" + Guid.NewGuid().ToString("N") + ".db");

    public GridPowerToolsTests()
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT, qty INTEGER DEFAULT 7);
            INSERT INTO items VALUES (1, 'a', 1), (2, 'b', 2), (3, 'c', 3);
            CREATE TABLE loose (label TEXT, qty INTEGER);
            INSERT INTO loose VALUES ('x', 1), ('y', 2), ('dup', 5), ('dup', 5);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }

    private List<string> Rows(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i)))));
        return rows;
    }

    private async Task<(MainWindowViewModel Vm, QueryTabViewModel Tab)> RunAsync(string sql)
    {
        var vm = CreateMainWindowViewModel(new DatabaseService());
        vm.Tabs.Clear();
        var tab = new QueryTabViewModel();
        vm.Tabs.Add(tab);
        vm.SelectedTab = tab;
        var session = new DatabaseSession(new ConnectionInfo { Name = "grid", Provider = "SQLite", ConnectionString = $"Data Source={_path};Pooling=False" }, new SqliteProvider());
        vm.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = sql;
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
        await tab.ResultEditor.PendingLoad;
        return (vm, tab);
    }

    [Fact]
    public void Resolve_ChecksTheColumnAndTheEngine()
    {
        Assert.Null(GridCellEdits.Resolve(CellEditValue.Null, ColumnDataType.Integer, null));
        Assert.Equal("", GridCellEdits.Resolve(CellEditValue.EmptyString, ColumnDataType.Text, null));
        Assert.Throws<FormatException>(() => GridCellEdits.Resolve(CellEditValue.EmptyString, ColumnDataType.Integer, null));
        Assert.Same(ColumnDefault.Value, GridCellEdits.Resolve(CellEditValue.Default, ColumnDataType.Integer, ProviderCapabilityDeclarations.PostgreSql));
        Assert.Throws<FormatException>(() => GridCellEdits.Resolve(CellEditValue.Default, ColumnDataType.Integer, ProviderCapabilityDeclarations.Sqlite));
        Assert.Equal(42L, GridCellEdits.Resolve(new CellEditValue(CellEditKind.Text, "42"), ColumnDataType.Integer, null));
    }

    [Fact]
    public void Default_IsTheKeywordInUpdates_AndLeftOutOfInserts()
    {
        var tracker = new DataChangeTracker("items", ["id", "label", "qty"], ["id"], ProviderCapabilityDeclarations.PostgreSql);
        var row = new EditableRow(new Dictionary<string, object?> { ["id"] = 1, ["label"] = "a", ["qty"] = 1 });
        row.Values["qty"] = ColumnDefault.Value;
        row.State = EditState.Modified;
        var command = tracker.BuildCommand(row)!;
        Assert.StartsWith("UPDATE \"items\" SET \"qty\" = DEFAULT WHERE", command.CommandText);
        Assert.DoesNotContain(command.Parameters.Values, v => v is ColumnDefault);
        Assert.Contains("SET \"qty\" = DEFAULT", tracker.BuildStatement(row));

        var inserted = new EditableRow(new Dictionary<string, object?> { ["id"] = 9, ["label"] = "z", ["qty"] = ColumnDefault.Value },
            ["id", "label", "qty"])
        { State = EditState.Inserted };
        var insert = tracker.BuildCommand(inserted)!;
        Assert.Contains("(\"id\", \"label\")", insert.CommandText);
    }

    [Fact]
    public async Task SetValueAndFillDown_OnTheSelection_CommitAsOrdinaryEdits()
    {
        var (_, tab) = await RunAsync("SELECT id, label, qty FROM items ORDER BY id");
        var selected = tab.DisplayResultRows.Cast<object>().ToList();

        Assert.Null(GridEditMenu.ApplyTo(tab, selected, 1, "label", fill: false, new CellEditValue(CellEditKind.Text, "set")));
        Assert.Null(GridEditMenu.ApplyTo(tab, selected, 2, "qty", fill: true, null));
        Assert.Contains("Only text columns", GridEditMenu.ApplyTo(tab, selected, 2, "qty", fill: false, CellEditValue.EmptyString));
        Assert.Contains("column default", GridEditMenu.ApplyTo(tab, selected, 2, "qty", fill: false, CellEditValue.Default));
        Assert.True(tab.ResultEditor.HasChanges);
        await tab.ResultEditor.ExecuteCommitAsync();

        Assert.Equal(["1|set|1", "2|set|1", "3|set|1"], Rows("SELECT id, label, qty FROM items ORDER BY id"));
        await tab.ReleaseDbSessionAsync();
    }

    [AvaloniaFact]
    public void GridMenus_OfferSetValueFillDownAndFormView()
    {
        var tab = new QueryTabViewModel();
        var view = new QueryTabView { DataContext = tab };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var menu = view.FindControl<DataGrid>("ResultsGrid")!.ContextMenu!;
            var headers = menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToList();
            Assert.Contains("Set Value", headers);
            Assert.Contains("Fill Down", headers);
            Assert.Contains("Form View", headers);
            var setValue = menu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Set Value");
            Assert.Equal(["NULL", "Empty String", "Default", "Value…"], setValue.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()));
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task KeylessResult_EditsAfterOptIn_AndDuplicateRowsRollBack()
    {
        var (_, tab) = await RunAsync("SELECT label, qty FROM loose ORDER BY rowid");
        Assert.False(tab.ResultEditor.IsEditable);
        Assert.True(tab.ResultEditor.OffersAllColumnIdentity);
        Assert.Contains("matching all column values", tab.ResultEditor.ReadOnlyReason);

        tab.ResultEditor.AllowAllColumnIdentity = true;
        await tab.ResultEditor.PendingLoad;
        Assert.True(tab.ResultEditor.IsEditable);

        var first = tab.ResultEditor.RowFor(tab.DisplayResultRows.First(r => r[0] == "x"))!;
        Assert.Null(tab.ResultEditor.SetCellText(first, 1, "10"));
        await tab.ResultEditor.ExecuteCommitAsync();
        Assert.True(Rows("SELECT label, qty FROM loose ORDER BY rowid")[0] == "x|10", tab.ResultEditor.StatusMessage);

        var dup = tab.ResultEditor.RowFor(tab.DisplayResultRows.First(r => r[0] == "dup"))!;
        Assert.Null(tab.ResultEditor.SetCellText(dup, 1, "6"));
        await tab.ResultEditor.ExecuteCommitAsync();
        Assert.Equal(["dup|5", "dup|5"], Rows("SELECT label, qty FROM loose WHERE label = 'dup'"));
        Assert.True(tab.ResultEditor.HasChanges);
        await tab.ReleaseDbSessionAsync();
    }

    [Fact]
    public async Task FormView_EditsTheSameRowsAsTheGrid()
    {
        var (_, tab) = await RunAsync("SELECT id, label, qty FROM items ORDER BY id");
        var record = tab.Record;
        record.SetGridRow(1);
        record.ToggleCommand.Execute(null);
        Assert.True(record.IsActive);
        Assert.Equal("Row 2 of 3", record.PositionText);
        Assert.Equal(["id", "label", "qty"], record.Fields.Select(f => f.Name));
        Assert.Equal("b", record.Fields[1].Text);

        record.Fields[1].Text = "edited";
        Assert.True(record.Fields[1].IsChanged);
        record.Fields[2].Text = "not a number";
        Assert.NotNull(record.Fields[2].Error);
        record.NextCommand.Execute(null);
        Assert.Equal("c", record.Fields[1].Text);
        Assert.False(record.NextCommand.CanExecute(null));

        record.FirstCommand.Execute(null);
        record.Fields[2].SetNullCommand.Execute(null);
        Assert.True(record.Fields[2].IsNull);
        await tab.ResultEditor.ExecuteCommitAsync();
        Assert.Equal(["1|a|NULL", "2|edited|2", "3|c|3"], Rows("SELECT id, label, qty FROM items ORDER BY id"));
        await tab.ReleaseDbSessionAsync();
    }

    [Fact]
    public async Task TableTabFormView_EditsLoadedRows()
    {
        var root = new SchemaNode { Name = "db", Type = SchemaNodeType.Database };
        var session = new DatabaseSession(new ConnectionInfo { Name = "grid", Provider = "SQLite", ConnectionString = $"Data Source={_path};Pooling=False" }, new SqliteProvider());
        var node = new SchemaNode { Name = "items", RawName = "items", Type = SchemaNodeType.Table };
        node.Children.Add(new SchemaNode { Name = "id", RawName = "id", Type = SchemaNodeType.Column, IsPrimaryKey = true });
        root.Children.Add(node);
        var tab = new TableDataTabViewModel("items", session, node);
        await tab.RefreshInternalAsync(bypassDirtyCheck: true);
        var record = tab.Record;
        record.ToggleCommand.Execute(null);
        record.Fields.Single(f => f.Name == "label").Text = "form";
        Assert.True(tab.HasDirtyRows);
        await tab.ExecuteCommitAsync();
        Assert.Equal("1|form", Rows("SELECT id, label FROM items WHERE id = 1")[0]);
        await tab.ReleaseDbSessionAsync();
    }
}
