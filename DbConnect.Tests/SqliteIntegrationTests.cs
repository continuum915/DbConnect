using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class SqliteIntegrationTests
{
    [Fact]
    public async Task ExecuteTransactionAsync_CommitsStatementsAgainstInMemoryDatabase()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (
                Id INTEGER NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL
            );
            """);

        var result = await provider.ExecuteTransactionAsync(connectionString,
        [
            "INSERT INTO Customers (Id, Name) VALUES (1, 'Ada');",
            "UPDATE Customers SET Name = 'Grace' WHERE Id = 1;"
        ]);
        var rows = await provider.ExecuteQueryAsync(connectionString, "SELECT Id, Name FROM Customers;");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.RecordsAffected);
        var row = Assert.Single(rows.ObjectRows);
        Assert.Equal(1L, row[rows.IndexOfColumn("Id")]);
        Assert.Equal("Grace", row[rows.IndexOfColumn("Name")]);
    }

    [Fact]
    public async Task ExecuteTransactionAsync_BindsParametersAgainstInMemoryDatabase()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (
                Id INTEGER NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL
            );
            """);

        var result = await provider.ExecuteTransactionAsync(connectionString,
        [
            new SqlCommandSpec(
                "INSERT INTO Customers (Id, Name) VALUES (@p0, @p1);",
                new Dictionary<string, object?>
                {
                    ["@p0"] = 1,
                    ["@p1"] = "O'Brien"
                })
        ]);
        var rows = await provider.ExecuteQueryAsync(connectionString, "SELECT Name FROM Customers WHERE Id = 1;");

        Assert.True(result.Succeeded);
        Assert.Equal("O'Brien", Assert.Single(rows.ObjectRows)[rows.IndexOfColumn("Name")]);
    }

    [Fact]
    public async Task ExecuteTransactionAsync_RollsBackAllStatementsOnFailure()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (
                Id INTEGER NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL
            );
            """);

        var result = await provider.ExecuteTransactionAsync(connectionString,
        [
            "INSERT INTO Customers (Id, Name) VALUES (1, 'Ada');",
            "INSERT INTO MissingTable (Id) VALUES (1);"
        ]);
        var rows = await provider.ExecuteQueryAsync(connectionString, "SELECT Id, Name FROM Customers;");

        Assert.False(result.Succeeded);
        Assert.Equal("INSERT INTO MissingTable (Id) VALUES (1);", result.FailedStatement);
        Assert.Contains("MissingTable", result.ErrorMessage);
        Assert.Empty(rows.ObjectRows);
    }

    [Fact]
    public async Task ExecuteTransactionAsync_ExpectedRowCountConflict_RollsBackPriorWrites()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL);
            INSERT INTO Customers (Id, Name) VALUES (1, 'Ada');
            """);

        var result = await provider.ExecuteTransactionAsync(connectionString,
        [
            new SqlCommandSpec(
                "UPDATE Customers SET Name = @name WHERE Id = @id;",
                new Dictionary<string, object?> { ["@name"] = "Grace", ["@id"] = 1 },
                expectedRecordsAffected: 1),
            new SqlCommandSpec(
                "UPDATE Customers SET Name = @name WHERE Id = @id;",
                new Dictionary<string, object?> { ["@name"] = "Linus", ["@id"] = 99 },
                expectedRecordsAffected: 1)
        ]);

        var rows = await provider.ExecuteQueryAsync(connectionString, "SELECT Name FROM Customers WHERE Id = 1;");

        Assert.False(result.Succeeded);
        Assert.True(result.IsConcurrencyConflict);
        Assert.Equal(1, result.ExpectedRecordsAffected);
        Assert.Equal(0, result.ActualRecordsAffected);
        Assert.Equal(TransactionOutcome.RolledBack, result.Outcome);
        Assert.Equal(0, result.RecordsAffected);
        Assert.Equal(1, result.AttemptedRecordsAffected);
        Assert.Equal("Ada", Assert.Single(rows.ObjectRows)[rows.IndexOfColumn("Name")]);
    }

    [Fact]
    public async Task ExecuteTransactionAsync_MultipleAffectedRows_IsSafetyConflict()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL);
            INSERT INTO Customers (Id, Name) VALUES (1, 'Ada');
            INSERT INTO Customers (Id, Name) VALUES (2, 'Grace');
            """);

        var result = await provider.ExecuteTransactionAsync(connectionString,
        [
            new SqlCommandSpec(
                "UPDATE Customers SET Name = @name;",
                new Dictionary<string, object?> { ["@name"] = "Changed" },
                expectedRecordsAffected: 1)
        ]);

        var rows = await provider.ExecuteQueryAsync(connectionString, "SELECT Id, Name FROM Customers ORDER BY Id;");

        Assert.False(result.Succeeded);
        Assert.True(result.IsConcurrencyConflict);
        Assert.Equal(1, result.ExpectedRecordsAffected);
        Assert.Equal(2, result.ActualRecordsAffected);
        Assert.Equal(new[] { "Ada", "Grace" }, rows.ObjectRows.Select(row => row[rows.IndexOfColumn("Name")]?.ToString()).ToArray());
    }

    [Fact]
    public async Task GetSchemaAsync_ReadsTablesAndPrimaryKeysFromInMemoryDatabase()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (
                Id INTEGER NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL
            );
            CREATE INDEX IX_Customers_Name ON Customers (Name);
            CREATE VIEW ActiveCustomers AS SELECT Id, Name FROM Customers;
            """);

        var schema = await provider.GetSchemaAsync(connectionString);
        var root = Assert.Single(schema);
        var table = Assert.Single(root.Children, n => n.Type == SchemaNodeType.Table && n.RawName == "Customers");
        var idColumn = Assert.Single(table.Children, n => n.RawName == "Id");
        var view = Assert.Single(root.Children, n => n.Type == SchemaNodeType.View && n.RawName == "ActiveCustomers");
        var indexes = await provider.GetIndexesAsync(connectionString, "Customers");

        Assert.True(idColumn.IsPrimaryKey);
        Assert.Equal("ActiveCustomers", view.RawName);
        var index = Assert.Single(indexes);
        Assert.Equal("IX_Customers_Name", index.Name);
        Assert.Equal(["Name"], index.Columns);
    }

    [Fact]
    public async Task ExecuteQueryAsync_StopsAtConfiguredRowCap()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (
                Id INTEGER NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL
            );
            INSERT INTO Customers (Id, Name) VALUES (1, 'Ada');
            INSERT INTO Customers (Id, Name) VALUES (2, 'Grace');
            INSERT INTO Customers (Id, Name) VALUES (3, 'Linus');
            """);

        var result = await provider.ExecuteQueryAsync(
            connectionString,
            "SELECT Id, Name FROM Customers ORDER BY Id;",
            new QueryExecutionOptions { MaxResultRows = 2 });

        Assert.Equal(2, result.ObjectRows.Count);
        Assert.True(result.IsTruncated);
        Assert.Equal(2, result.RowCap);
    }

    [Theory]
    [InlineData(50_000, false)]
    [InlineData(50_001, true)]
    public async Task ExecuteQueryAsync_FiftyThousandRowCap_ReportsTruncationOnlyWhenMoreRowsExist(
        int sourceRows, bool expectedTruncation)
    {
        var provider = new SqliteProvider();
        var result = await provider.ExecuteQueryAsync(
            "Data Source=:memory:",
            $"WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM rows WHERE n < {sourceRows}) SELECT n FROM rows;",
            new QueryExecutionOptions { MaxResultRows = 50_000 });

        Assert.False(result.HasError);
        Assert.Equal(50_000, result.ObjectRows.Count);
        Assert.Equal(1L, result.ObjectRows[0][0]);
        Assert.Equal(50_000L, result.ObjectRows[^1][0]);
        Assert.Equal(expectedTruncation, result.IsTruncated);
        Assert.Equal(expectedTruncation ? 50_000 : 0, result.RowCap);
    }

    // ── LoadTablePageAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task LoadTablePageAsync_ReturnsFirstPageWithHasNextPage()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL);
            INSERT INTO Customers (Id, Name) VALUES (1, 'A');
            INSERT INTO Customers (Id, Name) VALUES (2, 'B');
            INSERT INTO Customers (Id, Name) VALUES (3, 'C');
            INSERT INTO Customers (Id, Name) VALUES (4, 'D');
            INSERT INTO Customers (Id, Name) VALUES (5, 'E');
            """);

        var page = await provider.LoadTablePageAsync(connectionString, "Customers", ["Id"], 3, 0);

        Assert.Equal(3, page.Result.ObjectRows.Count);
        Assert.True(page.HasNextPage);
        Assert.Equal(0, page.PageIndex);
    }

    [Fact]
    public async Task LoadTablePageAsync_ReturnsLastPageWithNoNextPage()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL);
            INSERT INTO Customers (Id, Name) VALUES (1, 'A');
            INSERT INTO Customers (Id, Name) VALUES (2, 'B');
            INSERT INTO Customers (Id, Name) VALUES (3, 'C');
            INSERT INTO Customers (Id, Name) VALUES (4, 'D');
            INSERT INTO Customers (Id, Name) VALUES (5, 'E');
            """);

        var page = await provider.LoadTablePageAsync(connectionString, "Customers", ["Id"], 3, 1);

        Assert.Equal(2, page.Result.ObjectRows.Count);
        Assert.False(page.HasNextPage);
        Assert.Equal(1, page.PageIndex);
    }

    [Fact]
    public async Task LoadTablePageAsync_EmptyTable_ReturnsZeroRowsNoNextPage()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString,
            "CREATE TABLE Customers (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL);");

        var page = await provider.LoadTablePageAsync(connectionString, "Customers", [], 10, 0);

        Assert.Empty(page.Result.ObjectRows);
        Assert.False(page.HasNextPage);
    }

    [Fact]
    public async Task LoadTablePageAsync_OrderByColumn_RowsArriveSorted()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL);
            INSERT INTO Customers (Id, Name) VALUES (3, 'Charlie');
            INSERT INTO Customers (Id, Name) VALUES (1, 'Alice');
            INSERT INTO Customers (Id, Name) VALUES (2, 'Bob');
            """);

        var page = await provider.LoadTablePageAsync(connectionString, "Customers", ["Name"], 10, 0);

        var names = page.Result.ObjectRows.Select(r => r[page.Result.IndexOfColumn("Name")]?.ToString()).ToList();
        Assert.Equal(["Alice", "Bob", "Charlie"], names);
    }

    // ── DataChangeTracker integration ─────────────────────────────────────────

    [Fact]
    public async Task DataChangeTracker_UpdateRow_CommitsAgainstSQLite()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT);
            INSERT INTO Customers (Id, Name) VALUES (1, 'Ada');
            """);

        var columns = new[] { "Id", "Name" };
        var tracker = new DataChangeTracker("Customers", columns, ["Id"], provider.Capabilities);

        var row = new EditableRow(new Dictionary<string, object?> { ["Id"] = 1L, ["Name"] = "Ada" });
        row.Values["Name"] = "Grace";
        row.State = EditState.Modified;

        var command = tracker.BuildCommand(row)!;
        var result = await provider.ExecuteTransactionAsync(connectionString, [command]);

        Assert.True(result.Succeeded);
        var rows = await provider.ExecuteQueryAsync(connectionString, "SELECT Name FROM Customers WHERE Id = 1;");
        Assert.Equal("Grace", rows.ObjectRows[0][rows.IndexOfColumn("Name")]);
    }

    [Fact]
    public async Task DataChangeTracker_DeleteRow_CommitsAgainstSQLite()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT);
            INSERT INTO Customers (Id, Name) VALUES (1, 'Ada');
            INSERT INTO Customers (Id, Name) VALUES (2, 'Grace');
            """);

        var columns = new[] { "Id", "Name" };
        var tracker = new DataChangeTracker("Customers", columns, ["Id"], provider.Capabilities);

        var row = new EditableRow(new Dictionary<string, object?> { ["Id"] = 1L, ["Name"] = "Ada" });
        row.State = EditState.Deleted;

        var command = tracker.BuildCommand(row)!;
        await provider.ExecuteTransactionAsync(connectionString, [command]);

        var rows = await provider.ExecuteQueryAsync(connectionString, "SELECT Id FROM Customers ORDER BY Id;");
        var remaining = Assert.Single(rows.ObjectRows);
        Assert.Equal(2L, remaining[rows.IndexOfColumn("Id")]);
    }

    [Fact]
    public async Task ExecuteTransactionAsync_MultiRowBatch_AllRowsLand()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString,
            "CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT);");

        var columns = new[] { "Id", "Name" };
        var tracker = new DataChangeTracker("Customers", columns, ["Id"], provider.Capabilities);

        var rows = new[]
        {
            new EditableRow(new Dictionary<string, object?> { ["Id"] = 1L, ["Name"] = "Ada" }, columns),
            new EditableRow(new Dictionary<string, object?> { ["Id"] = 2L, ["Name"] = "Grace" }, columns),
            new EditableRow(new Dictionary<string, object?> { ["Id"] = 3L, ["Name"] = "Linus" }, columns)
        };
        foreach (var r in rows) r.State = EditState.Inserted;

        var commands = rows.Select(r => tracker.BuildCommand(r)!).ToList();
        var result = await provider.ExecuteTransactionAsync(connectionString, commands);

        Assert.True(result.Succeeded);
        var all = await provider.ExecuteQueryAsync(connectionString, "SELECT Id FROM Customers ORDER BY Id;");
        Assert.Equal(3, all.ObjectRows.Count);
    }

    private static string CreateSharedInMemoryConnectionString() =>
        $"Data Source=DbConnectTests_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    // ── TableDataTabViewModel end-to-end ──────────────────────────────────────

    [Fact]
    public async Task TableDataTabViewModel_RefreshAndCommitEdit_PersistsChangeToSQLite()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL);
            INSERT INTO Customers (Id, Name) VALUES (1, 'Ada');
            """);

        var session = new DatabaseSession(
            new ConnectionInfo { Name = "test", Provider = "SQLite", ConnectionString = connectionString },
            provider);

        var tableNode = new SchemaNode { Name = "Customers", RawName = "Customers", Type = SchemaNodeType.Table };
        tableNode.Children.Add(new SchemaNode { Name = "Id", RawName = "Id", Type = SchemaNodeType.Column, IsPrimaryKey = true });
        tableNode.Children.Add(new SchemaNode { Name = "Name", RawName = "Name", Type = SchemaNodeType.Column });

        var vm = new TableDataTabViewModel("Customers", session, tableNode);
        await vm.RefreshInternalAsync(bypassDirtyCheck: true);

        Assert.Single(vm.Rows);
        Assert.True(vm.HasReliableIdentity);

        vm.Rows[0].Values["Name"] = "Grace";
        vm.MarkRowModified(vm.Rows[0]);
        Assert.True(vm.HasDirtyRows);

        await vm.ExecuteCommitAsync();

        var result = await provider.ExecuteQueryAsync(connectionString, "SELECT Name FROM Customers WHERE Id = 1;");
        Assert.Equal("Grace", result.ObjectRows[0][result.IndexOfColumn("Name")]);
        Assert.False(vm.HasDirtyRows);
    }

    [Fact]
    public async Task TableDataTabViewModel_CommitRollsBackOnFailure_NoPartialWrite()
    {
        var connectionString = CreateSharedInMemoryConnectionString();
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL);
            INSERT INTO Customers (Id, Name) VALUES (1, 'Ada');
            INSERT INTO Customers (Id, Name) VALUES (2, 'Grace');
            """);

        var session = new DatabaseSession(
            new ConnectionInfo { Name = "test", Provider = "SQLite", ConnectionString = connectionString },
            provider);

        var tableNode = new SchemaNode { Name = "Customers", RawName = "Customers", Type = SchemaNodeType.Table };
        tableNode.Children.Add(new SchemaNode { Name = "Id", RawName = "Id", Type = SchemaNodeType.Column, IsPrimaryKey = true });
        tableNode.Children.Add(new SchemaNode { Name = "Name", RawName = "Name", Type = SchemaNodeType.Column });

        var vm = new TableDataTabViewModel("Customers", session, tableNode);
        await vm.RefreshInternalAsync(bypassDirtyCheck: true);

        // Attempt to insert a row that violates the PK constraint (duplicate Id=1)
        var conflictRow = new EditableRow(new Dictionary<string, object?> { ["Id"] = 1L, ["Name"] = "Duplicate" }, ["Id", "Name"])
        {
            State = EditState.Inserted
        };
        vm.Rows.Add(conflictRow);

        await vm.ExecuteCommitAsync();

        // The transaction should fail; original rows must be untouched
        var all = await provider.ExecuteQueryAsync(connectionString, "SELECT Id FROM Customers ORDER BY Id;");
        Assert.Equal(2, all.ObjectRows.Count);
        Assert.Contains("❌", vm.StatusMessage ?? "");
    }
}
