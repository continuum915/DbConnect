using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class CsvImportServiceTests
{
    private static string CreateTempCsvFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain_test_{System.Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    [Fact]
    public void ParseCsv_WithHeader_ReturnsHeadersAndRows()
    {
        var path = CreateTempCsvFile("Id,Name\n1,Alice\n2,Bob");
        var rows = CsvImportService.ParseCsv(path, ',', true, out var headers);
        Assert.Equal(new[] { "Id", "Name" }, headers);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Alice", rows[0][1]);
        File.Delete(path);
    }

    [Fact]
    public void ParseCsv_WithoutHeader_ReturnsAllRowsNoHeader()
    {
        var path = CreateTempCsvFile("1,Alice\n2,Bob");
        var rows = CsvImportService.ParseCsv(path, ',', false, out var headers);
        Assert.Empty(headers);
        Assert.Equal(2, rows.Count);
        File.Delete(path);
    }

    [Fact]
    public void ParseCsv_QuotedField_HandlesCommaInValue()
    {
        var path = CreateTempCsvFile("Id,Name\n1,\"Smith, Jr.\"");
        var rows = CsvImportService.ParseCsv(path, ',', true, out _);
        Assert.Single(rows);
        Assert.Equal("Smith, Jr.", rows[0][1]);
        File.Delete(path);
    }

    [Fact]
    public async Task ExecuteImportAsync_NewTable_InsertsRows()
    {
        var path = CreateTempCsvFile("Id,Name\n1,Alice\n2,Bob");
        var dbPath = Path.Combine(Path.GetTempPath(), $"rain_test_{System.Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        var conn = new ConnectionInfo { Name = "test", Provider = "SQLite", ConnectionString = connStr };
        var session = new DatabaseSession(conn, provider);

        var opts = new CsvImportOptions
        {
            FilePath = path,
            Delimiter = ',',
            HasHeaderRow = true,
            Mode = ImportMode.NewTable,
            TableName = "people"
        };

        var result = await CsvImportService.ExecuteImportAsync(session, opts);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.RowsImported);

        var queryResult = await provider.ExecuteQueryAsync(connStr, "SELECT * FROM \"people\";");
        Assert.Equal(2, queryResult.ObjectRows.Count);
        File.Delete(path);
        // db file is temp and will be cleaned by OS; skip deletion to avoid lock race
    }

    [Fact]
    public async Task ExecuteImportAsync_ExistingTable_AppendsRows()
    {
        var path = CreateTempCsvFile("Id,Name\n3,Carol");
        var dbPath = Path.Combine(Path.GetTempPath(), $"rain_test_{System.Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connStr, "CREATE TABLE people (Id TEXT, Name TEXT);");
        await provider.ExecuteNonQueryAsync(connStr, "INSERT INTO people VALUES ('1','Alice');");

        var conn = new ConnectionInfo { Name = "test", Provider = "SQLite", ConnectionString = connStr };
        var session = new DatabaseSession(conn, provider);

        var opts = new CsvImportOptions
        {
            FilePath = path,
            Delimiter = ',',
            HasHeaderRow = true,
            Mode = ImportMode.ExistingTable,
            TableName = "people",
            ColumnNames = new List<string> { "Id", "Name" }
        };

        var result = await CsvImportService.ExecuteImportAsync(session, opts);
        Assert.True(result.Succeeded);

        var queryResult = await provider.ExecuteQueryAsync(connStr, "SELECT * FROM people;");
        Assert.Equal(2, queryResult.ObjectRows.Count);
        File.Delete(path);
        // db file is temp and will be cleaned by OS; skip deletion to avoid lock race
    }
}
