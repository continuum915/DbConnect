using Moq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class CatalogCompletionTests
{
    [Theory]
    [InlineData("SELECT sales.orders.", "sales_id")]
    [InlineData("SELECT other.orders.", "other_id")]
    [InlineData("SELECT orders.", null)]
    [InlineData("SELECT o.| FROM sales.orders o", "sales_id")]
    [InlineData("SELECT \"sales\".\"a.b\".", "dot_id")]
    [InlineData("SELECT \"sales\".\"Orders\".", "upper_id")]
    [InlineData("SELECT SALES.ORDERS.", "sales_id")]
    [InlineData("SELECT o.| FROM \"sales\".\"a.b\" o", "dot_id")]
    [InlineData("SELECT \"sales\".\"a]]b\".", "sales_id")]
    [InlineData("SELECT \"sales\".\"a``b\".", "sales_id")]
    [InlineData("SELECT \"O\".| FROM sales.orders \"O\" JOIN other.orders \"o\" ON true", "sales_id")]
    [InlineData("SELECT \"o\".| FROM sales.orders \"O\" JOIN other.orders \"o\" ON true", "other_id")]
    [InlineData("SELECT O.| FROM sales.orders \"O\" JOIN other.orders o ON true", "other_id")]
    [InlineData("SELECT O.| FROM sales.orders O", "sales_id")]
    public async Task QualifiedRelationsKeepTheirOwnColumns(string marked, string? expected)
    {
        var provider = Provider();
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([new SchemaNode
        {
            Name = "db", Type = SchemaNodeType.Database,
            Children = new(new[] { "sales", "other" }.Select(name => new SchemaNode
            {
                Name = name, Type = SchemaNodeType.Schema,
                Children = [new SchemaNode { Name = "Tables", Type = SchemaNodeType.TableFolder, IsLazyLoadable = true }]
            }))
        }]);
        var role = provider.As<IStructuredSchemaProvider>();
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "orders", Type = SchemaNodeType.Table },
                new SchemaNode { Name = "a.b", Type = SchemaNodeType.Table },
                new SchemaNode { Name = "Orders", Type = SchemaNodeType.Table },
                new SchemaNode { Name = "a]]b", Type = SchemaNodeType.Table },
                new SchemaNode { Name = "a``b", Type = SchemaNodeType.Table }]);
        role.Setup(p => p.GetColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()))
            .ReturnsAsync((string _, DatabaseObjectIdentity id) => [new ColumnDefinition
            { Name = id.Name == "a.b" ? "dot_id" : id.Name == "Orders" ? "upper_id" : id.Schema + "_id", DataType = "integer" }]);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        var caret = marked.Contains('|') ? marked.IndexOf('|') : marked.Length;
        var candidates = await CatalogSqlCompletion.GetCandidatesAsync(marked.Replace("|", ""), caret, session);
        Assert.Equal(expected == null ? [] : new[] { expected }, candidates.Select(c => c.Text));
    }

    [Fact]
    public async Task AmbiguousBareRelationsRemainSchemaQualifiedCandidates()
    {
        var provider = Provider();
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([new SchemaNode
        {
            Name = "db", Type = SchemaNodeType.Database,
            Children = new(new[] { "sales", "other" }.Select(name => new SchemaNode
            {
                Name = name, Type = SchemaNodeType.Schema,
                Children = [new SchemaNode { Name = "Tables", Type = SchemaNodeType.TableFolder }]
            }))
        }]);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        var result = await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session);
        Assert.Equal(new[] { "other.orders", "sales.orders" }, result.Where(c => c.Kind == CompletionKind.Table).Select(c => c.Text).Order());
    }
    [Fact]
    public async Task CompletionDiscoversUnexpandedSchemasUsingTheExplorerCatalog()
    {
        var provider = Provider();
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        var roots = await session.GetSchemaAsync();
        var schema = roots[0].Children[0].Identity!;

        var candidates = await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session);

        Assert.Contains(candidates, c => c.Text == "orders" && c.Kind == CompletionKind.Table);
        Assert.Equal("orders", Assert.Single(session.Catalog.GetSnapshot(new(schema, SchemaNodeType.Table)).Records).Identity.Name);
        Assert.False(roots[0].Children[0].Children[0].IsLoaded);
    }

    [Fact]
    public async Task ExpressionContextPreservesCapabilityFunctionsAndCatalogRoutines()
    {
        var provider = Provider();
        provider.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
        { BatchDialect = SqlBatchDialect.PostgreSql, BuiltInFunctions = ["SPECIAL_BUILTIN"] });
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([new SchemaNode
        {
            Name = "db", Type = SchemaNodeType.Database,
            Children = [new SchemaNode { Name = "sales", Type = SchemaNodeType.Schema,
                Children = [new SchemaNode { Name = "Functions", Type = SchemaNodeType.FunctionFolder },
                    new SchemaNode { Name = "Procedures", Type = SchemaNodeType.ProcedureFolder }] }]
        }]);
        provider.As<IStructuredSchemaProvider>()
            .Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Function))
            .ReturnsAsync([new SchemaNode { Name = "calculate", Type = SchemaNodeType.Function, RoutineSignature = "integer" },
                new SchemaNode { Name = "calculate", Type = SchemaNodeType.Function, RoutineSignature = "text" }]);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        var expression = await CatalogSqlCompletion.GetCandidatesAsync("SELECT ", 7, session);
        Assert.Contains(expression, c => c.Text == "SPECIAL_BUILTIN" && c.Kind == CompletionKind.Function);
        Assert.Contains(expression, c => c.Text == "calculate" && c.Kind == CompletionKind.Function);
        Assert.DoesNotContain(await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session),
            c => c.Kind == CompletionKind.Function);
        Assert.Equal("calculate", Assert.Single(await CatalogSqlCompletion.GetCandidatesAsync("SELECT sales.", 13, session)).Text);
    }
    [Theory]
    [InlineData("123")]
    [InlineData("select")]
    public async Task CatalogCandidatesCarryQuotedInsertionText(string name)
    {
        var provider = Provider();
        var role = provider.As<IStructuredSchemaProvider>();
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = name, Type = SchemaNodeType.Table }]);
        role.Setup(p => p.GetColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()))
            .ReturnsAsync([new ColumnDefinition { Name = name, DataType = "int" }]);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        var tables = await CatalogSqlCompletion.GetCandidatesAsync("SELECT * FROM ", 14, session);
        Assert.Equal("\"" + name + "\"", Assert.Single(tables, c => c.Kind == CompletionKind.Table).InsertionText);
        var sql = "SELECT sales.\"" + name + "\".";
        Assert.Equal("\"" + name + "\"", Assert.Single(await CatalogSqlCompletion.GetCandidatesAsync(sql, sql.Length, session)).InsertionText);
    }
    internal static Mock<IDatabaseProvider> Provider()
    {
        var provider = new Mock<IDatabaseProvider>();
        provider.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
        { BatchDialect = SqlBatchDialect.PostgreSql });
        provider.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([new SchemaNode
        {
            Name = "database", Type = SchemaNodeType.Database,
            Children = [new SchemaNode { Name = "sales", Type = SchemaNodeType.Schema,
                Children = [new SchemaNode { Name = "Tables", Type = SchemaNodeType.TableFolder, IsLazyLoadable = true }] }]
        }]);
        var role = provider.As<IStructuredSchemaProvider>();
        role.Setup(p => p.GetChildrenAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "orders", Type = SchemaNodeType.Table }]);
        role.Setup(p => p.GetColumnsAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>()))
            .ReturnsAsync([new ColumnDefinition { Name = "id", DataType = "integer" }]);
        return provider;
    }
}
