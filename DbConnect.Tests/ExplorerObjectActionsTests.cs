using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Explorer table actions: Empty, Truncate, Rename, Duplicate, Copy DDL and Export Data (§1.7).</summary>
public sealed class ExplorerObjectActionsTests : MainWindowTestBase, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-explorer-" + Guid.NewGuid().ToString("N"));

    public ExplorerObjectActionsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private const string Fixture = """
        CREATE TABLE "order items" (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            qty INTEGER NOT NULL DEFAULT 1 CHECK (qty > 0),
            total INTEGER GENERATED ALWAYS AS (qty * 2) STORED
        );
        CREATE INDEX "ix.qty" ON "order items"(qty DESC) WHERE qty > 1;
        CREATE TABLE audit (n TEXT);
        CREATE TRIGGER trg AFTER INSERT ON "order items" BEGIN INSERT INTO audit VALUES (new.name); END;
        INSERT INTO "order items" (name, qty) VALUES ('a', 1), ('b', 2), ('c', 3);
        """;

    private string Sqlite(string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"), Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return cs;
    }

    private static object? Scalar(string cs, string sql)
    {
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private (MainWindowViewModel Vm, SchemaNode Root) Explorer(string cs, IDatabaseProvider provider, string? schema, params string[] tables)
    {
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var root = new SchemaNode { Name = "db", Type = SchemaNodeType.Database };
        foreach (var table in tables)
            root.Children.Add(new SchemaNode { Name = table, RawName = table, Type = SchemaNodeType.Table, SchemaName = schema });
        vm.ActiveSessions[root] = new DatabaseSession(
            new ConnectionInfo { Name = "db", Provider = provider.ProviderName, ConnectionString = cs }, provider);
        return (vm, root);
    }

    [Fact]
    public async Task Sqlite_TruncateIsUnavailable_EmptyDeletesEveryRow()
    {
        var cs = Sqlite(Fixture);
        var (vm, root) = Explorer(cs, new SqliteProvider(), "main", "order items");
        var table = root.Children[0];

        var truncate = vm.Explorer.Availability(table, ExplorerObjectAction.Truncate);
        Assert.True(truncate.IsVisible);
        Assert.False(truncate.IsEnabled);
        Assert.Contains("Empty Table", truncate.DisabledReason);

        DestructiveActionPreview? shown = null;
        vm.ShowDestructiveActionConfirmDialog = preview => { shown = preview; return Task.FromResult(true); };
        await vm.Explorer.EmptyAsync(table);
        Assert.Equal("DELETE FROM \"main\".\"order items\"", shown!.Sql);
        Assert.Equal(0L, Scalar(cs, "SELECT count(*) FROM \"order items\""));
    }

    [Fact]
    public async Task DecliningTheConfirmation_RunsNothing()
    {
        var cs = Sqlite(Fixture);
        var (vm, root) = Explorer(cs, new SqliteProvider(), "main", "order items");
        vm.ShowDestructiveActionConfirmDialog = _ => Task.FromResult(false);
        vm.Explorer.PromptForName = (_, _, _) => Task.FromResult<string?>("renamed");

        await vm.Explorer.EmptyAsync(root.Children[0]);
        await vm.Explorer.RenameAsync(root.Children[0]);

        Assert.Equal(3L, Scalar(cs, "SELECT count(*) FROM \"order items\""));
        Assert.Equal(0L, Scalar(cs, "SELECT count(*) FROM sqlite_master WHERE name = 'renamed'"));
    }

    [Fact]
    public async Task Sqlite_Rename_MovesTheTableAndViewsCannotBeRenamed()
    {
        var cs = Sqlite(Fixture + "CREATE VIEW v AS SELECT * FROM audit;");
        var (vm, root) = Explorer(cs, new SqliteProvider(), "main", "order items");
        root.Children.Add(new SchemaNode { Name = "v", RawName = "v", Type = SchemaNodeType.View, SchemaName = "main" });
        vm.Explorer.PromptForName = (_, _, initial) => Task.FromResult<string?>(initial + ".renamed");

        Assert.False(vm.Explorer.Availability(root.Children[1], ExplorerObjectAction.Rename).IsEnabled);
        await vm.Explorer.RenameAsync(root.Children[0]);

        Assert.Equal(0L, Scalar(cs, "SELECT count(*) FROM sqlite_master WHERE name = 'order items'"));
        Assert.Equal(3L, Scalar(cs, "SELECT count(*) FROM \"order items.renamed\""));
    }

    [Fact]
    public async Task Sqlite_Duplicate_CopiesStructureIndexesAndData()
    {
        var cs = Sqlite(Fixture);
        var (vm, root) = Explorer(cs, new SqliteProvider(), "main", "order items", "order items_copy1");

        await vm.Explorer.DuplicateAsync(root.Children[0], includeData: true);
        Assert.False(vm.StatusHasError, vm.StatusMessage);
        Assert.Contains("order items_copy2", vm.StatusMessage);
        Assert.Contains("Triggers", vm.StatusMessage);

        Assert.Equal(3L, Scalar(cs, "SELECT count(*) FROM \"order items_copy2\""));
        Assert.Equal(12L, Scalar(cs, "SELECT sum(total) FROM \"order items_copy2\""));
        Assert.Equal(1L, Scalar(cs, "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'order items_copy2' AND sql LIKE '%WHERE qty > 1%'"));
        var original = (string)Scalar(cs, "SELECT sql FROM sqlite_master WHERE name = 'order items'")!;
        var copy = (string)Scalar(cs, "SELECT sql FROM sqlite_master WHERE name = 'order items_copy2'")!;
        Assert.Equal(original.Replace("\"order items\"", "\"order items_copy2\""), copy);

        await vm.Explorer.DuplicateAsync(root.Children[0], includeData: false);
        Assert.Equal(0L, Scalar(cs, "SELECT count(*) FROM \"order items_copy2\" WHERE 0")); // exists
    }

    [Fact]
    public async Task DuckDb_DuplicateAndTruncate()
    {
        var cs = $"Data Source={Path.Combine(_directory, "duck.duckdb")}";
        await using (var connection = new DuckDBConnection(cs))
        {
            connection.Open();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE SCHEMA "s.x";
                CREATE TABLE "s.x"."T 1" (id INTEGER PRIMARY KEY, a INTEGER NOT NULL CHECK (a > 0), g INTEGER GENERATED ALWAYS AS (a * 2) VIRTUAL);
                CREATE INDEX ix_a ON "s.x"."T 1" (a);
                INSERT INTO "s.x"."T 1" (id, a) VALUES (1, 5), (2, 6);
                """;
            cmd.ExecuteNonQuery();
        }
        var (vm, root) = Explorer(cs, new DuckDbProvider(), "s.x", "T 1");

        await vm.Explorer.DuplicateAsync(root.Children[0], includeData: true);
        Assert.False(vm.StatusHasError, vm.StatusMessage);
        await vm.Explorer.TruncateAsync(root.Children[0]);
        Assert.False(vm.StatusHasError, vm.StatusMessage);

        await using var check = new DuckDBConnection(cs);
        check.Open();
        await using var query = check.CreateCommand();
        query.CommandText = """SELECT (SELECT sum(g)::BIGINT FROM "s.x"."T 1_copy1"), (SELECT count(*) FROM "s.x"."T 1"), (SELECT count(*) FROM duckdb_indexes() WHERE table_name = 'T 1_copy1')""";
        await using var reader = query.ExecuteReader();
        reader.Read();
        Assert.Equal(22L, Convert.ToInt64(reader.GetValue(0)));
        Assert.Equal(0L, Convert.ToInt64(reader.GetValue(1)));
        Assert.Equal(1L, Convert.ToInt64(reader.GetValue(2)));
    }

    [Fact]
    public async Task CopyDdl_PutsTheCompleteScriptOnTheClipboard()
    {
        var cs = Sqlite(Fixture);
        var (vm, root) = Explorer(cs, new SqliteProvider(), "main", "order items");
        string? clipboard = null;
        vm.Tools.CopyToClipboardAsync = text => { clipboard = text; return Task.CompletedTask; };

        await vm.Explorer.CopyDdlAsync(root.Children[0]);

        Assert.Contains("CREATE TRIGGER trg", clipboard);
        Assert.Contains("CREATE INDEX \"ix.qty\"", clipboard);
    }

    [Fact]
    public async Task ExportData_StreamsEveryRowOfEachTableAndReportsFailuresPerTable()
    {
        var rows = string.Join(",", Enumerable.Range(1, 12_345).Select(i => $"('n{i}', {i % 7 + 1})"));
        var cs = Sqlite(Fixture + $"INSERT INTO \"order items\" (name, qty) VALUES {rows};");
        var (vm, root) = Explorer(cs, new SqliteProvider(), "main", "order items", "audit", "missing");
        ExportDataViewModel? dialog = null;
        vm.Explorer.ShowExportDialog = export => { dialog = export; return Task.CompletedTask; };

        await vm.Explorer.ExportDataAsync(root.Children[0]);
        Assert.NotNull(dialog);
        Assert.Equal(["order items"], dialog!.Tables.Where(t => t.IsSelected).Select(t => t.Name));
        dialog.SelectAllCommand.Execute(null);
        dialog.Folder = _directory;
        await dialog.ExportCommand.ExecuteAsync(null);

        var file = Path.Combine(_directory, "main.order items.csv");
        Assert.True(File.Exists(file), string.Join("; ", dialog.Tables.Select(t => t.Name + ": " + t.Result)) + " " + dialog.Operation.Message);
        Assert.Equal(12_348 + 1, File.ReadAllLines(file).Length);
        Assert.True(File.Exists(Path.Combine(_directory, "main.audit.csv")));
        Assert.StartsWith("Failed", dialog.Tables.Single(t => t.Name == "missing").Result);
        Assert.Equal("Exported 2 of 3 tables; 1 failed or canceled.", dialog.Summary);
    }
}
