using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// Issue 15: index expressions, filters (predicates), included columns and methods through the designer's
/// public load/edit/preview/save workflow against real SQLite and DuckDB databases. Each engine offers only
/// what it has; malformed fragments and incompatible settings are listed before anything runs.
/// </summary>
public sealed class AdvancedIndexEditingWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-advanced-indexes-" + Guid.NewGuid().ToString("N"));

    public AdvancedIndexEditingWorkflowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<(string Cs, SqliteProvider Provider)> SqliteAsync(string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "indexes.db"), Pooling = false, ForeignKeys = true }.ToString();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, sql);
        return (cs, provider);
    }

    private static async Task<TableDesignerViewModel> OpenAsync(IDatabaseProvider provider, string cs, string table)
    {
        var session = new DatabaseSession(new ConnectionInfo { Name = "indexes", Provider = provider.ProviderName, ConnectionString = cs }, provider);
        var vm = new TableDesignerViewModel(table, session, new SchemaNode { Name = table, RawName = table, Type = SchemaNodeType.Table },
            new DatabaseObjectIdentity(null, null, table, SchemaNodeType.Table));
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

    private static string[] IndexLines(TableDesign design) =>
        TableDesignText.Lines(design).Where(l => l.StartsWith("index ")).ToArray();

    private static void AddExpression(TableDesignerViewModel vm, EditableIndex index, string expression)
    {
        vm.Indexes.Selected = index;
        vm.Indexes.NewExpression = expression;
        Assert.True(vm.Indexes.AddExpressionCommand.CanExecute(null));
        vm.Indexes.AddExpressionCommand.Execute(null);
    }

    [Fact]
    public async Task Sqlite_FiltersAndExpressionKeysAreEdited_SavedAndEnforced()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (id INTEGER PRIMARY KEY, a TEXT, b TEXT, active INTEGER);
            CREATE UNIQUE INDEX ix_a ON t (a) WHERE active = 1;
            CREATE INDEX ix_b ON t (lower(b));
            CREATE TRIGGER trg AFTER INSERT ON t BEGIN SELECT 1; END;
            INSERT INTO t VALUES (1, 'x', 'B1', 1), (2, 'x', 'B2', 0);
            """);
        var vm = await OpenAsync(provider, cs, "t");
        Assert.True(vm.Indexes.Rules.Expressions && vm.Indexes.Rules.Filters && !vm.Indexes.Rules.IncludedColumns);
        Assert.Empty(vm.Indexes.Methods);

        // Widen ix_a's filter, change ix_b's expression and add a second expression key, and add a partial expression index.
        vm.Index("ix_a").Filter = "active <> 0";
        Assert.Equal("Changed", vm.Index("ix_a").Status);
        vm.Index("ix_b").Parts[0].Expression = "upper(b)";
        AddExpression(vm, vm.Index("ix_b"), "length(a)");
        var added = vm.AddIndex("ix_new", true);
        AddExpression(vm, added, "a || b");
        added.Filter = "id > 1";

        Assert.Contains("CREATE UNIQUE INDEX \"ix_a\" ON \"t\" (\"a\") WHERE active <> 0;", vm.PreviewSql);
        Assert.Contains("CREATE INDEX \"ix_b\" ON \"t\" ((upper(b)), (length(a)));", vm.PreviewSql);
        Assert.Contains("CREATE UNIQUE INDEX \"ix_new\" ON \"t\" ((a || b)) WHERE id > 1;", vm.PreviewSql);
        Assert.Contains("Re-creates index ix_a as unique (a) where active <> 0 instead of unique (a) where active = 1; it is rebuilt from the " +
                        "table, and the save fails if existing rows violate it.", vm.Plan.Destructive);
        await SaveAsync(vm);

        var reopened = await OpenAsync(provider, cs, "t");
        Assert.Equal([
            "index ix_a unique CreateIndex (a) where=active <> 0",
            "index ix_b CreateIndex (((upper(b))), ((length(a))))",
            "index ix_new unique CreateIndex (((a || b))) where=id > 1"
        ], IndexLines(reopened.Design.Baseline!));
        Assert.Equal(["trg"], reopened.Design.Triggers.Select(t => t.Name));
        // The wider filter now covers row 2, so another active 'x' is refused; ix_new is enforced only where id > 1.
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO t VALUES (3, 'x', 'B3', 2);"));
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO t VALUES (4, 'y', 'B4', 0);");
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO t VALUES (5, 'y', 'B4', 0);"));

        // Clearing the filter removes it.
        reopened.Index("ix_new").Filter = "";
        Assert.Contains("CREATE UNIQUE INDEX \"ix_new\" ON \"t\" ((a || b));", reopened.PreviewSql);
        reopened.Index("ix_new").Filter = "id > 1";
        Assert.Equal("", reopened.PreviewSql);
    }

    [Fact]
    public async Task Sqlite_MalformedExpressionsAndFiltersAreListed_AndNothingRuns()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (id INTEGER PRIMARY KEY, a TEXT, gone TEXT);
            CREATE INDEX ix_a ON t (a) WHERE id > 0;
            """);
        var vm = await OpenAsync(provider, cs, "t");

        vm.Index("ix_a").Filter = "id > 0; DROP TABLE t";
        var added = vm.AddIndex("ix_x", false);
        AddExpression(vm, added, "lower(a");
        AddExpression(vm, added, "upper(a) -- note");
        AddExpression(vm, added, "length(gone)");
        AddExpression(vm, added, "LENGTH(gone)");
        added.Parts[0].Expression = " ";
        added.Filter = "gone IS NOT NULL";
        vm.DropColumnCommand.Execute(vm.Columns.Single(c => c.Name == "gone"));

        Assert.Contains("Index ix_a: the filter contains a statement separator (;).", vm.ValidationErrors);
        Assert.Contains("Index ix_x has an empty expression key.", vm.ValidationErrors);
        Assert.Contains("Index ix_x: the key expression (upper(a) -- note) contains a comment.", vm.ValidationErrors);
        Assert.Contains("Index ix_x lists expression (length(gone)) more than once.", vm.ValidationErrors);
        Assert.Contains("Index ix_x uses column \"gone\", which is being dropped, in a key expression; change the expression or drop the index.",
            vm.ValidationErrors);
        Assert.Contains("Index ix_x uses column \"gone\", which is being dropped, in its filter; change the filter or drop the index.",
            vm.ValidationErrors);
        added.Parts[0].Expression = "lower(a";
        Assert.Contains("Index ix_x: the key expression (lower(a) has an unclosed parenthesis.", vm.ValidationErrors);
        added.Parts[0].Expression = "\"a\"";
        Assert.Contains("Index ix_x has the expression (\"a\"), which is only column \"a\"; add the column as a key column instead.",
            vm.ValidationErrors);
        // A rename does not follow into expression or filter text, which an edited index would otherwise create with the old name.
        vm.Columns.Single(c => c.Name == "id").Name = "key";
        Assert.Contains("Index ix_a uses column \"id\", which is being renamed, in its filter; change the filter or drop the index.",
            vm.ValidationErrors);
        Assert.False(vm.PlanAllowsSave);
        await vm.SaveCommand.ExecuteAsync(null);

        var reopened = await OpenAsync(provider, cs, "t");
        Assert.Equal(["index ix_a CreateIndex (a) where=id > 0"], IndexLines(reopened.Design.Baseline!));
        Assert.Equal(["id", "a", "gone"], reopened.Columns.Select(c => c.Name));
        // SQLite offers no included columns or methods.
        reopened.Indexes.Selected = reopened.Index("ix_a");
        reopened.Indexes.NewIncluded = "id";
        Assert.False(reopened.Indexes.AddIncludedCommand.CanExecute(null));
        Assert.False(reopened.Indexes.CanEditMethod && reopened.Indexes.HasMethods);

        // A filter edit rides along with a column drop's rebuild, which creates the index from its edited definition.
        reopened.DropColumnCommand.Execute(reopened.Columns.Single(c => c.Name == "gone"));
        reopened.Index("ix_a").Filter = "id > 5";
        Assert.Contains("-- SQLite rebuild", reopened.PreviewSql);
        Assert.Contains("CREATE INDEX \"ix_a\" ON \"t\" (\"a\") WHERE id > 5;", reopened.PreviewSql);
        await SaveAsync(reopened);
        Assert.Equal(["index ix_a CreateIndex (a) where=id > 5"], IndexLines((await OpenAsync(provider, cs, "t")).Design.Baseline!));
    }

    [Fact]
    public async Task DuckDb_ExpressionKeysAreEditedAndAdded_AndNoFiltersAreOffered()
    {
        var cs = $"Data Source={Path.Combine(_directory, "indexes.duckdb")}";
        using (var connection = new DuckDBConnection(cs))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE t (id INTEGER PRIMARY KEY, a VARCHAR, b VARCHAR);
                CREATE INDEX ix_expr ON t (lower(a));
                INSERT INTO t VALUES (1, 'x', 'p'), (2, 'y', 'q');
                """;
            cmd.ExecuteNonQuery();
        }
        var provider = new DuckDbProvider();
        var vm = await OpenAsync(provider, cs, "t");
        Assert.False(vm.Indexes.HasFilters || vm.Indexes.HasIncludedColumns || vm.Indexes.HasMethods);
        Assert.True(vm.Indexes.HasExpressions);

        // A changed expression re-creates the index, under a new name (DuckDB cannot reuse it in one save).
        vm.Index("ix_expr").Name = "ix_expr2";
        vm.Index("ix_expr2").Parts[0].Expression = "upper(a)";
        vm.Index("ix_expr2").IsUnique = true;
        var added = vm.AddIndex("ix_ab", false, "id");
        AddExpression(vm, added, "a || b");
        Assert.Contains("CREATE UNIQUE INDEX \"ix_expr2\" ON \"main\".\"t\" ((upper(a)));", vm.PreviewSql);
        Assert.Contains("CREATE INDEX \"ix_ab\" ON \"main\".\"t\" (\"id\", (a || b));", vm.PreviewSql);
        await SaveAsync(vm);

        var reopened = await OpenAsync(provider, cs, "t");
        Assert.Equal([
            "index ix_ab CreateIndex (id, (((a || b))))", // DuckDB keeps the parentheses it was given
            "index ix_expr2 unique CreateIndex (((upper(a))))"
        ], IndexLines(reopened.Design.Baseline!));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO t VALUES (3, 'X', 'r');"));
        await reopened.SaveCommand.ExecuteAsync(null);
        Assert.Equal("No changes to save.", reopened.StatusMessage);
    }
}
