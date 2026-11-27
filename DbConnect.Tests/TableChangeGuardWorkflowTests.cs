using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// Guarded designer saves (issue 08) against real SQLite and DuckDB databases: a table changed by
/// someone else blocks the save, a failing step rolls the whole transaction back and keeps the
/// edits, and an open transaction in another tab blocks the save instead of being ended.
/// </summary>
public sealed class TableChangeGuardWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-guard-" + Guid.NewGuid().ToString("N"));

    public TableChangeGuardWorkflowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<(string Cs, SqliteProvider Provider)> SqliteAsync(string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "guard.db"), Pooling = false }.ToString();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, sql);
        return (cs, provider);
    }

    private static async Task<TableDesignerViewModel> OpenAsync(IDatabaseProvider provider, string cs, string table, string? schema = null)
    {
        var vm = new TableDesignerViewModel(table, new DatabaseSession(
                new ConnectionInfo { Name = "guard", Provider = provider.ProviderName, ConnectionString = cs }, provider),
            new SchemaNode { Name = table, RawName = table, Type = SchemaNodeType.Table },
            new DatabaseObjectIdentity(null, schema, table, SchemaNodeType.Table));
        await vm.LoadAsync();
        Assert.True(vm.Design.IsLoaded, vm.Design.Error ?? vm.StatusMessage);
        return vm;
    }

    private static async Task<string[]> NamesAsync(IDatabaseProvider provider, string cs, string sql) =>
        (await provider.ExecuteQueryAsync(cs, sql)).ObjectRows.Select(r => Convert.ToString(r[0])!).ToArray();

    [Fact]
    public async Task Sqlite_ATableChangedElsewhereBlocksSaveUntilReloaded_AndTheEditsStay()
    {
        var (cs, provider) = await SqliteAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, label TEXT);");
        var vm = await OpenAsync(provider, cs, "t");
        vm.Columns.Add(new ColumnDefinition { Name = "added", DataType = "INTEGER", IsNew = true });
        await provider.ExecuteNonQueryAsync(cs, "ALTER TABLE t ADD COLUMN elsewhere TEXT;");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("The table changed since it was loaded", vm.StatusMessage);
        Assert.Contains("elsewhere", vm.StatusMessage);
        Assert.True(vm.Review.RequiresReload);
        Assert.False(vm.PlanAllowsSave);
        Assert.Contains(vm.Columns, c => c.Name == "added");
        Assert.Equal(["id", "label", "elsewhere"], await NamesAsync(provider, cs, "SELECT name FROM pragma_table_info('t');"));

        await vm.ReloadCommand.ExecuteAsync(null);
        Assert.False(vm.Review.RequiresReload);
        Assert.Contains(vm.Columns, c => c.Name == "elsewhere");
        Assert.DoesNotContain(vm.Columns, c => c.Name == "added");
    }

    [Fact]
    public async Task Sqlite_AFailingStepRollsBackTheWholeSave_AndTheEditsCanBeFixedAndSaved()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (id INTEGER PRIMARY KEY, label TEXT);
            CREATE INDEX t_old ON t(label);
            INSERT INTO t VALUES (1, 'same'), (2, 'same');
            """);
        var vm = await OpenAsync(provider, cs, "t");
        vm.DropIndex("t_old");
        vm.AddIndex("t_unique", true, "label");
        // Dropping an index goes through review.
        Assert.Equal(["Drops index t_old on (label); queries that used it may become slower."], vm.Plan.Destructive);
        vm.Review.DestructiveConfirmed = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("❌ Save failed and nothing was applied", vm.StatusMessage);
        Assert.Equal(TableChangeOutcomeKind.NotApplied, vm.Review.Outcome!.Kind);
        Assert.Equal(["Rolled back", "Failed"], vm.Review.Steps.Select(s => s.Status));
        Assert.Contains("UNIQUE", vm.Review.Steps[1].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["t_old"], await NamesAsync(provider, cs, "SELECT name FROM sqlite_master WHERE type = 'index';"));
        // Nothing changed, so the edits stay and may be saved again once fixed.
        Assert.False(vm.Review.RequiresReload);
        Assert.Contains(vm.Indexes.Items, i => i.Name == "t_unique");

        await provider.ExecuteNonQueryAsync(cs, "UPDATE t SET label = 'other' WHERE id = 2;");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("successfully", vm.StatusMessage);
        Assert.Equal(["t_unique"], await NamesAsync(provider, cs, "SELECT name FROM sqlite_master WHERE type = 'index';"));
    }

    [Fact]
    public async Task Sqlite_AnOpenTransactionInAnotherTabBlocksSave_AndIsLeftAlone()
    {
        var (cs, provider) = await SqliteAsync("CREATE TABLE t (id INTEGER PRIMARY KEY);");
        var vm = await OpenAsync(provider, cs, "t");
        vm.OpenTransactions = () => ["Query 1"];
        vm.Columns.Add(new ColumnDefinition { Name = "added", DataType = "INTEGER", IsNew = true });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("Query 1 has an open transaction on this connection", vm.StatusMessage);
        Assert.Equal(["id"], await NamesAsync(provider, cs, "SELECT name FROM pragma_table_info('t');"));
        vm.OpenTransactions = () => [];
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("successfully", vm.StatusMessage);
    }

    [Fact]
    public async Task DuckDb_AFailingStepRollsBackTheAddedColumn()
    {
        var cs = $"Data Source={Path.Combine(_directory, "guard.duckdb")}";
        using (var connection = new DuckDBConnection(cs))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER, label VARCHAR); INSERT INTO t VALUES (1, 'same'), (2, 'same');";
            cmd.ExecuteNonQuery();
        }
        var provider = new DuckDbProvider();
        var vm = await OpenAsync(provider, cs, "t", "main");
        vm.Columns.Add(new ColumnDefinition { Name = "added", DataType = "INTEGER", IsNew = true });
        vm.AddIndex("t_unique", true, "label");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(TableChangeOutcomeKind.NotApplied, vm.Review.Outcome!.Kind);
        Assert.Equal(["Rolled back", "Failed"], vm.Review.Steps.Select(s => s.Status));
        Assert.Equal(["id", "label"], await NamesAsync(provider, cs,
            "SELECT column_name FROM duckdb_columns() WHERE table_name = 't' ORDER BY column_index;"));
        Assert.False(vm.Review.RequiresReload);
    }
}
