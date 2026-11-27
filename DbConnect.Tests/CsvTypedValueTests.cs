using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Tests.ProviderConformance;

namespace DbConnect.Tests;

public class CsvTypedValueTests
{
    [Fact]
    public async Task DateMappingPreservesTimeOfDayInExistingTextContract()
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync("SQLite");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, fixture.Provider);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "moment\n2026-10-25 14:15:16");
            var result = await CsvImportService.ExecuteImportAsync(session, new CsvImportOptions
            {
                FilePath = path,
                TableName = "moments",
                ColumnSpecs = [new CsvColumnSpec { SourceOrdinal = 0, TargetColumnName = "moment", OverrideType = DetectedColumnType.Date }]
            });
            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.Equal("2026-10-25 14:15:16", Assert.Single((await session.ExecuteQueryAsync("SELECT moment FROM moments")).ObjectRows)[0]);
        }
        finally { File.Delete(path); }
    }
}
