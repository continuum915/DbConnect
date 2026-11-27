using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public static class CsvFailurePositionsContract
{
    public static async Task RunAsync(DatabaseSession session)
    {
        var raw = "rain_csv_edges_" + Guid.NewGuid().ToString("N");
        var table = session.Capabilities.QuoteIdentifier(raw);
        var path = Path.GetTempFileName();
        try
        {
            await session.ExecuteNonQueryAsync($"CREATE TABLE {table}(id INTEGER CHECK(id > 0), label VARCHAR(30)); INSERT INTO {table} VALUES(10001, 'original')");
            foreach (var replace in new[] { false, true })
            {
                var options = new CsvImportOptions { FilePath = path, Mode = ImportMode.ExistingTable, TableName = raw, TruncateBeforeImport = replace };
                foreach (var failureRecord in new[] { 1, 3 })
                {
                    await File.WriteAllLinesAsync(path, new[] { "id,label" }.Concat(Enumerable.Range(1, 3)
                        .Select(i => $"{(i == failureRecord ? -1 : i)},record {i}")));
                    var result = await CsvImportService.ExecuteImportAsync(session, options);
                    Assert.Equal(TransactionOutcome.RolledBack, result.Outcome);
                    Assert.Equal(failureRecord, result.FailedDataRecord);
                    await AssertOriginal();
                }
                await File.WriteAllTextAsync(path, "id,label\n1,valid\n2,\"unclosed");
                var invalid = await CsvImportService.ExecuteImportAsync(session, options);
                Assert.Equal(TransactionOutcome.NoChanges, invalid.Outcome);
                await AssertOriginal();
            }
        }
        finally { File.Delete(path); await session.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {table}"); }

        async Task AssertOriginal()
        {
            var stored = await session.ExecuteQueryAsync($"SELECT id, label FROM {table}");
            var row = Assert.Single(stored.ObjectRows);
            Assert.Equal("10001", row[0]!.ToString());
            Assert.Equal("original", row[1]);
        }
    }
}
