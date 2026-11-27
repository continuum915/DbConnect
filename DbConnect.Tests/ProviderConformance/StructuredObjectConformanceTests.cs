using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests.ProviderConformance;

public sealed class StructuredObjectConformanceTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostgreSqlDiscoveryPreservesOverloadedRoutineSignatures()
    {
        var connectionString = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var provider = new PostgreSqlProvider();
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = connectionString }, provider);
        var schema = "rain_routines_" + Guid.NewGuid().ToString("N")[..12];
        var quoted = provider.Capabilities.QuoteIdentifier(schema);
        try
        {
            await provider.ExecuteNonQueryAsync(connectionString, $"CREATE SCHEMA {quoted};");
            await provider.ExecuteNonQueryAsync(connectionString,
                $"CREATE FUNCTION {quoted}.overloaded(integer) RETURNS integer LANGUAGE SQL AS 'SELECT 1'; CREATE FUNCTION {quoted}.overloaded(text) RETURNS integer LANGUAGE SQL AS 'SELECT 2';");
            var root = Assert.Single(await session.GetSchemaAsync());
            var owner = Assert.Single(root.Children, node => node.RawName == schema);
            var routines = await session.Objects.GetChildrenAsync(owner.Identity!, SchemaNodeType.Function);
            Assert.Equal(["integer", "text"], routines.Select(node => node.Identity!.RoutineSignature).Order());
            Assert.False(session.Objects.Same(routines[0].Identity, routines[1].Identity));
            Assert.All(routines, routine => Assert.Equal(owner.Identity, routine.Identity!.Parent));
        }
        finally { await provider.ExecuteNonQueryAsync(connectionString, $"DROP SCHEMA IF EXISTS {quoted} CASCADE;"); }
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    [InlineData("MySQL")]
    [Trait("Category", "Integration")]
    public async Task DiscoveredPunctuatedObjectRetainsItsAddressThroughMetadataAndTabQueries(string providerName)
    {
        var connectionString = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        IDatabaseProvider provider = new DatabaseService().GetProvider(providerName);
        var session = new DatabaseSession(new ConnectionInfo { ConnectionString = connectionString }, provider);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schemas = providerName == "MySQL" ? new string?[] { null } : [$"rain.{suffix}", $"rain_{suffix}"];
        var name = providerName == "MySQL" ? $"rain {suffix}'\"`]" : $"rain.{suffix}'\" ]";
        var created = new List<DatabaseObjectIdentity>();
        try
        {
            for (var i = 0; i < schemas.Length; i++)
            {
                var schema = schemas[i];
                if (schema != null)
                    await provider.ExecuteNonQueryAsync(connectionString, $"CREATE SCHEMA {provider.Capabilities.QuoteIdentifier(schema)};");
                var address = new DatabaseObjectIdentity(null, schema, name, SchemaNodeType.Table);
                var quoted = ObjectNames.Quote(provider.Capabilities, address);
                await provider.ExecuteNonQueryAsync(connectionString, $"CREATE TABLE {quoted} (id INTEGER PRIMARY KEY);");
                created.Add(address);
                await provider.ExecuteNonQueryAsync(connectionString, $"INSERT INTO {quoted} VALUES ({81 + i});");
            }

            var roots = await session.GetSchemaAsync();
            for (var i = 0; i < schemas.Length; i++)
            {
                SchemaNode table;
                if (schemas[i] is { } schema)
                {
                    var owner = Assert.Single(roots[0].Children, node => node.Type == SchemaNodeType.Schema && node.RawName == schema);
                    var tables = await session.Objects.GetChildrenAsync(owner.Identity!, SchemaNodeType.Table);
                    table = Assert.Single(tables, node => node.RawName == name);
                }
                else table = Assert.Single(roots[0].Children, node => node.RawName == name);
                Assert.NotNull(table.Identity!.Catalog);
                var columnSnapshot = await session.Catalog.LoadAsync(new(table.Identity, SchemaNodeType.Column));
                Assert.Equal("id", Assert.Single(columnSnapshot.Records).Identity.Name);
                var qualifier = (schemas[i] is { } ownerSchema ? provider.Capabilities.QuoteIdentifier(ownerSchema) + "." : "") +
                    provider.Capabilities.QuoteIdentifier(name);
                var completionSql = "SELECT " + qualifier + ".";
                var completion = await CatalogSqlCompletion.GetCandidatesAsync(completionSql, completionSql.Length, session);
                Assert.Equal("id", Assert.Single(completion).Text);
                if (providerName == "SQL Server")
                {
                    var comparison = await provider.ExecuteQueryAsync(connectionString,
                        "SELECT CASE WHEN N'RAIN' COLLATE DATABASE_DEFAULT = N'rain' THEN 1 ELSE 0 END;");
                    Assert.Equal(Convert.ToInt32(comparison.ObjectRows[0][0]) == 1,
                        session.Objects.Same(table.Identity, table.Identity with { Name = name.ToUpperInvariant() }));
                }
                var columns = await session.Objects.GetColumnsAsync(table.Identity);
                Assert.Equal("id", Assert.Single(columns).Name);
                var tab = new TableDataTabViewModel(name, session, table);
                try
                {
                    await tab.RefreshInternalAsync(true);
                    Assert.Equal(81L + i, Convert.ToInt64(Assert.Single(tab.Rows).Values["id"]));
                    Assert.Equal(1L, tab.TotalRowCount);
                }
                finally { await tab.ReleaseDbSessionAsync(); }
            }
            if (schemas.Length > 1) Assert.Null(await session.Objects.ResolveLegacyRelationAsync(name));
        }
        finally
        {
            foreach (var address in created)
                await provider.ExecuteNonQueryAsync(connectionString, $"DROP TABLE {ObjectNames.Quote(provider.Capabilities, address)};");
            foreach (var schema in schemas.Where(value => value != null))
                await provider.ExecuteNonQueryAsync(connectionString, $"DROP SCHEMA {provider.Capabilities.QuoteIdentifier(schema!)};");
        }
    }
}
