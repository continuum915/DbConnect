using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// Explorer ▸ Dump SQL File ▸ Structure and Data / Structure Only on connection, database, schema and table nodes, and
/// on several selected tables through the table list (sql-dump-and-insert-export/04, 05, 08).
/// </summary>
public sealed class ExplorerDumpSqlTests : MainWindowTestBase, IDisposable
{
    private const string Fixture = """
        CREATE TABLE parent (id INTEGER PRIMARY KEY);
        CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));
        """;

    private const string Grandchild = "CREATE TABLE grandchild (id INTEGER PRIMARY KEY, child_id INTEGER REFERENCES child(id));";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-explorer-dump-" + Guid.NewGuid().ToString("N"));
    public ExplorerDumpSqlTests() => Directory.CreateDirectory(_directory);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(SchemaNodeType.Database, true)]
    [InlineData(SchemaNodeType.Schema, true)]
    [InlineData(SchemaNodeType.Table, true)]
    [InlineData(SchemaNodeType.View, false)]
    [InlineData(SchemaNodeType.Column, false)]
    public void DumpIsOfferedOnConnectionDatabaseSchemaAndTableNodes(SchemaNodeType type, bool offered)
    {
        var (vm, root) = Explorer();
        var node = type == SchemaNodeType.Database ? root : new SchemaNode { Name = "x", RawName = "x", Type = type, SchemaName = "main" };
        if (node != root) root.Children.Add(node);

        var availability = vm.Explorer.Availability(node, ExplorerObjectAction.DumpSql);

        Assert.Equal(offered, availability.IsVisible);
        Assert.Equal(offered, availability.IsEnabled);
        var disconnected = vm.Explorer.Availability(new SchemaNode { Name = "other", Type = type }, ExplorerObjectAction.DumpSql);
        Assert.Equal(offered, disconnected.IsVisible);
        Assert.False(disconnected.IsEnabled);
    }

    [Fact]
    public async Task DumpingTheConnectionWritesEveryTableParentsFirstAndShowsProgress()
    {
        var (vm, root) = Explorer();
        var picker = new Picker(Path.Combine(_directory, "all.sql"));
        ExportOperationViewModel? shown = null;
        vm.Explorer.DumpPathPicker = picker;
        vm.Explorer.ShowDumpProgress = operation => { shown = operation; return Task.CompletedTask; };

        await vm.Explorer.DumpSqlAsync(root, DumpMode.StructureOnly);

        Assert.Equal("db.sql", picker.SuggestedName);
        Assert.NotNull(shown);
        Assert.True(shown!.Outcome?.Status == ExportStatus.Completed, shown.Message);
        Assert.StartsWith("Completed · Dump SQL File (Structure Only) · 2 tables", shown.Message);
        Assert.Equal(shown.Message, vm.StatusMessage);
        var script = await File.ReadAllTextAsync(picker.Path);
        Assert.True(script.IndexOf("-- Table main.parent\n", StringComparison.Ordinal) < script.IndexOf("-- Table main.child\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DumpingStructureAndDataReportsTheRowsWritten()
    {
        var (vm, root) = Explorer("INSERT INTO parent VALUES (1), (2); INSERT INTO child VALUES (5, 1);");
        var picker = new Picker(Path.Combine(_directory, "data.sql"));
        ExportOperationViewModel? shown = null;
        vm.Explorer.DumpPathPicker = picker;
        vm.Explorer.ShowDumpProgress = operation => { shown = operation; return Task.CompletedTask; };

        await vm.Explorer.DumpSqlAsync(root, DumpMode.StructureAndData);

        Assert.StartsWith("Completed · Dump SQL File (Structure and Data) · 2 tables · 3 rows · ", shown!.Message);
        Assert.Equal(3, shown.RowsWritten);
        var script = await File.ReadAllTextAsync(picker.Path);
        Assert.True(script.IndexOf("-- Data main.parent\n", StringComparison.Ordinal) < script.IndexOf("-- Data main.child\n", StringComparison.Ordinal));
    }

    [Fact]
    public void DataProgressNamesTheTableAndItsRows()
    {
        var operation = new ExportOperationViewModel();
        IProgress<DumpProgress> progress = operation;

        progress.Report(new DumpProgress(1, 3, "main.parent", 1000));
        progress.Report(new DumpProgress(1, 3, "main.parent", 1200));
        progress.Report(new DumpProgress(2, 3, "main.child", 1000));

        Assert.EndsWith("data for table 2 of 3 · main.child · 1000 rows", operation.Message);
        Assert.Equal(2200, operation.RowsWritten);
    }

    [Fact]
    public async Task DumpingATableWritesOnlyThatTable()
    {
        var (vm, root) = Explorer();
        var table = new SchemaNode { Name = "child", RawName = "child", Type = SchemaNodeType.Table, SchemaName = "main" };
        root.Children.Add(table);
        var picker = new Picker(Path.Combine(_directory, "child.sql"));
        vm.Explorer.DumpPathPicker = picker;

        await vm.Explorer.DumpSqlAsync(table, DumpMode.StructureOnly);

        Assert.Equal("main.child.sql", picker.SuggestedName);
        var script = await File.ReadAllTextAsync(picker.Path);
        Assert.Contains("-- Table main.child\n", script);
        Assert.DoesNotContain("-- Table main.parent", script);
        Assert.Contains("Referenced table \"parent\" (foreign key target)", script);
    }

    [Fact]
    public async Task DumpingSeveralSelectedTablesOpensTheTableListAndDumpsTheTickedOnesInDependencyOrder()
    {
        var (vm, root) = Explorer(Grandchild + "INSERT INTO parent VALUES (1); INSERT INTO child VALUES (5, 1); INSERT INTO grandchild VALUES (9, 5);");
        var tables = AddTables(root, "grandchild", "parent", "child");
        var picker = new Picker(Path.Combine(_directory, "selection.sql"));
        DumpTablesViewModel? list = null;
        DumpMode? offered = null;
        vm.Explorer.DumpPathPicker = picker;
        vm.Explorer.ShowDumpTables = dialog =>
        {
            list = dialog;
            offered = dialog.Mode.Mode;
            dialog.Mode = dialog.Modes.Single(m => m.Mode == DumpMode.StructureAndData);
            dialog.DumpCommand.Execute(null);
            return Task.CompletedTask;
        };

        await vm.Explorer.DumpSqlAsync(tables[2], DumpMode.StructureOnly, [tables[2], tables[0]]);

        Assert.Equal(DumpMode.StructureOnly, offered);
        Assert.Equal(["grandchild", "parent", "child"], list!.Tables.Select(t => t.Name));
        Assert.Equal([true, false, true], list.Tables.Select(t => t.IsSelected));
        Assert.Equal("db.sql", picker.SuggestedName);
        Assert.StartsWith("Completed · Dump SQL File (Structure and Data) · 2 tables · 2 rows", vm.StatusMessage);
        var script = await File.ReadAllTextAsync(picker.Path);
        Assert.True(script.IndexOf("-- Table main.child\n", StringComparison.Ordinal) < script.IndexOf("-- Table main.grandchild\n", StringComparison.Ordinal));
        Assert.DoesNotContain("-- Table main.parent", script);
        Assert.Contains("--   Referenced table \"parent\" (foreign key target)\n", script);
        Assert.DoesNotContain("Referenced table \"child\"", script);
    }

    [Fact]
    public async Task TablesTickedInTheListJoinTheDumpInDependencyOrder()
    {
        var (vm, root) = Explorer(Grandchild);
        var tables = AddTables(root, "grandchild", "parent", "child");
        var picker = new Picker(Path.Combine(_directory, "ticked.sql"));
        vm.Explorer.DumpPathPicker = picker;
        vm.Explorer.ShowDumpTables = dialog =>
        {
            dialog.Tables.Single(t => t.Name == "parent").IsSelected = true;
            dialog.DumpCommand.Execute(null);
            return Task.CompletedTask;
        };

        await vm.Explorer.DumpSqlAsync(tables[0], DumpMode.StructureOnly, [tables[0], tables[2]]);

        var script = await File.ReadAllTextAsync(picker.Path);
        var order = new[] { "parent", "child", "grandchild" }.Select(t => script.IndexOf($"-- Table main.{t}\n", StringComparison.Ordinal)).ToList();
        Assert.True(order[0] >= 0 && order[0] < order[1] && order[1] < order[2], script);
        Assert.DoesNotContain("Prerequisites", script);
    }

    [Fact]
    public async Task ClosingTheTableListOrUntickingEveryTableDumpsNothing()
    {
        var (vm, root) = Explorer();
        var tables = AddTables(root, "parent", "child");
        var picked = false;
        vm.Explorer.DumpPathPicker = new CountingPicker(() => picked = true);
        vm.Explorer.ShowDumpTables = _ => Task.CompletedTask;

        await vm.Explorer.DumpSqlAsync(tables[0], DumpMode.StructureOnly, tables);

        vm.Explorer.ShowDumpTables = dialog =>
        {
            dialog.SelectNoneCommand.Execute(null);
            Assert.False(dialog.DumpCommand.CanExecute(null));
            return Task.CompletedTask;
        };
        await vm.Explorer.DumpSqlAsync(tables[0], DumpMode.StructureOnly, tables);

        Assert.False(picked);
        Assert.Empty(Directory.GetFiles(_directory, "*.sql"));
    }

    [Fact]
    public async Task SelectedTablesFromAnotherSchemaFollowTheSiblingsNamedWithTheirSchema()
    {
        var (vm, root) = Explorer();
        var tables = AddTables(root, "parent", "child");
        var schema = new SchemaNode { Name = "aux", RawName = "aux", Type = SchemaNodeType.Schema };
        var other = new SchemaNode { Name = "child", RawName = "child", Type = SchemaNodeType.Table, SchemaName = "aux" };
        schema.Children.Add(other);
        root.Children.Add(schema);
        DumpTablesViewModel? list = null;
        vm.Explorer.DumpPathPicker = new Picker(null);
        vm.Explorer.ShowDumpTables = dialog => { list = dialog; return Task.CompletedTask; };

        await vm.Explorer.DumpSqlAsync(tables[1], DumpMode.StructureOnly, [tables[1], other]);

        Assert.Equal(["parent", "child", "aux.child"], list!.Tables.Select(t => t.Name));
        Assert.Equal([false, true, true], list.Tables.Select(t => t.IsSelected));
    }

    [Fact]
    public async Task OneSelectedTableDumpsWithoutTheTableList()
    {
        var (vm, root) = Explorer();
        var tables = AddTables(root, "parent", "child");
        var picker = new Picker(Path.Combine(_directory, "one.sql"));
        vm.Explorer.DumpPathPicker = picker;
        vm.Explorer.ShowDumpTables = _ => throw new InvalidOperationException("The table list should not open for one table.");

        await vm.Explorer.DumpSqlAsync(tables[1], DumpMode.StructureOnly, [tables[1]]);

        Assert.Equal("main.child.sql", picker.SuggestedName);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void TableListDialogShowsTheTablesTheModeAndDumpsOnlyWithATickedTable()
    {
        var (vm, root) = Explorer();
        var session = vm.ActiveSessions[root];
        var model = new DumpTablesViewModel([new("parent", session.Objects.Bind(AddTables(root, "parent")[0], null), true)], DumpMode.StructureOnly);
        var dialog = new DbConnect.Views.DumpTablesDialog { DataContext = model };
        dialog.Show();
        try
        {
            var dump = dialog.FindControl<Button>("DumpTables")!;
            Assert.True(dump.IsDefault);
            Assert.True(dialog.FindControl<Button>("CancelDumpTables")!.IsCancel);
            Assert.Equal("Structure Only", dialog.FindControl<ComboBox>("DumpModeChoice")!.SelectedItem?.ToString());
            Assert.Contains("parent", dialog.GetVisualDescendants().OfType<CheckBox>().Select(c => (c.Content as TextBlock)?.Text));
            Assert.True(dump.IsEffectivelyEnabled);

            model.Tables[0].IsSelected = false;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(dump.IsEffectivelyEnabled);

            model.Tables[0].IsSelected = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            dump.Command!.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(model.Confirmed);
            Assert.False(dialog.IsVisible);
        }
        finally { dialog.Close(); }
    }

    [Fact]
    public async Task CancellingTheSaveDialogDumpsNothing()
    {
        var (vm, root) = Explorer();
        var shown = false;
        vm.Explorer.DumpPathPicker = new Picker(null);
        vm.Explorer.ShowDumpProgress = _ => { shown = true; return Task.CompletedTask; };

        await vm.Explorer.DumpSqlAsync(root, DumpMode.StructureOnly);

        Assert.False(shown);
        Assert.Empty(Directory.GetFiles(_directory, "*.sql"));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void ProgressDialogOffersCancelWhileRunningAndCloseAfterwards()
    {
        var operation = new ExportOperationViewModel { IsRunning = true, Message = "Dump SQL File (Structure Only) · table 1 of 2 · main.parent" };
        var dialog = new DbConnect.Views.DumpSqlFileDialog { DataContext = operation };
        dialog.Show();
        try
        {
            var cancel = dialog.FindControl<Avalonia.Controls.Button>("CancelDump")!;
            var close = dialog.FindControl<Avalonia.Controls.Button>("CloseDump")!;
            Assert.True(cancel.IsEffectivelyVisible);
            Assert.False(close.IsEffectivelyEnabled);
            Assert.Equal(operation.Message, dialog.FindControl<Avalonia.Controls.TextBlock>("DumpMessage")!.Text);

            operation.IsRunning = false;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(cancel.IsEffectivelyVisible);
            Assert.True(close.IsEffectivelyEnabled);
        }
        finally { dialog.Close(); }
    }

    private (MainWindowViewModel Vm, SchemaNode Root) Explorer(string rows = "")
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "db.sqlite"), Pooling = false }.ToString();
        using (var connection = new SqliteConnection(cs))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = Fixture + rows;
            cmd.ExecuteNonQuery();
        }
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var root = new SchemaNode { Name = "db", RawName = "db", Type = SchemaNodeType.Database };
        vm.ActiveSessions[root] = new DatabaseSession(new ConnectionInfo { Name = "db", Provider = "SQLite", ConnectionString = cs }, new SqliteProvider());
        return (vm, root);
    }

    private static List<SchemaNode> AddTables(SchemaNode root, params string[] names)
    {
        var nodes = names.Select(n => new SchemaNode { Name = n, RawName = n, Type = SchemaNodeType.Table, SchemaName = "main" }).ToList();
        foreach (var node in nodes) root.Children.Add(node);
        return nodes;
    }

    private sealed class CountingPicker(Action picked) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string suggestedFileName, ExportFileType fileType)
        {
            picked();
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class Picker(string? path) : IExportPathPicker
    {
        public string Path => path!;
        public string? SuggestedName { get; private set; }
        public Task<string?> PickSavePathAsync(string suggestedFileName, ExportFileType fileType)
        {
            SuggestedName = suggestedFileName;
            Assert.Equal("sql", fileType.Extension);
            return Task.FromResult(path);
        }
    }
}
