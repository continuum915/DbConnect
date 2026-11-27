using DbConnect.ViewModels;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class CsvImportWizardTests : IDisposable
{
    private readonly string _path = Path.GetTempFileName();

    [Theory]
    [InlineData("")]
    [InlineData("left,right\n")]
    public async Task NoDataCannotAdvance(string content)
    {
        await File.WriteAllTextAsync(_path, content);
        var wizard = CreateWizard();
        await wizard.NextStepCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.PickFile, wizard.CurrentStep);
        Assert.Contains("no importable data", wizard.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(wizard.ColumnSpecs);
    }

    private CsvImportWizardViewModel CreateWizard() => new() { FilePath = _path, NewTableName = "imported" };

    [Fact]
    public async Task FailedReloadClearsPreviewAndCanRecover()
    {
        await File.WriteAllTextAsync(_path, "left,right\nfirst,second");
        var wizard = CreateWizard();
        await wizard.NextStepCommand.ExecuteAsync(null);
        wizard.PreviousStepCommand.Execute(null);
        await File.WriteAllTextAsync(_path, "left,right\n\"unclosed,second");
        await wizard.NextStepCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.PickFile, wizard.CurrentStep);
        Assert.Empty(wizard.ColumnSpecs);
        Assert.Empty(wizard.PreviewRows);
        Assert.Empty(wizard.DetectionSummary);
        Assert.Empty(wizard.PreviewDdl);
        Assert.Contains("record 2", wizard.StatusMessage, StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(_path, "left,right\nfixed,second");
        await wizard.NextStepCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.ConfigureColumns, wizard.CurrentStep);
        Assert.Equal("fixed", Assert.Single(wizard.PreviewRows)[0]);
    }

    [Theory]
    [InlineData("delimiter")]
    [InlineData("header")]
    [InlineData("file")]
    [InlineData("content")]
    [InlineData("excluded")]
    [InlineData("blank")]
    [InlineData("duplicate")]
    [InlineData("ordinal")]
    public async Task InvalidOrStaleStateCannotExecute(string change)
    {
        await File.WriteAllTextAsync(_path, "left,right\nfirst,second");
        var wizard = CreateWizard();
        await wizard.NextStepCommand.ExecuteAsync(null);
        switch (change)
        {
            case "delimiter": wizard.DelimiterIndex = 2; break;
            case "header": wizard.HasHeaderRow = false; break;
            case "file": wizard.FilePath = _path + ".missing"; break;
            case "content": await File.WriteAllTextAsync(_path, "other\nvalue"); break;
            case "excluded": foreach (var spec in wizard.ColumnSpecs) spec.Include = false; break;
            case "blank": wizard.ColumnSpecs[0].TargetColumnName = " "; break;
            case "duplicate": wizard.ColumnSpecs[0].TargetColumnName = "RIGHT"; break;
            case "ordinal": wizard.ColumnSpecs[0].SourceOrdinal = 9; break;
        }
        await wizard.NextStepCommand.ExecuteAsync(null);
        Assert.NotEqual(WizardStep.Done, wizard.CurrentStep);
        Assert.NotEqual(WizardStep.Executing, wizard.CurrentStep);
        Assert.False(string.IsNullOrWhiteSpace(wizard.StatusMessage));
    }

    public void Dispose() => File.Delete(_path);

    [Theory]
    [InlineData(false, "first|second|third", 1, "second")]
    [InlineData(true, "same|same|\nfirst|second|third", 0, "first")]
    [InlineData(true, "same|same|\nfirst|second|third", 2, "third")]
    public async Task ReloadAndImportPreserveSelectedSourcePosition(bool header, string content, int selected, string expected)
    {
        var connectionString = $"Data Source=wizard_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var provider = new SqliteProvider();
        var session = new DatabaseSession(new ConnectionInfo
        {
            Name = "csv",
            Provider = "SQLite",
            ConnectionString = connectionString
        }, provider);
        var wizard = new CsvImportWizardViewModel(session) { FilePath = _path, NewTableName = "imported" };
        await File.WriteAllTextAsync(_path, "\"broken");
        await wizard.NextStepCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.PickFile, wizard.CurrentStep);
        await File.WriteAllTextAsync(_path, content);
        wizard.HasHeaderRow = header;
        wizard.DelimiterIndex = 2;
        await wizard.NextStepCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.ConfigureColumns, wizard.CurrentStep);
        Assert.Equal(new int?[] { 0, 1, 2 }, wizard.ColumnSpecs.Select(s => s.SourceOrdinal));
        for (var i = 0; i < wizard.ColumnSpecs.Count; i++)
        {
            wizard.ColumnSpecs[i].Include = i == selected;
            wizard.ColumnSpecs[i].TargetColumnName = "renamed";
        }
        await wizard.NextStepCommand.ExecuteAsync(null);
        Assert.True(wizard.ImportSucceeded, wizard.ImportError);
        Assert.Equal(WizardStep.Done, wizard.CurrentStep);
        Assert.Equal(1, wizard.RowsImported);
        var result = await session.ExecuteQueryAsync("SELECT renamed FROM imported");
        Assert.Equal(new object?[] { expected }, Assert.Single(result.ObjectRows));
    }

    [Fact]
    public async Task PreviewShowsTwentyLogicalRecordsAndExactTotalAfterSettingsReload()
    {
        await File.WriteAllTextAsync(_path, "old\nvalue");
        var wizard = CreateWizard();
        await wizard.NextStepCommand.ExecuteAsync(null);
        wizard.PreviousStepCommand.Execute(null);
        wizard.DelimiterIndex = 3;
        wizard.HasHeaderRow = false;
        await File.WriteAllTextAsync(_path, string.Join("\n", Enumerable.Range(1, 25).Select(i => $"{i};\"line one\nline two\"")));
        await wizard.NextStepCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.ConfigureColumns, wizard.CurrentStep);
        Assert.Equal(20, wizard.PreviewRows.Count);
        Assert.Equal(new[] { "1", "line one\nline two" }, wizard.PreviewRows[0]);
        Assert.Equal("Detected 2 column(s) - 25 data row(s)", wizard.DetectionSummary);
        Assert.Equal(new int?[] { 0, 1 }, wizard.ColumnSpecs.Select(s => s.SourceOrdinal));
    }
}
