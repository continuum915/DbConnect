using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public static class CsvNewTableContract
{
    public static async Task RunAsync(DatabaseSession session, bool retainedDdl)
    {
        var raw = "rain_csv_new_" + Guid.NewGuid().ToString("N");
        var table = session.Capabilities.QuoteIdentifier(raw);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "id\n1\n2147483648");
            var options = new CsvImportOptions
            {
                FilePath = path,
                TableName = raw,
                ColumnSpecs = [new CsvColumnSpec { SourceOrdinal = 0, TargetColumnName = "id", OverrideType = DetectedColumnType.Integer }]
            };
            var result = await CsvImportService.ExecuteImportAsync(session, options);
            if (retainedDdl)
            {
                Assert.NotNull(result.RequiredAcknowledgement);
                Assert.True((await session.ExecuteQueryAsync($"SELECT * FROM {table}")).HasError);
                options.RiskAcknowledgement = result.RequiredAcknowledgement.Token;
                result = await CsvImportService.ExecuteImportAsync(session, options);
            }
            Assert.False(result.Succeeded);
            Assert.Equal(TransactionOutcome.RolledBack, result.Outcome);
            Assert.Equal(2, result.FailedDataRecord);
            Assert.Equal(0, result.RowsImported);
            var stored = await session.ExecuteQueryAsync($"SELECT id FROM {table}");
            Assert.Equal(!retainedDdl, stored.HasError);
            Assert.Empty(stored.ObjectRows);
            if (retainedDdl)
            {
                Assert.Contains("table remains", result.ErrorMessage);
                options.Mode = ImportMode.ExistingTable;
                options.RiskAcknowledgement = null;
            }
            await File.WriteAllTextAsync(path, "id\n1\n2");
            result = await CsvImportService.ExecuteImportAsync(session, options);
            Assert.True(result.Succeeded, "Corrected new-table import failed.");
            stored = await session.ExecuteQueryAsync($"SELECT id FROM {table} ORDER BY id");
            Assert.Equal(new[] { "1", "2" }, stored.ObjectRows.Select(r => r[0]!.ToString()));
            options.Mode = ImportMode.NewTable;
            if (retainedDdl) options.RiskAcknowledgement = (await CsvImportExecution.AssessAsync(session, options)).Token;
            result = await CsvImportService.ExecuteImportAsync(session, options);
            Assert.False(result.Succeeded);
            Assert.Null(result.FailedDataRecord);
            stored = await session.ExecuteQueryAsync($"SELECT id FROM {table} ORDER BY id");
            Assert.Equal(new[] { "1", "2" }, stored.ObjectRows.Select(r => r[0]!.ToString()));
        }
        finally { File.Delete(path); await session.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {table}"); }
    }
}
