using Avalonia.Headless.XUnit;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// Issue 13 on PostgreSQL, SQL Server and MySQL: open a table through explorer → Design Table…, change a
/// check's expression, rename a named one and one the engine named (whose expression has a string
/// literal), drop one and add one, then save and reopen. Everything but the checks reopens exactly as
/// loaded, the row survives, and the engine accepts and rejects rows by the saved checks. A check whose
/// enforcement state re-creating would lose stays blocked, and saving again does nothing.
/// </summary>
public sealed class CheckEditingConformanceTests : MainWindowTestBase
{
    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task PostgreSql_ChecksAreChangedRenamedDroppedAndAdded()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var schema = $"rain.ck_{Guid.NewGuid().ToString("N")[..10]}";
        var table = $"\"{schema}\".\"Checks.T\"";
        try
        {
            await TableDesignPostgreSqlTests.Execute(cs, $"""
                CREATE SCHEMA "{schema}";
                CREATE TABLE {table} (id int PRIMARY KEY, qty int, price numeric(8,2), note varchar(40),
                    CONSTRAINT ck_qty CHECK (qty >= 0), CONSTRAINT ck_price CHECK (price > 0), CHECK (note <> 'it''s bad'),
                    CONSTRAINT ck_cap CHECK (qty < 1000));
                INSERT INTO {table} VALUES (1, 5, 2.5, 'a');
                ALTER TABLE {table} ADD CONSTRAINT ck_nv CHECK (qty <> 7) NOT VALID;
                """);
            await EditAsync(cs, new PostgreSqlProvider(), schema, table, "Checks.T_note_check", blockedState: "ck_nv");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await TableDesignPostgreSqlTests.Execute(cs, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task SqlServer_ChecksAreChangedRenamedDroppedAndAdded()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var database = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        var connection = new SqlConnectionStringBuilder(cs) { InitialCatalog = database, Pooling = false }.ToString();
        try
        {
            await TableDesignSqlServerTests.Execute(cs, $"CREATE DATABASE [{database}]");
            await TableDesignSqlServerTests.Execute(connection, """
                CREATE SCHEMA ck;
                GO
                CREATE TABLE ck.[Checks.T] (id int NOT NULL PRIMARY KEY, qty int NULL, price decimal(8,2) NULL, note nvarchar(40) NULL,
                    CONSTRAINT ck_qty CHECK (qty >= 0), CONSTRAINT ck_price CHECK (price > 0), CHECK (note <> N'it''s bad'),
                    CONSTRAINT ck_cap CHECK (qty < 1000));
                INSERT INTO ck.[Checks.T] VALUES (1, 5, 2.5, N'a');
                ALTER TABLE ck.[Checks.T] WITH NOCHECK ADD CONSTRAINT ck_nv CHECK (qty <> 7);
                """);
            await EditAsync(connection, new SqlServerProvider(), "ck", "ck.[Checks.T]", generatedName: null, blockedState: "ck_nv");
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await TableDesignSqlServerTests.Execute(cs, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task MySql_ChecksAreChangedRenamedDroppedAndAdded()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var db = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        try
        {
            await TableDesignMySqlTests.Execute(cs, $"""
                CREATE DATABASE `{db}`;
                CREATE TABLE `{db}`.`Checks.T` (id INT NOT NULL PRIMARY KEY, qty INT, price DECIMAL(8,2), note VARCHAR(40),
                    CONSTRAINT ck_qty CHECK (qty >= 0), CONSTRAINT ck_price CHECK (price > 0), CHECK (note <> 'it''s bad'),
                    CONSTRAINT ck_cap CHECK (qty < 1000), CONSTRAINT ck_nv CHECK (qty <> 7) NOT ENFORCED);
                INSERT INTO `{db}`.`Checks.T` VALUES (1, 5, 2.5, 'a');
                """);
            await EditAsync(new MySqlConnectionStringBuilder(cs) { Database = db }.ToString(), new MySqlProvider(), null, "`Checks.T`",
                "Checks.T_chk_1", blockedState: "ck_nv");
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            await TableDesignMySqlTests.Drop(cs, db);
        }
    }

    private async Task EditAsync(string connection, IDatabaseProvider provider, string? schema, string table, string? generatedName, string blockedState)
    {
        var session = new DatabaseSession(new ConnectionInfo { Name = "checks", Provider = provider.ProviderName, ConnectionString = connection }, provider);
        var vm = CreateMainWindowViewModel();
        var root = await TableDesignRoundTrip.ConnectAsync(vm, session);
        var node = await TableDesignRoundTrip.FindTableAsync(vm, root, schema, "Checks.T");
        var designer = await TableDesignRoundTrip.OpenAsync(vm, node);
        Assert.True(designer.Design.IsLoaded, designer.Design.Error);
        Assert.Equal("", designer.PreviewSql);
        var source = (ITableDesignSource)provider;
        var before = designer.Design.Baseline!;
        EditableCheck Check(string name) => designer.Checks.Items.Single(c => c.Name == name);

        // Re-creating a check would lose its enforcement state, so it is blocked before anything runs.
        var stateful = Check(blockedState);
        Assert.NotNull(stateful.StateDisplay);
        stateful.Expression = "qty <> 8";
        Assert.Contains(designer.Plan.Blocked, b => b.StartsWith($"Check {blockedState} cannot be re-created without losing its"));
        Assert.False(designer.PlanAllowsSave);
        await designer.ReloadCommand.ExecuteAsync(null);
        Assert.Equal("", designer.PreviewSql);

        Check("ck_qty").Expression = "qty >= 1";
        Check("ck_price").Name = "ck_price2";
        // The engine-named check (its expression has a string literal) loads unnamed on SQL Server, or with its generated name.
        var literal = designer.Checks.Items.Single(c => c.Expression.Contains("it", StringComparison.Ordinal) && c.Expression.Contains("bad"));
        Assert.Equal(generatedName, literal.Name);
        literal.Name = "ck_literal";
        designer.Checks.DropCheckCommand.Execute(Check("ck_cap"));
        designer.Checks.AddCheckCommand.Execute(null);
        designer.Checks.Selected!.Name = "ck_new";
        designer.Checks.Selected!.Expression = "price < 100";
        Assert.True(designer.Plan.CanExecute, string.Join("\n", designer.Plan.Problems) + "\n" + designer.Plan.Sql);
        designer.Review.DestructiveConfirmed = designer.Review.NeedsConfirmation;
        await designer.SaveCommand.ExecuteAsync(null);
        Assert.True(designer.StatusMessage?.Contains("successfully") == true, designer.StatusMessage + "\n" + designer.Plan.Sql);

        var after = await source.GetTableDesignAsync(connection, designer.Design.Table!);
        Assert.Equal(["ck_literal", "ck_new", "ck_nv", "ck_price2", "ck_qty"], after.Checks.Select(c => c.Name ?? "(unnamed)").Order(StringComparer.Ordinal));
        Assert.Contains("1", after.Checks.Single(c => c.Name == "ck_qty").Expression);
        Assert.Equal(WithoutChecks(before), WithoutChecks(after));
        Assert.Equal(TableDesignText.Lines(after), TableDesignText.Lines(designer.Design.Baseline!));
        var row = Assert.Single((await provider.ExecuteQueryAsync(connection, $"SELECT id, qty, note FROM {table}")).ObjectRows);
        Assert.Equal(["1", "5", "a"], row.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)));

        // The saved checks accept and reject rows; the dropped one no longer applies.
        var literalNote = provider is SqlServerProvider ? "N'it''s bad'" : "'it''s bad'";
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(connection, $"INSERT INTO {table} (id, qty, price) VALUES (2, 0, 1)"));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(connection, $"INSERT INTO {table} (id, qty, price) VALUES (3, 1, 200)"));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(connection, $"INSERT INTO {table} (id, qty, price, note) VALUES (4, 1, 1, {literalNote})"));
        await provider.ExecuteNonQueryAsync(connection, $"INSERT INTO {table} (id, qty, price) VALUES (5, 5000, 1)");

        await designer.SaveCommand.ExecuteAsync(null);
        Assert.Equal("No changes to save.", designer.StatusMessage);
    }

    private static string[] WithoutChecks(TableDesign design) =>
        TableDesignText.Lines(design).Where(l => !l.StartsWith("check ")).ToArray();
}
