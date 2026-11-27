using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Tests.ProviderConformance;

namespace DbConnect.Tests;

public class CsvRollbackTests
{
    [Theory]
    [InlineData("SQLite", false)]
    [InlineData("SQLite", true)]
    [InlineData("DuckDB", false)]
    [InlineData("DuckDB", true)]
    public async Task LateFailureRestoresOriginalContentsAndAllowsRetry(string provider, bool replace)
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync(provider);
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, fixture.Provider);
        var table = fixture.Quote("csv_destination");
        await session.ExecuteNonQueryAsync($"CREATE TABLE {table} (id INTEGER UNIQUE, label VARCHAR); INSERT INTO {table} VALUES (-1, 'original');");
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(path, new[] { "id,label" }.Concat(Enumerable.Range(1, 10000)
                .Select(i => $"{(i == 8000 ? 1 : i)},record {i}")));
            var options = new CsvImportOptions { FilePath = path, TableName = "csv_destination", Mode = ImportMode.ExistingTable, TruncateBeforeImport = replace };
            var result = await CsvImportService.ExecuteImportAsync(session, options);
            Assert.False(result.Succeeded);
            Assert.Equal(0, result.RowsImported);
            var stored = await session.ExecuteQueryAsync($"SELECT id, label FROM {table}");
            Assert.Equal(new object?[] { -1L, "original" }, Assert.Single(stored.ObjectRows).Select(v => v is int n ? (object)(long)n : v));
            await File.WriteAllTextAsync(path, "id,label\n1,recovered\n2,second");
            result = await CsvImportService.ExecuteImportAsync(session, options);
            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.Equal(2, result.RowsImported);
            stored = await session.ExecuteQueryAsync($"SELECT id FROM {table} ORDER BY id");
            Assert.Equal(replace ? 2 : 3, stored.ObjectRows.Count);
        }
        finally { File.Delete(path); }
    }
}
