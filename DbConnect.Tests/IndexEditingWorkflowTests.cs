using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// Issue 14: index keys, their order and direction, and uniqueness through the designer's public
/// load/edit/preview/save workflow against real SQLite and DuckDB databases. Indexes load with their
/// identity and ordered key parts; independent indexes can be added, dropped, renamed and re-created,
/// keeping expressions and predicates; indexes backing keys stay read-only.
/// </summary>
public sealed class IndexEditingWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-indexes-" + Guid.NewGuid().ToString("N"));

    public IndexEditingWorkflowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<(string Cs, SqliteProvider Provider)> SqliteAsync(string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "indexes.db"), Pooling = false, ForeignKeys = true }.ToString();
        var provider = new SqliteProvider();
        if (sql.Length > 0) await provider.ExecuteNonQueryAsync(cs, sql);
        return (cs, provider);
    }

    private static DatabaseSession Session(IDatabaseProvider provider, string cs) =>
        new(new ConnectionInfo { Name = "indexes", Provider = provider.ProviderName, ConnectionString = cs }, provider);

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

    private static EditableIndex Index(TableDesignerViewModel vm, string name) => vm.Indexes.Items.Single(i => i.Name == name);

    private static string[] IndexLines(TableDesign design) =>
        TableDesignText.Lines(design).Where(l => l.StartsWith("index ")).ToArray();

    private static async Task<string[]> Rows(IDatabaseProvider provider, string cs, string sql) =>
        (await provider.ExecuteQueryAsync(cs, sql)).ObjectRows
            .Select(r => string.Join("|", r.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))).ToArray();

    [Fact]
    public async Task Sqlite_IndexesAreRenamedReorderedMadeUniqueDroppedAndAdded_KeepingExpressionsPredicatesAndTheTable()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (a INTEGER PRIMARY KEY, b TEXT, c TEXT, d TEXT UNIQUE);
            CREATE INDEX ix_a ON t (a);
            CREATE INDEX ix_bc ON t (b, c DESC);
            CREATE INDEX ix_expr ON t (lower(c)) WHERE a > 0;
            CREATE UNIQUE INDEX ix_u ON t (b);
            CREATE VIEW v AS SELECT a, b FROM t;
            CREATE TRIGGER trg AFTER UPDATE ON t BEGIN SELECT 1; END;
            INSERT INTO t VALUES (1, 'x', 'C1', 'd1'), (2, 'y', 'C2', 'd2');
            """);
        var vm = await OpenAsync(provider, cs, "t");
        Assert.Equal(["ix_a", "ix_bc", "ix_expr", "ix_u", "sqlite_autoindex_t_1"], vm.Indexes.Items.Select(i => i.Name));
        Assert.Equal(["b", "c DESC"], Index(vm, "ix_bc").Parts.Select(p => p.Display));
        Assert.Equal(["(lower(c))"], Index(vm, "ix_expr").Parts.Select(p => p.Display));
        var backing = Index(vm, "sqlite_autoindex_t_1");
        Assert.True(backing.IsReadOnly);
        Assert.Equal("Unique key", backing.BacksDisplay);

        // Rename one, re-order and flip another, make the expression index unique, drop the unique one, add one.
        Index(vm, "ix_a").Name = "ix_a2";
        var bc = Index(vm, "ix_bc");
        vm.Indexes.Selected = bc;
        vm.Indexes.MovePartDownCommand.Execute(bc.Parts[0]);
        bc.Parts[0].Descending = false;
        bc.Parts[1].Descending = true;
        Index(vm, "ix_expr").IsUnique = true;
        vm.Indexes.DropIndexCommand.Execute(Index(vm, "ix_u"));
        vm.Indexes.AddIndexCommand.Execute(null);
        var added = vm.Indexes.Selected!;
        added.Name = "ix_new";
        added.IsUnique = true;
        vm.Indexes.NewPart = "c";
        vm.Indexes.AddPartCommand.Execute(null);
        vm.Indexes.NewPart = "a";
        vm.Indexes.AddPartCommand.Execute(null);
        added.Parts[0].Descending = true;

        Assert.Contains("DROP INDEX \"ix_a\";", vm.PreviewSql);
        Assert.Contains("CREATE INDEX \"ix_a2\" ON \"t\" (\"a\");", vm.PreviewSql);
        Assert.Contains("CREATE INDEX \"ix_bc\" ON \"t\" (\"c\", \"b\" DESC);", vm.PreviewSql);
        Assert.Contains("CREATE UNIQUE INDEX \"ix_expr\" ON \"t\" ((lower(c))) WHERE a > 0;", vm.PreviewSql);
        Assert.Contains("DROP INDEX \"ix_u\";", vm.PreviewSql);
        Assert.Contains("CREATE UNIQUE INDEX \"ix_new\" ON \"t\" (\"c\" DESC, \"a\");", vm.PreviewSql);
        Assert.Equal(3, vm.Plan.Destructive.Count);
        Assert.Contains(vm.Plan.Destructive, d => d.StartsWith("Drops unique index ix_u;"));
        Assert.Contains(vm.Plan.Destructive, d => d.StartsWith("Re-creates index ix_bc"));
        Assert.Contains(vm.Plan.Destructive, d => d.StartsWith("Re-creates index ix_expr"));
        await SaveAsync(vm);

        var reopened = await OpenAsync(provider, cs, "t");
        Assert.Equal([
            "index ix_a2 CreateIndex (a)",
            "index ix_bc CreateIndex (c, b DESC)",
            "index ix_expr unique CreateIndex (((lower(c)))) where=a > 0",
            "index ix_new unique CreateIndex (c DESC, a)",
            "index sqlite_autoindex_t_1 unique UniqueKey (d)"
        ], IndexLines(reopened.Design.Baseline!));
        Assert.Equal(["trg"], reopened.Design.Triggers.Select(t => t.Name));
        Assert.Equal(["1|x", "2|y"], await Rows(provider, cs, "SELECT a, b FROM v ORDER BY a;"));
        // ix_u is gone, ix_new and ix_expr (where a > 0) are enforced.
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO t (a, b, c) VALUES (3, 'x', 'C3');");
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO t (a, b, c) VALUES (4, 'z', 'c1');"));
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO t (a, b, c) VALUES (-1, 'z', 'c1');");

        await reopened.SaveCommand.ExecuteAsync(null);
        Assert.Equal("No changes to save.", reopened.StatusMessage);
    }

    [Fact]
    public async Task Sqlite_IndexEditsRideAlongWithARebuild_AndAFailedUniqueIndexLeavesTheTableUnchanged()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (id INTEGER PRIMARY KEY, a TEXT, b TEXT, obsolete TEXT);
            CREATE INDEX ix_a ON t (a COLLATE NOCASE);
            CREATE INDEX ix_b ON t (b);
            CREATE INDEX ix_obsolete ON t (obsolete);
            INSERT INTO t VALUES (1, 'x', 'same', 'o'), (2, 'y', 'same', 'o');
            """);
        var vm = await OpenAsync(provider, cs, "t");

        // A column change rebuilds the table; the index edits go into the same rebuild.
        vm.DropColumnCommand.Execute(vm.Columns.Single(c => c.Name == "obsolete"));
        vm.Index("ix_b").Parts[0].Descending = true;
        vm.AddIndex("ix_ab", false, "a", "b");
        Assert.Contains("-- SQLite rebuild", vm.PreviewSql);
        Assert.Single(vm.Plan.Steps);
        Assert.Contains("CREATE INDEX \"ix_b\" ON \"t\" (\"b\" DESC);", vm.PreviewSql);
        Assert.Contains("CREATE INDEX \"ix_ab\" ON \"t\" (\"a\", \"b\");", vm.PreviewSql);
        Assert.Contains("CREATE INDEX ix_a ON t (a COLLATE NOCASE);", vm.PreviewSql); // unchanged: the engine's own text
        await SaveAsync(vm);
        var reopened = await OpenAsync(provider, cs, "t");
        Assert.Equal([
            "index ix_a CreateIndex (a COLLATE NOCASE)",
            "index ix_ab CreateIndex (a, b)",
            "index ix_b CreateIndex (b DESC)"
        ], IndexLines(reopened.Design.Baseline!));

        // A key part with its own collation cannot be restated, so re-creating that index is blocked.
        reopened.Index("ix_a").IsUnique = true;
        Assert.Contains("Index ix_a cannot be re-created without losing its key column settings.", reopened.Plan.Blocked);
        reopened.Index("ix_a").IsUnique = false;
        Assert.Equal("", reopened.PreviewSql);

        // Existing rows violate a unique ix_b: the save fails and the old index stays.
        reopened.Index("ix_b").IsUnique = true;
        Assert.Contains(reopened.Plan.Destructive, d => d.Contains("the save fails if existing rows violate it"));
        reopened.Review.DestructiveConfirmed = true;
        await reopened.SaveCommand.ExecuteAsync(null);
        Assert.DoesNotContain("successfully", reopened.StatusMessage);
        var after = await OpenAsync(provider, cs, "t");
        Assert.Equal(IndexLines(reopened.Design.Baseline!), IndexLines(after.Design.Baseline!));
    }

    [Fact]
    public async Task Sqlite_InvalidIndexesAreListed_AndAUniqueIndexAForeignKeyUsesCannotBeDropped()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE parent (id INTEGER PRIMARY KEY, code TEXT, other TEXT);
            CREATE UNIQUE INDEX ix_code ON parent (code);
            CREATE INDEX ix_other ON parent (other);
            CREATE TABLE child (id INTEGER PRIMARY KEY, code TEXT REFERENCES parent (code));
            """);
        var vm = await OpenAsync(provider, cs, "parent");

        vm.DropIndex("ix_code");
        Assert.Equal("Index ix_code is unique over the columns foreign key (unnamed) on main.child references; drop or change that foreign key first.",
            Assert.Single(vm.Plan.Blocked));
        vm.DropIndex("ix_code");
        vm.Index("ix_code").IsUnique = false;
        Assert.Contains(vm.Plan.Blocked, b => b.StartsWith("Index ix_code is unique over the columns"));
        vm.Index("ix_code").IsUnique = true;
        Assert.Equal("", vm.PreviewSql);

        vm.AddIndex("", false, "code");
        vm.AddIndex("IX_OTHER", false, "missing", "id", "id");
        vm.AddIndex("ix_same", false, "other");
        vm.AddIndex("ix_empty", true);
        vm.DropColumnCommand.Execute(vm.Columns.Single(c => c.Name == "other"));
        Assert.Contains("Index (code) has no name.", vm.ValidationErrors);
        Assert.Contains("Duplicate index name: \"ix_other\".", vm.ValidationErrors);
        Assert.Equal(["missing", "id"], vm.Index("IX_OTHER").Parts.Select(p => p.KeyText)); // a column is added once
        Assert.Contains("Index IX_OTHER refers to column \"missing\", which the table does not have.", vm.ValidationErrors);
        Assert.Contains("Index ix_same has the same definition as Index ix_other.", vm.ValidationErrors);
        Assert.Contains("Index ix_empty has no key columns.", vm.ValidationErrors);
        Assert.Contains("Index ix_same includes column \"other\", which is being dropped; remove it from the index or drop the index.",
            vm.ValidationErrors);
        // The loaded index over the dropped column goes with it, as before.
        Assert.DoesNotContain(vm.ValidationErrors, e => e.StartsWith("Index ix_other "));
        Assert.False(vm.PlanAllowsSave);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(["ix_code", "ix_other"], (await OpenAsync(provider, cs, "parent")).Indexes.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task DuckDb_IndexesAreAddedRenamedMadeUniqueAndDropped_AndDescendingKeysAreRejected()
    {
        var cs = $"Data Source={Path.Combine(_directory, "indexes.duckdb")}";
        using (var connection = new DuckDBConnection(cs))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE t (id INTEGER PRIMARY KEY, a VARCHAR, b VARCHAR);
                CREATE INDEX ix_a ON t (a);
                CREATE INDEX ix_b ON t (b);
                CREATE INDEX ix_expr ON t (lower(a));
                INSERT INTO t VALUES (1, 'x', 'p'), (2, 'y', 'q');
                """;
            cmd.ExecuteNonQuery();
        }
        var provider = new DuckDbProvider();
        var vm = await OpenAsync(provider, cs, "t");
        Assert.Equal(["ix_a", "ix_b", "ix_expr"], vm.Indexes.Items.Select(i => i.Name));

        vm.Index("ix_b").Parts[0].Descending = true;
        Assert.Contains("Index ix_b has a descending key; DuckDB indexes have no descending keys.", vm.ValidationErrors);
        vm.Index("ix_b").Parts[0].Descending = false;

        // DuckDB fails natively when an index is dropped and re-created under the same name in one transaction.
        vm.Index("ix_expr").IsUnique = true;
        Assert.Equal("Index ix_expr would be dropped and created under the same name in one save, which DuckDB cannot do; " +
                     "give it a new name, or drop it in one save and add it in the next.", Assert.Single(vm.Plan.Blocked));
        vm.Index("ix_expr").Name = "ix_lower";
        vm.Index("ix_a").Name = "ix_a2";
        vm.DropIndex("ix_b");
        vm.AddIndex("ix_ab", true, "a", "b");
        Assert.Equal("""
            DROP INDEX "main"."ix_b";
            DROP INDEX "main"."ix_expr";
            DROP INDEX "main"."ix_a";
            CREATE INDEX "ix_a2" ON "main"."t" ("a");
            CREATE UNIQUE INDEX "ix_lower" ON "main"."t" ((lower(a)));
            CREATE UNIQUE INDEX "ix_ab" ON "main"."t" ("a", "b");
            """.ReplaceLineEndings().TrimEnd(), vm.PreviewSql);
        await SaveAsync(vm);

        var reopened = await OpenAsync(provider, cs, "t");
        Assert.Equal([
            "index ix_a2 CreateIndex (a)",
            "index ix_ab unique CreateIndex (a, b)",
            "index ix_lower unique CreateIndex (((lower(a))))"
        ], IndexLines(reopened.Design.Baseline!));
        Assert.Equal(["1|x|p", "2|y|q"], await Rows(provider, cs, "SELECT id, a, b FROM t ORDER BY id;"));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO t VALUES (3, 'X', 'r');"));
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO t VALUES (4, 'z', 'p');");
        await reopened.SaveCommand.ExecuteAsync(null);
        Assert.Equal("No changes to save.", reopened.StatusMessage);
    }
}
