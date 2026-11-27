using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// Issue 11: primary and unique keys through the designer's public load/edit/preview/save workflow
/// against real SQLite and DuckDB databases. Keys load with their ordered members and identity
/// (named or not); adding, dropping, renaming and re-ordering them rebuilds SQLite tables while keeping
/// data, indexes, triggers and dependent views; the reopened table enforces the saved keys.
/// </summary>
public sealed class KeyEditingWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-keys-" + Guid.NewGuid().ToString("N"));

    public KeyEditingWorkflowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<(string Cs, SqliteProvider Provider)> SqliteAsync(string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "keys.db"), Pooling = false, ForeignKeys = true }.ToString();
        var provider = new SqliteProvider();
        if (sql.Length > 0) await provider.ExecuteNonQueryAsync(cs, sql);
        return (cs, provider);
    }

    private static DatabaseSession Session(IDatabaseProvider provider, string cs) =>
        new(new ConnectionInfo { Name = "keys", Provider = provider.ProviderName, ConnectionString = cs }, provider);

    private static async Task<TableDesignerViewModel> OpenAsync(IDatabaseProvider provider, string cs, string table, string? schema = null)
    {
        var vm = new TableDesignerViewModel(table, Session(provider, cs),
            new SchemaNode { Name = table, RawName = table, Type = SchemaNodeType.Table },
            new DatabaseObjectIdentity(null, schema, table, SchemaNodeType.Table));
        await vm.LoadAsync();
        Assert.True(vm.Design.IsLoaded, vm.Design.Error ?? vm.StatusMessage);
        Assert.Equal("", vm.PreviewSql);
        return vm;
    }

    private static async Task SaveAsync(TableDesignerViewModel vm)
    {
        Assert.True(vm.Plan.CanExecute, string.Join("\n", vm.ValidationErrors) + "\n" + vm.PreviewSql);
        vm.Review.DestructiveConfirmed = vm.Review.NeedsConfirmation;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage + "\n" + vm.PreviewSql);
        Assert.Equal("", vm.PreviewSql);
    }

    private static KeyDefinition Key(TableDesignerViewModel vm, string label) =>
        vm.Keys.Items.Single(k => $"{k.KindDisplay} {k.Name}".Trim() == label || $"{k.KindDisplay} ({k.ColumnsDisplay})" == label);

    private static string[] Keys(TableDesign design) =>
        [.. design.PrimaryKey is { } pk ? [$"primary {TableDesignText.Name(pk.Name)} ({string.Join(", ", pk.Columns)})"] : Array.Empty<string>(),
         .. design.UniqueKeys.Select(k => $"unique {TableDesignText.Name(k.Name)} ({string.Join(", ", k.Columns)})")];

    private static async Task<string[]> Rows(IDatabaseProvider provider, string cs, string sql) =>
        (await provider.ExecuteQueryAsync(cs, sql)).ObjectRows
            .Select(r => string.Join("|", r.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))).ToArray();

    private static async Task AssertRejectedAsync(IDatabaseProvider provider, string cs, string sql) =>
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, sql));

    [Fact]
    public async Task Sqlite_KeysAreReorderedRenamedAddedAndDropped_KeepingDataIndexesTriggersAndViews()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (a INTEGER NOT NULL, b TEXT NOT NULL, c TEXT, d TEXT CONSTRAINT uq_d UNIQUE, e TEXT UNIQUE,
                            PRIMARY KEY (a, b), CONSTRAINT uq_c UNIQUE (c));
            CREATE INDEX t_b ON t(b);
            CREATE VIEW v AS SELECT a, b, c FROM t;
            CREATE TRIGGER trg AFTER UPDATE ON t BEGIN SELECT 1; END;
            INSERT INTO t VALUES (1, 'x', 'c1', 'd1', 'e1'), (2, 'y', 'c2', 'd2', 'e2');
            """);
        var vm = await OpenAsync(provider, cs, "t");
        Assert.Equal(["primary (unnamed) (a, b)", "unique uq_d (d)", "unique (unnamed) (e)", "unique uq_c (c)"], Keys(vm.Design.Baseline!));
        Assert.Equal(["Primary key", "Unique uq_d", "Unique", "Unique uq_c"], vm.Keys.Items.Select(k => $"{k.KindDisplay} {k.Name}".Trim()));
        Assert.True(vm.Columns.Single(c => c.Name == "a").IsPrimaryKey);

        // Re-order the primary key, rename a table-level and a column-level key, drop the unnamed one, add a composite key.
        var pk = Key(vm, "Primary key");
        pk.Columns.Move(1, 0);
        Key(vm, "Unique uq_c").Name = "uq_c2";
        Key(vm, "Unique uq_d").Name = "uq_d2";
        vm.Keys.DropKeyCommand.Execute(Key(vm, "Unique (e)"));
        vm.Keys.AddUniqueKeyCommand.Execute(null);
        vm.Keys.NewMember = "b";
        vm.Keys.AddMemberCommand.Execute(null);
        vm.Keys.NewMember = "c";
        vm.Keys.AddMemberCommand.Execute(null);
        vm.Keys.Selected!.Name = "uq_bc";
        Assert.Contains("d TEXT CONSTRAINT \"uq_d2\" UNIQUE", vm.PreviewSql);
        Assert.Contains("PRIMARY KEY (\"b\", \"a\")", vm.PreviewSql);
        Assert.Contains("CONSTRAINT \"uq_c2\" UNIQUE (\"c\")", vm.PreviewSql);
        Assert.Contains("CONSTRAINT \"uq_bc\" UNIQUE (\"b\", \"c\")", vm.PreviewSql);
        Assert.Equal(2, vm.Plan.Destructive.Count);
        Assert.Contains(vm.Plan.Destructive, d => d.StartsWith("Re-creates Primary key (a, b) on (b, a) instead of (a, b)"));
        Assert.Contains(vm.Plan.Destructive, d => d.StartsWith("Drops Unique key (e)"));
        await SaveAsync(vm);

        var reopened = await OpenAsync(provider, cs, "t");
        Assert.Equal(["primary (unnamed) (b, a)", "unique uq_d2 (d)", "unique uq_c2 (c)", "unique uq_bc (b, c)"], Keys(reopened.Design.Baseline!));
        Assert.Equal(["trg"], reopened.Design.Triggers.Select(t => t.Name));
        Assert.Contains(reopened.Indexes.Items, i => i.Name == "t_b");
        Assert.Equal(["1|x|c1", "2|y|c2"], await Rows(provider, cs, "SELECT a, b, c FROM v ORDER BY a;"));
        await AssertRejectedAsync(provider, cs, "INSERT INTO t (a, b, c) VALUES (1, 'x', 'new');");
        await AssertRejectedAsync(provider, cs, "INSERT INTO t (a, b, c) VALUES (9, 'x', 'c1');");
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO t (a, b, e) VALUES (9, 'z', 'e1');"); // e is no longer unique

        // Saving the reopened table unchanged does nothing.
        await reopened.SaveCommand.ExecuteAsync(null);
        Assert.Equal("No changes to save.", reopened.StatusMessage);
    }

    [Fact]
    public async Task Sqlite_AFailedKeyRebuildRollsBackAndKeepsTheTable()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (id INTEGER PRIMARY KEY, code TEXT);
            CREATE VIEW v AS SELECT id, code FROM t;
            INSERT INTO t VALUES (1, 'same'), (2, 'same');
            """);
        var vm = await OpenAsync(provider, cs, "t");
        var before = TableDesignText.Lines(vm.Design.Baseline!);
        vm.Keys.AddUniqueKeyCommand.Execute(null);
        vm.Keys.NewMember = "code";
        vm.Keys.AddMemberCommand.Execute(null);
        Assert.True(vm.Plan.CanExecute, string.Join("\n", vm.ValidationErrors));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.DoesNotContain("successfully", vm.StatusMessage);

        var reopened = await OpenAsync(provider, cs, "t");
        Assert.Equal(before, TableDesignText.Lines(reopened.Design.Baseline!));
        Assert.Equal(["1|same", "2|same"], await Rows(provider, cs, "SELECT id, code FROM v ORDER BY id;"));
    }

    [Fact]
    public async Task Sqlite_UnsafeOrInvalidKeyEditsAreListedAndNothingRuns()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE parent (id INTEGER PRIMARY KEY AUTOINCREMENT, code TEXT, other TEXT);
            CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent (id));
            INSERT INTO parent (code) VALUES ('a');
            """);
        var vm = await OpenAsync(provider, cs, "parent");
        var pk = Key(vm, "Primary key");

        // The child's foreign key depends on the primary key; dropping it is blocked before anything runs.
        vm.Keys.DropKeyCommand.Execute(pk);
        Assert.Contains("Primary key (id) is referenced by foreign key (unnamed) on main.child; drop or change that foreign key first.", vm.Plan.Blocked);
        Assert.Contains("Column \"id\": SQLite gives Auto increment only to a column typed INTEGER that is the whole primary key.", vm.ValidationErrors);
        vm.Keys.DropKeyCommand.Execute(pk);
        Assert.Equal("", vm.PreviewSql);

        vm.Keys.AddPrimaryKeyCommand.Execute(null);
        Assert.Same(pk, vm.Keys.Selected);   // there is one primary key; the button selects it
        vm.Keys.AddUniqueKeyCommand.Execute(null);
        Assert.Contains("Unique key () has no columns.", vm.ValidationErrors);
        vm.Keys.NewMember = "nope";
        vm.Keys.AddMemberCommand.Execute(null);
        Assert.Contains("Unique key (nope) refers to column \"nope\", which the table does not have.", vm.ValidationErrors);
        vm.Keys.RemoveMemberCommand.Execute("nope");
        vm.Keys.NewMember = "id";
        vm.Keys.AddMemberCommand.Execute(null);
        Assert.Contains("Unique key (id) has the same columns as Primary key (id).", vm.ValidationErrors);
        vm.Keys.Selected!.Name = "other";
        vm.AddIndex("other", false, "code");
        Assert.Contains("The name \"other\" is used by more than one key, constraint or index.", vm.ValidationErrors);
        vm.Keys.DropKeyCommand.Execute(vm.Keys.Selected);
        vm.DropIndex("other");

        // A dropped column must leave its key first.
        vm.Keys.AddUniqueKeyCommand.Execute(null);
        vm.Keys.NewMember = "other";
        vm.Keys.AddMemberCommand.Execute(null);
        vm.DropColumnCommand.Execute(vm.Columns.Single(c => c.Name == "other"));
        Assert.Contains("Unique key (other) includes column \"other\", which is being dropped; remove it from the key or drop the key.", vm.ValidationErrors);
        Assert.False(vm.PlanAllowsSave);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(["1|a|"], await Rows(provider, cs, "SELECT id, code, other FROM parent;"));
    }

    [Fact]
    public async Task Sqlite_RenamingAnAutoincrementPrimaryKeyKeepsItOnItsColumn()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (id INTEGER PRIMARY KEY AUTOINCREMENT, label TEXT);
            INSERT INTO t (label) VALUES ('a'), ('b');
            DELETE FROM t WHERE id = 2;
            """);
        var vm = await OpenAsync(provider, cs, "t");
        Key(vm, "Primary key").Name = "pk_t";
        Assert.Contains("id INTEGER CONSTRAINT \"pk_t\" PRIMARY KEY AUTOINCREMENT", vm.PreviewSql);
        Assert.Empty(vm.Plan.Destructive);
        await SaveAsync(vm);

        var reopened = await OpenAsync(provider, cs, "t");
        Assert.Equal(["primary pk_t (id)"], Keys(reopened.Design.Baseline!));
        Assert.Equal(TableDesignIdentityKind.AutoIncrement, reopened.Columns.Single(c => c.Name == "id").IdentityKind);
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO t (label) VALUES ('c');");
        Assert.Equal(["1|a", "3|c"], await Rows(provider, cs, "SELECT id, label FROM t ORDER BY id;"));
    }

    [Fact]
    public async Task NewTables_DeclareOrderedCompositeAndNamedKeys_OnSqliteAndDuckDb()
    {
        var (sqliteCs, sqlite) = await SqliteAsync("");
        var duckCs = $"Data Source={Path.Combine(_directory, "keys.duckdb")}";
        foreach (var (provider, cs) in new (IDatabaseProvider, string)[] { (sqlite, sqliteCs), (new DuckDbProvider(), duckCs) })
        {
            var vm = new TableDesignerViewModel("made", Session(provider, cs));
            vm.Columns.Add(new ColumnDefinition { Name = "a", DataType = "INTEGER", IsNullable = false, IsNew = true });
            vm.Columns.Add(new ColumnDefinition { Name = "b", DataType = "INTEGER", IsNullable = false, IsNew = true });
            vm.Columns.Add(new ColumnDefinition { Name = "c", DataType = "VARCHAR", IsNew = true });
            // The PK checkboxes build the key in the order they are ticked: b, then a.
            vm.Columns[1].IsPrimaryKey = true;
            vm.Columns[0].IsPrimaryKey = true;
            vm.Keys.AddUniqueKeyCommand.Execute(null);
            vm.Keys.NewMember = "c";
            vm.Keys.AddMemberCommand.Execute(null);
            vm.Keys.Selected!.Name = "uq_c";
            Assert.Contains("PRIMARY KEY (\"b\", \"a\")", vm.PreviewSql);
            Assert.Contains("CONSTRAINT \"uq_c\" UNIQUE (\"c\")", vm.PreviewSql);
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage + "\n" + vm.PreviewSql);

            var reopened = await OpenAsync(provider, cs, "made");
            Assert.Equal(provider is SqliteProvider ? ["primary (unnamed) (b, a)", "unique uq_c (c)"] : ["primary (unnamed) (b, a)", "unique (unnamed) (c)"],
                Keys(reopened.Design.Baseline!));
            await provider.ExecuteNonQueryAsync(cs, "INSERT INTO made VALUES (1, 2, 'x');");
            await AssertRejectedAsync(provider, cs, "INSERT INTO made VALUES (1, 2, 'y');");
            await AssertRejectedAsync(provider, cs, "INSERT INTO made VALUES (3, 4, 'x');");
        }
    }

    [Fact]
    public async Task DuckDb_KeyChangesOnAnExistingTableAreBlockedWithTheReason()
    {
        var cs = $"Data Source={Path.Combine(_directory, "blocked.duckdb")}";
        using (var connection = new DuckDBConnection(cs))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, code VARCHAR UNIQUE, note VARCHAR);";
            cmd.ExecuteNonQuery();
        }
        var vm = await OpenAsync(new DuckDbProvider(), cs, "t");
        vm.Keys.DropKeyCommand.Execute(Key(vm, "Unique (code)"));
        Assert.Contains("Unique key (code) cannot be changed: changing keys after creation is Not supported by DuckDB.", vm.Plan.Blocked);
        Assert.Equal("Keys of this table cannot be changed after creation: Not supported by DuckDB.", vm.Keys.Summary);
        Assert.False(vm.PlanAllowsSave);
    }
}
