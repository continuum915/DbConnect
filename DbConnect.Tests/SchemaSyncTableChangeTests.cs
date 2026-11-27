using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Schema Diff applies a changed table through the table designer's planner (apply-comparisons/04).</summary>
public sealed class SchemaSyncTableChangeTests : DumpTestDatabases
{
    private const string Source = """
        CREATE TABLE cats (id INTEGER PRIMARY KEY, label TEXT);
        CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL, price NUMERIC(10,2) DEFAULT 0, sku TEXT UNIQUE,
            cat_id INTEGER REFERENCES cats(id), CHECK (price >= 0));
        CREATE INDEX items_name ON items(name);
        """;

    // items lacks price, cat_id, the key, the check and items_name, allows NULL names, and has legacy and its index.
    private const string Target = """
        CREATE TABLE cats (id INTEGER PRIMARY KEY, label TEXT);
        CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT, sku TEXT, legacy TEXT);
        CREATE INDEX items_legacy ON items(legacy);
        INSERT INTO items VALUES (1, 'a', 's1', 'old');
        """;

    private async Task<SchemaDiffViewModel> ComparedAsync(string provider, string source = Source, string target = Target)
    {
        var fix = (string sql) => provider == "DuckDB" ? sql.Replace(" TEXT", " VARCHAR") : sql;
        var s = await SessionAsync(provider, "source", fix(source));
        var t = await SessionAsync(provider, "target", fix(target));
        var vm = new SchemaDiffViewModel(new ObservableCollection<DatabaseSession> { s, t }) { SourceSession = s, TargetSession = t };
        await vm.RunCompareCommand.ExecuteAsync(null);
        vm.Apply.Confirm = _ => Task.FromResult(true);
        return vm;
    }

    private static List<string> Differences(SchemaDiffViewModel vm) => vm.DiffItems
        .Where(i => i.ChangeType != SchemaDiffChangeType.None).Select(i => $"{i.ChangeType} {i.ObjectKind} {i.ObjectName}").OrderBy(s => s).ToList();

    [Fact]
    public async Task SqliteRebuildsTheChangedTableStepByStepAndReComparesEqual()
    {
        var vm = await ComparedAsync("SQLite");
        Assert.Contains("Modify Table items", Differences(vm));

        await vm.Apply.PreviewCommand.ExecuteAsync(null);

        Assert.True(vm.Apply.Plan!.Steps.Count > 0, vm.Apply.Status + "\n" + vm.Apply.Script);
        Assert.False(vm.Apply.Plan.Transactional);
        Assert.Contains("SQLite rebuilds a changed table in a transaction of its own", vm.Apply.Script);
        Assert.True(vm.Apply.HasDestructive, vm.Apply.Script);
        vm.Apply.AcknowledgeDataLoss = true;
        await vm.Apply.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.Apply.Outcome!.Kind == SyncOutcomeKind.Applied, vm.Apply.Status + "\n" + vm.Apply.Log + "\n" + vm.Apply.Script);
        Assert.DoesNotContain(Differences(vm), d => d.Contains("items"));
        Assert.Equal("1|a|s1", await QueryAsync(Session("SQLite", "target"), "SELECT id, name, sku FROM items"));
        // The rebuild writes its own CREATE text, so the tables are compared as designs; added columns go last, as the designer adds them.
        Assert.Equal(await DesignAsync(Session("SQLite", "source")), await DesignAsync(Session("SQLite", "target")));
    }

    private static async Task<string> DesignAsync(DatabaseSession session) =>
        string.Join("\n", TableDesignText.Lines(await ((ITableDesignSource)session.Provider).GetTableDesignAsync(session.ConnectionString, Table("items"))).Order());

    [Fact]
    public async Task DuckDbAppliesWhatItsAlterCanAndNamesWhatItCannot()
    {
        // DuckDB alters columns in place; keys, checks and foreign keys on an existing table need a rebuild it does not do.
        var vm = await ComparedAsync("DuckDB",
            "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL, price DECIMAL(10,2) DEFAULT 0);",
            "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT, legacy TEXT); INSERT INTO items VALUES (1, 'a', 'old');");

        await vm.Apply.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.Apply.Plan!.Transactional);
        vm.Apply.AcknowledgeDataLoss = true;
        await vm.Apply.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.Apply.Outcome!.Kind == SyncOutcomeKind.Applied, vm.Apply.Status + "\n" + vm.Apply.Log + "\n" + vm.Apply.Script);
        Assert.DoesNotContain(Differences(vm), d => d.Contains("items"));
        Assert.Equal("1|a|0.00", await QueryAsync(Session("DuckDB", "target"), "SELECT id, name, price::VARCHAR FROM items"));
    }

    [Fact]
    public async Task ABlockedChangeIsNamedAndNotApplied()
    {
        // DuckDB cannot add a foreign key to an existing table.
        var vm = await ComparedAsync("DuckDB", Source.Replace("CHECK (price >= 0)", "note TEXT"), Target);

        await vm.Apply.PreviewCommand.ExecuteAsync(null);

        Assert.Contains("-- Table main.items is not changed:", vm.Apply.Script);
        Assert.DoesNotContain(vm.Apply.Plan!.Steps, s => s.Object == "Table main.items");
    }
}
