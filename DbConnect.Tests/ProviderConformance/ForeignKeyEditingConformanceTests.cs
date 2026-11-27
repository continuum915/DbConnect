using Avalonia.Headless.XUnit;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// Issue 12 on PostgreSQL, SQL Server and MySQL: open a table through explorer → Design Table…, add a
/// composite foreign key to a table chosen from the catalog (one of two same-named tables in different
/// schemas where the engine has schemas), add a self-reference, change an action, rename or re-create a
/// key the engine named, save and reopen. Everything but the foreign keys (and MySQL's indexes for them)
/// reopens exactly as loaded, the rows survive, and the engine enforces the saved keys and actions.
/// </summary>
public sealed class ForeignKeyEditingConformanceTests : MainWindowTestBase
{
    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task PostgreSql_CompositeSelfAndRenamedForeignKeysSaveAndReopen()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var schema = $"rain.fk_{Guid.NewGuid().ToString("N")[..10]}";
        var other = schema + "_b";
        try
        {
            await TableDesignPostgreSqlTests.Execute(cs, $"""
                CREATE SCHEMA "{schema}";
                CREATE SCHEMA "{other}";
                CREATE TABLE "{schema}".parent (x int PRIMARY KEY);
                CREATE TABLE "{other}".parent (a int NOT NULL, b int NOT NULL, PRIMARY KEY (a, b));
                CREATE TABLE "{schema}".lookup (id int PRIMARY KEY);
                CREATE TABLE "{schema}"."Child.T" (id int PRIMARY KEY, pa int, pb int, manager_id int, boss_id int, peer_id int, note text,
                    CONSTRAINT fk_other FOREIGN KEY (manager_id) REFERENCES "{schema}".lookup (id) ON DELETE SET NULL,
                    FOREIGN KEY (boss_id) REFERENCES "{schema}".lookup (id));
                CREATE INDEX "ix.note" ON "{schema}"."Child.T" (note);
                INSERT INTO "{other}".parent VALUES (1, 10), (2, 20);
                INSERT INTO "{schema}".lookup VALUES (1), (2);
                INSERT INTO "{schema}"."Child.T" VALUES (1, 1, 10, 1, NULL, NULL, 'n1'), (2, 2, 20, 2, NULL, NULL, 'n2');
                """);
            await EditAsync(cs, new PostgreSqlProvider(), schema, $"\"{schema}\"",
                new DatabaseObjectIdentity(null, other, "parent", SchemaNodeType.Table),
                generatedName: "Child.T_boss_id_fkey", renamed: "fk_boss_renamed",
                expected: [$"fk_boss_renamed (boss_id -> id) {schema}.lookup NO ACTION/NO ACTION",
                    $"fk_other (manager_id -> id) {schema}.lookup CASCADE/NO ACTION",
                    $"fk_parent (pa -> a, pb -> b) {other}.parent CASCADE/NO ACTION",
                    $"fk_self (peer_id -> id) {schema}.Child.T NO ACTION/NO ACTION"]);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await TableDesignPostgreSqlTests.Execute(cs, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE; DROP SCHEMA IF EXISTS \"{other}\" CASCADE;");
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task SqlServer_CompositeSelfAndRenamedForeignKeysSaveAndReopen()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var database = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        var connection = new SqlConnectionStringBuilder(cs) { InitialCatalog = database, Pooling = false }.ToString();
        try
        {
            await TableDesignSqlServerTests.Execute(cs, $"CREATE DATABASE [{database}]");
            await TableDesignSqlServerTests.Execute(connection, """
                CREATE SCHEMA fa;
                GO
                CREATE SCHEMA fb;
                GO
                CREATE TABLE fa.parent (x int NOT NULL PRIMARY KEY);
                CREATE TABLE fb.parent (a int NOT NULL, b int NOT NULL, PRIMARY KEY (a, b));
                CREATE TABLE fa.lookup (id int NOT NULL PRIMARY KEY);
                CREATE TABLE fa.[Child.T] (id int NOT NULL PRIMARY KEY, pa int NULL, pb int NULL, manager_id int NULL, boss_id int NULL, peer_id int NULL,
                    note nvarchar(20) NULL,
                    CONSTRAINT fk_other FOREIGN KEY (manager_id) REFERENCES fa.lookup (id) ON DELETE SET NULL,
                    FOREIGN KEY (boss_id) REFERENCES fa.lookup (id));
                CREATE INDEX [ix.note] ON fa.[Child.T] (note);
                INSERT INTO fb.parent VALUES (1, 10), (2, 20);
                INSERT INTO fa.lookup VALUES (1), (2);
                INSERT INTO fa.[Child.T] VALUES (1, 1, 10, 1, NULL, NULL, N'n1'), (2, 2, 20, 2, NULL, NULL, N'n2');
                """);
            await EditAsync(connection, new SqlServerProvider(), "fa", "fa",
                new DatabaseObjectIdentity(database, "fb", "parent", SchemaNodeType.Table),
                generatedName: null, renamed: "fk_boss_renamed",
                expected: [$"fk_boss_renamed (boss_id -> id) {database}.fa.lookup NO ACTION/NO ACTION",
                    $"fk_other (manager_id -> id) {database}.fa.lookup CASCADE/NO ACTION",
                    $"fk_parent (pa -> a, pb -> b) {database}.fb.parent CASCADE/NO ACTION",
                    $"fk_self (peer_id -> id) {database}.fa.Child.T NO ACTION/NO ACTION"]);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await TableDesignSqlServerTests.Execute(cs, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task MySql_CompositeSelfAndRecreatedForeignKeysSaveAndReopen()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var db = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        try
        {
            await TableDesignMySqlTests.Execute(cs, $"""
                CREATE DATABASE `{db}`;
                CREATE TABLE `{db}`.parent (a INT NOT NULL, b INT NOT NULL, PRIMARY KEY (a, b));
                CREATE TABLE `{db}`.lookup (id INT NOT NULL PRIMARY KEY);
                CREATE TABLE `{db}`.`Child.T` (id INT NOT NULL PRIMARY KEY, pa INT, pb INT, manager_id INT, boss_id INT, peer_id INT, note VARCHAR(20),
                    INDEX `ix.note` (note),
                    CONSTRAINT fk_other FOREIGN KEY (manager_id) REFERENCES `{db}`.lookup (id) ON DELETE SET NULL,
                    FOREIGN KEY (boss_id) REFERENCES `{db}`.lookup (id));
                INSERT INTO `{db}`.parent VALUES (1, 10), (2, 20);
                INSERT INTO `{db}`.lookup VALUES (1), (2);
                INSERT INTO `{db}`.`Child.T` VALUES (1, 1, 10, 1, NULL, NULL, 'n1'), (2, 2, 20, 2, NULL, NULL, 'n2');
                """);
            await EditAsync(new MySqlConnectionStringBuilder(cs) { Database = db }.ToString(), new MySqlProvider(), null, $"`{db}`",
                new DatabaseObjectIdentity(db, null, "parent", SchemaNodeType.Table),
                generatedName: "Child.T_ibfk_1", renamed: "fk_boss_renamed",
                expected: [$"fk_boss_renamed (boss_id -> id) {db}.lookup NO ACTION/NO ACTION",
                    $"fk_other (manager_id -> id) {db}.lookup CASCADE/NO ACTION",
                    $"fk_parent (pa -> a, pb -> b) {db}.parent CASCADE/NO ACTION",
                    $"fk_self (peer_id -> id) {db}.Child.T NO ACTION/NO ACTION"]);
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            await TableDesignMySqlTests.Drop(cs, db);
        }
    }

    private async Task EditAsync(string connection, IDatabaseProvider provider, string? schema, string qualifier,
        DatabaseObjectIdentity parent, string? generatedName, string renamed, string[] expected)
    {
        var session = new DatabaseSession(new ConnectionInfo { Name = "fks", Provider = provider.ProviderName, ConnectionString = connection }, provider);
        var vm = CreateMainWindowViewModel();
        var root = await TableDesignRoundTrip.ConnectAsync(vm, session);
        var node = await TableDesignRoundTrip.FindTableAsync(vm, root, schema, "Child.T");
        var designer = await TableDesignRoundTrip.OpenAsync(vm, node);
        Assert.True(designer.Design.IsLoaded, designer.Design.Error);
        Assert.Equal("", designer.PreviewSql);
        await WaitAsync(() => designer.ForeignKeys.TargetsState == MetadataLoadState.Loaded, "the referenced tables");
        var source = (ITableDesignSource)provider;
        var before = designer.Design.Baseline!;
        var table = $"{qualifier}.{provider.Capabilities.QuoteIdentifier("Child.T")}";

        // The engine-named key loads unnamed (SQL Server) or with its generated name.
        var boss = designer.ForeignKeys.Items.Single(k => k.Pairs.Single().Column == "boss_id");
        Assert.Equal(generatedName, boss.Name);
        boss.Name = renamed;

        // A composite key to a table chosen from the catalog, never matched by its bare name.
        designer.ForeignKeys.AddForeignKeyCommand.Execute(null);
        var composite = designer.ForeignKeys.Selected!;
        composite.Name = "fk_parent";
        var targets = designer.ForeignKeys.Targets.Where(t => t.Identity.Name == "parent").ToList();
        designer.ForeignKeys.SelectedTarget = targets.Single(t => TableIdentity.Same(t.Identity, parent));
        composite.Pairs[0].Column = "pa";
        composite.Pairs[0].ReferencedColumn = "a";
        designer.ForeignKeys.AddPairCommand.Execute(null);
        designer.ForeignKeys.SelectedPair!.Column = "pb";
        designer.ForeignKeys.SelectedPair!.ReferencedColumn = "b";
        composite.OnDelete = "CASCADE";
        if (provider is MySqlProvider)
        {
            // InnoDB needs the referenced columns in the key's own order.
            composite.Pairs.Move(1, 0);
            await WaitAsync(() => designer.ValidationErrors.Any(e => e.Contains("needs the referenced columns in the key's order (a, b)")), "the order check");
            composite.Pairs.Move(1, 0);
        }

        designer.ForeignKeys.AddForeignKeyCommand.Execute(null);
        var self = designer.ForeignKeys.Selected!;
        self.Name = "fk_self";
        designer.ForeignKeys.SelectedTarget = designer.ForeignKeys.Targets.Single(t => TableIdentity.Same(t.Identity, before.Table));
        self.Pairs[0].Column = "peer_id";
        self.Pairs[0].ReferencedColumn = "id";

        designer.ForeignKeys.Items.Single(k => k.Name == "fk_other").OnDelete = "CASCADE";
        await WaitAsync(() => !designer.ValidationErrors.Any(e => e.Contains("still loading")), "the referenced columns");
        Assert.True(designer.Plan.CanExecute, string.Join("\n", designer.Plan.Problems) + "\n" + designer.Plan.Sql);
        designer.Review.DestructiveConfirmed = designer.Review.NeedsConfirmation;
        await designer.SaveCommand.ExecuteAsync(null);
        Assert.True(designer.StatusMessage?.Contains("successfully") == true, designer.StatusMessage + "\n" + designer.Plan.Sql);

        var after = await source.GetTableDesignAsync(connection, designer.Design.Table!);
        Assert.Equal(expected, ForeignKeys(after));
        Assert.Equal(WithoutForeignKeys(before), WithoutForeignKeys(after));
        Assert.Contains(after.Indexes, i => i.Name == "ix.note");
        Assert.Equal(TableDesignText.Lines(after), TableDesignText.Lines(designer.Design.Baseline!));

        // The saved keys are enforced, and deleting referenced rows cascades.
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(connection, $"INSERT INTO {table} (id, pa, pb) VALUES (3, 9, 9)"));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(connection, $"INSERT INTO {table} (id, peer_id) VALUES (3, 99)"));
        var owner = parent.Schema ?? parent.Catalog;
        var parentTable = (owner == null ? "" : provider.Capabilities.QuoteIdentifier(owner) + ".") + "parent";
        await provider.ExecuteNonQueryAsync(connection, $"DELETE FROM {table} WHERE id = 1");
        await provider.ExecuteNonQueryAsync(connection, $"DELETE FROM {parentTable} WHERE a = 2");
        var rows = (await provider.ExecuteQueryAsync(connection, $"SELECT id FROM {table}")).ObjectRows;
        Assert.Empty(rows);

        await designer.SaveCommand.ExecuteAsync(null);
        Assert.Equal("No changes to save.", designer.StatusMessage);
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), $"Timed out waiting for {what}.");
    }

    private static string[] ForeignKeys(TableDesign design) =>
        design.ForeignKeys.Select(k => $"{TableDesignText.Name(k.Name)} ({TableDesignText.Pairs(k)}) {TableDesignText.Qualified(k.ReferencedTable)} {k.OnDelete}/{k.OnUpdate}")
            .Order(StringComparer.Ordinal).ToArray();

    /// <summary>The design's lines without foreign keys, references to the table, and indexes (MySQL adds them for foreign keys).</summary>
    private static string[] WithoutForeignKeys(TableDesign design) =>
        TableDesignText.Lines(design).Where(l => !l.StartsWith("foreign key ") && !l.StartsWith("referenced by ") && !l.StartsWith("index ")).ToArray();
}
