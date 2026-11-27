using Avalonia.Headless.XUnit;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// Designer load and unrelated-edit round trip for the PostgreSQL, SQL Server and MySQL fixtures
/// (issue 06), and the shared preview/save plan (issue 07). The fixtures sit outside the
/// connection's default schema next to same-named tables: the plan names tables by their schema.
/// </summary>
public sealed class TableDesignWorkflowConformanceTests : MainWindowTestBase
{
    [AvaloniaTheory]
    [InlineData("Order.Items", false)]
    [InlineData("order.items", false)]
    [InlineData("measurements", true)]
    [InlineData("measurements_2026", true)]
    [Trait("Category", "Integration")]
    public async Task PostgreSql_UnrelatedEditRoundTripKeepsTheDesignOrIsBlocked(string table, bool blocked)
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var schema = $"rain.flow_{Guid.NewGuid().ToString("N")[..10]}";
        try
        {
            await TableDesignPostgreSqlTests.Execute(cs, TableDesignPostgreSqlTests.Fixture(schema));
            var lost = await RoundTripAsync(cs, new PostgreSqlProvider(), schema, null, table, "integer");
            AssertOutcome(lost, blocked);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await TableDesignPostgreSqlTests.Drop(cs, schema);
        }
    }

    [AvaloniaTheory]
    [InlineData("Order.Items", false)]
    [InlineData("parent.t", false)]
    [InlineData("Versioned", true)]
    [Trait("Category", "Integration")]
    public async Task SqlServer_UnrelatedEditRoundTripKeepsTheDesignOrIsBlocked(string table, bool blocked)
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var database = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        var connection = new SqlConnectionStringBuilder(cs) { InitialCatalog = database, Pooling = false }.ToString();
        try
        {
            await TableDesignSqlServerTests.Execute(cs, $"CREATE DATABASE [{database}]");
            // Not dbo, the connection's default schema; flow_b holds a same-named table.
            await TableDesignSqlServerTests.Execute(connection, TableDesignSqlServerTests.Fixture("flow"));
            var lost = await RoundTripAsync(connection, new SqlServerProvider(), "flow", database, table, "int",
                TableDesignSqlServerTests.Normalized);
            AssertOutcome(lost, blocked);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await TableDesignSqlServerTests.Execute(cs, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    [AvaloniaTheory]
    // Adding a column no longer rewrites every column to store comments, so Order.Items keeps its defaults, collations and generation.
    [InlineData("Order.Items", false)]
    [InlineData("order.items", false)]
    [InlineData("parted", true)]
    [Trait("Category", "Integration")]
    public async Task MySql_UnrelatedEditRoundTripKeepsTheDesignOrIsBlocked(string table, bool blocked)
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var db = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        try
        {
            await TableDesignMySqlTests.Execute(cs, TableDesignMySqlTests.Fixture(db));
            var connection = new MySqlConnectionStringBuilder(cs) { Database = db }.ToString();
            var lost = await RoundTripAsync(connection, new MySqlProvider(), null, db, table, "INT");
            AssertOutcome(lost, blocked);
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            await TableDesignMySqlTests.Drop(cs, db);
        }
    }

    [AvaloniaTheory]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    [InlineData("MySQL")]
    [Trait("Category", "Integration")]
    public async Task CommentsArePartOfThePlan_AndAnUnchangedTablePlansNothing(string providerName)
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (string.IsNullOrWhiteSpace(cs)) return;
        var suffix = Guid.NewGuid().ToString("N")[..10];
        switch (providerName)
        {
            case "PostgreSQL":
                {
                    var schema = $"rain.plan_{suffix}";
                    try
                    {
                        await TableDesignPostgreSqlTests.Execute(cs,
                            $"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".\"Plan.T\" (id int PRIMARY KEY, label varchar(20));");
                        await CommentsAsync(cs, new PostgreSqlProvider(), schema, "Plan.T");
                    }
                    finally
                    {
                        NpgsqlConnection.ClearAllPools();
                        await TableDesignPostgreSqlTests.Execute(cs, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
                    }
                    break;
                }
            case "SQL Server":
                {
                    var database = $"rain_scratch_{suffix}";
                    var connection = new SqlConnectionStringBuilder(cs) { InitialCatalog = database, Pooling = false }.ToString();
                    try
                    {
                        await TableDesignSqlServerTests.Execute(cs, $"CREATE DATABASE [{database}]");
                        await TableDesignSqlServerTests.Execute(connection,
                            "CREATE SCHEMA [plan.s];\nGO\nCREATE TABLE [plan.s].[Plan.T] (id int NOT NULL PRIMARY KEY, label nvarchar(20) NULL);");
                        await CommentsAsync(connection, new SqlServerProvider(), "plan.s", "Plan.T");
                    }
                    finally
                    {
                        SqlConnection.ClearAllPools();
                        await TableDesignSqlServerTests.Execute(cs, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
                    }
                    break;
                }
            default:
                {
                    var db = $"rain_scratch_{suffix}";
                    try
                    {
                        await TableDesignMySqlTests.Execute(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.`Plan.T` (id INT NOT NULL PRIMARY KEY, label VARCHAR(20));");
                        await CommentsAsync(new MySqlConnectionStringBuilder(cs) { Database = db }.ToString(), new MySqlProvider(), null, "Plan.T");
                    }
                    finally
                    {
                        MySqlConnection.ClearAllPools();
                        await TableDesignMySqlTests.Drop(cs, db);
                    }
                    break;
                }
        }
    }

    /// <summary>
    /// Issue 08: a save whose second step fails (a unique index over duplicate values). Engines with
    /// transactional DDL roll the added column back; MySQL commits each step, so the column stays and
    /// the outcome is partial, with Save waiting for a reload.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    [InlineData("MySQL")]
    [Trait("Category", "Integration")]
    public async Task AFailingStep_IsRolledBackOrReportedAsPartialByTheEnginesDdlSemantics(string providerName)
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (string.IsNullOrWhiteSpace(cs)) return;
        var suffix = Guid.NewGuid().ToString("N")[..10];
        const string Rows = " INSERT INTO {0} VALUES (1, 'same'); INSERT INTO {0} VALUES (2, 'same');";
        switch (providerName)
        {
            case "PostgreSQL":
                {
                    var schema = $"rain.guard_{suffix}";
                    var table = $"\"{schema}\".\"Guard.T\"";
                    try
                    {
                        await TableDesignPostgreSqlTests.Execute(cs, $"CREATE SCHEMA \"{schema}\"; CREATE TABLE {table} (id int PRIMARY KEY, label varchar(20));"
                            + string.Format(Rows, table));
                        await FailingStepAsync(cs, new PostgreSqlProvider(), schema, partial: false);
                    }
                    finally
                    {
                        NpgsqlConnection.ClearAllPools();
                        await TableDesignPostgreSqlTests.Execute(cs, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
                    }
                    break;
                }
            case "SQL Server":
                {
                    var database = $"rain_scratch_{suffix}";
                    var connection = new SqlConnectionStringBuilder(cs) { InitialCatalog = database, Pooling = false }.ToString();
                    try
                    {
                        await TableDesignSqlServerTests.Execute(cs, $"CREATE DATABASE [{database}]");
                        await TableDesignSqlServerTests.Execute(connection, "CREATE SCHEMA [guard];\nGO\nCREATE TABLE [guard].[Guard.T] (id int NOT NULL PRIMARY KEY, label nvarchar(20) NULL);"
                            + string.Format(Rows, "[guard].[Guard.T]"));
                        await FailingStepAsync(connection, new SqlServerProvider(), "guard", partial: false);
                    }
                    finally
                    {
                        SqlConnection.ClearAllPools();
                        await TableDesignSqlServerTests.Execute(cs, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
                    }
                    break;
                }
            default:
                {
                    var db = $"rain_scratch_{suffix}";
                    try
                    {
                        await TableDesignMySqlTests.Execute(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.`Guard.T` (id INT NOT NULL PRIMARY KEY, label VARCHAR(20));"
                            + string.Format(Rows, $"`{db}`.`Guard.T`"));
                        await FailingStepAsync(new MySqlConnectionStringBuilder(cs) { Database = db }.ToString(), new MySqlProvider(), null, partial: true);
                    }
                    finally
                    {
                        MySqlConnection.ClearAllPools();
                        await TableDesignMySqlTests.Drop(cs, db);
                    }
                    break;
                }
        }
    }

    private async Task FailingStepAsync(string connection, IDatabaseProvider provider, string? schema, bool partial)
    {
        var session = new DatabaseSession(new ConnectionInfo { Name = "guard", Provider = provider.ProviderName, ConnectionString = connection }, provider);
        var vm = CreateMainWindowViewModel();
        var root = await TableDesignRoundTrip.ConnectAsync(vm, session);
        var node = await TableDesignRoundTrip.FindTableAsync(vm, root, schema, "Guard.T");
        var designer = await TableDesignRoundTrip.OpenAsync(vm, node);
        designer.Columns.Add(new ColumnDefinition { Name = "added", DataType = "int", IsNew = true });
        designer.AddIndex("guard_unique", true, "label");
        Assert.Equal(2, designer.Plan.Steps.Count);
        Assert.Equal(partial, designer.Review.CommitBoundary != null);

        await designer.SaveCommand.ExecuteAsync(null);

        var outcome = designer.Review.Outcome!;
        Assert.Equal(partial ? TableChangeOutcomeKind.Partial : TableChangeOutcomeKind.NotApplied, outcome.Kind);
        Assert.Equal(partial ? ["Applied", "Failed"] : new[] { "Rolled back", "Failed" }, designer.Review.Steps.Select(s => s.Status));
        Assert.NotNull(designer.Review.Steps[1].Error);
        var design = await ((ITableDesignSource)provider).GetTableDesignAsync(connection, designer.Design.Table!);
        Assert.Equal(partial, design.Columns.Any(c => c.Name == "added"));
        Assert.DoesNotContain(design.Indexes, i => i.Name == "guard_unique");
        Assert.Equal(partial, designer.Review.RequiresReload);
        Assert.Contains(designer.Columns, c => c.Name == "added");
    }

    /// <summary>
    /// Sets, then clears, a table and a column comment through the designer. Each save runs the
    /// previewed SQL, the reopened design shows the result, and the reloaded designer plans nothing.
    /// </summary>
    private async Task CommentsAsync(string connection, IDatabaseProvider provider, string? schema, string table)
    {
        var session = new DatabaseSession(new ConnectionInfo { Name = "plan", Provider = provider.ProviderName, ConnectionString = connection }, provider);
        var vm = CreateMainWindowViewModel();
        var root = await TableDesignRoundTrip.ConnectAsync(vm, session);
        var node = await TableDesignRoundTrip.FindTableAsync(vm, root, schema, table);
        var designer = await TableDesignRoundTrip.OpenAsync(vm, node);
        var source = (ITableDesignSource)provider;
        Assert.True(designer.Design.IsLoaded, designer.Design.Error);
        Assert.Equal("", designer.PreviewSql);

        foreach (var (tableComment, columnComment) in new[] { ("it's the \\ plan", "the label"), ("", (string?)null) })
        {
            designer.TableComment = tableComment;
            designer.Columns.Single(c => c.Name == "label").Comment = columnComment;
            Assert.NotEqual("", designer.PreviewSql);
            Assert.True(designer.PlanAllowsSave, string.Join("\n", designer.ValidationErrors));
            await designer.SaveCommand.ExecuteAsync(null);
            Assert.True(designer.StatusMessage?.Contains("successfully") == true, designer.StatusMessage + "\n" + designer.Plan.Sql);
            var design = await source.GetTableDesignAsync(connection, designer.Design.Table!);
            Assert.Equal(tableComment == "" ? null : tableComment, design.Comment);
            Assert.Equal(columnComment, design.Columns.Single(c => c.Name == "label").Comment);
            Assert.Equal(["id"], design.PrimaryKey!.Columns);
            Assert.Equal("", designer.PreviewSql);
        }
    }

    private async Task<List<string>?> RoundTripAsync(string connection, IDatabaseProvider provider, string? schema, string? catalog,
        string table, string addedType, Func<TableDesign, IReadOnlyList<string>>? lines = null)
    {
        var session = new DatabaseSession(new ConnectionInfo { Name = "flow", Provider = provider.ProviderName, ConnectionString = connection }, provider);
        var vm = CreateMainWindowViewModel();
        var root = await TableDesignRoundTrip.ConnectAsync(vm, session);
        var node = await TableDesignRoundTrip.FindTableAsync(vm, root, schema, table);
        return await TableDesignRoundTrip.RunAsync(vm, node, session,
            new DatabaseObjectIdentity(catalog, schema, table, SchemaNodeType.Table), addedType, lines);
    }

    private static void AssertOutcome(List<string>? lost, bool blocked)
    {
        if (blocked) Assert.True(lost == null, "Save was not blocked.");
        else Assert.True(lost is { Count: 0 }, lost == null ? "Save was blocked." : string.Join("\n", lost));
    }
}
