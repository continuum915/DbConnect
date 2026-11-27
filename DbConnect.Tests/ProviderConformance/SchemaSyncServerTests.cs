using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// Schema Diff Apply on the servers (apply-comparisons/06, 07): a step that fails part-way stops a MySQL plan with the
/// committed steps kept (or, with continue-on-error, skips it), and rolls back the whole plan on PostgreSQL and SQL
/// Server. Skipped unless the container lane's DBCONNECT_CONFORMANCE_*_CONNECTION_STRING variables are set.
/// </summary>
public sealed class SchemaSyncServerTests
{
    // ab_child references z_parent, which stays unticked, so creating ab_child (or its foreign key) fails.
    private static string Source(string provider) => provider == "MySQL"
        ? """
          CREATE TABLE z_parent (id int PRIMARY KEY) ENGINE=InnoDB;
          CREATE TABLE aa_first (id int PRIMARY KEY) ENGINE=InnoDB;
          CREATE TABLE ab_child (id int PRIMARY KEY, parent_id int, CONSTRAINT fk_child FOREIGN KEY (parent_id) REFERENCES z_parent(id)) ENGINE=InnoDB;
          CREATE TABLE ac_last (id int PRIMARY KEY) ENGINE=InnoDB;
          """
        : """
          CREATE TABLE z_parent (id int PRIMARY KEY);
          CREATE TABLE aa_first (id int PRIMARY KEY);
          CREATE TABLE ab_child (id int PRIMARY KEY, parent_id int, CONSTRAINT fk_child FOREIGN KEY (parent_id) REFERENCES z_parent(id));
          CREATE TABLE ac_last (id int PRIMARY KEY);
          """;

    private static async Task<SchemaDiffViewModel> PreviewedAsync(ScratchServer server, string provider, string suffix)
    {
        var source = await server.CreateDatabaseAsync("src" + suffix, Source(provider));
        var target = await server.CreateDatabaseAsync("dst" + suffix, null);
        var vm = new SchemaDiffViewModel(new ObservableCollection<DatabaseSession> { source, target }) { SourceSession = source, TargetSession = target };
        await vm.RunCompareCommand.ExecuteAsync(null);
        foreach (var item in vm.DiffItems.Where(i => i.ObjectName != "aa_first" && i.ObjectName != "ab_child" && i.ObjectName != "ac_last"))
            item.IsSelected = false;
        DestructiveActionPreview? asked = null;
        vm.Apply.Confirm = preview => { asked = preview; return Task.FromResult(true); };
        await vm.Apply.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.Apply.Plan != null, vm.Apply.Status);
        return vm;
    }

    private static async Task<string> TablesAsync(ScratchServer server, DatabaseSession session)
    {
        var tables = (await DumpCatalog.ResolveAsync(session, DumpSelection.Database)).Tables.Select(t => t.Name).Order();
        return string.Join(",", tables);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    [InlineData("MySQL")]
    public async Task AFailingStepRollsBackOrStopsAsTheEngineAllows(string provider)
    {
        await using var server = ScratchServer.For(provider);
        if (server == null) return;
        var vm = await PreviewedAsync(server, provider, "a");
        var target = vm.TargetSession!;

        if (provider != "MySQL")
        {
            Assert.True(vm.Apply.Plan!.Transactional);
            Assert.False(vm.Apply.CanContinueOnError);
            await vm.Apply.ApplyCommand.ExecuteAsync(null);
            Assert.Equal(SyncOutcomeKind.NotApplied, vm.Apply.Outcome!.Kind);
            Assert.Contains("Every step was rolled back.", vm.Apply.Status);
            await target.SchemaChanges.InvalidateAsync(MetadataScope.Root);
            Assert.Throws<DatabaseDumpException>(() => DumpCatalog.ResolveAsync(target, DumpSelection.Database).GetAwaiter().GetResult());
            return;
        }

        Assert.False(vm.Apply.Plan!.Transactional);
        Assert.True(vm.Apply.CanContinueOnError);
        Assert.Contains("-- COMMITS AT ONCE: cannot be rolled back.", vm.Apply.Script);
        await vm.Apply.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(SyncOutcomeKind.Partial, vm.Apply.Outcome!.Kind);
        Assert.Equal([SyncStepStatus.Applied, SyncStepStatus.Failed, SyncStepStatus.NotRun],
            vm.Apply.Outcome.Steps.Select(s => s.Status));
        Assert.Equal("aa_first", await TablesAsync(server, target));

        // With continue-on-error the table after the failure is created too.
        var again = await PreviewedAsync(server, provider, "b");
        again.Apply.ContinueOnError = true;
        await again.Apply.ApplyCommand.ExecuteAsync(null);
        Assert.Equal([SyncStepStatus.Applied, SyncStepStatus.Failed, SyncStepStatus.Applied], again.Apply.Outcome!.Steps.Select(s => s.Status));
        Assert.Contains("1 failed and were skipped: Table", again.Apply.Status);
        Assert.Equal("aa_first,ac_last", await TablesAsync(server, again.TargetSession!));
    }
}
