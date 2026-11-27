using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

public sealed class SchemaChangeInvalidationConformanceTests : MainWindowTestBase
{
    [AvaloniaTheory]
    [InlineData("PostgreSQL", "failure")]
    [InlineData("SQL Server", "failure")]
    [InlineData("MySQL", "failure")]
    [InlineData("PostgreSQL", "commit")]
    [InlineData("SQL Server", "commit")]
    [InlineData("MySQL", "commit")]
    [InlineData("PostgreSQL", "rollback")]
    [InlineData("SQL Server", "rollback")]
    [InlineData("MySQL", "rollback")]
    [InlineData("PostgreSQL", "designer")]
    [InlineData("SQL Server", "designer")]
    [InlineData("MySQL", "designer")]
    [InlineData("PostgreSQL", "cancellation")]
    [InlineData("SQL Server", "cancellation")]
    [InlineData("MySQL", "cancellation")]
    [InlineData("SQL Server", "unterminated batch")]
    [InlineData("SQL Server", "unterminated commit")]
    [Trait("Category", "Integration")]
    public async Task ExecutionAndDesignerChangesKeepCatalogTreeAndCompletionAligned(string providerName, string workflow)
    {
        var connection = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (string.IsNullOrWhiteSpace(connection)) return;
        var provider = new DatabaseService().GetProvider(providerName);
        var first = new DatabaseSession(new ConnectionInfo { ConnectionString = connection }, provider);
        var second = new DatabaseSession(new ConnectionInfo { ConnectionString = connection }, provider);
        var vm = CreateMainWindowViewModel();
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(first);
        tab.SelectedSession = first;
        var name = "rain_invalidate_" + Guid.NewGuid().ToString("N")[..12];
        var schema = providerName == "PostgreSQL" ? "public" : providerName == "SQL Server" ? "dbo" : null;
        var quoted = ObjectNames.Quote(provider.Capabilities, new(null, schema, name, SchemaNodeType.Table));
        try
        {
            await first.ExecuteNonQueryAsync($"CREATE TABLE {quoted} (id INTEGER PRIMARY KEY);");
            var root = Assert.Single(await first.GetSchemaAsync());
            vm.ActiveConnections[root] = first.Connection;
            vm.ActiveSessions[root] = first;
            vm.SchemaNodes.Add(root);
            var owner = schema == null ? root : Assert.Single(root.Children, node => node.RawName == schema);
            var folder = schema == null ? owner : Assert.Single(owner.Children, node => node.Type == SchemaNodeType.TableFolder);
            if (folder != root) await vm.SchemaTree.LoadSchemaNodeChildrenAsync(folder);
            var table = Assert.Single(folder.Children, node => node.RawName == name);
            await vm.SchemaTree.LoadSchemaNodeChildrenAsync(table);
            var completionSql = "SELECT " + (schema == null ? provider.Capabilities.QuoteIdentifier(first.DatabaseLabel!) + "." : "") + quoted + ".";
            async Task<string[]> Complete(DatabaseSession session) => (await CatalogSqlCompletion.GetCandidatesAsync(completionSql, completionSql.Length, session))
                .Select(candidate => candidate.Text).Order().ToArray();
            Assert.Equal(["id"], await Complete(first));
            Assert.Equal(["id"], await Complete(second));

            if (workflow == "designer")
            {
                vm.Tools.OpenTableDesignerTab(table);
                var designer = Assert.IsType<TableDesignerViewModel>(vm.SelectedTab);
                await designer.LoadAsync();
                designer.Columns.Add(new ColumnDefinition { Name = "changed", DataType = "INTEGER", IsNew = true });
                await designer.SaveCommand.ExecuteAsync(null);
                Assert.Contains("successfully", designer.StatusMessage);
            }
            else
            {
                tab.IsManualCommit = workflow is "commit" or "rollback" or "unterminated commit";
                var suffix = workflow == "failure" ? " SELECT * FROM rain_missing_invalidation_object;" : "";
                if (workflow == "cancellation") suffix = providerName switch
                {
                    "PostgreSQL" => " SELECT pg_sleep(30);",
                    "SQL Server" => "\nGO\nWAITFOR DELAY '00:00:30';",
                    _ => " SELECT SLEEP(30), 1;"
                };
                tab.QueryText = (workflow.StartsWith("unterminated", StringComparison.Ordinal) ? "SELECT 1\n" : "") + $"ALTER TABLE {quoted} ADD changed INTEGER;" + suffix;
                var enteredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Observe(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(tab.RunningStatementSpan) && tab.RunningStatementSpan?.Start > 0)
                        enteredSecond.TrySetResult();
                }
                tab.PropertyChanged += Observe;
                try
                {
                    var execution = vm.ExecuteQueryCommand.ExecuteAsync(null);
                    if (workflow == "cancellation")
                    {
                        await enteredSecond.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        await Task.Delay(100);
                        Assert.True(tab.IsExecuting, "The delay statement must still be executing before cancellation.");
                        tab.CancelExecutionCommand.Execute(null);
                    }
                    await execution.WaitAsync(TimeSpan.FromSeconds(20));
                }
                finally { tab.PropertyChanged -= Observe; }
                // The existing MySQL SLEEP cancellation returns a successful result rather than an error.
                // Keep that result contract; the in-flight cancellation and metadata agreement are asserted.
                var expectsError = workflow == "failure" || workflow == "cancellation" && providerName != "MySQL";
                Assert.Equal(expectsError, tab.CurrentResult!.HasError);
                if (workflow == "cancellation" && providerName != "MySQL") Assert.True(tab.CurrentResult.Error?.IsCanceled);
                if (tab.IsManualCommit)
                {
                    Assert.True(tab.IsInTransaction);
                    Assert.Contains("transaction", root.MetadataStatus, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(["id"], await Complete(first));
                    await vm.SchemaTree.RefreshNodeAsync(root);
                    Assert.Equal(MetadataLoadState.Pending, root.MetadataState);
                    if (workflow is "commit" or "unterminated commit") await tab.CommitCommand.ExecuteAsync(null);
                    else await tab.RollbackCommand.ExecuteAsync(null);
                    Assert.False(tab.IsInTransaction);
                }
            }

            // MySQL DDL implicitly commits; rollback cannot undo its structural effect.
            var changed = workflow != "rollback" || providerName == "MySQL";
            Assert.Equal(changed ? ["changed", "id"] : ["id"], await Complete(first));
            Assert.Equal(["id"], await Complete(second));
            Assert.Equal(changed ? ["changed", "id"] : ["id"], table.Children.Where(node => node.Type == SchemaNodeType.Column)
                .Select(node => node.RawName).Order());
        }
        finally
        {
            await tab.ReleaseDbSessionAsync();
            first.Catalog.Dispose();
            second.Catalog.Dispose();
            await first.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {quoted};");
        }
    }
}
