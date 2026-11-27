using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// Data Compare and Schema Diff Apply on PostgreSQL, SQL Server and MySQL (apply-comparisons/07): apply, then
/// re-compare equal, and refuse a target that changed after the preview. Skipped unless the container lane's
/// DBCONNECT_CONFORMANCE_*_CONNECTION_STRING variables are set (build/databases/run-provider-integration.ps1).
/// </summary>
public sealed class ApplyComparisonsServerTests
{
    private static string Users(string provider, bool target) => (provider, target) switch
    {
        ("SQL Server", false) => "CREATE TABLE users (id int PRIMARY KEY, name nvarchar(40) NOT NULL, score decimal(8,2), seen datetime2(6), photo varbinary(16)); " +
                                 "INSERT INTO users VALUES (1, N'Ann', 1.50, '2026-01-02 03:04:05.123456', 0x01), (2, N'Bo', 2.25, NULL, NULL), (3, N'Cy', NULL, '2000-01-01', 0x0203);",
        ("SQL Server", true) => "CREATE TABLE users (id int PRIMARY KEY, name nvarchar(40) NOT NULL, score decimal(8,2), seen datetime2(6), photo varbinary(16)); " +
                                "INSERT INTO users VALUES (1, N'Ann', 1.50, '2026-01-02 03:04:05.123456', 0x01), (2, N'Bob', 0.10, '1999-01-01', 0xFF), (4, N'Di', 4.00, NULL, NULL);",
        ("MySQL", false) => "CREATE TABLE users (id int PRIMARY KEY, name varchar(40) NOT NULL, score decimal(8,2), seen datetime(6), photo varbinary(16)) ENGINE=InnoDB; " +
                            "INSERT INTO users VALUES (1, 'Ann', 1.50, '2026-01-02 03:04:05.123456', 0x01), (2, 'Bo', 2.25, NULL, NULL), (3, 'Cy', NULL, '2000-01-01', 0x0203);",
        ("MySQL", true) => "CREATE TABLE users (id int PRIMARY KEY, name varchar(40) NOT NULL, score decimal(8,2), seen datetime(6), photo varbinary(16)) ENGINE=InnoDB; " +
                           "INSERT INTO users VALUES (1, 'Ann', 1.50, '2026-01-02 03:04:05.123456', 0x01), (2, 'Bob', 0.10, '1999-01-01', 0xFF), (4, 'Di', 4.00, NULL, NULL);",
        (_, false) => "CREATE TABLE users (id int PRIMARY KEY, name varchar(40) NOT NULL, score numeric(8,2), seen timestamp(6), photo bytea); " +
                       "INSERT INTO users VALUES (1, 'Ann', 1.50, '2026-01-02 03:04:05.123456', '\\x01'), (2, 'Bo', 2.25, NULL, NULL), (3, 'Cy', NULL, '2000-01-01', '\\x0203');",
        _ => "CREATE TABLE users (id int PRIMARY KEY, name varchar(40) NOT NULL, score numeric(8,2), seen timestamp(6), photo bytea); " +
             "INSERT INTO users VALUES (1, 'Ann', 1.50, '2026-01-02 03:04:05.123456', '\\x01'), (2, 'Bob', 0.10, '1999-01-01', '\\xFF'), (4, 'Di', 4.00, NULL, NULL);"
    };

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    [InlineData("MySQL")]
    public async Task DataDifferencesApplyAndADriftedRowIsRefused(string provider)
    {
        await using var server = ScratchServer.For(provider);
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("dsrc", Users(provider, false));
        var target = await server.CreateDatabaseAsync("ddst", Users(provider, true));
        var vm = new DataCompareViewModel(new ObservableCollection<DatabaseSession> { source, target })
        {
            SourceSession = source,
            TargetSession = target,
            SourceTableName = "users",
            TargetTableName = "users"
        };
        await vm.RunCompareCommand.ExecuteAsync(null);
        Assert.True(vm.DiffRows.Count == 3, vm.CompareStatus);
        vm.Apply.Confirm = _ => Task.FromResult(true);

        // A row changed after the preview stops Apply; nothing runs.
        await vm.Apply.PreviewCommand.ExecuteAsync(null);
        vm.Apply.AcknowledgeDataLoss = true;
        await server.RunScriptAsync(target, "UPDATE users SET score = 9.99 WHERE id = 2");
        await vm.Apply.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(SyncOutcomeKind.NotApplied, vm.Apply.Outcome!.Kind);
        Assert.Contains("row (2) has a different score", vm.Apply.Status);

        await vm.Apply.RecompareCommand.ExecuteAsync(null);
        await vm.Apply.PreviewCommand.ExecuteAsync(null);
        vm.Apply.AcknowledgeDataLoss = true;
        await vm.Apply.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.Apply.Outcome!.Kind == SyncOutcomeKind.Applied, vm.Apply.Status + "\n" + vm.Apply.Log);
        Assert.Empty(vm.DiffRows);
        Assert.Equal(await ScratchComparisons.RowsAsync(source, "users"), await ScratchComparisons.RowsAsync(target, "users"));
    }

    private static string Schema(string provider, bool target)
    {
        var engine = provider == "MySQL" ? " ENGINE=InnoDB" : "";
        var text = provider == "SQL Server" ? "nvarchar(40)" : "varchar(40)";
        return target
            ? $"CREATE TABLE items (id int PRIMARY KEY, name {text}, legacy {text}){engine}; CREATE TABLE stale (id int PRIMARY KEY){engine}; " +
              "INSERT INTO items (id, name, legacy) VALUES (1, 'a', 'x');"
            : $"CREATE TABLE cats (id int PRIMARY KEY, label {text}){engine}; " +
              $"CREATE TABLE items (id int PRIMARY KEY, name {text} NOT NULL, price decimal(10,2), cat_id int, " +
              $"CONSTRAINT fk_items_cat FOREIGN KEY (cat_id) REFERENCES cats(id)){engine}; " +
              (provider == "SQL Server" ? "\nGO\n" : "") + "CREATE VIEW item_names AS SELECT id, name FROM items;";
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    [InlineData("MySQL")]
    public async Task SchemaDifferencesApplyAndReCompareEqual(string provider)
    {
        await using var server = ScratchServer.For(provider);
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("ssrc", Schema(provider, false));
        var target = await server.CreateDatabaseAsync("sdst", Schema(provider, true));
        var vm = new SchemaDiffViewModel(new ObservableCollection<DatabaseSession> { source, target }) { SourceSession = source, TargetSession = target };
        await vm.RunCompareCommand.ExecuteAsync(null);
        string[] Differences() => vm.DiffItems.Where(i => i.ChangeType != SchemaDiffChangeType.None && i.ObjectKind != SchemaDiffObjectType.Index)
            .Select(i => $"{i.ChangeType} {i.ObjectKind} {i.ObjectName}").Order().ToArray();
        Assert.Contains("Add Table cats", Differences());
        Assert.Contains("Remove Table stale", Differences());
        vm.Apply.Confirm = _ => Task.FromResult(true);

        await vm.Apply.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.Apply.Plan != null, vm.Apply.Status);
        vm.Apply.AcknowledgeDataLoss = true;
        var script = vm.Apply.Script;
        await vm.Apply.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.Apply.Outcome!.Kind == SyncOutcomeKind.Applied, vm.Apply.Status + "\n" + vm.Apply.Log + "\n" + script);
        Assert.True(Differences().Length == 0, string.Join("\n", vm.DiffItems.Where(i => i.ChangeType != SchemaDiffChangeType.None)
            .Select(i => $"{i.ChangeType} {i.ObjectName}: {i.SourceDdl}")) + "\n" + script);
        Assert.Equal("Int32:1|String:a", await ScratchComparisons.RowsAsync(target, "items", "id, name"));
    }
}
