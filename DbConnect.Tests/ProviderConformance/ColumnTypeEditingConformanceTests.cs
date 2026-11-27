using Avalonia.Headless.XUnit;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// Issue 09 on PostgreSQL, SQL Server and MySQL: open a table through explorer → Design Table…,
/// widen a precision, a length and fractional seconds and change a collation, save, and reopen.
/// The reopened design must equal the loaded one with exactly those column types and collations
/// changed, and the row must survive. Then a narrowing length goes through the destructive review.
/// Edits the engine's ALTER cannot make without losing a loaded property stay blocked with their reason.
/// </summary>
public sealed class ColumnTypeEditingConformanceTests : MainWindowTestBase
{
    /// <summary>One edit and the declared type and collation the reopened design must show.</summary>
    private sealed record Edit(string Column, Action<ColumnDefinition> Apply, string Type, string? Collation);

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task PostgreSql_PrecisionLengthAndCollationEditsSaveAndReopen()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var schema = $"rain.types_{Guid.NewGuid().ToString("N")[..10]}";
        var table = $"\"{schema}\".\"Types.T\"";
        try
        {
            await TableDesignPostgreSqlTests.Execute(cs, $"""
                CREATE SCHEMA "{schema}";
                CREATE TABLE {table} (id int PRIMARY KEY, qty numeric(10,2) NOT NULL DEFAULT 1 CHECK (qty >= 0),
                    name text COLLATE "C", code varchar(20) DEFAULT 'none', at timestamp(3), total int GENERATED ALWAYS AS (id * 2) STORED);
                CREATE INDEX "ix.code" ON {table} (code);
                COMMENT ON COLUMN {table}.qty IS 'how many';
                INSERT INTO {table} (id, qty, name, code, at) VALUES (1, 12.5, 'Ada', 'abc', '2026-01-02 03:04:05.678');
                """);
            await EditAsync(cs, new PostgreSqlProvider(), schema, null,
                ("qty", "numeric", null, 10, 2, null),
                [
                    new("qty", c => c.Precision = 12, "numeric(12,2)", null),
                    new("code", c => c.Length = "40", "character varying(40)", null),
                    new("at", c => c.Precision = 6, "timestamp(6) without time zone", null),
                    new("name", c => c.Collation = "\"POSIX\"", "text", "\"POSIX\"")
                ],
                new("code", c => c.Length = "10", "character varying(10)", null),
                blocked: new("total", c => c.DataType = "bigint", "", null),
                $"SELECT qty::text, name, code, to_char(at, 'HH24:MI:SS.MS') FROM {table}",
                ["12.50", "Ada", "abc", "03:04:05.678"]);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await TableDesignPostgreSqlTests.Execute(cs, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task SqlServer_PrecisionLengthAndCollationEditsSaveAndReopen()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var database = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        var connection = new SqlConnectionStringBuilder(cs) { InitialCatalog = database, Pooling = false }.ToString();
        try
        {
            await TableDesignSqlServerTests.Execute(cs, $"CREATE DATABASE [{database}]");
            await TableDesignSqlServerTests.Execute(connection, """
                CREATE SCHEMA [types];
                GO
                CREATE TABLE [types].[Types.T] (id int NOT NULL PRIMARY KEY, qty decimal(10,2) NOT NULL CHECK (qty >= 0),
                    name nvarchar(40) COLLATE Latin1_General_CS_AS NULL, code varchar(20) NULL, at datetime2(3) NULL,
                    price decimal(8,2) NOT NULL CONSTRAINT df_price DEFAULT 0, tag int NULL);
                CREATE INDEX [ix.tag] ON [types].[Types.T] (tag);
                EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'how many', @level0type = N'SCHEMA',
                    @level0name = N'types', @level1type = N'TABLE', @level1name = N'Types.T', @level2type = N'COLUMN', @level2name = N'qty';
                INSERT INTO [types].[Types.T] (id, qty, name, code, at) VALUES (1, 12.5, N'Ada', 'abc', '2026-01-02T03:04:05.678');
                """);
            await EditAsync(connection, new SqlServerProvider(), "types", database,
                ("qty", "decimal", null, 10, 2, null),
                [
                    new("qty", c => c.Precision = 12, "decimal(12, 2)", null),
                    new("code", c => c.Length = "40", "varchar(40)", null),
                    new("at", c => c.Precision = 7, "datetime2(7)", null),
                    new("name", c => c.Collation = "Latin1_General_CI_AS", "nvarchar(40)", "Latin1_General_CI_AS")
                ],
                new("code", c => c.Length = "10", "varchar(10)", null),
                // ALTER COLUMN cannot change a column a default constraint depends on.
                blocked: new("price", c => c.Precision = 10, "", null),
                "SELECT CONVERT(varchar(20), qty), name, code, CONVERT(varchar(12), CAST(at AS time(3))) FROM [types].[Types.T]",
                ["12.50", "Ada", "abc", "03:04:05.678"]);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await TableDesignSqlServerTests.Execute(cs, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task MySql_PrecisionLengthAndCollationEditsSaveAndReopen_RestatingDefaultsAndComments()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var db = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        try
        {
            await TableDesignMySqlTests.Execute(cs, $"""
                CREATE DATABASE `{db}`;
                CREATE TABLE `{db}`.`Types.T` (id INT NOT NULL PRIMARY KEY, qty DECIMAL(10,2) NOT NULL DEFAULT 1.00 COMMENT 'how many',
                    name VARCHAR(40) COLLATE utf8mb4_bin, code VARCHAR(20) DEFAULT 'it''s', at DATETIME(3),
                    changed TIMESTAMP NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP, CHECK (qty >= 0), INDEX `ix.code` (code));
                INSERT INTO `{db}`.`Types.T` (id, qty, name, code, at) VALUES (1, 12.5, 'Ada', 'abc', '2026-01-02 03:04:05.678');
                """);
            await EditAsync(new MySqlConnectionStringBuilder(cs) { Database = db }.ToString(), new MySqlProvider(), null, db,
                ("qty", "decimal", null, 10, 2, null),
                [
                    new("qty", c => c.Precision = 12, "decimal(12,2)", null),
                    new("code", c => c.Length = "40", "varchar(40)", null),
                    new("at", c => c.Precision = 6, "datetime(6)", null),
                    new("name", c => c.Collation = "utf8mb4_general_ci", "varchar(40)", "utf8mb4_general_ci")
                ],
                new("code", c => c.Length = "10", "varchar(10)", null),
                // MODIFY would drop ON UPDATE CURRENT_TIMESTAMP.
                blocked: new("changed", c => c.Precision = 3, "", null),
                $"SELECT CAST(qty AS CHAR), name, code, DATE_FORMAT(at, '%H:%i:%s.%f') FROM `{db}`.`Types.T`",
                ["12.50", "Ada", "abc", "03:04:05.678000"]);
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            await TableDesignMySqlTests.Drop(cs, db);
        }
    }

    private async Task EditAsync(string connection, IDatabaseProvider provider, string? schema, string? catalog,
        (string Column, string Label, string? Length, int? Precision, int? Scale, string? Collation) loaded,
        IReadOnlyList<Edit> widening, Edit narrowing, Edit blocked, string rowQuery, string[] row)
    {
        var session = new DatabaseSession(new ConnectionInfo { Name = "types", Provider = provider.ProviderName, ConnectionString = connection }, provider);
        var vm = CreateMainWindowViewModel();
        var root = await TableDesignRoundTrip.ConnectAsync(vm, session);
        var node = await TableDesignRoundTrip.FindTableAsync(vm, root, schema, "Types.T");
        var designer = await TableDesignRoundTrip.OpenAsync(vm, node);
        Assert.True(designer.Design.IsLoaded, designer.Design.Error);
        var source = (ITableDesignSource)provider;
        var before = designer.Design.Baseline!;

        // The loaded declared type is split into its name and modifiers; nothing is planned until something is edited.
        var column = designer.Columns.Single(c => c.Name == loaded.Column);
        Assert.Equal(loaded, (column.Name, column.DataType, column.Length, column.Precision, column.Scale, column.Collation));
        Assert.Equal("", designer.PreviewSql);

        // A change the engine's ALTER would make by dropping a loaded property is blocked before anything runs.
        blocked.Apply(designer.Columns.Single(c => c.Name == blocked.Column));
        Assert.Contains(designer.Plan.Blocked, b => b.StartsWith($"Column {blocked.Column}: "));
        Assert.False(designer.PlanAllowsSave);
        await designer.ReloadCommand.ExecuteAsync(null);
        Assert.Equal("", designer.PreviewSql);

        foreach (var edit in widening) edit.Apply(designer.Columns.Single(c => c.Name == edit.Column));
        Assert.True(designer.Plan.CanExecute, string.Join("\n", designer.Plan.Problems) + "\n" + designer.PreviewSql);
        // Widening needs no review; the collation change does.
        Assert.StartsWith($"Changes the collation of {widening[^1].Column} ", Assert.Single(designer.Plan.Destructive));
        await SaveAsync(designer);
        var after = await source.GetTableDesignAsync(connection, designer.Design.Table!);
        Assert.Equal(TableDesignText.Lines(Expected(before, widening)), TableDesignText.Lines(after));
        Assert.Equal(TableDesignText.Lines(after), TableDesignText.Lines(designer.Design.Baseline!));

        narrowing.Apply(designer.Columns.Single(c => c.Name == narrowing.Column));
        Assert.Contains(designer.Plan.Destructive, d => d.StartsWith($"Changes the type of {narrowing.Column} from "));
        Assert.False(designer.PlanAllowsSave);
        await SaveAsync(designer);
        Assert.Equal(TableDesignText.Lines(Expected(after, [narrowing])),
            TableDesignText.Lines(await source.GetTableDesignAsync(connection, designer.Design.Table!)));

        var values = Assert.Single((await provider.ExecuteQueryAsync(connection, rowQuery)).ObjectRows);
        Assert.Equal(row, values.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static async Task SaveAsync(TableDesignerViewModel designer)
    {
        designer.Review.DestructiveConfirmed = designer.Review.NeedsConfirmation;
        await designer.SaveCommand.ExecuteAsync(null);
        Assert.True(designer.StatusMessage?.Contains("successfully") == true, designer.StatusMessage + "\n" + designer.Plan.Sql);
        Assert.Equal("", designer.PreviewSql);
    }

    private static TableDesign Expected(TableDesign design, IReadOnlyList<Edit> edits) => design with
    {
        Columns = design.Columns.Select(c => edits.FirstOrDefault(e => e.Column == c.Name) is { } edit
            ? c with { DataType = edit.Type, Collation = edit.Collation ?? c.Collation }
            : c).ToList()
    };
}
