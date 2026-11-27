using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// Issue 10: identity and generated columns through the designer's public load/edit/preview/save
/// workflow against real SQLite and DuckDB databases. Loaded settings appear in their own fields,
/// edits are validated by the engine's rules, changes the engine cannot make are blocked before
/// anything runs, Save runs the previewed plan, and the reopened table shows what was saved.
/// </summary>
public sealed class IdentityGenerationWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-generation-" + Guid.NewGuid().ToString("N"));

    public IdentityGenerationWorkflowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<(string Cs, SqliteProvider Provider)> SqliteAsync(string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "generation.db"), Pooling = false }.ToString();
        var provider = new SqliteProvider();
        if (sql.Length > 0) await provider.ExecuteNonQueryAsync(cs, sql);
        return (cs, provider);
    }

    private string DuckDb(string sql)
    {
        var cs = $"Data Source={Path.Combine(_directory, "generation.duckdb")}";
        using var connection = new DuckDBConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return cs;
    }

    private static DatabaseSession Session(IDatabaseProvider provider, string cs) =>
        new(new ConnectionInfo { Name = "generation", Provider = provider.ProviderName, ConnectionString = cs }, provider);

    private static async Task<TableDesignerViewModel> OpenAsync(IDatabaseProvider provider, string cs, string table, string? schema = null)
    {
        var vm = new TableDesignerViewModel(table, Session(provider, cs),
            new SchemaNode { Name = table, RawName = table, Type = SchemaNodeType.Table },
            new DatabaseObjectIdentity(null, schema, table, SchemaNodeType.Table));
        await vm.LoadAsync();
        Assert.True(vm.Design.IsLoaded, vm.Design.Error ?? vm.StatusMessage);
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

    private static async Task<string[]> Rows(IDatabaseProvider provider, string cs, string sql) =>
        (await provider.ExecuteQueryAsync(cs, sql)).ObjectRows
            .Select(r => string.Join("|", r.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))).ToArray();

    [Fact]
    public async Task Sqlite_IdentityAndGenerationEditsRebuild_KeepingDataAndTheAutoincrementSequence()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (id INTEGER PRIMARY KEY, a INT NOT NULL DEFAULT 1 CHECK (a > 0),
                            twice INT GENERATED ALWAYS AS (a * 2) VIRTUAL, total INT AS (a + 100) STORED, note TEXT);
            CREATE INDEX t_note ON t(note);
            CREATE TRIGGER t_touch AFTER UPDATE ON t BEGIN SELECT 1; END;
            INSERT INTO t (id, a, note) VALUES (1, 2, 'x'), (7, 3, 'y');
            """);
        var vm = await OpenAsync(provider, cs, "t");
        var id = vm.Columns.Single(c => c.Name == "id");
        var twice = vm.Columns.Single(c => c.Name == "twice");
        var total = vm.Columns.Single(c => c.Name == "total");
        Assert.Equal(TableDesignIdentityKind.RowIdAlias, id.IdentityKind);
        Assert.Equal(("a * 2", TableDesignGeneratedStorage.Virtual), (twice.GeneratedExpression, twice.GeneratedStorage));
        Assert.Equal(("a + 100", TableDesignGeneratedStorage.Stored), (total.GeneratedExpression, total.GeneratedStorage));
        Assert.Equal("", vm.PreviewSql);

        id.IdentityLabel = "Auto increment";
        twice.GeneratedExpression = "a * 3";
        twice.GeneratedStorageLabel = "Stored";
        total.GeneratedExpression = null;      // keeps its computed values as ordinary data
        Assert.Contains("id INTEGER PRIMARY KEY AUTOINCREMENT", vm.PreviewSql);
        Assert.Contains("twice INT GENERATED ALWAYS AS (a * 3) STORED", vm.PreviewSql);
        Assert.DoesNotContain("a + 100", vm.PreviewSql);
        Assert.Contains(vm.Plan.Destructive, d => d.StartsWith("Changes the expression of twice to (a * 3)"));
        Assert.False(vm.PlanAllowsSave);
        await SaveAsync(vm);

        var reopened = await OpenAsync(provider, cs, "t");
        Assert.Equal(TableDesignIdentityKind.AutoIncrement, reopened.Columns.Single(c => c.Name == "id").IdentityKind);
        twice = reopened.Columns.Single(c => c.Name == "twice");
        Assert.Equal(("a * 3", TableDesignGeneratedStorage.Stored), (twice.GeneratedExpression, twice.GeneratedStorage));
        Assert.Null(reopened.Columns.Single(c => c.Name == "total").GeneratedExpression);
        Assert.Equal(["t_touch"], reopened.Design.Triggers.Select(t => t.Name));
        Assert.Contains(reopened.Indexes.Items, i => i.Name == "t_note");
        Assert.Single(reopened.Design.Baseline!.Checks);
        Assert.Equal(["1|2|6|102|x", "7|3|9|103|y"], await Rows(provider, cs, "SELECT id, a, twice, total, note FROM t ORDER BY id;"));

        // AUTOINCREMENT never reuses a deleted maximum, and a later rebuild keeps that sequence.
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO t (a) VALUES (5); DELETE FROM t WHERE id = 8;");
        reopened.Columns.Single(c => c.Name == "twice").GeneratedExpression = "a * 4";
        await SaveAsync(reopened);
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO t (a, note) VALUES (1, 'new');");
        Assert.Equal(["1|8", "7|12", "9|4"], await Rows(provider, cs, "SELECT id, twice FROM t ORDER BY id;"));

        // Removing AUTOINCREMENT leaves a rowid alias.
        var again = await OpenAsync(provider, cs, "t");
        again.Columns.Single(c => c.Name == "id").IdentityLabel = "";
        Assert.DoesNotContain("AUTOINCREMENT", again.PreviewSql);
        await SaveAsync(again);
        Assert.Equal(TableDesignIdentityKind.RowIdAlias, (await OpenAsync(provider, cs, "t")).Columns.Single(c => c.Name == "id").IdentityKind);
    }

    [Fact]
    public async Task Sqlite_NewTableWithAutoincrementAndGeneratedColumnsReopensWithThem()
    {
        var (cs, provider) = await SqliteAsync("");
        var vm = new TableDesignerViewModel("made", Session(provider, cs));
        vm.Columns.Add(new ColumnDefinition { Name = "id", DataType = "INTEGER", IsPrimaryKey = true, IsNew = true, IdentityLabel = "Auto increment" });
        vm.Columns.Add(new ColumnDefinition { Name = "a", DataType = "INT", IsNew = true });
        vm.Columns.Add(new ColumnDefinition { Name = "v", DataType = "INT", IsNew = true, GeneratedExpression = "a + 1" });
        vm.Columns.Add(new ColumnDefinition { Name = "s", DataType = "INT", IsNew = true, GeneratedExpression = "a * 10", GeneratedStorageLabel = "Stored" });
        Assert.Contains("\"id\" INTEGER PRIMARY KEY AUTOINCREMENT", vm.PreviewSql);
        Assert.Contains("\"v\" INT GENERATED ALWAYS AS (a + 1) VIRTUAL", vm.PreviewSql);
        Assert.Contains("\"s\" INT GENERATED ALWAYS AS (a * 10) STORED", vm.PreviewSql);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage + "\n" + vm.PreviewSql);

        var reopened = await OpenAsync(provider, cs, "made");
        Assert.Equal(TableDesignIdentityKind.AutoIncrement, reopened.Columns.Single(c => c.Name == "id").IdentityKind);
        var v = reopened.Columns.Single(c => c.Name == "v");
        Assert.Equal(("a + 1", TableDesignGeneratedStorage.Virtual), (v.GeneratedExpression, v.GeneratedStorage));
        Assert.Equal(TableDesignGeneratedStorage.Stored, reopened.Columns.Single(c => c.Name == "s").GeneratedStorage);
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO made (a) VALUES (4);");
        Assert.Equal(["1|5|40"], await Rows(provider, cs, "SELECT id, v, s FROM made;"));
    }

    [Fact]
    public async Task Sqlite_InvalidIdentityAndGenerationSettingsAreListedAndNothingRuns()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (id INTEGER PRIMARY KEY, a INT DEFAULT 1, label TEXT, g INT AS (a + 1));
            INSERT INTO t (id, a, label) VALUES (1, 2, 'x');
            """);
        var vm = await OpenAsync(provider, cs, "t");
        vm.Columns.Single(c => c.Name == "a").IdentityLabel = "Auto increment";   // has a default, is not the INTEGER PRIMARY KEY
        vm.Columns.Single(c => c.Name == "label").GeneratedExpression = "upper(a); DROP TABLE t";
        vm.Columns.Single(c => c.Name == "g").IdentitySeed = "5";                 // no identity, and SQLite has no seed
        Assert.Contains("Column \"a\" cannot have both an identity and a default.", vm.ValidationErrors);
        Assert.Contains("Column \"a\": SQLite gives Auto increment only to a column typed INTEGER that is the whole primary key.", vm.ValidationErrors);
        Assert.Contains("The generation expression of column \"label\" contains a statement separator (;).", vm.ValidationErrors);
        Assert.Contains("Column \"g\" has an identity seed but no identity.", vm.ValidationErrors);
        Assert.False(vm.PlanAllowsSave);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.DoesNotContain("successfully", vm.StatusMessage);
        Assert.Equal(["1|2|x|3"], await Rows(provider, cs, "SELECT id, a, label, g FROM t;"));
    }

    [Fact]
    public async Task DuckDb_GeneratedColumnsAreCreatedVirtual_IdentityIsUnavailable_AndExistingGenerationIsBlocked()
    {
        var cs = DuckDb("""
            CREATE SCHEMA "s.x";
            CREATE SEQUENCE "s.x".ids START 10;
            CREATE TABLE "s.x".t (id INTEGER PRIMARY KEY DEFAULT nextval('"s.x".ids'), a INTEGER,
                                  twice INTEGER GENERATED ALWAYS AS (a * 2) VIRTUAL, note VARCHAR);
            INSERT INTO "s.x".t (a, note) VALUES (4, 'n');
            """);
        var provider = new DuckDbProvider();
        var vm = await OpenAsync(provider, cs, "t", "s.x");
        Assert.False(vm.IdentityEditable);
        Assert.False(vm.SeedAndIncrementEditable);
        Assert.Equal(["", "Virtual"], vm.GeneratedStorageOptions);
        Assert.Contains("DuckDB has no identity columns", vm.GenerationSummary);
        var twice = vm.Columns.Single(c => c.Name == "twice");
        var loadedExpression = twice.GeneratedExpression;
        Assert.Contains("a * 2", loadedExpression);
        Assert.Equal(TableDesignGeneratedStorage.Virtual, twice.GeneratedStorage);

        twice.GeneratedExpression = "a * 3";
        Assert.Contains("Column twice: DuckDB cannot add or change generated columns on an existing table.", vm.ValidationErrors);
        Assert.False(vm.PlanAllowsSave);
        twice.GeneratedExpression = loadedExpression;
        Assert.Equal("", vm.PreviewSql);
        vm.Columns.Add(new ColumnDefinition { Name = "thrice", DataType = "INTEGER", IsNew = true, GeneratedExpression = "a * 3" });
        Assert.Contains("Column thrice: DuckDB cannot add or change generated columns on an existing table.", vm.ValidationErrors);
        vm.Columns.RemoveAt(vm.Columns.Count - 1);

        // An unrelated edit keeps the generated column and the external sequence default.
        vm.Columns.Single(c => c.Name == "note").DefaultValue = "'d'";
        await SaveAsync(vm);
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO \"s.x\".t (a) VALUES (5);");
        Assert.Equal(["10|8", "11|10"], await Rows(provider, cs, "SELECT id, twice FROM \"s.x\".t ORDER BY id;"));

        var created = new TableDesignerViewModel("made", Session(provider, cs));
        created.Columns.Add(new ColumnDefinition { Name = "a", DataType = "INTEGER", IsNew = true });
        created.Columns.Add(new ColumnDefinition { Name = "id", DataType = "INTEGER", IsNew = true, IdentityLabel = "Identity", IsNullable = false });
        Assert.Contains("Column \"id\": DuckDB has no identity columns; use a sequence with DEFAULT nextval('sequence').", created.ValidationErrors);
        created.Columns.RemoveAt(1);
        created.Columns.Add(new ColumnDefinition { Name = "s", DataType = "INTEGER", IsNew = true, GeneratedExpression = "a + 1", GeneratedStorageLabel = "Stored" });
        Assert.Contains("Column \"s\": DuckDB has no STORED generated columns.", created.ValidationErrors);
        created.Columns.Last().GeneratedStorageLabel = "";
        Assert.Contains("\"s\" INTEGER GENERATED ALWAYS AS (a + 1) VIRTUAL", created.PreviewSql);
        await created.SaveCommand.ExecuteAsync(null);
        Assert.True(created.StatusMessage?.Contains("successfully") == true, created.StatusMessage + "\n" + created.PreviewSql);
        var s = (await OpenAsync(provider, cs, "made")).Columns.Single(c => c.Name == "s");
        Assert.Equal(TableDesignGeneratedStorage.Virtual, s.GeneratedStorage);
        Assert.Contains("a + 1", s.GeneratedExpression);
    }
}
