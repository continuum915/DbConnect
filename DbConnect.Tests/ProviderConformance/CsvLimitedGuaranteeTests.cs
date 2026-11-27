using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

public class CsvLimitedGuaranteeTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task MySqlWizardRequiresExplicitChoiceBeforeCreatingDestination()
    {
        var connection = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL");
        if (connection == null) { Assert.False(CsvImportConformanceTests.IsSelected("MySQL")); return; }
        await using var fixture = await ProviderConformanceFixture.CreateExternalAsync("MySQL", connection);
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = connection }, fixture.Provider);
        var raw = "rain_csv_wizard_" + Guid.NewGuid().ToString("N");
        var table = fixture.Quote(raw);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "id\n1");
            var wizard = new CsvImportWizardViewModel(session) { FilePath = path, NewTableName = raw };
            await wizard.NextStepCommand.ExecuteAsync(null);
            await wizard.NextStepCommand.ExecuteAsync(null);
            Assert.True(wizard.HasImportWarning);
            Assert.True((await session.ExecuteQueryAsync($"SELECT * FROM {table}")).HasError);
            wizard.CancelImportWarningCommand.Execute(null);
            Assert.True((await session.ExecuteQueryAsync($"SELECT * FROM {table}")).HasError);
            await wizard.NextStepCommand.ExecuteAsync(null);
            await wizard.ProceedAnywayCommand.ExecuteAsync(null);
            Assert.True(wizard.ImportSucceeded, wizard.ImportError);
            Assert.Single((await session.ExecuteQueryAsync($"SELECT * FROM {table}")).ObjectRows);
        }
        finally { File.Delete(path); await session.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {table}"); }
    }
    [Fact]
    [Trait("Category", "Integration")]
    public async Task MyIsamRequiresAcknowledgementAndReportsPersistedPrefix()
    {
        var connection = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL");
        if (connection == null) { Assert.False(CsvImportConformanceTests.IsSelected("MySQL")); return; }
        await using var fixture = await ProviderConformanceFixture.CreateExternalAsync("MySQL", connection);
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = connection }, fixture.Provider);
        var raw = "rain_csv_" + Guid.NewGuid().ToString("N");
        var table = fixture.Quote(raw);
        var path = Path.GetTempFileName();
        try
        {
            await session.ExecuteNonQueryAsync($"CREATE TABLE {table}(id INTEGER UNIQUE) ENGINE=MyISAM; INSERT INTO {table} VALUES(9)");
            await File.WriteAllTextAsync(path, "id\n1\n1");
            var options = new CsvImportOptions { FilePath = path, TableName = raw, Mode = ImportMode.ExistingTable, TruncateBeforeImport = true };
            var warning = await CsvImportService.ExecuteImportAsync(session, options);
            Assert.NotNull(warning.RequiredAcknowledgement);
            Assert.Equal("9", Assert.Single((await session.ExecuteQueryAsync($"SELECT id FROM {table}")).ObjectRows)[0]!.ToString());
            options.RiskAcknowledgement = warning.RequiredAcknowledgement.Token;
            var result = await CsvImportService.ExecuteImportAsync(session, options);
            Assert.False(result.Succeeded);
            Assert.Equal(TransactionOutcome.Partial, result.Outcome);
            Assert.Equal(1, result.RowsImported);
            Assert.Equal("1", Assert.Single((await session.ExecuteQueryAsync($"SELECT id FROM {table}")).ObjectRows)[0]!.ToString());
        }
        finally { File.Delete(path); await session.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {table}"); }
    }
}
