using Avalonia.Headless.XUnit;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// Issue 11 on PostgreSQL, SQL Server and MySQL: open a table through explorer → Design Table…,
/// re-order its composite primary key, rename a unique key, drop one the engine named itself and add
/// another, then save and reopen. Everything but the keys and their backing indexes reopens exactly as
/// loaded, the rows survive, and the engine enforces the saved keys. Dropping a key a foreign key
/// depends on is blocked before anything runs, and saving the reopened table does nothing.
/// </summary>
public sealed class KeyEditingConformanceTests : MainWindowTestBase
{
    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task PostgreSql_KeysAreReorderedRenamedDroppedAndAdded()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var schema = $"rain.keys_{Guid.NewGuid().ToString("N")[..10]}";
        var table = $"\"{schema}\".\"Keys.T\"";
        try
        {
            await TableDesignPostgreSqlTests.Execute(cs, $"""
                CREATE SCHEMA "{schema}";
                CREATE TABLE {table} (a int NOT NULL, b int NOT NULL, c text, d text, e text, note text,
                    CONSTRAINT pk_t PRIMARY KEY (a, b), CONSTRAINT uq_c UNIQUE (c), UNIQUE (d), CONSTRAINT uq_e UNIQUE (e));
                CREATE INDEX "ix.note" ON {table} (note);
                COMMENT ON COLUMN {table}.note IS 'kept';
                CREATE TABLE "{schema}".child (id int PRIMARY KEY, e text REFERENCES {table} (e));
                INSERT INTO {table} VALUES (1, 10, 'c1', 'd1', 'e1', 'n1'), (2, 20, 'c2', 'd2', 'e2', 'n2');
                """);
            await EditAsync(cs, new PostgreSqlProvider(), schema, table,
                ["primary pk_t (a, b)", "unique Keys.T_d_key (d)", "unique uq_c (c)", "unique uq_e (e)"],
                "uq_e", $"foreign key child_e_fkey on {schema}.child",
                ["primary pk_t (b, a)", "unique uq_c2 (c)", "unique uq_e (e)", "unique uq_note (note)"],
                pk => pk.Name = "pk_t");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await TableDesignPostgreSqlTests.Execute(cs, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task SqlServer_KeysAreReorderedRenamedDroppedAndAdded_KeepingTheClusteredKey()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var database = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        var connection = new SqlConnectionStringBuilder(cs) { InitialCatalog = database, Pooling = false }.ToString();
        try
        {
            await TableDesignSqlServerTests.Execute(cs, $"CREATE DATABASE [{database}]");
            await TableDesignSqlServerTests.Execute(connection, """
                CREATE SCHEMA [keys];
                GO
                CREATE TABLE [keys].[Keys.T] (a int NOT NULL, b int NOT NULL, c nvarchar(20) NULL, d nvarchar(20) NULL,
                    e nvarchar(20) NULL, note nvarchar(20) NULL,
                    CONSTRAINT pk_t PRIMARY KEY CLUSTERED (a, b), CONSTRAINT uq_c UNIQUE (c), UNIQUE (d), CONSTRAINT uq_e UNIQUE (e));
                CREATE INDEX [ix.note] ON [keys].[Keys.T] (note);
                CREATE TABLE [keys].child (id int NOT NULL PRIMARY KEY, e nvarchar(20) NULL CONSTRAINT fk_child REFERENCES [keys].[Keys.T] (e));
                INSERT INTO [keys].[Keys.T] VALUES (1, 10, N'c1', N'd1', N'e1', N'n1'), (2, 20, N'c2', N'd2', N'e2', N'n2');
                """);
            var design = await EditAsync(connection, new SqlServerProvider(), "keys", "[keys].[Keys.T]",
                ["primary pk_t (a, b)", "unique (unnamed) (d)", "unique uq_c (c)", "unique uq_e (e)"],
                "uq_e", "foreign key fk_child on " + database + ".keys.child",
                ["primary pk_t (b, a)", "unique uq_c2 (c)", "unique uq_e (e)", "unique uq_note (note)"],
                pk => pk.Name = "pk_t");
            Assert.Equal("CLUSTERED", design.Indexes.Single(i => i.Origin == TableDesignIndexOrigin.PrimaryKey).Method);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await TableDesignSqlServerTests.Execute(cs, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    /// <summary>A key SQL Server named itself is addressed through its backing index, which carries the generated name.</summary>
    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task SqlServer_AKeyTheEngineNamedCanBeNamed()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var database = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        var connection = new SqlConnectionStringBuilder(cs) { InitialCatalog = database, Pooling = false }.ToString();
        try
        {
            await TableDesignSqlServerTests.Execute(cs, $"CREATE DATABASE [{database}]");
            await TableDesignSqlServerTests.Execute(connection, """
                CREATE TABLE dbo.[Keys.T] (id int NOT NULL PRIMARY KEY, d nvarchar(20) NULL UNIQUE);
                INSERT INTO dbo.[Keys.T] VALUES (1, N'd1');
                """);
            var provider = new SqlServerProvider();
            var session = new DatabaseSession(new ConnectionInfo { Name = "keys", Provider = provider.ProviderName, ConnectionString = connection }, provider);
            var vm = CreateMainWindowViewModel();
            var designer = await TableDesignRoundTrip.OpenAsync(vm,
                await TableDesignRoundTrip.FindTableAsync(vm, await TableDesignRoundTrip.ConnectAsync(vm, session), "dbo", "Keys.T"));
            Assert.True(designer.Design.IsLoaded, designer.Design.Error);
            var key = Key(designer, ["d"]);
            Assert.Null(key.Name);
            Assert.StartsWith("UQ__", key.BackingIndex!.Name);
            key.Name = "uq_d";
            Assert.Contains($"QUOTENAME(N'{key.BackingIndex.Name}')", designer.PreviewSql);
            Assert.Empty(designer.Plan.Destructive);
            await designer.SaveCommand.ExecuteAsync(null);
            Assert.True(designer.StatusMessage?.Contains("successfully") == true, designer.StatusMessage + "\n" + designer.Plan.Sql);
            Assert.Equal(["primary (unnamed) (id)", "unique uq_d (d)"], Keys(designer.Design.Baseline!));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await TableDesignSqlServerTests.Execute(cs, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task MySql_KeysAreReorderedRenamedDroppedAndAdded_InOneStatementEach()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var db = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        try
        {
            await TableDesignMySqlTests.Execute(cs, $"""
                CREATE DATABASE `{db}`;
                CREATE TABLE `{db}`.`Keys.T` (a INT NOT NULL, b INT NOT NULL, c VARCHAR(20), d VARCHAR(20), e VARCHAR(20),
                    note VARCHAR(20) COMMENT 'kept', PRIMARY KEY (a, b), CONSTRAINT uq_c UNIQUE (c), UNIQUE (d), CONSTRAINT uq_e UNIQUE (e),
                    INDEX `ix.note` (note));
                CREATE TABLE `{db}`.child (id INT PRIMARY KEY, e VARCHAR(20), CONSTRAINT fk_child FOREIGN KEY (e) REFERENCES `{db}`.`Keys.T` (e));
                INSERT INTO `{db}`.`Keys.T` VALUES (1, 10, 'c1', 'd1', 'e1', 'n1'), (2, 20, 'c2', 'd2', 'e2', 'n2');
                """);
            var connection = new MySqlConnectionStringBuilder(cs) { Database = db }.ToString();
            await EditAsync(connection, new MySqlProvider(), null, "`Keys.T`",
                ["primary (unnamed) (a, b)", "unique d (d)", "unique uq_c (c)", "unique uq_e (e)"],
                "uq_e", $"foreign key fk_child on {db}.child",
                ["primary (unnamed) (b, a)", "unique uq_c2 (c)", "unique uq_e (e)", "unique uq_note (note)"],
                pk =>
                {
                    // MySQL always calls its primary key PRIMARY: the name cannot be set.
                    Assert.True(pk.NameLocked);
                });
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            await TableDesignMySqlTests.Drop(cs, db);
        }
    }

    private async Task<TableDesign> EditAsync(string connection, IDatabaseProvider provider, string? schema, string table,
        string[] loadedKeys, string referencedKey, string reference, string[] savedKeys, Action<KeyDefinition> checkPrimaryKey)
    {
        var session = new DatabaseSession(new ConnectionInfo { Name = "keys", Provider = provider.ProviderName, ConnectionString = connection }, provider);
        var vm = CreateMainWindowViewModel();
        var root = await TableDesignRoundTrip.ConnectAsync(vm, session);
        var node = await TableDesignRoundTrip.FindTableAsync(vm, root, schema, "Keys.T");
        var designer = await TableDesignRoundTrip.OpenAsync(vm, node);
        Assert.True(designer.Design.IsLoaded, designer.Design.Error);
        Assert.Equal("", designer.PreviewSql);
        var source = (ITableDesignSource)provider;
        var before = designer.Design.Baseline!;
        Assert.Equal(loadedKeys, Keys(before));
        checkPrimaryKey(Key(designer, ["a", "b"]));

        // A foreign key depends on this key, so dropping it is blocked before anything runs.
        designer.Keys.DropKeyCommand.Execute(Key(designer, ["e"]));
        Assert.Contains(designer.Plan.Blocked, b => b.StartsWith($"Unique key {referencedKey} is referenced by {reference}"));
        Assert.False(designer.PlanAllowsSave);
        await designer.ReloadCommand.ExecuteAsync(null);
        Assert.Equal("", designer.PreviewSql);

        Key(designer, ["a", "b"]).Columns.Move(1, 0);
        Key(designer, ["c"]).Name = "uq_c2";
        designer.Keys.DropKeyCommand.Execute(Key(designer, ["d"]));
        designer.Keys.AddUniqueKeyCommand.Execute(null);
        designer.Keys.NewMember = "note";
        designer.Keys.AddMemberCommand.Execute(null);
        designer.Keys.Selected!.Name = "uq_note";
        Assert.Equal(2, designer.Plan.Destructive.Count);
        Assert.False(designer.PlanAllowsSave);
        designer.Review.DestructiveConfirmed = true;
        await designer.SaveCommand.ExecuteAsync(null);
        Assert.True(designer.StatusMessage?.Contains("successfully") == true, designer.StatusMessage + "\n" + designer.Plan.Sql);

        var after = await source.GetTableDesignAsync(connection, designer.Design.Table!);
        Assert.Equal(savedKeys, Keys(after));
        Assert.Equal(WithoutKeys(before), WithoutKeys(after));
        Assert.Equal(TableDesignText.Lines(after), TableDesignText.Lines(designer.Design.Baseline!));
        Assert.Equal(["1|10|c1|d1|e1|n1", "2|20|c2|d2|e2|n2"],
            await Rows(provider, connection, $"SELECT a, b, c, d, e, note FROM {table} ORDER BY a"));

        // The saved keys are enforced; the dropped one is not.
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(connection, $"INSERT INTO {table} (a, b) VALUES (1, 10)"));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(connection, $"INSERT INTO {table} (a, b, note) VALUES (3, 30, 'n1')"));
        await provider.ExecuteNonQueryAsync(connection, $"INSERT INTO {table} (a, b, d) VALUES (4, 40, 'd1')");

        await designer.SaveCommand.ExecuteAsync(null);
        Assert.Equal("No changes to save.", designer.StatusMessage);
        return after;
    }

    private static KeyDefinition Key(TableDesignerViewModel designer, string[] columns) =>
        designer.Keys.Items.Single(k => k.Columns.SequenceEqual(columns));

    private static string[] Keys(TableDesign design) =>
        [.. design.PrimaryKey is { } pk ? [$"primary {TableDesignText.Name(pk.Name)} ({string.Join(", ", pk.Columns)})"] : Array.Empty<string>(),
         .. design.UniqueKeys.Select(k => $"unique {TableDesignText.Name(k.Name)} ({string.Join(", ", k.Columns)})").Order(StringComparer.Ordinal)];

    /// <summary>The design's lines without its keys and the indexes that back them.</summary>
    private static string[] WithoutKeys(TableDesign design) =>
        TableDesignText.Lines(design).Where(l => !l.StartsWith("primary key ") && !l.StartsWith("unique ")
            && !(l.StartsWith("index ") && (l.Contains(" PrimaryKey (") || l.Contains(" UniqueKey (")))).ToArray();

    private static async Task<string[]> Rows(IDatabaseProvider provider, string connection, string sql) =>
        (await provider.ExecuteQueryAsync(connection, sql)).ObjectRows
            .Select(r => string.Join("|", r.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
}
