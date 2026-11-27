using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class SchemaDiffDdlGenerationTests
{
    private static string CreateSharedInMemoryConnectionString() =>
        $"Data Source=SchemaDiffTests_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private static async Task<(DatabaseSession session, Microsoft.Data.Sqlite.SqliteConnection keepAlive)>
        CreateSqliteSessionAsync(string sql)
    {
        var cs = CreateSharedInMemoryConnectionString();
        var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await keepAlive.OpenAsync();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, sql);
        var session = new DatabaseSession(
            new ConnectionInfo { Name = "source", Provider = "SQLite", ConnectionString = cs },
            provider);
        return (session, keepAlive);
    }

    [Fact]
    public async Task GenerateSyncScript_MissingTable_EmitsCreateTable()
    {
        var (source, keepAlive) = await CreateSqliteSessionAsync("""
            CREATE TABLE Orders (
                Id   INTEGER NOT NULL PRIMARY KEY,
                Name TEXT    NOT NULL
            );
            """);
        await using var _ = keepAlive;

        var (target, keepAlive2) = await CreateSqliteSessionAsync("SELECT 1");
        await using var _2 = keepAlive2;

        var vm = new SchemaDiffViewModel(new ObservableCollection<DatabaseSession>());
        vm.SourceSession = source;
        vm.TargetSession = target;

        vm.DiffItems.Add(new SchemaDiffItem
        {
            ObjectName = "Orders",
            ObjectType = "table",
            Status = DiffStatus.OnlyInSource,
            IsSelected = true
        });

        await vm.GenerateScriptCommand.ExecuteAsync(null);

        Assert.Contains("CREATE TABLE", vm.MigrationScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Orders", vm.MigrationScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-- TODO:", vm.MigrationScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateSyncScript_MissingView_EmitsCreateView()
    {
        var (source, keepAlive) = await CreateSqliteSessionAsync("""
            CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT);
            CREATE VIEW ProductNames AS SELECT Name FROM Products;
            """);
        await using var _ = keepAlive;

        var (target, keepAlive2) = await CreateSqliteSessionAsync("SELECT 1");
        await using var _2 = keepAlive2;

        var vm = new SchemaDiffViewModel(new ObservableCollection<DatabaseSession>());
        vm.SourceSession = source;
        vm.TargetSession = target;

        vm.DiffItems.Add(new SchemaDiffItem
        {
            ObjectName = "ProductNames",
            ObjectType = "view",
            Status = DiffStatus.OnlyInSource,
            IsSelected = true
        });

        await vm.GenerateScriptCommand.ExecuteAsync(null);

        Assert.Contains("CREATE VIEW", vm.MigrationScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ProductNames", vm.MigrationScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-- TODO:", vm.MigrationScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateSyncScript_UnsupportedType_EmitsWarning()
    {
        var (source, keepAlive) = await CreateSqliteSessionAsync("SELECT 1");
        await using var _ = keepAlive;

        var (target, keepAlive2) = await CreateSqliteSessionAsync("SELECT 1");
        await using var _2 = keepAlive2;

        var vm = new SchemaDiffViewModel(new ObservableCollection<DatabaseSession>());
        vm.SourceSession = source;
        vm.TargetSession = target;

        vm.DiffItems.Add(new SchemaDiffItem
        {
            ObjectName = "trg_audit",
            ObjectType = "trigger",
            Status = DiffStatus.OnlyInSource,
            IsSelected = true
        });

        await vm.GenerateScriptCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.ScriptWarnings);
        Assert.Contains("UNSUPPORTED", vm.ScriptWarnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-- UNSUPPORTED:", vm.MigrationScript, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.HasScriptWarnings);
        Assert.DoesNotContain("-- TODO:", vm.MigrationScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateSyncScript_OnlyInTarget_EmitsDrop()
    {
        var (source, keepAlive) = await CreateSqliteSessionAsync("SELECT 1");
        await using var _ = keepAlive;

        var (target, keepAlive2) = await CreateSqliteSessionAsync("SELECT 1");
        await using var _2 = keepAlive2;

        var vm = new SchemaDiffViewModel(new ObservableCollection<DatabaseSession>());
        vm.SourceSession = source;
        vm.TargetSession = target;

        vm.DiffItems.Add(new SchemaDiffItem
        {
            ObjectName = "OldTable",
            ObjectType = "table",
            Status = DiffStatus.OnlyInTarget,
            IsSelected = true
        });

        await vm.GenerateScriptCommand.ExecuteAsync(null);

        Assert.Contains("DROP TABLE", vm.MigrationScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OldTable", vm.MigrationScript);
        Assert.Empty(vm.ScriptWarnings);
    }
}
