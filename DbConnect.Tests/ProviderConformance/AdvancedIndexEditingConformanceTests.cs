using Avalonia.Headless.XUnit;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// Issue 15 on PostgreSQL, SQL Server and MySQL: open a table through explorer → Design Table…, edit index
/// expressions, included columns, filters and methods where the engine has them, see incompatible settings
/// listed before anything runs, then save and reopen. The reopened indexes are exactly the saved ones,
/// everything else is unchanged, the rows survive, the engine enforces the saved uniqueness and filters, and
/// saving again does nothing.
/// </summary>
public sealed class AdvancedIndexEditingConformanceTests : MainWindowTestBase
{
    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task PostgreSql_ExpressionsIncludedColumnsFiltersAndMethodsAreEdited()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var schema = $"rain.adv_{Guid.NewGuid().ToString("N")[..10]}";
        var table = $"\"{schema}\".\"Adv.T\"";
        try
        {
            await TableDesignPostgreSqlTests.Execute(cs, $"""
                CREATE SCHEMA "{schema}";
                CREATE TABLE {table} (id int PRIMARY KEY, a int, b int, note text);
                CREATE INDEX ix_a ON {table} (a) INCLUDE (b) WHERE a > 0;
                CREATE INDEX ix_note ON {table} (note);
                CREATE INDEX ix_hash ON {table} USING hash (b);
                INSERT INTO {table} VALUES (1, 1, 10, 'N1'), (2, 2, 20, 'N2');
                """);
            var provider = new PostgreSqlProvider();
            var (designer, before) = await OpenAsync(cs, provider, schema);
            Assert.Equal(["btree", "hash", "gist", "spgist", "gin", "brin"], designer.Indexes.Methods);

            // Incompatible methods are listed first: a unique or multi-key hash index, a GIN index with included columns.
            Choose(designer, "ix_note", "hash");
            designer.Index("ix_note").IsUnique = true;
            designer.Indexes.NewPart = "a";
            designer.Indexes.AddPartCommand.Execute(null);
            Choose(designer, "ix_a", "gin");
            Assert.Contains("Index ix_note is unique, which hash indexes cannot be.", designer.ValidationErrors);
            Assert.Contains("Index ix_note has more than one key part, which hash indexes cannot have.", designer.ValidationErrors);
            Assert.Contains("Index ix_a has included columns, which gin indexes cannot have.", designer.ValidationErrors);
            Assert.False(designer.PlanAllowsSave);
            await designer.ReloadCommand.ExecuteAsync(null);
            Assert.Equal("", designer.PreviewSql);

            // ix_a includes note instead of b under a narrower filter; ix_note becomes a hash index; ix_hash a unique B-tree
            // over (b, a); ix_expr is added: unique on lower(note) DESC, including a, where note is not null.
            var a = Select(designer, "ix_a");
            designer.Indexes.RemoveIncludedCommand.Execute("b");
            designer.Indexes.NewIncluded = "note";
            designer.Indexes.AddIncludedCommand.Execute(null);
            a.Filter = "a > 1";
            Choose(designer, "ix_note", "hash");
            var hash = Select(designer, "ix_hash");
            designer.Indexes.SelectedMethod = "btree";
            hash.IsUnique = true;
            designer.Indexes.NewPart = "a";
            designer.Indexes.AddPartCommand.Execute(null);
            var added = designer.AddIndex("ix_expr", true);
            Assert.Equal("btree", added.Method);
            designer.Indexes.NewExpression = "lower(note)";
            designer.Indexes.AddExpressionCommand.Execute(null);
            added.Parts[0].Descending = true;
            designer.Indexes.NewIncluded = "a";
            designer.Indexes.AddIncludedCommand.Execute(null);
            added.Filter = "note IS NOT NULL";
            Assert.Contains($"CREATE INDEX \"ix_a\" ON {table} (\"a\") INCLUDE (\"note\") WHERE a > 1;", designer.PreviewSql);
            Assert.Contains($"CREATE INDEX \"ix_note\" ON {table} USING hash (\"note\");", designer.PreviewSql);
            Assert.Contains($"CREATE UNIQUE INDEX \"ix_hash\" ON {table} (\"b\", \"a\");", designer.PreviewSql);
            Assert.Contains($"CREATE UNIQUE INDEX \"ix_expr\" ON {table} ((lower(note)) DESC) INCLUDE (\"a\") WHERE note IS NOT NULL;",
                designer.PreviewSql);
            var after = await SaveAsync(designer, provider, cs, table, before, [
                "index Adv.T_pkey unique PrimaryKey (id) using=btree",
                "index ix_a CreateIndex (a) include (note) where=a > 1 using=btree",
                "index ix_expr unique CreateIndex ((lower(note)) DESC) include (a) where=note IS NOT NULL using=btree",
                "index ix_hash unique CreateIndex (b, a) using=btree",
                "index ix_note CreateIndex (note) using=hash"
            ]);

            // ix_expr is enforced only where note is not null; ix_hash now enforces (b, a).
            await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, $"INSERT INTO {table} VALUES (3, 3, 30, 'n1')"));
            await provider.ExecuteNonQueryAsync(cs, $"INSERT INTO {table} VALUES (4, 4, 40, NULL), (5, 5, 50, NULL)");
            await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(cs, $"INSERT INTO {table} VALUES (6, 1, 10, 'x')"));
            Assert.Single(after.Indexes, i => i.Name == "ix_note" && i.Method == "hash");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await TableDesignPostgreSqlTests.Execute(cs, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task SqlServer_IncludedColumnsFiltersAndClusteringAreEdited()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var database = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        var connection = new SqlConnectionStringBuilder(cs) { InitialCatalog = database, Pooling = false }.ToString();
        try
        {
            await TableDesignSqlServerTests.Execute(cs, $"CREATE DATABASE [{database}]");
            await TableDesignSqlServerTests.Execute(connection, """
                CREATE SCHEMA [adv];
                GO
                CREATE TABLE [adv].[Adv.T] (id int NOT NULL CONSTRAINT pk_t PRIMARY KEY NONCLUSTERED, a int NULL, b int NULL,
                    note nvarchar(20) NULL);
                CREATE INDEX ix_a ON [adv].[Adv.T] (a) INCLUDE (b) WHERE a > 0;
                CREATE INDEX ix_b ON [adv].[Adv.T] (b);
                CREATE TABLE [adv].[Clu] (id int NOT NULL CONSTRAINT pk_clu PRIMARY KEY, x int NULL);
                CREATE INDEX ix_x ON [adv].[Clu] (x);
                INSERT INTO [adv].[Adv.T] VALUES (1, 1, 10, N'n1'), (2, 2, 20, N'n2');
                """);
            var provider = new SqlServerProvider();
            var (designer, before) = await OpenAsync(connection, provider, "adv");
            Assert.Equal(["NONCLUSTERED", "CLUSTERED"], designer.Indexes.Methods);
            Assert.False(designer.Indexes.HasExpressions);

            // A clustered index has no included columns or filter, and a table has one.
            Choose(designer, "ix_b", "CLUSTERED");
            Choose(designer, "ix_a", "CLUSTERED");
            Assert.Contains("Index ix_a has included columns, which CLUSTERED indexes cannot have.", designer.ValidationErrors);
            Assert.Contains("Index ix_a has a filter, which CLUSTERED indexes cannot have.", designer.ValidationErrors);
            Assert.Contains("Index ix_a is CLUSTERED, and Index ix_b already is; a table has one CLUSTERED index.", designer.ValidationErrors);
            designer.Indexes.NewIncluded = "a";
            designer.Indexes.AddIncludedCommand.Execute(null);
            Assert.Contains("Index ix_a includes column \"a\", which is already a key column.", designer.ValidationErrors);
            await designer.ReloadCommand.ExecuteAsync(null);
            Assert.Equal("", designer.PreviewSql);

            // ix_a includes note instead of b under a narrower filter; ix_b becomes the clustered index; ix_new is unique
            // on note, including a, where note is not null.
            Select(designer, "ix_a");
            designer.Indexes.RemoveIncludedCommand.Execute("b");
            designer.Indexes.NewIncluded = "note";
            designer.Indexes.AddIncludedCommand.Execute(null);
            designer.Index("ix_a").Filter = "a > 1";
            Choose(designer, "ix_b", "CLUSTERED");
            var added = designer.AddIndex("ix_new", true, "note");
            designer.Indexes.NewIncluded = "a";
            designer.Indexes.AddIncludedCommand.Execute(null);
            added.Filter = "note IS NOT NULL";
            Assert.Contains("CREATE NONCLUSTERED INDEX [ix_a] ON [adv].[Adv.T] ([a]) INCLUDE ([note]) WHERE a > 1;", designer.PreviewSql);
            Assert.Contains("CREATE CLUSTERED INDEX [ix_b] ON [adv].[Adv.T] ([b]);", designer.PreviewSql);
            Assert.Contains("CREATE UNIQUE NONCLUSTERED INDEX [ix_new] ON [adv].[Adv.T] ([note]) INCLUDE ([a]) WHERE note IS NOT NULL;",
                designer.PreviewSql);
            await SaveAsync(designer, provider, connection, "[adv].[Adv.T]", before, [
                "index ix_a CreateIndex (a) include (note) where=([a]>(1)) using=NONCLUSTERED",
                "index ix_b CreateIndex (b) using=CLUSTERED",
                "index ix_new unique CreateIndex (note) include (a) where=([note] IS NOT NULL) using=NONCLUSTERED",
                "index pk_t unique PrimaryKey (id) using=NONCLUSTERED"
            ]);
            await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(connection, "INSERT INTO [adv].[Adv.T] VALUES (3, 3, 30, N'n1')"));
            await provider.ExecuteNonQueryAsync(connection, "INSERT INTO [adv].[Adv.T] VALUES (4, 4, 40, NULL), (5, 5, 50, NULL)");

            // A primary key is clustered by default, and its read-only backing index takes the table's one clustered index.
            var (clu, _) = await OpenAsync(connection, provider, "adv", "Clu");
            Assert.Equal("CLUSTERED", clu.Index("pk_clu").Method);
            Choose(clu, "ix_x", "CLUSTERED");
            Assert.Contains("Index ix_x is CLUSTERED, and Index pk_clu already is; a table has one CLUSTERED index.", clu.ValidationErrors);
            Assert.False(clu.PlanAllowsSave);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await TableDesignSqlServerTests.Execute(cs, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    public async Task MySql_FunctionalKeyPartsAreEditedAndAdded()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var db = $"rain_scratch_{Guid.NewGuid().ToString("N")[..10]}";
        try
        {
            await TableDesignMySqlTests.Execute(cs, $"""
                CREATE DATABASE `{db}`;
                CREATE TABLE `{db}`.`Adv.T` (id INT PRIMARY KEY, a VARCHAR(20), b VARCHAR(20), INDEX ix_expr ((lower(a))), INDEX ix_b (b));
                INSERT INTO `{db}`.`Adv.T` VALUES (1, 'A1', 'b1'), (2, 'A2', 'b2');
                """);
            var connection = new MySqlConnectionStringBuilder(cs) { Database = db }.ToString();
            var provider = new MySqlProvider();
            var (designer, before) = await OpenAsync(connection, provider, null);
            Assert.True(designer.Indexes.HasExpressions);
            Assert.False(designer.Indexes.HasFilters || designer.Indexes.HasIncludedColumns || designer.Indexes.HasMethods);
            Assert.Equal(["(lower(`a`))"], designer.Index("ix_expr").Parts.Select(p => p.KeyText));

            // ix_expr's expression changes and it becomes unique; ix_b gains a descending expression key; ix_new is added.
            designer.Index("ix_expr").Parts[0].Expression = "upper(`a`)";
            designer.Index("ix_expr").IsUnique = true;
            Select(designer, "ix_b");
            designer.Indexes.NewExpression = "length(`a`)";
            designer.Indexes.AddExpressionCommand.Execute(null);
            designer.Index("ix_b").Parts[1].Descending = true;
            designer.AddIndex("ix_new", false, "id");
            designer.Indexes.NewExpression = "concat(a, b)";
            designer.Indexes.AddExpressionCommand.Execute(null);
            Assert.Contains($"ALTER TABLE `{db}`.`Adv.T` DROP INDEX `ix_expr`, ADD UNIQUE INDEX `ix_expr` ((upper(`a`)));", designer.PreviewSql);
            Assert.Contains($"ALTER TABLE `{db}`.`Adv.T` DROP INDEX `ix_b`, ADD INDEX `ix_b` (`b`, (length(`a`)) DESC);", designer.PreviewSql);
            Assert.Contains($"ALTER TABLE `{db}`.`Adv.T` ADD INDEX `ix_new` (`id`, (concat(a, b)));", designer.PreviewSql);
            await SaveAsync(designer, provider, connection, $"`{db}`.`Adv.T`", before, [
                "index ix_b CreateIndex (b, (length(`a`)) DESC) using=BTREE",
                "index ix_expr unique CreateIndex ((upper(`a`))) using=BTREE",
                "index ix_new CreateIndex (id, (concat(`a`,`b`))) using=BTREE",
                "index PRIMARY unique PrimaryKey (id) using=BTREE"
            ]);
            await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(connection, $"INSERT INTO `{db}`.`Adv.T` VALUES (3, 'a1', 'x')"));
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            await TableDesignMySqlTests.Drop(cs, db);
        }
    }

    private static EditableIndex Select(TableDesignerViewModel designer, string name)
    {
        designer.Indexes.Selected = designer.Index(name);
        return designer.Indexes.Selected;
    }

    /// <summary>Selects the index and picks its method in the Method list.</summary>
    private static void Choose(TableDesignerViewModel designer, string name, string method)
    {
        Select(designer, name);
        designer.Indexes.SelectedMethod = method;
        Assert.Equal(method, designer.Index(name).Method);
    }

    private async Task<(TableDesignerViewModel Designer, TableDesign Before)> OpenAsync(string connection, IDatabaseProvider provider, string? schema,
        string table = "Adv.T")
    {
        var session = new DatabaseSession(new ConnectionInfo { Name = "indexes", Provider = provider.ProviderName, ConnectionString = connection }, provider);
        var vm = CreateMainWindowViewModel();
        var root = await TableDesignRoundTrip.ConnectAsync(vm, session);
        var designer = await TableDesignRoundTrip.OpenAsync(vm, await TableDesignRoundTrip.FindTableAsync(vm, root, schema, table));
        Assert.True(designer.Design.IsLoaded, designer.Design.Error);
        Assert.Equal("", designer.PreviewSql);
        return (designer, designer.Design.Baseline!);
    }

    /// <summary>Saves after review, then checks the reopened indexes, that nothing else changed, and that saving again does nothing.</summary>
    private static async Task<TableDesign> SaveAsync(TableDesignerViewModel designer, IDatabaseProvider provider, string connection,
        string table, TableDesign before, string[] savedIndexes)
    {
        Assert.True(designer.Plan.CanExecute, string.Join("\n", designer.ValidationErrors) + "\n" + designer.PreviewSql);
        designer.Review.DestructiveConfirmed = designer.Review.NeedsConfirmation;
        await designer.SaveCommand.ExecuteAsync(null);
        Assert.True(designer.StatusMessage?.Contains("successfully") == true, designer.StatusMessage + "\n" + designer.Plan.Sql);

        var after = await ((ITableDesignSource)provider).GetTableDesignAsync(connection, designer.Design.Table!);
        Assert.Equal(savedIndexes, after.Indexes.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => TableDesignText.Lines(after with { Indexes = [i] }).Single(l => l.StartsWith("index "))));
        Assert.Equal(WithoutIndexes(before), WithoutIndexes(after));
        Assert.Equal(TableDesignText.Lines(after), TableDesignText.Lines(designer.Design.Baseline!));
        Assert.Equal(["1", "2"], (await provider.ExecuteQueryAsync(connection, $"SELECT id FROM {table} ORDER BY id")).ObjectRows
            .Select(r => Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture)));

        await designer.SaveCommand.ExecuteAsync(null);
        Assert.Equal("No changes to save.", designer.StatusMessage);
        return after;
    }

    private static string[] WithoutIndexes(TableDesign design) =>
        TableDesignText.Lines(design).Where(l => !l.StartsWith("index ") && !l.StartsWith("unique ")).ToArray();
}
