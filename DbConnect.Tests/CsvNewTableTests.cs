using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Tests.ProviderConformance;
using Moq;

namespace DbConnect.Tests;

public class CsvNewTableTests
{
    [Fact]
    public async Task SqliteConstraintFixtureRollsBackCreationAndRowsTogether()
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync("SQLite");
        var provider = new Mock<IDatabaseProvider>();
        provider.SetupGet(p => p.Capabilities).Returns(fixture.Provider.Capabilities);
        provider.SetupGet(p => p.ProviderName).Returns("SQLite");
        // SQLite accepts out-of-range integers. Supply a deterministic engine constraint at the database boundary instead.
        provider.Setup(p => p.ExecuteTransactionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<SqlCommandSpec>>()))
            .Returns((string connection, IReadOnlyList<SqlCommandSpec> commands) => fixture.Provider.ExecuteTransactionAsync(connection,
                new[] { new SqlCommandSpec("CREATE TABLE destination(id INTEGER CHECK(id > 0))", new Dictionary<string, object?>()) }.Concat(commands.Skip(1)).ToList()));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "id\n1\n-1");
            var result = await CsvImportService.ExecuteImportAsync(new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, provider.Object),
                new CsvImportOptions { FilePath = path, TableName = "destination" });
            Assert.Equal(TransactionOutcome.RolledBack, result.Outcome);
            Assert.Equal(2, result.FailedDataRecord);
            Assert.True((await fixture.Provider.ExecuteQueryAsync(fixture.ConnectionString, "SELECT * FROM destination")).HasError);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task StrictEngineFailureRollsBackNewDestination()
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync("DuckDB");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, fixture.Provider);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "id\n1\n2147483648");
            var result = await CsvImportService.ExecuteImportAsync(session, new CsvImportOptions
            {
                FilePath = path,
                TableName = "range_failure",
                ColumnSpecs = [new CsvColumnSpec { SourceOrdinal = 0, TargetColumnName = "id", OverrideType = DetectedColumnType.Integer }]
            });
            Assert.False(result.Succeeded);
            Assert.Equal(TransactionOutcome.RolledBack, result.Outcome);
            Assert.Equal(2, result.FailedDataRecord);
            Assert.True((await session.ExecuteQueryAsync("SELECT * FROM range_failure")).HasError);
        }
        finally { File.Delete(path); }
    }
}
