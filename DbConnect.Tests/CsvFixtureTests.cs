using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Tests.ProviderConformance;

namespace DbConnect.Tests;

public class CsvFixtureTests
{
    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task LocalImportsPreserveManifestValues(string provider)
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync(provider);
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, fixture.Provider);
        await CsvFixtureContract.RunAsync(session);
        await CsvTypedValueContract.RunAsync(session);
        await CsvFailurePositionsContract.RunAsync(session);
    }
}
