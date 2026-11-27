using Avalonia.Headless.XUnit;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// Issue 14 on PostgreSQL, SQL Server and MySQL: open a table through explorer → Design Table…, rename an
/// index in place, re-order and flip the keys of another, make a third unique (keeping its filter, included
/// columns or expression), drop one and add one, then save and reopen. Everything but those indexes reopens
/// exactly as loaded, the rows survive, and the engine enforces the saved uniqueness. Changes a foreign key
/// depends on are blocked before anything runs, and saving the reopened table does nothing.
/// </summary>
public sealed class IndexEditingConformanceTests : MainWindowTestBase
{
    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task PostgreSql_IndexesAreRenamedReorderedMadeUniqueDroppedAndAdded()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var schema = $"rain.idx_{Guid.NewGuid().ToString("N")[..10]}";
        var table = $"\"{schema}\".\"Idx.T\"";
        try
        {
            await TableDesignPostgreSqlTests.Execute(cs, $"""
                CREATE SCHEMA "{schema}";
                CREATE TABLE {table} (id int PRIMARY KEY, a int, b int, c text, note text, ref int);
                CREATE INDEX ix_a ON {table} (a);
                CREATE INDEX ix_bc ON {table} (b, c DESC);
                CREATE INDEX ix_special ON {table} (lower(note)) INCLUDE (a) WHERE a > 0;
                CREATE UNIQUE INDEX ix_u ON {table} (c);
                CREATE UNIQUE INDEX ix_ref ON {table} (ref);
                CREATE TABLE "{schema}".child (id int PRIMARY KEY, ref int REFERENCES {table} (ref));
                INSERT INTO {table} VALUES (1, 1, 10, 'c1', 'N1', 1), (2, 2, 20, 'c2', 'N2', 2);
                """);
            var after = await EditAsync(cs, new PostgreSqlProvider(), schema, table, $"foreign key child_ref_fkey on {schema}.child",
                "ALTER INDEX \"" + schema + "\".\"ix_a\" RENAME TO \"ix_a2\";",
                [
                    "index Idx.T_pkey unique PrimaryKey (id) using=btree",
                    "index ix_a2 CreateIndex (a) using=btree",
                    "index ix_bc CreateIndex (c, b DESC) using=btree",
                    "index ix_new unique CreateIndex (note DESC, a) using=btree",
                    "index ix_ref unique CreateIndex (ref) using=btree",
                    "index ix_special unique CreateIndex ((lower(note))) include (a) where=a > 0 using=btree"
                ]);
            // The filter applies: a duplicate lower(note) is accepted where a <= 0.
            await new PostgreSqlProvider().ExecuteNonQueryAsync(cs, $"INSERT INTO {table} (id, a, note) VALUES (5, 0, 'n1')");
            Assert.Contains(after.Indexes, i => i.Name == "ix_special" && i.IsUnique);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await TableDesignPostgreSqlTests.Execute(cs, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task SqlServer_IndexesAreRenamedReorderedMadeUniqueDroppedAndAdded_KeepingFiltersAndIncludes()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var database = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        var connection = new SqlConnectionStringBuilder(cs) { InitialCatalog = database, Pooling = false }.ToString();
        try
        {
            await TableDesignSqlServerTests.Execute(cs, $"CREATE DATABASE [{database}]");
            await TableDesignSqlServerTests.Execute(connection, """
                CREATE SCHEMA [idx];
                GO
                CREATE TABLE [idx].[Idx.T] (id int NOT NULL CONSTRAINT pk_t PRIMARY KEY, a int NULL, b int NULL, c nvarchar(20) NULL,
                    note nvarchar(20) NULL, ref int NULL);
                CREATE INDEX ix_a ON [idx].[Idx.T] (a);
                CREATE INDEX ix_bc ON [idx].[Idx.T] (b, c DESC);
                CREATE INDEX ix_special ON [idx].[Idx.T] (note) INCLUDE (a) WHERE a > 0;
                CREATE UNIQUE INDEX ix_u ON [idx].[Idx.T] (c);
                CREATE UNIQUE INDEX ix_ref ON [idx].[Idx.T] (ref);
                CREATE TABLE [idx].child (id int NOT NULL PRIMARY KEY, ref int NULL CONSTRAINT fk_child REFERENCES [idx].[Idx.T] (ref));
                INSERT INTO [idx].[Idx.T] VALUES (1, 1, 10, N'c1', N'n1', 1), (2, 2, 20, N'c2', N'n2', 2);
                """);
            await EditAsync(connection, new SqlServerProvider(), "idx", "[idx].[Idx.T]", $"foreign key fk_child on {database}.idx.child",
                "EXEC sp_rename @rain_object, N'ix_a2', N'INDEX';",
                [
                    "index ix_a2 NonClusteredIndex (a)",
                    "index ix_bc NonClusteredIndex (c, b DESC)",
                    "index ix_new unique NonClusteredIndex (note DESC, a)",
                    "index ix_ref unique NonClusteredIndex (ref)",
                    "index ix_special unique NonClusteredIndex (note) include (a) where=([a]>(0))",
                    "index pk_t unique PrimaryKeyClustered (id)"
                ]);
            await new SqlServerProvider().ExecuteNonQueryAsync(connection, "INSERT INTO [idx].[Idx.T] (id, a, note, ref) VALUES (5, 0, N'n1', 5)");

            // SQL Server refuses to drop a column an index uses, also as an included column or in its filter, so
            // ix_a2 (key), ix_new (key) and ix_special (INCLUDE (a) WHERE a > 0) are dropped first, after review.
            var provider = new SqlServerProvider();
            var session = new DatabaseSession(new ConnectionInfo { Name = "indexes", Provider = provider.ProviderName, ConnectionString = connection }, provider);
            var vm = CreateMainWindowViewModel();
            var designer = await TableDesignRoundTrip.OpenAsync(vm,
                await TableDesignRoundTrip.FindTableAsync(vm, await TableDesignRoundTrip.ConnectAsync(vm, session), "idx", "Idx.T"));
            designer.DropColumnCommand.Execute(designer.Columns.Single(c => c.Name == "a"));
            Assert.StartsWith("""
                DROP INDEX [ix_a2] ON [idx].[Idx.T];
                DROP INDEX [ix_new] ON [idx].[Idx.T];
                DROP INDEX [ix_special] ON [idx].[Idx.T];
                ALTER TABLE [idx].[Idx.T] DROP COLUMN [a];
                """.ReplaceLineEndings().TrimEnd(), designer.PreviewSql);
            Assert.Contains(designer.Plan.Destructive, d => d.StartsWith("Drops unique index ix_special;"));
            designer.Review.DestructiveConfirmed = true;
            await designer.SaveCommand.ExecuteAsync(null);
            Assert.True(designer.StatusMessage?.Contains("successfully") == true, designer.StatusMessage + " | " + designer.Plan.Sql);
            Assert.Equal(["ix_bc", "ix_ref", "pk_t"], designer.Design.Baseline!.Indexes.Select(i => i.Name).Order(StringComparer.Ordinal));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await TableDesignSqlServerTests.Execute(cs, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task MySql_IndexesAreRenamedReorderedMadeUniqueDroppedAndAdded_InOneStatementEach()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var db = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        try
        {
            await TableDesignMySqlTests.Execute(cs, $"""
                CREATE DATABASE `{db}`;
                CREATE TABLE `{db}`.parent (id INT PRIMARY KEY);
                CREATE TABLE `{db}`.`Idx.T` (id INT PRIMARY KEY, a INT, b INT, c VARCHAR(20), note VARCHAR(20), ref INT,
                    INDEX ix_a (a), INDEX ix_bc (b, c DESC), INDEX ix_special (note), UNIQUE INDEX ix_u (c, a), INDEX ix_ref (ref),
                    INDEX ix_ref2 (ref, b),
                    CONSTRAINT fk_ref FOREIGN KEY (ref) REFERENCES `{db}`.parent (id));
                INSERT INTO `{db}`.parent VALUES (1), (2);
                INSERT INTO `{db}`.`Idx.T` VALUES (1, 1, 10, 'c1', 'n1', 1), (2, 2, 20, 'c2', 'n2', 2);
                """);
            var connection = new MySqlConnectionStringBuilder(cs) { Database = db }.ToString();
            // MySQL lists a unique index over plain columns as a unique key; ix_u is dropped from the Constraints tab here.
            await EditAsync(connection, new MySqlProvider(), null, "`Idx.T`", null,
                "ALTER TABLE `" + db + "`.`Idx.T` RENAME INDEX `ix_a` TO `ix_a2`;",
                [
                    "index ix_a2 CreateIndex (a) using=BTREE",
                    "index ix_bc CreateIndex (c, b DESC) using=BTREE",
                    "index ix_new unique UniqueKey (note DESC, a) using=BTREE",
                    "index ix_ref CreateIndex (ref) using=BTREE",
                    "index ix_ref2 CreateIndex (ref, b) using=BTREE",
                    "index ix_special unique UniqueKey (note) using=BTREE",
                    "index PRIMARY unique PrimaryKey (id) using=BTREE"
                ], mySqlForeignKeyIndex: "ix_ref");
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            await TableDesignMySqlTests.Drop(cs, db);
        }
    }

    private async Task<TableDesign> EditAsync(string connection, IDatabaseProvider provider, string? schema, string table, string? reference,
        string renameSql, string[] savedIndexes, string? mySqlForeignKeyIndex = null)
    {
        var session = new DatabaseSession(new ConnectionInfo { Name = "indexes", Provider = provider.ProviderName, ConnectionString = connection }, provider);
        var vm = CreateMainWindowViewModel();
        var root = await TableDesignRoundTrip.ConnectAsync(vm, session);
        var node = await TableDesignRoundTrip.FindTableAsync(vm, root, schema, "Idx.T");
        var designer = await TableDesignRoundTrip.OpenAsync(vm, node);
        Assert.True(designer.Design.IsLoaded, designer.Design.Error);
        Assert.Equal("", designer.PreviewSql);
        var source = (ITableDesignSource)provider;
        var before = designer.Design.Baseline!;

        // Dependencies are blocked before anything runs: an incoming foreign key's unique index, or MySQL's only index for a foreign key.
        if (reference != null)
        {
            designer.DropIndex("ix_ref");
            Assert.Contains($"Index ix_ref is unique over the columns {reference} references; drop or change that foreign key first.", designer.Plan.Blocked);
        }
        if (mySqlForeignKeyIndex != null)
        {
            // Either index can serve the foreign key alone, but not both going in one save.
            designer.DropIndex(mySqlForeignKeyIndex);
            Assert.True(designer.PlanAllowsSave || designer.Review.NeedsConfirmation);
            Assert.Empty(designer.Plan.Blocked);
            designer.DropIndex(mySqlForeignKeyIndex + "2");
            Assert.Contains($"Index {mySqlForeignKeyIndex} is the only index for foreign key fk_ref, which MySQL needs; add an index on its columns first.",
                designer.Plan.Blocked);
        }
        Assert.False(designer.PlanAllowsSave);
        await designer.ReloadCommand.ExecuteAsync(null);
        Assert.Equal("", designer.PreviewSql);

        designer.Index("ix_a").Name = "ix_a2";
        var bc = designer.Index("ix_bc");
        designer.Indexes.Selected = bc;
        designer.Indexes.MovePartDownCommand.Execute(bc.Parts[0]);
        bc.Parts[0].Descending = false;
        bc.Parts[1].Descending = true;
        designer.Index("ix_special").IsUnique = true;
        if (designer.Indexes.Items.Any(i => i.Name == "ix_u" && i.IsEditable)) designer.DropIndex("ix_u");
        else designer.Keys.DropKeyCommand.Execute(designer.Keys.Items.Single(k => k.Name == "ix_u"));
        var added = designer.AddIndex("ix_new", true, "note", "a");
        added.Parts[0].Descending = true;
        Assert.Contains(renameSql, designer.PreviewSql);
        Assert.Equal(3, designer.Plan.Destructive.Count);
        Assert.False(designer.PlanAllowsSave);
        designer.Review.DestructiveConfirmed = true;
        await designer.SaveCommand.ExecuteAsync(null);
        Assert.True(designer.StatusMessage?.Contains("successfully") == true, designer.StatusMessage + "\n" + designer.Plan.Sql);

        var after = await source.GetTableDesignAsync(connection, designer.Design.Table!);
        Assert.Equal(savedIndexes, IndexLines(after));
        Assert.Equal(WithoutIndexes(before), WithoutIndexes(after));
        Assert.Equal(TableDesignText.Lines(after), TableDesignText.Lines(designer.Design.Baseline!));
        Assert.Equal(["1|1|10|c1", "2|2|20|c2"], await Rows(provider, connection, $"SELECT id, a, b, c FROM {table} ORDER BY id"));

        // The new unique index is enforced; the dropped one is not.
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(connection, $"INSERT INTO {table} (id, a, note) VALUES (3, 1, 'n1')"));
        await provider.ExecuteNonQueryAsync(connection, $"INSERT INTO {table} (id, a, c) VALUES (4, 1, 'c1')");

        await designer.SaveCommand.ExecuteAsync(null);
        Assert.Equal("No changes to save.", designer.StatusMessage);
        return after;
    }

    /// <summary>The index lines, ordered by name, with the engine's index kind folded into the origin for SQL Server.</summary>
    private static string[] IndexLines(TableDesign design) =>
        design.Indexes.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(i =>
        {
            var line = TableDesignText.Lines(design with { Indexes = [i] }).Single(l => l.StartsWith("index "));
            return i.Method is "CLUSTERED" or "NONCLUSTERED"
                ? line.Replace($" using={i.Method}", "").Replace(" CreateIndex ", " NonClusteredIndex ")
                    .Replace(" PrimaryKey ", i.Method == "CLUSTERED" ? " PrimaryKeyClustered " : " PrimaryKey ")
                : line;
        }).ToArray();

    /// <summary>The design's lines without its indexes and the unique keys MySQL reports for unique indexes.</summary>
    private static string[] WithoutIndexes(TableDesign design) =>
        TableDesignText.Lines(design).Where(l => !l.StartsWith("index ") && !l.StartsWith("unique ")).ToArray();

    private static async Task<string[]> Rows(IDatabaseProvider provider, string connection, string sql) =>
        (await provider.ExecuteQueryAsync(connection, sql)).ObjectRows
            .Select(r => string.Join("|", r.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
}
