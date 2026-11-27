using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class SchemaChangeWorkflowTests : MainWindowTestBase
{
    [AvaloniaFact]
    public async Task EditingKnownTableInvalidatesItsDetailsWithoutEvictingAnotherTableOrRootDiscovery()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-designer-scope-{Guid.NewGuid():N}.sqlite");
        var provider = new SqliteProvider();
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={path}" }, provider);
        try
        {
            await provider.ExecuteNonQueryAsync(session.ConnectionString, "CREATE TABLE selected (id INTEGER); CREATE TABLE unrelated (id INTEGER);");
            var workspace = CreateMainWindowViewModel();
            var root = Assert.Single(await session.GetSchemaAsync());
            workspace.ActiveSessions[root] = session;
            workspace.ActiveConnections[root] = session.Connection;
            workspace.SchemaNodes.Add(root);
            var selected = Assert.Single(root.Children, node => node.RawName == "selected");
            var unrelated = Assert.Single(root.Children, node => node.RawName == "unrelated");
            await workspace.SchemaTree.LoadSchemaNodeChildrenAsync(selected);
            await workspace.SchemaTree.LoadSchemaNodeChildrenAsync(unrelated);
            var rootSnapshot = session.Catalog.GetSnapshot(MetadataScope.Root);
            var otherScope = new MetadataScope(unrelated.Identity, SchemaNodeType.Column);
            var otherSnapshot = session.Catalog.GetSnapshot(otherScope);
            var designer = new TableDesignerViewModel("selected", session, selected);
            await designer.LoadAsync();
            designer.Columns.Add(new ColumnDefinition { Name = "added", DataType = "INTEGER", IsNew = true });

            await designer.SaveCommand.ExecuteAsync(null);

            Assert.Contains("successfully", designer.StatusMessage);
            Assert.Same(rootSnapshot, session.Catalog.GetSnapshot(MetadataScope.Root));
            Assert.Same(otherSnapshot, session.Catalog.GetSnapshot(otherScope));
            Assert.Contains(selected.Children, node => node.RawName == "added");
            Assert.Contains(await CatalogSqlCompletion.GetCandidatesAsync("SELECT main.selected.", 21, session), candidate => candidate.Text == "added");
        }
        finally
        {
            session.Catalog.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false, true)]
    public async Task ManualDdlRetainsCommittedMetadataUntilCommitOrRollback(bool commit, bool typed = false, bool close = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-pending-schema-{Guid.NewGuid():N}.sqlite");
        var provider = new SqliteProvider();
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={path}" }, provider);
        QueryTabViewModel? tab = null;
        try
        {
            await provider.ExecuteNonQueryAsync(session.ConnectionString, "CREATE TABLE items (id INTEGER);");
            var root = Assert.Single(await session.GetSchemaAsync());
            var workspace = CreateMainWindowViewModel();
            workspace.ActiveSessions[root] = session;
            workspace.ActiveConnections[root] = session.Connection;
            workspace.SchemaNodes.Add(root);
            workspace.ActiveSessionOptions.Add(session);
            tab = workspace.NewQueryTabWithSession(session);
            Assert.Equal("id", Assert.Single(await Complete()).Text);
            tab.IsManualCommit = true;
            tab.QueryText = "ALTER TABLE items ADD COLUMN pending_column INTEGER;";
            await workspace.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.False(tab.CurrentResult!.HasError);
            Assert.True(tab.IsInTransaction);
            Assert.Equal(MetadataLoadState.Pending, root.MetadataState);
            Assert.Contains("metadata", tab.TransactionStatusText, StringComparison.OrdinalIgnoreCase);
            await workspace.SchemaTree.RefreshNodeAsync(root);
            Assert.Equal("id", Assert.Single(await Complete()).Text);
            Assert.DoesNotContain(root.Children.Single(node => node.RawName == "items").Children, node => node.RawName == "pending_column");

            if (close) await tab.ReleaseDbSessionAsync();
            else if (typed)
            {
                tab.QueryText = commit ? "COMMIT;" : "ROLLBACK;";
                await workspace.ExecuteQueryCommand.ExecuteAsync(null);
            }
            else if (commit) await tab.CommitCommand.ExecuteAsync(null);
            else await tab.RollbackCommand.ExecuteAsync(null);

            Assert.False(tab.IsInTransaction);
            Assert.Equal(MetadataLoadState.Loaded, root.MetadataState);
            Assert.Equal(commit, (await Complete()).Any(candidate => candidate.Text == "pending_column"));
            Assert.Equal(commit, root.Children.Single(node => node.RawName == "items").Children.Any(node => node.RawName == "pending_column"));
            Task<IReadOnlyList<SqlCompletionCandidate>> Complete() => CatalogSqlCompletion.GetCandidatesAsync("SELECT main.items.", 18, session);
        }
        finally
        {
            if (tab != null) await tab.ReleaseDbSessionAsync();
            session.Catalog.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task QueryDdlUpdatesExplorerAndCompletionWhileSelectKeepsTheSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-schema-change-{Guid.NewGuid():N}.sqlite");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={path}" }, new SqliteProvider());
        var workspace = CreateMainWindowViewModel();
        QueryTabViewModel? tab = null;
        try
        {
            var root = Assert.Single(await session.GetSchemaAsync());
            workspace.ActiveSessions[root] = session;
            workspace.ActiveConnections[root] = session.Connection;
            workspace.SchemaNodes.Add(root);
            workspace.ActiveSessionOptions.Add(session);
            tab = workspace.NewQueryTabWithSession(session);
            tab.QueryText = "CREATE TABLE created_in_rain (id INTEGER);";
            await workspace.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.False(tab.CurrentResult!.HasError);
            Assert.Contains(root.Children, node => node.RawName == "created_in_rain");
            Assert.Contains(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session), candidate => candidate.Text == "created_in_rain");
            var snapshot = session.Catalog.GetSnapshot(MetadataScope.Root);
            tab.QueryText = "SELECT 'CREATE TABLE quoted_text' AS value; -- DROP TABLE ignored";
            await workspace.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.Same(snapshot, session.Catalog.GetSnapshot(MetadataScope.Root));
        }
        finally
        {
            if (tab != null) await tab.ReleaseDbSessionAsync();
            session.Catalog.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task DesignerCreateAndDropActionsUpdateTheSharedCatalogWithoutAnExternalCallback()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-designer-change-{Guid.NewGuid():N}.sqlite");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={path}" }, new SqliteProvider());
        try
        {
            var workspace = CreateMainWindowViewModel();
            var root = Assert.Single(await session.GetSchemaAsync());
            workspace.ActiveSessions[root] = session;
            workspace.ActiveConnections[root] = session.Connection;
            workspace.SchemaNodes.Add(root);
            var designer = new TableDesignerViewModel("designed", session);
            designer.Columns.Add(new ColumnDefinition { Name = "id", DataType = "INTEGER", IsNew = true });
            await designer.SaveCommand.ExecuteAsync(null);
            Assert.Contains("successfully", designer.StatusMessage);
            var table = Assert.Single(root.Children, node => node.RawName == "designed");
            Assert.Contains(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session), candidate => candidate.Text == "designed");
            await workspace.DropObjectAsync(table);
            Assert.DoesNotContain(root.Children, node => node.RawName == "designed");
            Assert.DoesNotContain(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session), candidate => candidate.Text == "designed");
        }
        finally
        {
            session.Catalog.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlierSuccessfulDdlRemainsDiscoverableAfterBatchFailureOrCancellation(bool cancel)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-partial-ddl-{Guid.NewGuid():N}.sqlite");
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={path}" }, new SqliteProvider());
        QueryTabViewModel? tab = null;
        try
        {
            var workspace = CreateMainWindowViewModel();
            var root = Assert.Single(await session.GetSchemaAsync());
            workspace.ActiveSessions[root] = session;
            workspace.ActiveConnections[root] = session.Connection;
            workspace.SchemaNodes.Add(root);
            workspace.ActiveSessionOptions.Add(session);
            tab = workspace.NewQueryTabWithSession(session);
            tab.QueryText = cancel
                ? "CREATE TABLE survived (id INTEGER); SELECT 2;"
                : "CREATE TABLE survived (id INTEGER); CREATE TABLE malformed (";
            if (cancel)
                tab.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(tab.RunningStatementSpan) && tab.RunningStatementSpan?.Start > 35)
                        tab.CancelExecutionCommand.Execute(null);
                };
            await workspace.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.True(tab.CurrentResult!.HasError);
            if (cancel) Assert.True(tab.CurrentResult.Error!.IsCanceled);
            Assert.Contains(root.Children, node => node.RawName == "survived");
            Assert.Contains(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session), candidate => candidate.Text == "survived");
        }
        finally
        {
            if (tab != null) await tab.ReleaseDbSessionAsync();
            session.Catalog.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
