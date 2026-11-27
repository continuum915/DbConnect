using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

public class CsvImportConformanceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public CsvImportConformanceTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    [InlineData("MySQL")]
    [Trait("Category", "Integration")]
    public async Task ConfiguredProviderProtectsReplacementAndWizardRecovery(string provider)
    {
        var connection = ProviderConformanceFixture.GetEnvironmentConnectionString(provider);
        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.False(IsSelected(provider), "Selected CSV provider requires its conformance connection setting.");
            return;
        }
        await using var fixture = await ProviderConformanceFixture.CreateExternalAsync(provider, connection);
        _output.WriteLine($"Provider: {provider}; version: {fixture.DatabaseVersion}; CSV manifest + first/final/8000 failure recipes.");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = connection }, fixture.Provider);
        await CsvFixtureContract.RunAsync(session);
        await CsvTypedValueContract.RunAsync(session);
        await CsvFailurePositionsContract.RunAsync(session);
        await CsvNewTableContract.RunAsync(session, provider == "MySQL");
        var raw = "rain_csv_" + Guid.NewGuid().ToString("N");
        var table = fixture.Quote(raw);
        var path = Path.GetTempFileName();
        try
        {
            await session.ExecuteNonQueryAsync($"CREATE TABLE {table} (id INTEGER UNIQUE, label VARCHAR(100)); INSERT INTO {table} VALUES(-1, 'original');");
            foreach (var replace in new[] { false, true })
            {
                await File.WriteAllLinesAsync(path, new[] { "id,label" }.Concat(Enumerable.Range(1, 10000)
                    .Select(i => $"{(i == 8000 ? 1 : i)},record {i}")));
                var options = new CsvImportOptions { FilePath = path, TableName = raw, Mode = ImportMode.ExistingTable, TruncateBeforeImport = replace };
                var result = await CsvImportService.ExecuteImportAsync(session, options);
                Assert.False(result.Succeeded);
                Assert.Equal(TransactionOutcome.RolledBack, result.Outcome);
                Assert.Equal(8000, result.FailedDataRecord);
                var stored = await session.ExecuteQueryAsync($"SELECT id, label FROM {table}");
                var original = Assert.Single(stored.ObjectRows);
                Assert.Equal("-1", original[0]!.ToString());
                Assert.Equal("original", original[1]);
                if (replace)
                {
                    var failingWizard = new CsvImportWizardViewModel(session)
                    {
                        FilePath = path,
                        ImportMode = ImportMode.ExistingTable,
                        ExistingTableName = raw,
                        TruncateBeforeImport = true
                    };
                    await failingWizard.NextStepCommand.ExecuteAsync(null);
                    await failingWizard.NextStepCommand.ExecuteAsync(null);
                    Assert.False(failingWizard.ImportSucceeded);
                    Assert.Contains("Data record 8000", failingWizard.ImportError);
                    Assert.Contains("rolled back", failingWizard.ImportError);
                    Assert.Equal("original", Assert.Single((await session.ExecuteQueryAsync($"SELECT label FROM {table}")).ObjectRows)[0]);
                }
            }
            await File.WriteAllTextAsync(path, "id,label\n1,recovered\n2,second");
            var wizard = new CsvImportWizardViewModel(session)
            {
                FilePath = path,
                ImportMode = ImportMode.ExistingTable,
                ExistingTableName = raw,
                TruncateBeforeImport = true
            };
            await wizard.NextStepCommand.ExecuteAsync(null);
            await wizard.NextStepCommand.ExecuteAsync(null);
            Assert.True(wizard.ImportSucceeded, wizard.ImportError);
            Assert.Equal(2, wizard.RowsImported);
            Assert.Equal(2, (await session.ExecuteQueryAsync($"SELECT id FROM {table}")).ObjectRows.Count);
        }
        finally
        {
            File.Delete(path);
            await session.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {table}");
        }
    }

    internal static bool IsSelected(string provider)
    {
        var selected = Environment.GetEnvironmentVariable("DBCONNECT_CSV_REQUIRED_PROVIDERS");
        return selected?.Split(',').Any(p => p == "All" || p.Replace(" ", "") == provider.Replace(" ", "")) == true;
    }
}
