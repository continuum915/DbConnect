using Moq;
using System.Linq;
using DbConnect.Services;
using DbConnect.Models;
using Xunit;

namespace DbConnect.Tests;

public class SqlCompletionScopeTests
{
    [Theory]
    [InlineData("SELECT o.| FROM orders o WHERE EXISTS (SELECT 1 FROM customers o)", "id")]
    [InlineData("SELECT 1 FROM orders o WHERE EXISTS (SELECT o.| FROM customers o)", "name")]
    [InlineData("WITH recent AS (SELECT o.| FROM orders o) SELECT * FROM customers o", "id")]
    [InlineData("SELECT 1 FROM orders o WHERE EXISTS (SELECT c.| FROM customers c)", "name")]
    [InlineData("SELECT c.| FROM orders o WHERE EXISTS (SELECT 1 FROM customers c)", null)]
    [InlineData("SELECT 1 FROM orders o WHERE EXISTS (SELECT o.| FROM customers c)", null)]
    [InlineData("WITH a AS (SELECT * FROM orders o), b AS (SELECT o.| FROM customers o) SELECT 1", "name")]
    [InlineData("SELECT o.| FROM orders o WHERE id IN (1, 2)", "id")]
    [InlineData("SELECT * FROM customers o; SELECT o.| FROM orders o", "id")]
    [InlineData("SELECT o.| FROM orders AS o -- JOIN customers o", "id")]
    [InlineData("SELECT o.| FROM \"orders\" AS \"o\"", "id")]
    [InlineData("SELECT 1 FROM orders o WHERE EXISTS (SELECT c.| FROM customers c", "name")]
    public async System.Threading.Tasks.Task AliasDot_UsesOnlyTheCurrentQueryScope(string markedSql, string? expected)
    {
        var caret = markedSql.IndexOf('|');
        var index = new SchemaIndex();
        index.TableColumns["orders"] = ["id"];
        index.TableColumns["customers"] = ["name"];

        var candidates = SqlCompletionEngine.GetCandidates(markedSql.Replace("|", ""), caret, index);

        Assert.Equal(expected == null ? [] : new[] { expected }, candidates.Select(c => c.Text));
        var provider = CatalogCompletionTests.Provider();
        provider.As<IStructuredSchemaProvider>()
            .Setup(p => p.GetChildrenAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<DatabaseObjectIdentity>(), SchemaNodeType.Table))
            .ReturnsAsync([new SchemaNode { Name = "orders", Type = SchemaNodeType.Table },
                new SchemaNode { Name = "customers", Type = SchemaNodeType.Table }]);
        provider.As<IStructuredSchemaProvider>()
            .Setup(p => p.GetColumnsAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<DatabaseObjectIdentity>()))
            .ReturnsAsync((string _, DatabaseObjectIdentity id) => [new ColumnDefinition
            { Name = id.Name == "orders" ? "id" : "name", DataType = "text" }]);
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        var catalogCandidates = await CatalogSqlCompletion.GetCandidatesAsync(markedSql.Replace("|", ""), caret, session);
        Assert.Equal(expected == null ? [] : new[] { expected }, catalogCandidates.Select(c => c.Text));
    }
}
