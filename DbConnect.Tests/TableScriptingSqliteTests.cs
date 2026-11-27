using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Script as CREATE recreates SQLite tables with every owned object (§1.6, issue 01).</summary>
public sealed class TableScriptingSqliteTests : MainWindowTestBase, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-script-" + Guid.NewGuid().ToString("N"));

    public TableScriptingSqliteTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private const string Fixture = """
        CREATE TABLE "parent.table" (id INTEGER PRIMARY KEY, code TEXT NOT NULL COLLATE NOCASE UNIQUE);
        CREATE TABLE "order items" (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            parent_id INTEGER REFERENCES "parent.table"(id) ON DELETE CASCADE,
            "Parent Code" TEXT COLLATE NOCASE,
            manager_id INTEGER REFERENCES "order items"(id),
            qty INTEGER NOT NULL DEFAULT 1 CHECK (qty > 0),
            price REAL NOT NULL DEFAULT 0.0,
            total REAL GENERATED ALWAYS AS (qty * price) STORED,
            note TEXT,
            CONSTRAINT uq_parent_code UNIQUE (parent_id, "Parent Code"),
            CONSTRAINT ck_note CHECK (note IS NULL OR length(note) < 50)
        );
        CREATE INDEX "ix.partial" ON "order items"(qty DESC) WHERE note IS NOT NULL;
        CREATE INDEX ix_expression ON "order items"(lower("Parent Code"), price);
        CREATE TABLE audit (item_id INTEGER, action TEXT);
        CREATE TRIGGER trg_items_insert AFTER INSERT ON "order items"
        BEGIN
            INSERT INTO audit (item_id, action) VALUES (new.id, 'insert');
        END;
        CREATE TABLE strict_items (k TEXT PRIMARY KEY, v INTEGER) STRICT, WITHOUT ROWID;
        """;

    private string NewDatabase(string name, string? sql = null)
    {
        var path = Path.Combine(_directory, name + ".db");
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        if (sql != null) Execute(cs, sql);
        return cs;
    }

    private static void Execute(string cs, string sql)
    {
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<(string Type, string Name, string? Sql)> Schema(string cs, string table)
    {
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE tbl_name = @t ORDER BY type, name";
        cmd.Parameters.AddWithValue("@t", table);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string, string?)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return rows;
    }

    private static Task<TableScriptResult> Script(string cs, string table, string? schema = "main") =>
        TableScripting.ScriptCreateAsync(
            new DatabaseSession(new ConnectionInfo { Name = "s", Provider = "SQLite", ConnectionString = cs }, new SqliteProvider()),
            new DatabaseObjectIdentity(null, schema, table, SchemaNodeType.Table));

    [Fact]
    public async Task ScriptedTable_RecreatesIdenticalDefinitionAndBehavior()
    {
        var source = NewDatabase("source", Fixture);
        var result = await Script(source, "order items");
        Assert.Null(result.Error);
        var sql = result.Sql!;
        Assert.Contains("Referenced table \"parent.table\"", sql);
        Assert.Contains("AUTOINCREMENT counter", sql);

        var target = NewDatabase("target", """
            CREATE TABLE "parent.table" (id INTEGER PRIMARY KEY, code TEXT NOT NULL COLLATE NOCASE UNIQUE);
            CREATE TABLE audit (item_id INTEGER, action TEXT);
            """);
        Execute(target, sql);

        Assert.Equal(Schema(source, "order items"), Schema(target, "order items"));

        Execute(target, "PRAGMA foreign_keys=ON; INSERT INTO \"parent.table\" VALUES (1, 'a');" +
                        "INSERT INTO \"order items\" (parent_id, \"Parent Code\", qty, price) VALUES (1, 'A', 2, 1.5);");
        using var connection = new SqliteConnection(target);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT total FROM \"order items\"; ";
        Assert.Equal(3.0, Convert.ToDouble(cmd.ExecuteScalar()));
        cmd.CommandText = "SELECT count(*) FROM audit WHERE action = 'insert'";
        Assert.Equal(1L, cmd.ExecuteScalar());
        cmd.CommandText = "INSERT INTO \"order items\" (qty) VALUES (0)";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        cmd.CommandText = "INSERT INTO \"order items\" (parent_id, \"Parent Code\") VALUES (1, 'a')";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public async Task ConstraintBackingIndexes_AreNotDuplicated_AndTableOptionsSurvive()
    {
        var source = NewDatabase("options", Fixture);
        var result = await Script(source, "strict_items");
        Assert.Null(result.Error);
        Assert.Contains("STRICT, WITHOUT ROWID", result.Sql);
        Assert.DoesNotContain("sqlite_autoindex", result.Sql);

        var parent = await Script(source, "parent.table");
        Assert.Null(parent.Error);
        Assert.DoesNotContain("CREATE UNIQUE INDEX", parent.Sql);
        var target = NewDatabase("options-target");
        Execute(target, parent.Sql!);
        Assert.Equal(Schema(source, "parent.table"), Schema(target, "parent.table"));
    }

    [Fact]
    public async Task SchemaThatTheConnectionCannotSee_ReportsErrorInsteadOfMisroutedScript()
    {
        var source = NewDatabase("unattached", "CREATE TABLE t (id INTEGER);");
        var result = await Script(source, "t", "aux");
        Assert.Null(result.Sql);
        Assert.Contains("t", result.Error);
    }

    [Fact]
    public async Task MissingTable_ReportsErrorInsteadOfScript()
    {
        var source = NewDatabase("missing", "CREATE TABLE x (id INTEGER);");
        var result = await Script(source, "nope");
        Assert.Null(result.Sql);
        Assert.Contains("nope", result.Error);
    }

    [Fact]
    public async Task ExplorerScriptAsCreate_OpensTabWithCompleteScript()
    {
        var cs = NewDatabase("explorer", Fixture);
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var root = new SchemaNode { Name = "db", Type = SchemaNodeType.Database };
        var table = new SchemaNode { Name = "order items", RawName = "order items", Type = SchemaNodeType.Table, SchemaName = "main" };
        root.Children.Add(table);
        vm.ActiveSessions[root] = new DatabaseSession(
            new ConnectionInfo { Name = "db", Provider = "SQLite", ConnectionString = cs }, new SqliteProvider());

        await vm.ScriptObjectAsCreate(table);

        var tab = Assert.IsType<QueryTabViewModel>(vm.SelectedTab);
        Assert.Contains("CREATE TRIGGER trg_items_insert", tab.QueryText);
        Assert.Contains("CREATE INDEX \"ix.partial\"", tab.QueryText);
        Assert.Contains("GENERATED ALWAYS AS (qty * price) STORED", tab.QueryText);
    }
    private (MainWindowViewModel Vm, SchemaNode Table, SchemaNode Audit) ExplorerWith(string cs)
    {
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var root = new SchemaNode { Name = "db", Type = SchemaNodeType.Database };
        var table = new SchemaNode { Name = "order items", RawName = "order items", Type = SchemaNodeType.Table, SchemaName = "main" };
        var audit = new SchemaNode { Name = "audit", RawName = "audit", Type = SchemaNodeType.Table, SchemaName = "main" };
        root.Children.Add(table);
        root.Children.Add(audit);
        vm.ActiveSessions[root] = new DatabaseSession(
            new ConnectionInfo { Name = "db", Provider = "SQLite", ConnectionString = cs }, new SqliteProvider());
        return (vm, table, audit);
    }

    [Fact]
    public async Task DmlTemplates_UseNamedParametersAndPrimaryKeyFilter()
    {
        var (vm, table, audit) = ExplorerWith(NewDatabase("dml", Fixture));

        await vm.Tools.ScriptObjectAsync(table, ScriptKind.Update, ScriptTarget.NewTab);
        var update = Assert.IsType<QueryTabViewModel>(vm.SelectedTab).QueryText;
        Assert.Contains("UPDATE \"main\".\"order items\"", update);
        Assert.Contains("\"Parent Code\" = @Parent_Code", update);
        Assert.Contains("WHERE \"id\" = @id", update);

        await vm.Tools.ScriptObjectAsync(table, ScriptKind.Insert, ScriptTarget.NewTab);
        var insert = Assert.IsType<QueryTabViewModel>(vm.SelectedTab).QueryText;
        Assert.Contains("INSERT INTO \"main\".\"order items\"", insert);
        Assert.Contains("@parent_id,", insert);

        await vm.Tools.ScriptObjectAsync(audit, ScriptKind.Delete, ScriptTarget.NewTab);
        var delete = Assert.IsType<QueryTabViewModel>(vm.SelectedTab).QueryText;
        Assert.StartsWith("DELETE FROM \"main\".\"audit\"", delete);
        Assert.EndsWith("WHERE <search_condition>;", delete);
    }

    [Fact]
    public async Task ScriptTargets_ClipboardAndFile_ReceiveTheScriptWithoutOpeningTabs()
    {
        var (vm, table, _) = ExplorerWith(NewDatabase("targets", Fixture));
        var tabs = vm.Tabs.Count;
        string? clipboard = null;
        vm.Tools.CopyToClipboardAsync = text => { clipboard = text; return Task.CompletedTask; };
        var path = Path.Combine(_directory, "out.sql");
        vm.Tools.PickScriptPathAsync = _ => Task.FromResult<string?>(path);

        await vm.Tools.ScriptObjectAsync(table, ScriptKind.Create, ScriptTarget.Clipboard);
        Assert.Contains("CREATE TRIGGER trg_items_insert", clipboard);

        await vm.Tools.ScriptObjectAsync(table, ScriptKind.Drop, ScriptTarget.File);
        Assert.Equal("DROP TABLE \"main\".\"order items\";", File.ReadAllText(path));
        Assert.Equal(tabs, vm.Tabs.Count);
    }
}
