using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services;
using Xunit;

namespace DbConnect.Tests;

/// <summary>RAIN-P2-24 — Provider function catalog and schema-qualified completion.</summary>
public class SqlCompletionEngineTests
{
    [Theory]
    [InlineData("PostgreSQL", "STRING_AGG", "GETDATE")]
    [InlineData("SQL Server", "GETDATE", "LENGTH")]
    [InlineData("MySQL", "GROUP_CONCAT", "STRING_AGG")]
    [InlineData("SQLite", "IFNULL", "NOW")]
    [InlineData("DuckDB", "READ_PARQUET", "GETDATE")]
    public void BuiltInFunctions_AreValidForTheProvider(string providerName, string expected, string foreign)
    {
        var capabilities = new DatabaseService().GetProvider(providerName).Capabilities;

        Assert.Contains(expected, capabilities.BuiltInFunctions);
        Assert.DoesNotContain(foreign, capabilities.BuiltInFunctions);
    }

    [Fact]
    public void BuildIndex_TakesBuiltInFunctionsFromCapabilities()
    {
        var capabilities = new DatabaseService().GetProvider("SQL Server").Capabilities;

        var index = new SchemaIndexer().BuildIndex([], capabilities);

        Assert.Contains("GETDATE", index.Functions);
    }

    [Fact]
    public void BuildIndex_RegistersSchemasWithoutTreatingThemAsTables()
    {
        var index = new SchemaIndexer().BuildIndex([Schema("public"), Schema("sales")]);

        Assert.Empty(index.Tables);
        Assert.True(index.SchemaObjects.ContainsKey("public"));
        Assert.True(index.SchemaObjects.ContainsKey("sales"));
    }

    [Fact]
    public void BuildIndex_SkipsFoldersThatAreNotRelations()
    {
        var root = new List<SchemaNode>
        {
            Table("orders", "id"),
            new() { Name = "Database Info", Type = SchemaNodeType.DatabaseInfoFolder }
        };

        var index = new SchemaIndexer().BuildIndex(root);

        Assert.Equal(["orders"], index.Tables);
    }

    [Fact]
    public void Functions_AreSuggestedInColumnContext()
    {
        var index = Index(functions: ["COUNT", "STRING_AGG"]);

        var candidates = Complete("SELECT S", index);

        Assert.Contains(candidates, c => c is { Text: "STRING_AGG", Kind: CompletionKind.Function });
    }

    [Fact]
    public void Functions_AreNotSuggestedAfterFrom()
    {
        var index = Index(functions: ["COUNT"]);
        index.Tables.Add("orders");

        var candidates = Complete("SELECT * FROM o", index);

        Assert.DoesNotContain(candidates, c => c.Kind == CompletionKind.Function);
        Assert.Contains(candidates, c => c is { Text: "orders", Kind: CompletionKind.Table });
    }

    [Fact]
    public void SchemaDot_ListsOnlyThatSchemasObjects()
    {
        var index = new SchemaIndexer().BuildIndex(
        [
            Schema("public", Folder(SchemaNodeType.TableFolder, Table("orders", "id")),
                             Folder(SchemaNodeType.ViewFolder, View("open_orders"))),
            Schema("sales", Folder(SchemaNodeType.TableFolder, Table("invoices", "id")))
        ]);

        var candidates = Complete("SELECT * FROM public.", index);

        Assert.Equal(
            [("open_orders", CompletionKind.View), ("orders", CompletionKind.Table)],
            candidates.Select(c => (c.Text, c.Kind)).OrderBy(c => c.Text));
    }

    [Fact]
    public void SchemaDot_WithPartialName_StillListsTheSchemasObjects()
    {
        var index = new SchemaIndexer().BuildIndex(
            [Schema("sales", Folder(SchemaNodeType.TableFolder, Table("invoices", "id")))]);

        var candidates = Complete("SELECT * FROM sales.inv", index);

        Assert.Equal(["invoices"], candidates.Select(c => c.Text));
    }

    [Fact]
    public void QuotedSchemaDot_ListsThatSchemasObjects()
    {
        var index = new SchemaIndexer().BuildIndex(
            [Schema("Sales", Folder(SchemaNodeType.TableFolder, Table("invoices", "id")))]);

        var candidates = Complete("SELECT * FROM \"Sales\".", index);

        Assert.Equal(["invoices"], candidates.Select(c => c.Text));
    }

    [Fact]
    public void SchemaDot_IncludesTheSchemasRoutines()
    {
        var index = new SchemaIndexer().BuildIndex(
            [Schema("public", Folder(SchemaNodeType.FunctionFolder, new SchemaNode { Name = "calc_total", Type = SchemaNodeType.Function }))]);

        var candidates = Complete("SELECT public.", index);

        Assert.Contains(candidates, c => c is { Text: "calc_total", Kind: CompletionKind.Function });
    }

    [Fact]
    public void SchemaDot_LeavesOutProcedures()
    {
        var index = new SchemaIndexer().BuildIndex(
            [Schema("dbo", Folder(SchemaNodeType.ProcedureFolder, new SchemaNode { Name = "usp_archive", Type = SchemaNodeType.Procedure }))]);

        Assert.Empty(Complete("SELECT dbo.", index));
        Assert.DoesNotContain("usp_archive", index.Functions);
    }

    [Fact]
    public void SchemaTableDot_ListsThatTablesColumns()
    {
        var index = new SchemaIndexer().BuildIndex(
        [
            Schema("public", Folder(SchemaNodeType.TableFolder, Table("orders", "id", "total"))),
            Schema("sales", Folder(SchemaNodeType.TableFolder, Table("orders", "invoice_no")))
        ]);

        var candidates = Complete("SELECT sales.orders.", index);

        Assert.Equal(["invoice_no"], candidates.Select(c => c.Text));
    }

    [Fact]
    public void AliasDot_ListsTheAliasedTablesColumns()
    {
        var index = new SchemaIndexer().BuildIndex([Table("orders", "id", "total"), Table("customers", "name")]);

        var candidates = Complete("SELECT o. FROM orders o", index, caret: "SELECT o.".Length);

        Assert.Equal(["id", "total"], candidates.Select(c => c.Text).OrderBy(t => t));
        Assert.All(candidates, c => Assert.Equal(CompletionKind.Column, c.Kind));
    }

    [Fact]
    public void UnknownQualifier_SuggestsNothing()
    {
        var index = new SchemaIndexer().BuildIndex([Table("orders", "id")]);

        Assert.Empty(Complete("SELECT nope.", index));
    }

    [Fact]
    public void UnqualifiedTableContext_SuggestsSchemas()
    {
        var index = new SchemaIndexer().BuildIndex([Schema("sales")]);

        var candidates = Complete("SELECT * FROM s", index);

        Assert.Contains(candidates, c => c is { Text: "sales", Kind: CompletionKind.Schema });
    }

    [Fact]
    public void WordStart_IsAfterTheQualifierDot()
    {
        Assert.Equal("SELECT * FROM sales.".Length, SqlCompletionEngine.WordStart("SELECT * FROM sales.inv", "SELECT * FROM sales.inv".Length));
    }

    [Fact]
    public void SchemaToLoad_IsTheQualifierWhenItsObjectsHaveNotBeenFetched()
    {
        var index = new SchemaIndexer().BuildIndex([Schema("sales")]);
        const string text = "SELECT * FROM sales.";

        Assert.Equal("sales", SqlCompletionEngine.SchemaToLoad(text, text.Length, index));

        index.LoadedSchemas.Add("sales");
        Assert.Null(SqlCompletionEngine.SchemaToLoad(text, text.Length, index));
    }

    [Fact]
    public void SchemaToLoad_IsNullForAnAliasOrUnknownQualifier()
    {
        var index = new SchemaIndexer().BuildIndex([Schema("sales")]);

        Assert.Null(SqlCompletionEngine.SchemaToLoad("SELECT o.", "SELECT o.".Length, index));
        Assert.Null(SqlCompletionEngine.SchemaToLoad("SELECT sal", "SELECT sal".Length, index));
    }

    private static IReadOnlyList<SqlCompletionCandidate> Complete(string text, SchemaIndex index, int? caret = null) =>
        SqlCompletionEngine.GetCandidates(text, caret ?? text.Length, index);

    private static SchemaIndex Index(IEnumerable<string> functions)
    {
        var index = new SchemaIndex();
        index.Functions.UnionWith(functions);
        return index;
    }

    private static SchemaNode Schema(string name, params SchemaNode[] children) => WithChildren(
        new SchemaNode { Name = name, RawName = name, Type = SchemaNodeType.Schema, SchemaPath = name }, children);

    private static SchemaNode Folder(SchemaNodeType type, params SchemaNode[] children) => WithChildren(
        new SchemaNode { Name = type.ToString(), Type = type, IsLazyLoadable = true, IsLoaded = true }, children);

    private static SchemaNode Table(string name, params string[] columns) => WithChildren(
        new SchemaNode { Name = name, RawName = name, Type = SchemaNodeType.Table },
        columns.Select(c => new SchemaNode { Name = c, RawName = c, Type = SchemaNodeType.Column }).ToArray());

    private static SchemaNode View(string name) => new() { Name = name, RawName = name, Type = SchemaNodeType.View };

    private static SchemaNode WithChildren(SchemaNode node, SchemaNode[] children)
    {
        foreach (var child in children)
            node.Children.Add(child);
        return node;
    }
}
