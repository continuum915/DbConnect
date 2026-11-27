using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Tests.ProviderConformance;
using DbConnect.ViewModels;
using DbConnect.Views;
using Moq;

namespace DbConnect.Tests;

public class CsvWizardHeadlessTests
{
    [AvaloniaTheory]
    [InlineData("unknown", "uncertain")]
    [InlineData("partial", "At least 1")]
    [InlineData("retained", "destination table remains")]
    public async Task BoundaryOutcomesRemainVisibleWithoutAutomaticRetry(string fault, string expected)
    {
        var provider = new Mock<IDatabaseProvider>();
        provider.SetupGet(p => p.Capabilities).Returns(fault == "retained" ? new MySqlProvider().Capabilities : new SqliteProvider().Capabilities);
        provider.SetupGet(p => p.ProviderName).Returns(fault == "retained" ? "MySQL" : "SQLite");
        provider.Setup(p => p.ExecuteQueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryExecutionOptions?>()))
            .ReturnsAsync(new QueryResult { ObjectRows = [new object?[] { 1L, 0L }] });
        provider.Setup(p => p.ExecuteNonQueryAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(0);
        var executions = 0;
        provider.Setup(p => p.ExecuteTransactionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<SqlCommandSpec>>()))
            .Returns((string _, IReadOnlyList<SqlCommandSpec> commands) =>
            {
                executions++;
                return Task.FromResult(new TransactionResult
                {
                    Outcome = fault == "unknown" ? TransactionOutcome.Unknown : fault == "partial" ? TransactionOutcome.Partial : TransactionOutcome.RolledBack,
                    FailedStatement = fault == "unknown" ? null : commands.Last().CommandText,
                    ErrorMessage = "Controlled database boundary failure",
                    CommandRecordsAffected = commands.Take(commands.Count - 1).ToDictionary(c => c, _ => 1)
                });
            });
        var path = Path.GetTempFileName();
        var wizard = new CsvImportWizardViewModel(new DatabaseSession(new ConnectionInfo(), provider.Object)) { FilePath = path, NewTableName = "destination" };
        var view = new CsvImportWizardView { DataContext = wizard };
        view.Show();
        try
        {
            await File.WriteAllTextAsync(path, "id\n1\n2");
            await wizard.NextStepCommand.ExecuteAsync(null);
            await wizard.NextStepCommand.ExecuteAsync(null);
            if (fault == "retained") await wizard.ProceedAnywayCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(WizardStep.Done, wizard.CurrentStep);
            var shown = string.Join("\n", view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
            Assert.Contains(expected, shown);
            Assert.Equal(1, executions);
        }
        finally { view.Close(); File.Delete(path); }
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VisibleWarningAllowsCancelOrExplicitContinuation(bool proceed)
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync("SQLite");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, fixture.Provider);
        await session.ExecuteNonQueryAsync("CREATE TABLE destination(id INTEGER); CREATE TRIGGER importing AFTER INSERT ON destination BEGIN SELECT 1; END;");
        var path = Path.GetTempFileName();
        var wizard = new CsvImportWizardViewModel(session) { FilePath = path, ImportMode = ImportMode.ExistingTable, ExistingTableName = "destination" };
        var view = new CsvImportWizardView { DataContext = wizard };
        view.Show();
        try
        {
            await File.WriteAllTextAsync(path, "id\n1");
            await ClickAsync("Next");
            await ClickAsync("Next");
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<Button>("ProceedAnyway")!.IsVisible);
            Assert.Contains("Trigger", VisibleText());
            if (proceed)
            {
                wizard.TruncateBeforeImport = true;
                await ClickAsync("Proceed anyway");
                Assert.Equal(WizardStep.ConfigureColumns, wizard.CurrentStep);
                Assert.True(wizard.HasImportWarning);
                Assert.Empty((await session.ExecuteQueryAsync("SELECT id FROM destination")).ObjectRows);
            }
            await ClickAsync(proceed ? "Proceed anyway" : "Don't import");
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<Button>("ProceedAnyway")!.IsVisible);
            Assert.Equal(proceed ? WizardStep.Done : WizardStep.ConfigureColumns, wizard.CurrentStep);
            Assert.Equal(proceed ? 1 : 0, (await session.ExecuteQueryAsync("SELECT id FROM destination")).ObjectRows.Count);
            Assert.Contains(proceed ? "Successfully imported 1" : "No writes", VisibleText());
        }
        finally { view.Close(); File.Delete(path); }

        string VisibleText() => string.Join("\n", view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
        async Task ClickAsync(string content)
        {
            var button = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, content));
            Click(view, button);
            if (content == "Next") await wizard.NextStepCommand.ExecutionTask!;
            else if (content == "Proceed anyway") await wizard.ProceedAnywayCommand.ExecutionTask!;
        }
    }

    [AvaloniaTheory]
    [InlineData("success")]
    [InlineData("malformed")]
    [InlineData("stale")]
    [InlineData("rollback")]
    public async Task WizardControlsRenderRealImportOutcomes(string scenario)
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync("SQLite");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, fixture.Provider);
        await session.ExecuteNonQueryAsync("CREATE TABLE destination(id TEXT UNIQUE); INSERT INTO destination VALUES('original');");
        var path = Path.GetTempFileName();
        var wizard = new CsvImportWizardViewModel(session) { FilePath = path, ImportMode = ImportMode.ExistingTable, ExistingTableName = "destination", TruncateBeforeImport = true };
        var view = new CsvImportWizardView { DataContext = wizard };
        view.Show();
        try
        {
            await File.WriteAllTextAsync(path, scenario == "malformed" ? "code,discard\n\"unclosed" : "code,discard\n001,unused\n002,unused");
            await ClickNext();
            if (scenario == "malformed")
            {
                Assert.Contains("Error reading file", Text());
                await File.WriteAllTextAsync(path, "code,discard\n001,unused\n002,unused");
                await ClickNext();
            }
            wizard.ColumnSpecs[0].TargetColumnName = "id";
            wizard.ColumnSpecs[0].OverrideType = DetectedColumnType.Text;
            wizard.ColumnSpecs[1].Include = false;
            if (scenario == "stale") await File.WriteAllTextAsync(path, "changed\n999");
            if (scenario == "rollback")
            {
                // Preserve the preview while exercising a real late constraint failure.
                wizard.PreviousStepCommand.Execute(null);
                await File.WriteAllLinesAsync(path, new[] { "code,discard" }.Concat(Enumerable.Range(1, 10000).Select(i => $"{(i == 8000 ? 1 : i)},unused")));
                await ClickNext();
                wizard.ColumnSpecs[0].TargetColumnName = "id";
                wizard.ColumnSpecs[1].Include = false;
            }
            await ClickNext();
            Dispatcher.UIThread.RunJobs();
            if (scenario == "stale") Assert.Contains("Reload the preview", Text());
            else if (scenario == "rollback")
            {
                Assert.Contains("Data record 8000", Text());
                Assert.Contains("rolled back", Text());
            }
            else Assert.Contains("Successfully imported 2", Text());
            var stored = await session.ExecuteQueryAsync("SELECT id FROM destination ORDER BY id");
            Assert.Equal(scenario is "stale" or "rollback" ? new[] { "original" } : new[] { "001", "002" }, stored.ObjectRows.Select(r => r[0]!.ToString()));
            if (scenario == "rollback")
            {
                view.Close();
                await File.WriteAllTextAsync(path, "id\n1\n2");
                wizard = new CsvImportWizardViewModel(session) { FilePath = path, ImportMode = ImportMode.ExistingTable, ExistingTableName = "destination", TruncateBeforeImport = true };
                view = new CsvImportWizardView { DataContext = wizard };
                view.Show();
                await ClickNext();
                await ClickNext();
                Assert.True(wizard.ImportSucceeded, wizard.ImportError);
                Assert.Contains("Successfully imported 2", Text());
            }
        }
        finally { view.Close(); File.Delete(path); }

        string Text() => string.Join("\n", view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
        async Task ClickNext()
        {
            var next = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Next"));
            Click(view, next);
            await wizard.NextStepCommand.ExecutionTask!;
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Click(Window window, Button button)
    {
        Dispatcher.UIThread.RunJobs();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }
}
