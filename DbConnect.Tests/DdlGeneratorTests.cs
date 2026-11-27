using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Services.Builders;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class DdlGeneratorTests
{
    [Fact]
    public void Quote_UsesProviderSpecificIdentifierQuoting()
    {
        Assert.Equal("[Customers]", DdlGenerator.Quote("Customers", "SQL Server"));
        Assert.Equal("\"Customers\"", DdlGenerator.Quote("Customers", "SQLite"));
        Assert.Equal("\"Customers\"", DdlGenerator.Quote("Customers", "PostgreSQL"));
    }

    [Fact]
    public void Quote_EscapesProviderSpecificIdentifierTerminators()
    {
        Assert.Equal("[Order]]History]", DdlGenerator.Quote("Order]History", "SQL Server"));
        Assert.Equal("\"Customer\"\"Archive\"", DdlGenerator.Quote("Customer\"Archive", "SQLite"));
        Assert.Equal("\"Customer\"\"Archive\"", DdlGenerator.Quote("Customer\"Archive", "PostgreSQL"));
    }

    [Fact]
    public void GenerateCreateTable_ForSqlite_InlinesPrimaryKey()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Name", DataType = "TEXT", IsNullable = false, DefaultValue = "'unknown'" }
        };

        var sql = DdlGenerator.GenerateCreateTable("Customers", columns, "SQLite");

        Assert.Equal(
            """
            CREATE TABLE "Customers" (
                "Id" INTEGER NOT NULL PRIMARY KEY,
                "Name" TEXT NOT NULL DEFAULT 'unknown'
            );
            """.ReplaceLineEndings(),
            sql);
    }

    [Fact]
    public void GenerateCreateTable_ForSqlServer_AddsTablePrimaryKeyConstraint()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "TenantId", DataType = "INT", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Id", DataType = "INT", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Name", DataType = "NVARCHAR(200)", IsNullable = true }
        };

        var sql = DdlGenerator.GenerateCreateTable("Customers", columns, "SQL Server");

        Assert.Equal(
            """
            CREATE TABLE [Customers] (
                [TenantId] INT NOT NULL,
                [Id] INT NOT NULL,
                [Name] NVARCHAR(200),
                PRIMARY KEY ([TenantId], [Id])
            );
            """.ReplaceLineEndings(),
            sql);
    }

    [Fact]
    public void GenerateCreateTable_EscapesIdentifiers()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "Id]Archive", DataType = "INT", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Customer]Name", DataType = "NVARCHAR(200)", IsNullable = true }
        };

        var sql = DdlGenerator.GenerateCreateTable("Order]History", columns, "SQL Server");

        Assert.Contains("CREATE TABLE [Order]]History]", sql);
        Assert.Contains("[Id]]Archive] INT NOT NULL", sql);
        Assert.Contains("[Customer]]Name] NVARCHAR(200)", sql);
        Assert.Contains("PRIMARY KEY ([Id]]Archive])", sql);
    }

    [Fact]
    public void GenerateCreateIndex_AndCreateView_UseProviderQuoting()
    {
        var index = new IndexDefinition
        {
            Name = "IX_Customers_Name",
            TableName = "Customers",
            Columns = ["LastName", "FirstName"],
            IsUnique = true
        };

        Assert.Equal(
            """CREATE UNIQUE INDEX "IX_Customers_Name" ON "Customers" ("LastName" ASC, "FirstName" ASC);""",
            DdlGenerator.GenerateCreateIndex(index, "PostgreSQL"));
        Assert.Equal(
            """
            CREATE VIEW [ActiveCustomers] AS
            SELECT * FROM Customers WHERE IsActive = 1;
            """.ReplaceLineEndings(),
            DdlGenerator.GenerateCreateView("ActiveCustomers", "SELECT * FROM Customers WHERE IsActive = 1", "SQL Server"));
    }

    // -------------------------------------------------------------------------
    // Epic F new tests
    // -------------------------------------------------------------------------

    [Fact]
    public void GenerateDropIndex_ForSqlServer_IncludesOnTableClause()
    {
        var sql = DdlBuilderFactory.For("SQL Server").GenerateDropIndex("IX_Foo", "Customers");
        Assert.Equal("DROP INDEX [IX_Foo] ON [Customers];", sql);
    }

    [Fact]
    public void GenerateDropIndex_ForSqlite_NoTableClause()
    {
        var sql = DdlBuilderFactory.For("SQLite").GenerateDropIndex("IX_Foo");
        Assert.Equal("""DROP INDEX "IX_Foo";""", sql);
    }

    [Fact]
    public void GenerateAlterTable_SqliteRebuild_UsesStableTempName()
    {
        var original = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true }
        };
        var modified = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Email", DataType = "TEXT", IsNullable = true, IsNew = true }
        };

        var sql = DdlBuilderFactory.For("SQLite").GenerateAlterTable("Users", original, modified);

        Assert.Contains("_rain_tmp_Users", sql);
        Assert.DoesNotContain("_new_Users_", sql);
    }

    [Fact]
    public void GenerateAlterTable_SqliteRebuild_RecreatesIndexes()
    {
        var original = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Email", DataType = "TEXT", IsNullable = true }
        };
        var modified = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "Email", DataType = "TEXT", IsNullable = true }
        };
        var indexes = new List<IndexDefinition>
        {
            new() { Name = "IX_Users_Email", TableName = "Users", Columns = ["Email"], IsUnique = true }
        };

        var sql = DdlBuilderFactory.For("SQLite").GenerateAlterTable("Users", original, modified, indexes);

        var renamePos = sql.IndexOf("RENAME TO", StringComparison.Ordinal);
        var createIndexPos = sql.IndexOf("CREATE UNIQUE INDEX", StringComparison.Ordinal);
        Assert.True(createIndexPos > renamePos, "CREATE INDEX should appear after RENAME TO");
        Assert.Contains("""CREATE UNIQUE INDEX "IX_Users_Email" ON "Users" ("Email" ASC);""", sql);
    }

    [Fact]
    public void GenerateAddForeignKey_ForPostgreSQL_EmitsAlterTableForeignKey()
    {
        var fk = new ForeignKeyDefinition
        {
            ConstraintName = "FK_Orders_Customers",
            Table = "Orders",
            Column = "CustomerId",
            ReferencedTable = "Customers",
            ReferencedColumn = "Id",
            OnDelete = "CASCADE",
            OnUpdate = "NO ACTION"
        };

        var sql = DdlBuilderFactory.For("PostgreSQL").GenerateAddForeignKey(fk);

        Assert.Equal(
            """ALTER TABLE "Orders" ADD CONSTRAINT "FK_Orders_Customers" FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE CASCADE ON UPDATE NO ACTION;""",
            sql);
    }

    [Fact]
    public void GenerateAddForeignKey_ForSQLite_EmitsWarningComment()
    {
        var fk = new ForeignKeyDefinition
        {
            ConstraintName = "FK_Orders_Customers",
            Table = "Orders",
            Column = "CustomerId",
            ReferencedTable = "Customers",
            ReferencedColumn = "Id"
        };

        var sql = DdlBuilderFactory.For("SQLite").GenerateAddForeignKey(fk);

        Assert.StartsWith("--", sql);
        Assert.Contains("⚠️", sql);
        Assert.Contains("SQLite", sql);
    }

    [Fact]
    public void GenerateAddUniqueConstraint_ForSqlite_UsesCreateUniqueIndex()
    {
        var sql = DdlBuilderFactory.For("SQLite").GenerateAddUniqueConstraint("Customers", "UQ_Customers_Email", ["Email"]);
        Assert.Equal("""CREATE UNIQUE INDEX "UQ_Customers_Email" ON "Customers" ("Email");""", sql);
    }

    [Fact]
    public void GenerateAddUniqueConstraint_ForPostgreSQL_UsesAlterTableAddConstraint()
    {
        var sql = DdlBuilderFactory.For("PostgreSQL").GenerateAddUniqueConstraint("Customers", "UQ_Customers_Email", ["Email"]);
        Assert.Equal("""ALTER TABLE "Customers" ADD CONSTRAINT "UQ_Customers_Email" UNIQUE ("Email");""", sql);
    }

    [Fact]
    public void GenerateCreateView_SqlServer_CreateOrReplace_UsesCreateOrAlter()
    {
        var sql = DdlBuilderFactory.For("SQL Server").GenerateCreateView(
            "ActiveCustomers",
            "SELECT * FROM Customers WHERE IsActive = 1",
            createOrReplace: true);

        Assert.StartsWith("CREATE OR ALTER VIEW", sql);
        Assert.Contains("[ActiveCustomers]", sql);
    }

    [Fact]
    public void TableDesignerViewModel_PreviewSql_UpdatesWhenColumnsChange()
    {
        var provider = new StubProvider("SQLite");
        var session = new DatabaseSession(
            new ConnectionInfo { Name = "test", Provider = "SQLite", ConnectionString = "Data Source=:memory:" },
            provider);
        var vm = new TableDesignerViewModel("Orders", session);

        vm.Columns.Add(new ColumnDefinition { Name = "OrderId", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true, IsNew = true });

        Assert.Contains("OrderId", vm.PreviewSql);
    }

    [Fact]
    public void ViewDesignerViewModel_PreviewSql_ReflectsViewNameAndSelectSql()
    {
        var provider = new StubProvider("PostgreSQL");
        var session = new DatabaseSession(
            new ConnectionInfo { Name = "test", Provider = "PostgreSQL", ConnectionString = "Host=localhost" },
            provider);
        var vm = new ViewDesignerViewModel(session);
        vm.ViewName = "ActiveOrders";
        vm.SelectSql = "SELECT * FROM Orders WHERE IsActive = 1";
        vm.UpdateViewPreview();

        Assert.Contains("ActiveOrders", vm.PreviewSql);
        Assert.Contains("SELECT * FROM Orders", vm.PreviewSql);
    }

    [Fact]
    public void GenerateCreateTable_SqliteWithForeignKeys_InlinesFkConstraints()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true },
            new() { Name = "CustomerId", DataType = "INTEGER", IsNullable = false }
        };
        var fks = new List<ForeignKeyDefinition>
        {
            new() { ConstraintName = "FK_Orders_Customers", Column = "CustomerId", ReferencedTable = "Customers", ReferencedColumn = "Id", OnDelete = "CASCADE", OnUpdate = "NO ACTION" }
        };

        var sql = DdlBuilderFactory.For("SQLite").GenerateCreateTable("Orders", columns, fks);

        Assert.Contains("FOREIGN KEY", sql);
        Assert.Contains("REFERENCES", sql);
        Assert.Contains("Customers", sql);
        Assert.Contains("CASCADE", sql);
        // Should NOT be a comment
        Assert.DoesNotContain("--", sql);
    }

    [Fact]
    public void GenerateCreateTable_PostgreSqlWithForeignKeys_DeclaresThemInline()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true }
        };
        var fks = new List<ForeignKeyDefinition>
        {
            new() { ConstraintName = "FK_Test", Column = "Id", ReferencedTable = "Other", ReferencedColumn = "Id", OnDelete = "NO ACTION", OnUpdate = "NO ACTION" }
        };

        var sql = DdlBuilderFactory.For("PostgreSQL").GenerateCreateTable("Test", columns, fks);

        // Every builder declares a new table's foreign keys in CREATE TABLE, so none is added separately.
        Assert.Contains("""CONSTRAINT "FK_Test" FOREIGN KEY ("Id") REFERENCES "Other" ("Id") ON DELETE NO ACTION ON UPDATE NO ACTION""", sql);
    }

    [Fact]
    public void ExtractSelectBody_HandlesFullCreateViewDefinition()
    {
        // Simulate what SQLite and SQL Server return — a full definition
        var definition = "CREATE VIEW active_orders AS SELECT id, total FROM orders WHERE active = 1";
        var vm = new ViewDesignerViewModel();
        // Access via reflection is not ideal; test via the regex directly
        var method = typeof(ViewDesignerViewModel).GetMethod("ExtractSelectBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = (string?)method?.Invoke(null, [definition, "active_orders"]);

        Assert.NotNull(result);
        Assert.StartsWith("SELECT", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE VIEW", result);
    }

    [Fact]
    public void ExtractSelectBody_DoesNotSplitOnColumnAlias()
    {
        // "AS" inside column alias should not cause an early split
        var definition = "CREATE VIEW summary AS SELECT count(*) AS total, name FROM items";
        var method = typeof(ViewDesignerViewModel).GetMethod("ExtractSelectBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = (string?)method?.Invoke(null, [definition, "summary"]);

        Assert.NotNull(result);
        Assert.Equal("SELECT count(*) AS total, name FROM items", result);
    }

    [Fact]
    public void TableDesignerValidation_RequiresReferencedColumn()
    {
        var provider = new StubProvider("PostgreSQL");
        var session = new DatabaseSession(
            new ConnectionInfo { Name = "test", Provider = "PostgreSQL", ConnectionString = "Host=localhost" },
            provider);
        var vm = new TableDesignerViewModel("Orders", session);
        vm.Columns.Add(new ColumnDefinition { Name = "Id", DataType = "INTEGER", IsNew = true });
        var fk = new EditableForeignKey { Name = "FK_Test", ReferencedTable = new DatabaseObjectIdentity(null, null, "Customers", SchemaNodeType.Table) };
        fk.Pairs.Add(new ForeignKeyPair { Column = "CustomerId", ReferencedColumn = "" }); // blank — should fail validation
        vm.ForeignKeys.Items.Add(fk);

        var errors = vm.ValidateDesigner();

        Assert.Contains(errors, e => e.Contains("incomplete column pair", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TableDesignerPreview_IncludesFkDdl()
    {
        var provider = new StubProvider("PostgreSQL");
        var session = new DatabaseSession(
            new ConnectionInfo { Name = "test", Provider = "PostgreSQL", ConnectionString = "Host=localhost" },
            provider);
        var vm = new TableDesignerViewModel("Orders", session);
        vm.Columns.Add(new ColumnDefinition { Name = "Id", DataType = "INTEGER", IsNew = true });
        var fk = new EditableForeignKey
        {
            Name = "FK_Orders_Customers",
            ReferencedTable = new DatabaseObjectIdentity(null, null, "Customers", SchemaNodeType.Table),
            OnDelete = "CASCADE"
        };
        fk.Pairs.Add(new ForeignKeyPair { Column = "CustomerId", ReferencedColumn = "Id" });
        vm.ForeignKeys.Items.Add(fk);
        vm.UpdatePreview();

        Assert.Contains("FOREIGN KEY", vm.PreviewSql);
    }


    private sealed class StubProvider : IDatabaseProvider
    {
        public StubProvider(string providerName) => ProviderName = providerName;
        public string ProviderName { get; }
        public string ProviderIcon => "";
        public DatabaseProviderCapabilities Capabilities { get; init; } = new("\"", "\"", "LIMIT");
        public string ExplainPrefix => "EXPLAIN";
        public Task<ConnectionTestResult> TestConnectionAsync(string connectionString) => Task.FromResult(ConnectionTestResult.Succeeded("Fake", default));
        public Task<string> GetServerVersionAsync(string connectionString) => Task.FromResult("test");
        public Task<List<SchemaNode>> GetSchemaAsync(string connectionString) => Task.FromResult(new List<SchemaNode>());
        public Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type) => Task.FromResult(new List<SchemaNode>());
        public Task<QueryResult> ExecuteQueryAsync(string connectionString, string query, QueryExecutionOptions? options = null) => Task.FromResult(new QueryResult());

        public async System.Collections.Generic.IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(
            string connectionString, string query, QueryExecutionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            yield return await ExecuteQueryAsync(connectionString, query, options);
        }
        public Task<TablePageResult> LoadTablePageAsync(string connectionString, string tableName, IReadOnlyList<string> orderByColumns, int pageSize, int pageIndex, QueryExecutionOptions? options = null) =>
            Task.FromResult(new TablePageResult { PageIndex = pageIndex, PageSize = pageSize });
        public Task<int> ExecuteNonQueryAsync(string connectionString, string sql) => Task.FromResult(0);
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<string> statements) => Task.FromResult(TransactionResult.Success(statements.Count));
        public Task<TransactionResult> ExecuteTransactionAsync(string connectionString, IReadOnlyList<SqlCommandSpec> commands) => Task.FromResult(TransactionResult.Success(commands.Count));
        public string FormatLimitQuery(string query, int limit) => query;
        public QueryResult NormalizeExplainResult(QueryResult raw) => raw;
        public Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName) => Task.FromResult(new List<IndexDefinition>());
        public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName) => Task.FromResult(new List<ColumnDefinition>());
        public Task<string?> GetViewDefinitionAsync(string connectionString, string viewName) => Task.FromResult<string?>(null);
        public List<string> GetSupportedDataTypes() => ["TEXT", "INTEGER", "REAL", "BLOB"];
    }
}
