using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// Issue 12: composite foreign keys through the designer's public load/edit/preview/save workflow
/// against real SQLite and DuckDB databases. Foreign keys load with their ordered pairs, qualified
/// target, actions and identity; the referenced table is chosen from the catalog; adding, re-creating,
/// renaming and dropping them rebuilds SQLite tables while keeping data, indexes and views and checking
/// every foreign key before committing; the reopened table enforces the saved keys and their actions.
/// </summary>
public sealed class ForeignKeyEditingWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-fks-" + Guid.NewGuid().ToString("N"));

    public ForeignKeyEditingWorkflowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<(string Cs, SqliteProvider Provider)> SqliteAsync(string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "fks.db"), Pooling = false, ForeignKeys = true }.ToString();
        var provider = new SqliteProvider();
        if (sql.Length > 0) await provider.ExecuteNonQueryAsync(cs, sql);
        return (cs, provider);
    }

    private static DatabaseSession Session(IDatabaseProvider provider, string cs) =>
        new(new ConnectionInfo { Name = "fks", Provider = provider.ProviderName, ConnectionString = cs }, provider);

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), $"Timed out waiting for {what}.");
    }

    private static async Task<TableDesignerViewModel> OpenAsync(IDatabaseProvider provider, string cs, string table, string? schema = null)
    {
        var vm = new TableDesignerViewModel(table, Session(provider, cs),
            new SchemaNode { Name = table, RawName = table, Type = SchemaNodeType.Table },
            new DatabaseObjectIdentity(null, schema, table, SchemaNodeType.Table));
        await vm.LoadAsync();
        Assert.True(vm.Design.IsLoaded, vm.Design.Error ?? vm.StatusMessage);
        Assert.Equal("", vm.PreviewSql);
        await WaitAsync(() => vm.ForeignKeys.TargetsState == MetadataLoadState.Loaded, "the referenced tables");
        return vm;
    }

    /// <summary>Adds a foreign key through the editor's commands: picks the target from the catalog, then the pairs.</summary>
    private static async Task<EditableForeignKey> AddAsync(TableDesignerViewModel vm, string? name, string target, params (string Column, string Referenced)[] pairs)
    {
        vm.ForeignKeys.AddForeignKeyCommand.Execute(null);
        var fk = vm.ForeignKeys.Selected!;
        fk.Name = name;
        vm.ForeignKeys.SelectedTarget = vm.ForeignKeys.Targets.Single(t => t.Identity.Name == target);
        fk.Pairs.Clear();
        foreach (var (column, referenced) in pairs) fk.Pairs.Add(new ForeignKeyPair { Column = column, ReferencedColumn = referenced });
        await WaitAsync(() => !vm.ValidationErrors.Any(e => e.Contains("still loading")), $"the columns of {target}");
        return fk;
    }

    private static EditableForeignKey Fk(TableDesignerViewModel vm, string label) =>
        vm.ForeignKeys.Items.Single(k => (k.Name ?? $"({string.Join(", ", k.Pairs.Select(p => p.Column))})") == label);

    private static async Task SaveAsync(TableDesignerViewModel vm)
    {
        Assert.True(vm.Plan.CanExecute, string.Join("\n", vm.ValidationErrors) + "\n" + vm.PreviewSql);
        vm.Review.DestructiveConfirmed = vm.Review.NeedsConfirmation;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage + "\n" + vm.PreviewSql);
        Assert.Equal("", vm.PreviewSql);
    }

    private static string[] ForeignKeys(TableDesign design) =>
        design.ForeignKeys.Select(k => $"{TableDesignText.Name(k.Name)} ({TableDesignText.Pairs(k)}) {k.ReferencedTable.Name} {k.OnDelete}/{k.OnUpdate}")
            .Order(StringComparer.Ordinal).ToArray();

    private static async Task<string[]> Rows(IDatabaseProvider provider, string cs, string sql) =>
        (await provider.ExecuteQueryAsync(cs, sql)).ObjectRows
            .Select(r => string.Join("|", r.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))).ToArray();

    private const string Fixture = """
        CREATE TABLE parent (a INTEGER NOT NULL, b INTEGER NOT NULL, label TEXT, PRIMARY KEY (a, b));
        CREATE TABLE other (id INTEGER PRIMARY KEY);
        CREATE TABLE child (id INTEGER PRIMARY KEY, pa INTEGER, pb INTEGER, manager_id INTEGER, note TEXT,
                            other_id INTEGER REFERENCES other (id),
                            CONSTRAINT fk_other FOREIGN KEY (manager_id) REFERENCES other (id) ON DELETE SET NULL);
        CREATE INDEX child_note ON child (note);
        CREATE VIEW child_v AS SELECT id, note FROM child;
        INSERT INTO parent VALUES (1, 10, 'p1'), (2, 20, 'p2');
        INSERT INTO other VALUES (1), (2);
        INSERT INTO child VALUES (1, 1, 10, 1, 'n1', 2), (2, 2, 20, 2, 'n2', NULL);
        """;

    [Fact]
    public async Task Sqlite_CompositeAndSelfReferencingKeysAreAdded_ActionsChanged_AndUnnamedKeysDropped()
    {
        var (cs, provider) = await SqliteAsync(Fixture);
        var vm = await OpenAsync(provider, cs, "child");
        Assert.Equal(["(unnamed) (other_id -> id) other NO ACTION/NO ACTION", "fk_other (manager_id -> id) other SET NULL/NO ACTION"],
            ForeignKeys(vm.Design.Baseline!));
        Assert.Contains(vm.ForeignKeys.Targets, t => t.Identity.Name == "parent");

        // A composite key to parent's ordered primary key, a self-reference, a changed action and a dropped unnamed key.
        await AddAsync(vm, "fk_parent", "parent", ("pa", "a"), ("pb", "b"));
        Fk(vm, "fk_parent").OnDelete = "CASCADE";
        vm.Columns.Add(new ColumnDefinition { Name = "boss_id", DataType = "INTEGER", IsNew = true });
        await AddAsync(vm, null, "child", ("boss_id", "id"));
        Fk(vm, "fk_other").OnDelete = "CASCADE";
        vm.ForeignKeys.DropForeignKeyCommand.Execute(Fk(vm, "(other_id)"));
        Assert.Contains("CONSTRAINT \"fk_parent\" FOREIGN KEY (\"pa\", \"pb\") REFERENCES \"parent\" (\"a\", \"b\") ON DELETE CASCADE ON UPDATE NO ACTION", vm.PreviewSql);
        Assert.Contains("FOREIGN KEY (\"boss_id\") REFERENCES \"child\" (\"id\")", vm.PreviewSql);
        Assert.Contains("CONSTRAINT \"fk_other\" FOREIGN KEY (\"manager_id\") REFERENCES \"other\" (\"id\") ON DELETE CASCADE", vm.PreviewSql);
        Assert.Contains("other_id INTEGER", vm.PreviewSql);
        Assert.DoesNotContain("other_id INTEGER REFERENCES", vm.PreviewSql);
        Assert.Equal(2, vm.Plan.Destructive.Count(d => d.Contains("Foreign key")));
        await SaveAsync(vm);

        var reopened = await OpenAsync(provider, cs, "child");
        Assert.Equal(["(unnamed) (boss_id -> id) child NO ACTION/NO ACTION", "fk_other (manager_id -> id) other CASCADE/NO ACTION",
            "fk_parent (pa -> a, pb -> b) parent CASCADE/NO ACTION"], ForeignKeys(reopened.Design.Baseline!));
        Assert.Contains(reopened.Indexes.Items, i => i.Name == "child_note");
        Assert.Equal(["1|n1", "2|n2"], await Rows(provider, cs, "SELECT id, note FROM child_v ORDER BY id;"));

        // Enforcement and the saved actions.
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO child (id, pa, pb) VALUES (3, 9, 9);"));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO child (id, boss_id) VALUES (3, 99);"));
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO child (id, boss_id, other_id) VALUES (3, 3, 99);"); // other_id is no longer checked
        await provider.ExecuteNonQueryAsync(cs, "DELETE FROM parent WHERE a = 2; DELETE FROM other WHERE id = 1;");
        Assert.Equal(["3"], await Rows(provider, cs, "SELECT id FROM child ORDER BY id;"));

        await reopened.SaveCommand.ExecuteAsync(null);
        Assert.Equal("No changes to save.", reopened.StatusMessage);
    }

    [Fact]
    public async Task Sqlite_AForeignKeyExistingRowsViolate_RollsTheRebuildBack()
    {
        var (cs, provider) = await SqliteAsync(Fixture + "INSERT INTO child (id, pa, pb) VALUES (9, 7, 70);");
        var vm = await OpenAsync(provider, cs, "child");
        var before = TableDesignText.Lines(vm.Design.Baseline!);
        await AddAsync(vm, "fk_parent", "parent", ("pa", "a"), ("pb", "b"));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.DoesNotContain("successfully", vm.StatusMessage);

        var reopened = await OpenAsync(provider, cs, "child");
        Assert.Equal(before, TableDesignText.Lines(reopened.Design.Baseline!));
        Assert.Equal(["1", "2", "9"], await Rows(provider, cs, "SELECT id FROM child_v ORDER BY id;"));
    }

    [Fact]
    public async Task Sqlite_InvalidForeignKeysAreListedAndNothingRuns()
    {
        var (cs, provider) = await SqliteAsync(Fixture);
        var vm = await OpenAsync(provider, cs, "child");
        var fk = await AddAsync(vm, "fk_bad", "parent", ("pa", "a"));
        Assert.Contains("Foreign key fk_bad: (a) is not a primary or unique key of main.parent.", vm.ValidationErrors);
        fk.Pairs[0].ReferencedColumn = "nope";
        Assert.Contains("Foreign key fk_bad references column \"nope\", which main.parent does not have.", vm.ValidationErrors);
        fk.Pairs[0] = new ForeignKeyPair { Column = "missing", ReferencedColumn = "a" };
        fk.Pairs.Add(new ForeignKeyPair { Column = "pb", ReferencedColumn = "b" });
        Assert.Contains("Foreign key fk_bad refers to column \"missing\", which the table does not have.", vm.ValidationErrors);
        fk.Pairs.Add(new ForeignKeyPair { Column = "pb" });
        Assert.Contains("Foreign key fk_bad has an incomplete column pair.", vm.ValidationErrors);
        fk.Pairs.RemoveAt(2);
        fk.Pairs[0].Column = "pa";
        vm.Columns.Single(c => c.Name == "pa").IsNullable = false;
        fk.OnDelete = "SET NULL";
        Assert.Contains("Foreign key fk_bad sets column \"pa\" to NULL, but it is Not Null.", vm.ValidationErrors);
        fk.OnDelete = "NO ACTION";
        vm.Columns.Single(c => c.Name == "pa").IsNullable = true;
        fk.Name = "fk_other";
        Assert.Contains("Duplicate foreign key constraint name: \"fk_other\".", vm.ValidationErrors);
        fk.Name = "fk_ok";
        Assert.Empty(vm.ValidationErrors);
        vm.ForeignKeys.DropForeignKeyCommand.Execute(fk);

        // A self-reference to a column set that is not a key of this table.
        await AddAsync(vm, "fk_self", "child", ("manager_id", "note"));
        Assert.Contains("Foreign key fk_self: (note) is not a primary or unique key of main.child.", vm.ValidationErrors);
        Assert.False(vm.PlanAllowsSave);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.DoesNotContain("successfully", vm.StatusMessage);
        Assert.Equal(2, (await provider.ExecuteQueryAsync(cs, "SELECT * FROM pragma_foreign_key_list('child');")).ObjectRows.Count);
    }

    [Fact]
    public async Task Sqlite_DroppingAKeyItsOwnForeignKeyUsesIsAllowedWhenThatForeignKeyGoesToo()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE node (id INTEGER NOT NULL, code TEXT NOT NULL, parent_code TEXT, PRIMARY KEY (id), CONSTRAINT uq_code UNIQUE (code),
                               CONSTRAINT fk_parent FOREIGN KEY (parent_code) REFERENCES node (code));
            INSERT INTO node VALUES (1, 'a', NULL), (2, 'b', 'a');
            """);
        var vm = await OpenAsync(provider, cs, "node");
        var unique = vm.Keys.Items.Single(k => k.Name == "uq_code");
        vm.Keys.DropKeyCommand.Execute(unique);
        Assert.Contains("Unique key uq_code is referenced by foreign key fk_parent on main.node; drop or change that foreign key first.", vm.Plan.Blocked);
        vm.ForeignKeys.DropForeignKeyCommand.Execute(Fk(vm, "fk_parent"));
        Assert.Empty(vm.Plan.Blocked);
        await SaveAsync(vm);
        var reopened = await OpenAsync(provider, cs, "node");
        Assert.Empty(reopened.Design.Baseline!.ForeignKeys);
        Assert.Empty(reopened.Design.Baseline!.UniqueKeys);
    }

    [Fact]
    public async Task DuckDb_NewTablesDeclareCompositeForeignKeys_AndChangesOnExistingTablesAreBlocked()
    {
        var cs = $"Data Source={Path.Combine(_directory, "fks.duckdb")}";
        using (var connection = new DuckDBConnection(cs))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE parent (a INTEGER NOT NULL, b INTEGER NOT NULL, PRIMARY KEY (a, b));
                CREATE TABLE linked (id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, FOREIGN KEY (a, b) REFERENCES parent (a, b));
                INSERT INTO parent VALUES (1, 10);
                """;
            cmd.ExecuteNonQuery();
        }
        var provider = new DuckDbProvider();
        var vm = new TableDesignerViewModel("made", Session(provider, cs));
        await WaitAsync(() => vm.ForeignKeys.TargetsState == MetadataLoadState.Loaded, "the referenced tables");
        vm.Columns.Add(new ColumnDefinition { Name = "id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true, IsNew = true });
        vm.Columns.Add(new ColumnDefinition { Name = "x", DataType = "INTEGER", IsNew = true });
        vm.Columns.Add(new ColumnDefinition { Name = "y", DataType = "INTEGER", IsNew = true });
        Assert.Equal(["NO ACTION"], vm.ForeignKeys.Actions);
        await AddAsync(vm, null, "parent", ("x", "a"), ("y", "b"));
        Assert.Contains("FOREIGN KEY (\"x\", \"y\") REFERENCES \"main\".\"parent\" (\"a\", \"b\")", vm.PreviewSql);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage + "\n" + vm.PreviewSql);
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO made VALUES (1, 1, 10);");
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO made VALUES (2, 9, 9);"));

        var existing = await OpenAsync(provider, cs, "linked");
        Assert.Equal("a -> a, b -> b", Assert.Single(existing.ForeignKeys.Items).PairsDisplay);
        existing.ForeignKeys.DropForeignKeyCommand.Execute(existing.ForeignKeys.Items[0]);
        Assert.Contains("Foreign key (a, b) cannot be changed: changing foreign keys after creation is Not supported by DuckDB.", existing.Plan.Blocked);
    }
}
