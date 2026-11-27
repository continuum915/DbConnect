using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Tests.ProviderConformance;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class CsvRiskAcknowledgementTests
{
    [Fact]
    public async Task AcknowledgedTriggerThatSkipsRowsDoesNotInflateCommittedCount()
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync("SQLite");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, fixture.Provider);
        await session.ExecuteNonQueryAsync("CREATE TABLE destination(id INTEGER); CREATE TRIGGER skip_record BEFORE INSERT ON destination WHEN NEW.id=2 BEGIN SELECT RAISE(IGNORE); END;");
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "id\n1\n2\n3");
            var options = new CsvImportOptions { FilePath = path, TableName = "destination", Mode = ImportMode.ExistingTable };
            var result = await CsvImportService.ExecuteImportAsync(session, options);
            options.RiskAcknowledgement = Assert.IsType<CsvImportAssessment>(result.RequiredAcknowledgement).Token;
            result = await CsvImportService.ExecuteImportAsync(session, options);
            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.Equal(2, result.RowsImported);
            Assert.Equal(2, (await session.ExecuteQueryAsync("SELECT id FROM destination")).ObjectRows.Count);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task ExplicitTextOverridePreservesLeadingZerosDespiteIntegerInference()
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync("SQLite");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, fixture.Provider);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "code\n00013");
            var wizard = new CsvImportWizardViewModel(session) { FilePath = path, NewTableName = "codes" };
            await wizard.NextStepCommand.ExecuteAsync(null);
            wizard.ColumnSpecs[0].OverrideType = DetectedColumnType.Text;
            await wizard.NextStepCommand.ExecuteAsync(null);
            Assert.True(wizard.ImportSucceeded, wizard.ImportError);
            Assert.Equal("00013", Assert.Single((await session.ExecuteQueryAsync("SELECT code FROM codes")).ObjectRows)[0]);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task WizardKeepsWarningInConfigureStepAndDoesNotWriteUntilExplicitContinuation()
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync("SQLite");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, fixture.Provider);
        await session.ExecuteNonQueryAsync("CREATE TABLE destination(id INTEGER UNIQUE); CREATE TRIGGER import_trigger AFTER INSERT ON destination BEGIN SELECT 1; END;");
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "id\n1");
            var wizard = new CsvImportWizardViewModel(session) { FilePath = path, ImportMode = ImportMode.ExistingTable, ExistingTableName = "destination" };
            await wizard.NextStepCommand.ExecuteAsync(null);
            await wizard.NextStepCommand.ExecuteAsync(null);
            Assert.Equal(WizardStep.ConfigureColumns, wizard.CurrentStep);
            Assert.Contains("Trigger", wizard.StatusMessage);
            Assert.Empty((await session.ExecuteQueryAsync("SELECT * FROM destination")).ObjectRows);
            wizard.CancelImportWarningCommand.Execute(null);
            Assert.False(wizard.HasImportWarning);
            Assert.Empty((await session.ExecuteQueryAsync("SELECT * FROM destination")).ObjectRows);
            await wizard.NextStepCommand.ExecuteAsync(null);
            await wizard.ProceedAnywayCommand.ExecuteAsync(null);
            Assert.True(wizard.ImportSucceeded, wizard.ImportError);
            Assert.Single((await session.ExecuteQueryAsync("SELECT * FROM destination")).ObjectRows);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ChangedReplacementRiskRequiresRenewedAcknowledgement()
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync("SQLite");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, fixture.Provider);
        await session.ExecuteNonQueryAsync("CREATE TABLE destination(id INTEGER); INSERT INTO destination VALUES(9); CREATE TRIGGER import_trigger AFTER INSERT ON destination BEGIN SELECT 1; END;");
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "id\n1");
            var options = new CsvImportOptions { FilePath = path, TableName = "destination", Mode = ImportMode.ExistingTable };
            var warning = await CsvImportService.ExecuteImportAsync(session, options);
            options.RiskAcknowledgement = Assert.IsType<CsvImportAssessment>(warning.RequiredAcknowledgement).Token;
            options.TruncateBeforeImport = true;
            var result = await CsvImportService.ExecuteImportAsync(session, options);
            Assert.NotNull(result.RequiredAcknowledgement);
            Assert.Equal("9", Assert.Single((await session.ExecuteQueryAsync("SELECT id FROM destination")).ObjectRows)[0]!.ToString());
        }
        finally { File.Delete(path); }
    }
}
