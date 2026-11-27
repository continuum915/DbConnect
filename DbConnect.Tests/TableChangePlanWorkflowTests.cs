using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// The designer's shared preview/save plan (issue 07) against real SQLite and DuckDB databases:
/// an unchanged table plans nothing, the preview follows every edit, Save runs exactly the
/// previewed SQL, changes the engine cannot make are blocked instead of reported as saved, and
/// the plan names the table by its schema.
/// </summary>
public sealed class TableChangePlanWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-plan-" + Guid.NewGuid().ToString("N"));

    public TableChangePlanWorkflowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    /// <summary>Records the SQL the designer runs, so a test can compare it with the preview.</summary>
    private sealed class RecordingSqliteProvider : SqliteProvider, IDatabaseQueryProvider
    {
        public List<string> Executed { get; } = [];

        // Sessions call the provider through its interface, so re-implementing it records what the designer runs.
        Task<int> IDatabaseQueryProvider.ExecuteNonQueryAsync(string connectionString, string sql)
        {
            Executed.Add(sql);
            return ExecuteNonQueryAsync(connectionString, sql);
        }
    }

    private async Task<(string Cs, RecordingSqliteProvider Provider)> SqliteAsync(string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "plan.db"), Pooling = false }.ToString();
        var provider = new RecordingSqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, sql);
        provider.Executed.Clear();
        return (cs, provider);
    }

    private static async Task<TableDesignerViewModel> OpenAsync(IDatabaseProvider provider, string cs, string table, string? schema = null)
    {
        var vm = new TableDesignerViewModel(table, new DatabaseSession(
                new ConnectionInfo { Name = "plan", Provider = provider.ProviderName, ConnectionString = cs }, provider),
            new SchemaNode { Name = table, RawName = table, Type = SchemaNodeType.Table },
            new DatabaseObjectIdentity(null, schema, table, SchemaNodeType.Table));
        await vm.LoadAsync();
        Assert.True(vm.Design.IsLoaded, vm.Design.Error ?? vm.StatusMessage);
        return vm;
    }

    private static async Task<string> StoredSqlAsync(IDatabaseProvider provider, string cs, string name) =>
        (string)(await provider.ExecuteQueryAsync(cs, $"SELECT sql FROM sqlite_master WHERE name = '{name}';")).ObjectRows.Single()[0]!;

    [Fact]
    public async Task Sqlite_UnchangedTablePlansNothing_AndEditingBackRestoresTheNoOp()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (id INTEGER PRIMARY KEY, label TEXT DEFAULT 'x' NOT NULL, note TEXT);
            CREATE INDEX t_label ON t(label DESC) WHERE note IS NOT NULL;
            """);
        var before = await StoredSqlAsync(provider, cs, "t");
        var vm = await OpenAsync(provider, cs, "t");
        Assert.Equal("", vm.PreviewSql);
        Assert.False(vm.Plan.HasChanges);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("No changes to save.", vm.StatusMessage);
        Assert.Empty(provider.Executed);

        var label = vm.Columns.Single(c => c.Name == "label");
        label.DataType = "VARCHAR(10)";
        Assert.StartsWith(SqliteRebuildExecution.Marker, vm.PreviewSql);
        label.DataType = "TEXT";
        Assert.Equal("", vm.PreviewSql);
        label.DefaultValue = "";
        Assert.NotEqual("", vm.PreviewSql);
        label.DefaultValue = "'x'";
        label.IsNullable = true;
        label.IsNullable = false;
        Assert.Equal("", vm.PreviewSql);
        Assert.Equal(before, await StoredSqlAsync(provider, cs, "t"));
    }

    [Fact]
    public async Task Sqlite_SaveRunsThePreviewAndReopensUnchanged()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (id INTEGER PRIMARY KEY, label TEXT, obsolete INTEGER);
            CREATE INDEX t_label ON t(lower(label));
            INSERT INTO t VALUES (1, 'Ada', 7);
            """);
        var vm = await OpenAsync(provider, cs, "t");
        vm.Columns.Single(c => c.Name == "obsolete").IsDropped = true;
        vm.Columns.Add(new ColumnDefinition { Name = "added", DataType = "INTEGER", IsNew = true });
        vm.AddIndex("t_added", false, "added");
        var preview = vm.PreviewSql;
        Assert.Equal(["Drops column obsolete and deletes its data. Objects that depend on it are removed; the preview lists them."], vm.Plan.Destructive);
        vm.Review.DestructiveConfirmed = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("successfully", vm.StatusMessage);
        Assert.Equal([preview], provider.Executed);
        Assert.Equal("", vm.PreviewSql);
        var indexes = await provider.ExecuteQueryAsync(cs, "SELECT name, sql FROM sqlite_master WHERE type = 'index' ORDER BY name;");
        Assert.Equal(["t_added", "t_label"], indexes.ObjectRows.Select(r => (string)r[0]!));
        Assert.Contains("lower(label)", (string)indexes.ObjectRows[1][1]!);
        var row = Assert.Single((await provider.ExecuteQueryAsync(cs, "SELECT id, label FROM t;")).ObjectRows);
        Assert.Equal("Ada", row[1]);
    }

    [Fact]
    public async Task Sqlite_IndexOnlyChangesRunWithoutARebuild()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (id INTEGER PRIMARY KEY, label TEXT);
            CREATE INDEX t_old ON t(label);
            """);
        var vm = await OpenAsync(provider, cs, "t");
        vm.DropIndex("t_old");
        vm.AddIndex("t_new", true, "label", "id");

        Assert.Equal("""
            DROP INDEX "t_old";
            CREATE UNIQUE INDEX "t_new" ON "t" ("label", "id");
            """.ReplaceLineEndings().TrimEnd(), vm.PreviewSql);
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("successfully", vm.StatusMessage);
        var indexes = await provider.ExecuteQueryAsync(cs, "SELECT name FROM sqlite_master WHERE type = 'index';");
        Assert.Equal("t_new", Assert.Single(indexes.ObjectRows)[0]);
    }

    /// <summary>
    /// A check on an existing SQLite table used to append a warning comment and report success without changing
    /// anything. Since issue 13 it is made by the rebuild (CheckEditingWorkflowTests saves and enforces one).
    /// </summary>
    [Fact]
    public async Task Sqlite_ChecksOnAnExistingTableArePlannedAsARebuild_NotAWarningComment()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE parent (id INTEGER PRIMARY KEY);
            CREATE TABLE t (id INTEGER PRIMARY KEY, parent_id INTEGER);
            """);
        var vm = await OpenAsync(provider, cs, "t");
        vm.Checks.AddCheckCommand.Execute(null);
        vm.Checks.Selected!.Name = "ck_parent";
        vm.Checks.Selected!.Expression = "parent_id > 0";

        Assert.Empty(vm.Plan.Blocked);
        Assert.Contains("CONSTRAINT \"ck_parent\" CHECK (parent_id > 0)", vm.PreviewSql);
        Assert.Contains("-- SQLite rebuild", vm.PreviewSql);
        Assert.DoesNotContain("does not support", vm.PreviewSql);
        Assert.Empty(provider.Executed);
    }

    [Fact]
    public async Task Sqlite_TablesOutsideMainAreBlockedWithTheReason()
    {
        var (cs, provider) = await SqliteAsync("CREATE TABLE t (id INTEGER PRIMARY KEY);");
        var plan = TableChangePlanner.Plan(new TableDesignerEdits("SQLite", ProviderCapabilityDeclarations.Sqlite, "t",
            [new ColumnDefinition { Name = "added", IsNew = true }], [], [], [], null)
        {
            IsExisting = true,
            OpenedName = "t",
            Baseline = new TableDesign(new DatabaseObjectIdentity(null, "aux", "t", SchemaNodeType.Table), "SQLite")
        });
        Assert.Equal("", plan.Sql);
        Assert.Contains("only in the main database; this table is in aux", Assert.Single(plan.Blocked));
        Assert.Empty(provider.Executed);
    }

    [Fact]
    public async Task Sqlite_InvalidEditsAreErrorsAndNothingRuns()
    {
        var (cs, provider) = await SqliteAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, label TEXT);");
        var vm = await OpenAsync(provider, cs, "t");
        vm.Columns.Single(c => c.Name == "label").DefaultValue = "'a'; DROP TABLE t";
        vm.AddIndex("t_missing", false, "missing");
        vm.TableName = "renamed";

        Assert.Contains("The default of column \"label\" contains a statement separator (;).", vm.ValidationErrors);
        Assert.Contains("Index t_missing refers to column \"missing\", which the table does not have.", vm.ValidationErrors);
        Assert.Contains(vm.ValidationErrors, e => e.StartsWith("Renaming the table is not available in the designer"));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("⚠️", vm.StatusMessage);
        Assert.Empty(provider.Executed);
    }

    [Fact]
    public async Task DuckDb_PlanNamesTheTableByItsSchema()
    {
        var cs = $"Data Source={Path.Combine(_directory, "plan.duckdb")}";
        using (var connection = new DuckDBConnection(cs))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE SCHEMA "s.x";
                CREATE TABLE "s.x"."Order.Items" (id INTEGER PRIMARY KEY, label VARCHAR, obsolete INTEGER);
                CREATE INDEX "ix.label" ON "s.x"."Order.Items" (label);
                CREATE TABLE main."Order.Items" (id INTEGER, label VARCHAR, obsolete INTEGER);
                CREATE INDEX "ix.label" ON main."Order.Items" (label);
                INSERT INTO "s.x"."Order.Items" VALUES (1, 'Ada', 2);
                """;
            cmd.ExecuteNonQuery();
        }
        var provider = new DuckDbProvider();
        var vm = await OpenAsync(provider, cs, "Order.Items", "s.x");
        vm.Columns.Add(new ColumnDefinition { Name = "added", DataType = "INTEGER", IsNew = true });
        vm.Columns.Single(c => c.Name == "obsolete").IsDropped = true;
        vm.Columns.Single(c => c.Name == "label").DefaultValue = "'none'";
        vm.DropIndex("ix.label");

        Assert.Equal("""
            DROP INDEX "s.x"."ix.label";
            ALTER TABLE "s.x"."Order.Items" DROP COLUMN "obsolete";
            ALTER TABLE "s.x"."Order.Items" ALTER COLUMN "label" SET DEFAULT 'none';
            ALTER TABLE "s.x"."Order.Items" ADD "added" INTEGER;
            """.ReplaceLineEndings().TrimEnd(), vm.PreviewSql);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("confirm", vm.StatusMessage);
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("successfully", vm.StatusMessage);
        Assert.Equal("", vm.PreviewSql);

        var moved = await provider.ExecuteQueryAsync(cs, """
            SELECT schema_name, string_agg(column_name, ',' ORDER BY column_index) FROM duckdb_columns()
            WHERE table_name = 'Order.Items' GROUP BY schema_name ORDER BY schema_name;
            """);
        Assert.Equal(["main|id,label,obsolete", "s.x|id,label,added"], moved.ObjectRows.Select(r => $"{r[0]}|{r[1]}"));
        var indexes = await provider.ExecuteQueryAsync(cs, "SELECT schema_name FROM duckdb_indexes() WHERE index_name = 'ix.label';");
        Assert.Equal("main", Assert.Single(indexes.ObjectRows)[0]);

        vm.Checks.AddCheckCommand.Execute(null);
        vm.Checks.Selected!.Name = "ck";
        vm.Checks.Selected!.Expression = "id > 0";
        Assert.Contains(vm.Plan.Blocked, b => b.Contains("Not supported by DuckDB"));
    }
}
