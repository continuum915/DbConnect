using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Saved Data Transfer profiles, in the wizard and as Automation jobs (data-transfer/08).</summary>
public sealed class DataTransferProfileTests : DumpTestDatabases
{
    private const string Source = """
        CREATE TABLE a (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO a VALUES (1, 'x');
        CREATE TABLE b (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO b VALUES (1, 'one'), (2, 'two');
        """;

    private string Store => Path.Combine(DumpDirectory, "app");

    private async Task<(DatabaseSession Source, DatabaseSession Target)> DatabasesAsync() =>
        (await SessionAsync("SQLite", "source", Source), await SessionAsync("SQLite", "target", "CREATE TABLE b (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO b VALUES (9, 'old');"));

    private static DataTransferProfile Nightly(DatabaseSession source, DatabaseSession target, DataTransferTargetMode mode = DataTransferTargetMode.Empty) => new()
    {
        Name = "nightly",
        Source = new() { ConnectionId = source.Connection.Id, ConnectionName = source.Name },
        Target = new() { ConnectionId = target.Connection.Id, ConnectionName = target.Name },
        Tables = [new() { Schema = "main", Name = "b", Mode = mode }]
    };

    [Fact]
    public async Task ProfilesSaveReplaceAndDeleteByNameAndHoldNoCredentials()
    {
        var (source, target) = await DatabasesAsync();
        source.Connection.ConnectionString += ";Password=secret-password";
        var store = new DataTransferProfiles(Store);

        await store.SaveAsync(Nightly(source, target));
        await store.SaveAsync(Nightly(source, target, DataTransferTargetMode.DropAndRecreate));
        await store.SaveAsync(new DataTransferProfile { Name = "weekly" });

        var profiles = await store.LoadAsync();
        Assert.Equal(["nightly", "weekly"], profiles.Select(p => p.Name));
        Assert.Equal(DataTransferTargetMode.DropAndRecreate, profiles[0].Tables[0].Mode);
        var file = await File.ReadAllTextAsync(Path.Combine(Store, "data-transfer-profiles.json"));
        Assert.DoesNotContain("secret-password", file);
        Assert.DoesNotContain("Data Source", file);
        Assert.Contains("\"DropAndRecreate\"", file);
        await store.DeleteAsync("NIGHTLY");
        Assert.Equal(["weekly"], (await store.LoadAsync()).Select(p => p.Name));
    }

    [Fact]
    public async Task TheWizardSavesATransferAndLoadsItBackIntoEveryStep()
    {
        var (source, target) = await DatabasesAsync();
        var sessions = new ObservableCollection<DatabaseSession> { source, target };
        var wizard = new DataTransferViewModel(sessions, new DataTransferProfiles(Store)) { SourceSession = source };
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.TargetSession = target;
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.Tables.Single(t => t.Name == "main.a").IsSelected = false;
        await wizard.NextCommand.ExecuteAsync(null);
        ((DataTransferTableChoice)wizard.Tables.Single(t => t.Name == "main.b")).Mode =
            DataTransferViewModel.TableModeChoices.Single(c => c.Mode == DataTransferTargetMode.Empty);
        wizard.Transaction = DataTransferViewModel.TransactionChoices.Single(c => c.Mode == DataTransferTransactionMode.PerBatch);
        wizard.ContinueOnError = true;
        wizard.BatchSize = 7;
        wizard.IncludeIndexes = false;
        wizard.Profiles.NewName = "nightly";
        await wizard.Profiles.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("Saved \"nightly\"", wizard.Profiles.Message);

        var again = new DataTransferViewModel(sessions, new DataTransferProfiles(Store));
        await again.Profiles.RefreshAsync();
        again.Profiles.Selected = "nightly";
        await again.Profiles.LoadCommand.ExecuteAsync(null);

        Assert.True(again.IsOptionsStep, again.Profiles.Message);
        Assert.Same(source, again.Source);
        Assert.Same(target, again.Target);
        Assert.Equal(["main.b"], again.Tables.Where(t => t.IsSelected).Select(t => t.Name));
        Assert.Equal(DataTransferTargetMode.Empty, ((DataTransferTableChoice)again.Tables.Single(t => t.Name == "main.b")).Mode.Mode);
        Assert.Equal(DataTransferTransactionMode.PerBatch, again.Transaction.Mode);
        Assert.True(again.ContinueOnError);
        Assert.Equal(7, again.BatchSize);
        Assert.False(again.IncludeIndexes);

        // A profile whose connection is not open says so and changes nothing.
        var closed = new DataTransferViewModel([target], new DataTransferProfiles(Store));
        await closed.Profiles.RefreshAsync();
        closed.Profiles.Selected = "nightly";
        await closed.Profiles.LoadCommand.ExecuteAsync(null);
        Assert.Equal("Open the source connection \"source\" first.", closed.Profiles.Message);
        Assert.True(closed.IsSourceStep);
    }

    [Fact]
    public async Task AutomationRunsAProfileOnDemandAndRecordsTheOutcome()
    {
        var (source, target) = await DatabasesAsync();
        await new DataTransferProfiles(Store).SaveAsync(Nightly(source, target));
        var service = new AutomationService(dataDirectory: Store);
        var job = new AutomationJob { Name = "Copy b", Type = JobType.DataTransfer, SqlOrFilePath = "nightly" };
        await service.SaveJobAsync(job);
        var automation = new AutomationViewModel(new ObservableCollection<DatabaseSession> { source, target }, service);
        await automation.LoadJobs();
        automation.SelectedJob = automation.Jobs.Single();

        await automation.RunNowCommand.ExecuteAsync(null);

        Assert.Equal("\"Copy b\" succeeded: 2 rows transferred.", automation.StatusMessage);
        Assert.Equal("1|one\n2|two", await QueryAsync(target, "SELECT * FROM b ORDER BY id"));
        var record = Assert.Single(await service.LoadRunHistoryAsync(job.Id));
        Assert.Equal(JobStatus.Succeeded, record.Status);
        Assert.Equal(2, record.RowsAffected);
    }

    [Fact]
    public async Task AutomationReportsAMissingProfileAClosedConnectionAndAFailedTable()
    {
        var (source, target) = await DatabasesAsync();
        var service = new AutomationService(dataDirectory: Store);
        var job = new AutomationJob { Name = "Copy", Type = JobType.DataTransfer, SqlOrFilePath = "nightly" };

        var missing = await service.ExecuteTransferJobAsync(job, [source, target], CancellationToken.None);
        Assert.Equal(JobStatus.Failed, missing.Status);
        Assert.Equal("There is no transfer profile named \"nightly\".", missing.ErrorMessage);

        await new DataTransferProfiles(Store).SaveAsync(Nightly(source, target, DataTransferTargetMode.Create));
        var closed = await service.ExecuteTransferJobAsync(job, [source], CancellationToken.None);
        Assert.Equal("Open the target connection \"target\" first, then run the transfer again.", closed.ErrorMessage);

        // Create refuses the existing table, so the job fails and names it.
        var failed = await service.ExecuteTransferJobAsync(job, [source, target], CancellationToken.None);
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal("main.b: Failed The target table already exists, so it was left unchanged.", failed.ErrorMessage);
    }

    [Fact]
    public void TheJobEditorTakesAProfileInsteadOfAConnection()
    {
        var editor = new JobEditorDialogViewModel { Name = "Copy", Type = JobType.DataTransfer, SqlOrFilePath = "nightly" };

        Assert.False(editor.NeedsConnection);
        Assert.True(editor.CanSave);
        Assert.Equal("Transfer profile", editor.DetailLabel);
        editor.Type = JobType.RunQuery;
        Assert.False(editor.CanSave);
    }
}
