using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// Issue 13: check constraints through the designer's public load/edit/preview/save workflow against real
/// SQLite and DuckDB databases. Loaded checks keep their identity (named or not, on a column or the table);
/// changing, renaming, dropping and adding them rebuilds SQLite tables while keeping data, indexes, triggers
/// and views; the reopened table enforces the saved checks; a rebuild existing rows violate is rolled back.
/// </summary>
public sealed class CheckEditingWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-checks-" + Guid.NewGuid().ToString("N"));

    public CheckEditingWorkflowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<(string Cs, SqliteProvider Provider)> SqliteAsync(string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "checks.db"), Pooling = false }.ToString();
        var provider = new SqliteProvider();
        if (sql.Length > 0) await provider.ExecuteNonQueryAsync(cs, sql);
        return (cs, provider);
    }

    private static DatabaseSession Session(IDatabaseProvider provider, string cs) =>
        new(new ConnectionInfo { Name = "checks", Provider = provider.ProviderName, ConnectionString = cs }, provider);

    private static async Task<TableDesignerViewModel> OpenAsync(IDatabaseProvider provider, string cs, string table)
    {
        var vm = new TableDesignerViewModel(table, Session(provider, cs),
            new SchemaNode { Name = table, RawName = table, Type = SchemaNodeType.Table },
            new DatabaseObjectIdentity(null, null, table, SchemaNodeType.Table));
        await vm.LoadAsync();
        Assert.True(vm.Design.IsLoaded, vm.Design.Error ?? vm.StatusMessage);
        Assert.Equal("", vm.PreviewSql);
        return vm;
    }

    private static EditableCheck Check(TableDesignerViewModel vm, string nameOrExpression) =>
        vm.Checks.Items.Single(c => c.Name == nameOrExpression || c.Name == null && c.Expression == nameOrExpression);

    private static EditableCheck Add(TableDesignerViewModel vm, string? name, string expression)
    {
        vm.Checks.AddCheckCommand.Execute(null);
        var check = vm.Checks.Selected!;
        check.Name = name;
        check.Expression = expression;
        return check;
    }

    private static async Task SaveAsync(TableDesignerViewModel vm)
    {
        Assert.True(vm.Plan.CanExecute, string.Join("\n", vm.ValidationErrors) + "\n" + vm.PreviewSql);
        vm.Review.DestructiveConfirmed = vm.Review.NeedsConfirmation;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage + "\n" + vm.PreviewSql);
        Assert.Equal("", vm.PreviewSql);
    }

    private static string[] Checks(TableDesign design) =>
        design.Checks.Select(c => $"{TableDesignText.Name(c.Name)}: {c.Expression}{(c.Column == null ? "" : $" on {c.Column}")}")
            .Order(StringComparer.Ordinal).ToArray();

    private static async Task<string[]> Rows(IDatabaseProvider provider, string cs, string sql) =>
        (await provider.ExecuteQueryAsync(cs, sql)).ObjectRows
            .Select(r => string.Join("|", r.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))).ToArray();

    private const string Fixture = """
        CREATE TABLE t (id INTEGER PRIMARY KEY, qty INT CHECK (qty >= 0), price REAL CONSTRAINT ck_price CHECK (price > 0), note TEXT,
                        CONSTRAINT ck_cap CHECK (qty < 1000), CHECK (length(note) < 50));
        CREATE INDEX t_note ON t (note);
        CREATE VIEW v AS SELECT id, note FROM t;
        CREATE TRIGGER trg AFTER UPDATE ON t BEGIN SELECT 1; END;
        INSERT INTO t VALUES (1, 5, 2.5, 'a');
        """;

    [Fact]
    public async Task Sqlite_ChecksAreChangedRenamedDroppedAndAdded_KeepingDataIndexesTriggersAndViews()
    {
        var (cs, provider) = await SqliteAsync(Fixture);
        var vm = await OpenAsync(provider, cs, "t");
        Assert.Equal(["(unnamed): length(note) < 50", "(unnamed): qty >= 0 on qty", "ck_cap: qty < 1000", "ck_price: price > 0 on price"],
            Checks(vm.Design.Baseline!));

        Check(vm, "qty >= 0").Expression = "qty >= 1";
        Check(vm, "ck_price").Name = "ck_price2";
        vm.Checks.DropCheckCommand.Execute(Check(vm, "ck_cap"));
        Add(vm, "ck_note", "note <> 'it''s bad'");
        Assert.DoesNotContain("CHECK (qty >= 0)", vm.PreviewSql);
        Assert.Contains("CHECK (qty >= 1)", vm.PreviewSql);
        Assert.Contains("CONSTRAINT \"ck_price2\" CHECK (price > 0)", vm.PreviewSql);
        Assert.DoesNotContain("ck_cap", vm.PreviewSql.Split("-- SQLite rebuild")[1]);
        Assert.Contains("CONSTRAINT \"ck_note\" CHECK (note <> 'it''s bad')", vm.PreviewSql);
        Assert.Equal(2, vm.Plan.Destructive.Count(d => d.Contains("Check")));
        await SaveAsync(vm);

        var reopened = await OpenAsync(provider, cs, "t");
        Assert.Equal(["(unnamed): length(note) < 50", "(unnamed): qty >= 1", "ck_note: note <> 'it''s bad'", "ck_price2: price > 0"],
            Checks(reopened.Design.Baseline!));
        Assert.Equal(["trg"], reopened.Design.Triggers.Select(t => t.Name));
        Assert.Contains(reopened.Indexes.Items, i => i.Name == "t_note");
        Assert.Equal(["1|a"], await Rows(provider, cs, "SELECT id, note FROM v;"));

        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO t (qty, price) VALUES (0, 1);"));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO t (qty, price, note) VALUES (1, 1, 'it''s bad');"));
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO t (qty, price) VALUES (5000, 1);"); // ck_cap is gone

        await reopened.SaveCommand.ExecuteAsync(null);
        Assert.Equal("No changes to save.", reopened.StatusMessage);
    }

    [Fact]
    public async Task Sqlite_ACheckExistingRowsViolate_RollsTheRebuildBack()
    {
        var (cs, provider) = await SqliteAsync(Fixture);
        var vm = await OpenAsync(provider, cs, "t");
        var before = TableDesignText.Lines(vm.Design.Baseline!);
        Add(vm, "ck_big", "qty > 100");
        Assert.True(vm.Plan.CanExecute, string.Join("\n", vm.ValidationErrors));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.DoesNotContain("successfully", vm.StatusMessage);

        var reopened = await OpenAsync(provider, cs, "t");
        Assert.Equal(before, TableDesignText.Lines(reopened.Design.Baseline!));
        Assert.Equal(["1|a"], await Rows(provider, cs, "SELECT id, note FROM v;"));
    }

    [Fact]
    public async Task Sqlite_MalformedChecksAreListedAndNothingRuns()
    {
        var (cs, provider) = await SqliteAsync(Fixture);
        var vm = await OpenAsync(provider, cs, "t");
        var check = Add(vm, "ck_bad", "");
        Assert.Contains("Check ck_bad has no expression.", vm.ValidationErrors);
        check.Expression = "qty > 0; DROP TABLE t";
        Assert.Contains("Check ck_bad: the expression contains a statement separator (;).", vm.ValidationErrors);
        check.Expression = "note <> 'open";
        Assert.Contains("Check ck_bad: the expression has an unterminated quote or comment.", vm.ValidationErrors);
        check.Expression = "(qty > 0";
        Assert.Contains("Check ck_bad: the expression has an unclosed parenthesis.", vm.ValidationErrors);
        check.Expression = "qty > 0";
        check.Name = "ck_price";
        Assert.Contains("Duplicate check constraint name: \"ck_price\".", vm.ValidationErrors);
        check.Name = "ck_ok";

        // A new check may not name a column being dropped; an unchanged loaded one goes with the column, as before.
        check.Expression = "note <> ''";
        vm.DropColumnCommand.Execute(vm.Columns.Single(c => c.Name == "note"));
        Assert.Contains("Check ck_ok refers to column \"note\", which is being dropped.", vm.ValidationErrors);
        Assert.DoesNotContain(vm.ValidationErrors, e => e.Contains("length(note)"));
        Assert.False(vm.PlanAllowsSave);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(["1|5|2.5|a"], await Rows(provider, cs, "SELECT id, qty, price, note FROM t;"));
    }

    [Fact]
    public async Task DuckDb_NewTablesDeclareChecks_AndChangesOnExistingTablesAreBlocked()
    {
        var cs = $"Data Source={Path.Combine(_directory, "checks.duckdb")}";
        var provider = new DuckDbProvider();
        var vm = new TableDesignerViewModel("made", Session(provider, cs));
        vm.Columns.Add(new ColumnDefinition { Name = "id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true, IsNew = true });
        vm.Columns.Add(new ColumnDefinition { Name = "qty", DataType = "INTEGER", IsNew = true });
        Add(vm, null, "qty >= 0");
        Assert.Contains("CHECK (qty >= 0)", vm.PreviewSql);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage + "\n" + vm.PreviewSql);
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO made VALUES (1, -1);"));

        var existing = await OpenAsync(provider, cs, "made");
        var loaded = Assert.Single(existing.Checks.Items);
        loaded.Expression = "qty >= 1";
        Assert.Contains(existing.Plan.Blocked, b => b.EndsWith("cannot be changed: changing checks after creation is Not supported by DuckDB."));
        Assert.False(existing.PlanAllowsSave);
    }
}
