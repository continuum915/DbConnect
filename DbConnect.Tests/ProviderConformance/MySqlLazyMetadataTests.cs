using System.Collections.Concurrent;
using System.Diagnostics;
using DbConnect.Models;
using DbConnect.Services;
using Xunit.Abstractions;

namespace DbConnect.Tests.ProviderConformance;

[Trait("Category", "Integration")]
public sealed class MySqlLazyMetadataTests(ITestOutputHelper output)
{
    [MySqlExportFact]
    public async Task InitialQueryCountStaysConstantAsTheSchemaGrows()
    {
        var provider = new MySqlProvider();
        var connectionString = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL")!;
        var prefix = $"rain_scale_{Guid.NewGuid():N}";
        var tables = new List<string>();
        var views = new List<string>();
        int? baselineQueryCount = null;
        try
        {
            foreach (var size in new[] { 2, 12, 500 })
            {
                while (tables.Count < size)
                {
                    var table = $"{prefix}_t{tables.Count}";
                    await provider.ExecuteNonQueryAsync(connectionString, $"CREATE TABLE `{table}` (id INT PRIMARY KEY, label VARCHAR(40));");
                    tables.Add(table);
                }
                while (views.Count < Math.Max(1, size / 10))
                {
                    var view = $"{prefix}_v{views.Count}";
                    await provider.ExecuteNonQueryAsync(connectionString, $"CREATE VIEW `{view}` AS SELECT id FROM `{tables[0]}`;");
                    views.Add(view);
                }

                var session = new DatabaseSession(new ConnectionInfo { ConnectionString = connectionString }, provider);
                using var catalog = session.Catalog;
                using var trace = new MetadataTrace();
                var timer = Stopwatch.StartNew();
                var root = Assert.Single(await session.GetSchemaAsync());
                var browseTime = timer.Elapsed;
                var commands = trace.Take();
                var relations = root.Children.Where(node => node.Type is SchemaNodeType.Table or SchemaNodeType.View).ToArray();
                baselineQueryCount ??= commands.Length;
                output.WriteLine($"Fixture: {tables.Count} tables, {views.Count} views; total relations: {relations.Length}; initial browse: {commands.Length} queries, {browseTime.TotalMilliseconds:F2} ms.");
                Assert.Equal(tables.Count + views.Count, relations.Count(node => node.RawName!.StartsWith(prefix, StringComparison.Ordinal)));
                Assert.Equal(2, commands.Length);
                Assert.Equal(baselineQueryCount.Value, commands.Length);
                Assert.Single(commands, sql => sql.Contains("INFORMATION_SCHEMA.TABLES", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(commands, sql => sql.Contains("INFORMATION_SCHEMA.COLUMNS", StringComparison.OrdinalIgnoreCase));
                Assert.All(relations, node => Assert.False(node.IsLoaded));

                await session.GetSchemaAsync();
                Assert.Empty(trace.Take());
                timer.Restart();
                var selected = Assert.Single(relations, node => node.RawName == tables[0]);
                var columns = await catalog.LoadAsync(new(selected.Identity!, SchemaNodeType.Column));
                var columnTime = timer.Elapsed;
                var columnCommands = trace.Take();
                output.WriteLine($"Selected table columns: {columnCommands.Length} queries, {columnTime.TotalMilliseconds:F2} ms; cached browse: 0 queries.");
                Assert.Equal(["id", "label"], columns.Records.Select(column => column.Identity.Name));
                Assert.Contains("INFORMATION_SCHEMA.COLUMNS", Assert.Single(columnCommands), StringComparison.OrdinalIgnoreCase);
                Assert.All(relations.Where(node => node != selected), node =>
                    Assert.Equal(MetadataLoadState.NotLoaded, catalog.GetSnapshot(new(node.Identity!, SchemaNodeType.Column)).State));
            }
        }
        finally
        {
            foreach (var view in views)
                await provider.ExecuteNonQueryAsync(connectionString, $"DROP VIEW IF EXISTS `{view}`;");
            foreach (var table in tables)
                await provider.ExecuteNonQueryAsync(connectionString, $"DROP TABLE IF EXISTS `{table}`;");
        }
    }

    [MySqlExportFact]
    public async Task BrowsingListsRelationsWithoutColumnsAndLoadsOnlyTheSelectedRelation()
    {
        var provider = new MySqlProvider();
        var connectionString = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL")!;
        var prefix = $"rain_lazy_{Guid.NewGuid():N}";
        var table = prefix + "_table";
        var other = prefix + "_other";
        var view = prefix + "_view";
        try
        {
            await provider.ExecuteNonQueryAsync(connectionString,
                $"CREATE TABLE `{table}` (id INT PRIMARY KEY, label VARCHAR(40)); " +
                $"CREATE TABLE `{other}` (unrelated INT); " +
                $"CREATE VIEW `{view}` AS SELECT id FROM `{table}`;");
            var session = new DatabaseSession(new ConnectionInfo { ConnectionString = connectionString }, provider);
            using var catalog = session.Catalog;
            using var trace = new MetadataTrace();
            var root = Assert.Single(await session.GetSchemaAsync());
            var browse = trace.Take();
            Assert.Equal(2, browse.Length); // One relation set plus identifier-comparison settings.
            Assert.DoesNotContain(browse, sql => sql.Contains("INFORMATION_SCHEMA.COLUMNS", StringComparison.OrdinalIgnoreCase));
            var selected = Assert.Single(root.Children, node => node.RawName == table);
            var unrelated = Assert.Single(root.Children, node => node.RawName == other);
            var selectedView = Assert.Single(root.Children, node => node.RawName == view);
            Assert.Equal(SchemaNodeType.View, selectedView.Type);
            foreach (var relation in new[] { selected, unrelated, selectedView })
            {
                Assert.True(relation.IsLazyLoadable);
                Assert.False(relation.IsLoaded);
                Assert.Equal(MetadataLoadState.NotLoaded, relation.MetadataState);
                Assert.DoesNotContain(relation.Children, child => child.DataType != null);
            }
            Assert.Equal(provider.Capabilities.SupportsIndexes, selected.Children.Any(node => node.Type == SchemaNodeType.IndexFolder));
            Assert.Equal(provider.Capabilities.SupportsTriggers, selected.Children.Any(node => node.Type == SchemaNodeType.TriggerFolder));
            Assert.Equal(provider.Capabilities.SupportsFunctions, root.Children.Any(node => node.Type == SchemaNodeType.FunctionFolder));
            Assert.Equal(provider.Capabilities.SupportsProcedures, root.Children.Any(node => node.Type == SchemaNodeType.ProcedureFolder));

            var columns = await catalog.LoadAsync(new(selected.Identity!, SchemaNodeType.Column));
            Assert.Equal(MetadataLoadState.Loaded, columns.State);
            Assert.Equal(["id", "label"], columns.Records.Select(column => column.Identity.Name));
            Assert.True(columns.Records[0].IsPrimaryKey);
            Assert.Equal("int", columns.Records[0].DataType);
            Assert.All(columns.Records, column => Assert.Equal(selected.Identity, column.Identity.Parent));
            Assert.Single(trace.Take());
            Assert.Equal(MetadataLoadState.NotLoaded, catalog.GetSnapshot(new(unrelated.Identity!, SchemaNodeType.Column)).State);
            Assert.Equal(MetadataLoadState.NotLoaded, catalog.GetSnapshot(new(selectedView.Identity!, SchemaNodeType.Column)).State);
            await catalog.LoadAsync(new(selected.Identity!, SchemaNodeType.Column));
            Assert.Empty(trace.Take());
            var viewColumns = await catalog.LoadAsync(new(selectedView.Identity!, SchemaNodeType.Column));
            Assert.Equal("id", Assert.Single(viewColumns.Records).Identity.Name);
            Assert.Single(trace.Take());
        }
        finally
        {
            await provider.ExecuteNonQueryAsync(connectionString,
                $"DROP VIEW IF EXISTS `{view}`; DROP TABLE IF EXISTS `{other}`, `{table}`;");
        }
    }

    private sealed class MetadataTrace : IDisposable
    {
        private readonly Activity _operation = new Activity("metadata-test").Start();
        private readonly ConcurrentQueue<string> _commands = new();
        private readonly ActivityListener _listener;

        public MetadataTrace()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "MySqlConnector",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.TraceId != _operation.TraceId) return;
                    var sql = activity.GetTagItem("db.query.text") ?? activity.GetTagItem("db.statement");
                    if (sql is string text) _commands.Enqueue(text);
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public string[] Take()
        {
            var commands = new List<string>();
            while (_commands.TryDequeue(out var command)) commands.Add(command);
            return commands.ToArray();
        }

        public void Dispose()
        {
            _listener.Dispose();
            _operation.Dispose();
        }
    }
}
