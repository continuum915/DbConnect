using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class StructuredObjectIntegrationTests
{
    [Fact]
    public async Task AmbiguousLegacyTabRequiresReselectionBeforeAllRowsExport()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-export-identity-{Guid.NewGuid():N}.db");
        var provider = new SqliteProvider();
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={path}" }, provider);
        var tab = new TableDataTabViewModel("main.orders", session);
        try
        {
            await provider.ExecuteNonQueryAsync(session.ConnectionString,
                "CREATE TABLE orders (id INTEGER); CREATE TABLE \"main.orders\" (id INTEGER);");
            tab.ObjectSelection.Restore(session, null, "main.orders");
            await tab.RefreshInternalAsync(true);
            Assert.True(tab.ObjectSelection.RequiresReselection);
            var picker = new RecordingPicker();
            tab.ExportPathPicker = picker;
            tab.ExportScope = ExportScope.AllMatchingTableRows;
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.False(picker.Called);
            Assert.Contains("Select this table again", tab.StatusMessage);
        }
        finally
        {
            await tab.ReleaseDbSessionAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private sealed class RecordingPicker : IExportPathPicker
    {
        public bool Called { get; private set; }
        public Task<string?> PickSavePathAsync(string suggestedFileName, ExportFileType fileType)
        {
            Called = true;
            return Task.FromResult<string?>(null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryPreservesLiteralDotsAndCaseDistinctUnicodeNames(bool duck)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-distinct-{Guid.NewGuid():N}.db");
        IDatabaseProvider provider = duck ? new DuckDbProvider() : new SqliteProvider();
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = $"Data Source={path}" }, provider);
        try
        {
            await provider.ExecuteNonQueryAsync(session.ConnectionString,
                "CREATE TABLE orders (id INTEGER); CREATE TABLE \"main.orders\" (id INTEGER); CREATE TABLE \"Ä\" (id INTEGER); CREATE TABLE \"ä\" (id INTEGER);");
            Assert.Null(await session.Objects.ResolveLegacyRelationAsync("main.orders"));
            var upper = await session.Objects.ResolveLegacyRelationAsync("Ä");
            var lower = await session.Objects.ResolveLegacyRelationAsync("ä");
            Assert.NotNull(upper);
            Assert.NotNull(lower);
            Assert.False(session.Objects.Same(upper, lower));
            Assert.True(session.Objects.Same(upper, upper with { Name = "Ä" }));
            var orders = await session.Objects.ResolveLegacyRelationAsync("ORDERS");
            Assert.Equal("orders", orders!.Name);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false, "order. details")]
    [InlineData(true, "order. details")]
    [InlineData(false, "a\"b'c] d")]
    [InlineData(true, "a\"b'c] d")]
    public async Task PunctuatedRelationUsesTheSameObjectForColumnsPageAndCount(bool duck, string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-identity-{Guid.NewGuid():N}.db");
        IDatabaseProvider provider = duck ? new DuckDbProvider() : new SqliteProvider();
        var connection = new ConnectionInfo { ConnectionString = $"Data Source={path}", Provider = provider.ProviderName };
        var session = new DatabaseSession(connection, provider);
        try
        {
            var quoted = provider.Capabilities.QuoteIdentifier(name);
            await provider.ExecuteNonQueryAsync(connection.ConnectionString,
                $"CREATE TABLE {quoted} (\"id]\" INTEGER); INSERT INTO {quoted} VALUES (73);");
            var root = Assert.Single(await session.GetSchemaAsync());
            var tables = duck
                ? await session.Objects.GetChildrenAsync(Assert.Single(root.Children, node => node.RawName == "main").Identity!, SchemaNodeType.Table)
                : root.Children.ToList();
            var identity = Assert.Single(tables, node => node.RawName == name).Identity!;
            var columns = await session.Objects.GetColumnsAsync(identity);
            Assert.Equal("id]", Assert.Single(columns).Name);
            var sql = "SELECT " + quoted + ".";
            var completion = await CatalogSqlCompletion.GetCandidatesAsync(sql, sql.Length, session);
            Assert.Equal(provider.Capabilities.QuoteIdentifier("id]"), Assert.Single(completion).Text);
            var spec = new TableQuerySpec([], 0, 10) { Object = identity };
            var page = await provider.LoadTablePageAsync(connection.ConnectionString, "unused", spec);
            Assert.False(page.Result.HasError, page.Result.ErrorMessage);
            Assert.Equal(73L, Convert.ToInt64(Assert.Single(page.Result.ObjectRows)[0]));
            Assert.Equal(1L, await provider.CountTableRowsAsync(connection.ConnectionString, "unused", spec));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LegacyDuplicateSchemaNamesRequireExplicitReselection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-legacy-{Guid.NewGuid():N}.duckdb");
        var provider = new DuckDbProvider();
        var connection = new ConnectionInfo { ConnectionString = $"Data Source={path}" };
        var session = new DatabaseSession(connection, provider);
        try
        {
            await provider.ExecuteNonQueryAsync(connection.ConnectionString,
                "CREATE SCHEMA \"sales.eu\"; CREATE SCHEMA archive; CREATE TABLE \"sales.eu\".orders (id INTEGER); CREATE TABLE archive.orders (id INTEGER);");
            Assert.Null(await session.Objects.ResolveLegacyRelationAsync("orders"));
            var qualified = await session.Objects.ResolveLegacyRelationAsync("sales.eu.orders");
            Assert.Equal("sales.eu", qualified!.Schema);
            Assert.Equal("orders", qualified.Name);
        }
        finally { File.Delete(path); }
    }
}
