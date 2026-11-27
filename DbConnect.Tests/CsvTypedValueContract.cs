using System.Globalization;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public static class CsvTypedValueContract
{
    public static async Task RunAsync(DatabaseSession session)
    {
        var raw = "rain_csv_types_" + Guid.NewGuid().ToString("N");
        var table = session.Capabilities.QuoteIdentifier(raw);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "id,amount,moment,flag,label\n1,1.25,2026-10-25 14:15:16,true,00013");
            var names = new[] { "id", "amount", "moment", "flag", "label" };
            var types = new[] { DetectedColumnType.Integer, DetectedColumnType.Real, DetectedColumnType.Date, DetectedColumnType.Boolean, DetectedColumnType.Text };
            var options = new CsvImportOptions
            {
                FilePath = path,
                TableName = raw,
                ColumnSpecs = names.Select((name, i) => new CsvColumnSpec { SourceOrdinal = i, TargetColumnName = name, OverrideType = types[i] }).ToList()
            };
            var result = await CsvImportService.ExecuteImportAsync(session, options);
            if (result.RequiredAcknowledgement is { } warning)
            {
                options.RiskAcknowledgement = warning.Token;
                result = await CsvImportService.ExecuteImportAsync(session, options);
            }
            Assert.True(result.Succeeded, "Typed CSV fixture failed.");
            Assert.Equal(1, result.RowsImported);
            var row = Assert.Single((await session.ExecuteQueryAsync($"SELECT {string.Join(",", names.Select(session.Capabilities.QuoteIdentifier))} FROM {table}")).ObjectRows);
            Assert.Equal(1L, Convert.ToInt64(row[0], CultureInfo.InvariantCulture));
            Assert.Equal(1.25m, Convert.ToDecimal(row[1], CultureInfo.InvariantCulture));
            Assert.Equal(new DateTime(2026, 10, 25, 14, 15, 16), Convert.ToDateTime(row[2], CultureInfo.InvariantCulture));
            Assert.True(Convert.ToBoolean(row[3], CultureInfo.InvariantCulture));
            Assert.Equal("00013", row[4]);
        }
        finally { File.Delete(path); await session.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {table}"); }
    }
}
