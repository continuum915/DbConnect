using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;
using Xunit;

namespace DbConnect.Tests;

/// <summary>
/// RAIN-P2-16 — When a query result may be edited: every column maps to one source table and the
/// result carries all of that table's key columns. Otherwise it is read-only, with a reason.
/// </summary>
public class QueryResultEditabilityTests
{
    private static readonly DatabaseProviderCapabilities Reporting =
        new DatabaseService().GetProvider("SQLite").Capabilities;

    private static readonly DatabaseProviderCapabilities SchemaReporting =
        new DatabaseService().GetProvider("PostgreSQL").Capabilities;

    private static ColumnInfo Column(string name, string? table, string? source = null, string? schema = null) => new()
    {
        Name = name,
        SourceSchema = table is null ? null : schema,
        SourceTable = table,
        SourceColumn = table is null ? null : source ?? name
    };

    private static QueryResult Result(params ColumnInfo[] columns)
    {
        for (var i = 0; i < columns.Length; i++)
            columns[i].Ordinal = i;
        return new QueryResult { ColumnInfos = columns.ToList() };
    }

    private static Task<IReadOnlyList<string>> Keys(params string[] keys) => Task.FromResult<IReadOnlyList<string>>(keys);

    [Fact]
    public async Task SingleTable_WithItsKey_IsEditable_AndNamesEachColumnsSource()
    {
        var result = Result(Column("id", "orders"), Column("label", "orders", "name"));

        var editability = await QueryResultEditability.EvaluateAsync(result, Reporting, (_, _) => Keys("id"));

        Assert.True(editability.IsEditable);
        Assert.Null(editability.ReadOnlyReason);
        var target = editability.Target!;
        Assert.Equal("orders", target.Table);
        Assert.Equal(["id", "name"], target.SourceColumns);
        Assert.Equal(["id"], target.KeyColumns);
    }

    [Fact]
    public async Task ColumnsFromTwoTables_AreReadOnly_NamingBoth()
    {
        var result = Result(Column("id", "orders"), Column("email", "customers"));

        var editability = await QueryResultEditability.EvaluateAsync(result, Reporting, (_, _) => Keys("id"));

        Assert.False(editability.IsEditable);
        Assert.Contains("orders", editability.ReadOnlyReason);
        Assert.Contains("customers", editability.ReadOnlyReason);
    }

    [Fact]
    public async Task ExpressionColumn_IsReadOnly_NamingTheColumn()
    {
        var result = Result(Column("id", "orders"), Column("total_with_tax", null));

        var editability = await QueryResultEditability.EvaluateAsync(result, Reporting, (_, _) => Keys("id"));

        Assert.False(editability.IsEditable);
        Assert.Contains("total_with_tax", editability.ReadOnlyReason);
    }

    [Fact]
    public async Task MissingKeyColumn_IsReadOnly_NamingTheMissingKey()
    {
        var result = Result(Column("name", "orders"));

        var editability = await QueryResultEditability.EvaluateAsync(result, Reporting, (_, _) => Keys("id"));

        Assert.False(editability.IsEditable);
        Assert.Contains("id", editability.ReadOnlyReason);
    }

    [Fact]
    public async Task PartOfACompositeKey_IsReadOnly()
    {
        var result = Result(Column("order_id", "order_lines"), Column("qty", "order_lines"));

        var editability = await QueryResultEditability.EvaluateAsync(result, Reporting, (_, _) => Keys("order_id", "line_no"));

        Assert.False(editability.IsEditable);
        Assert.Contains("line_no", editability.ReadOnlyReason);
    }

    [Fact]
    public async Task TableWithoutAPrimaryKey_IsReadOnly()
    {
        var result = Result(Column("id", "log"), Column("message", "log"));

        var editability = await QueryResultEditability.EvaluateAsync(result, Reporting, (_, _) => Keys());

        Assert.False(editability.IsEditable);
        Assert.Contains("primary key", editability.ReadOnlyReason);
    }

    [Fact]
    public async Task SameSourceColumnTwice_IsReadOnly()
    {
        // Two cells for one column couldn't both be written back.
        var result = Result(Column("id", "orders"), Column("again", "orders", "id"));

        var editability = await QueryResultEditability.EvaluateAsync(result, Reporting, (_, _) => Keys("id"));

        Assert.False(editability.IsEditable);
        Assert.Contains("id", editability.ReadOnlyReason);
    }

    [Fact]
    public async Task ProviderThatDoesNotReportSources_IsReadOnly()
    {
        var capabilities = new DatabaseService().GetProvider("MySQL").Capabilities;
        var result = Result(Column("id", "orders"));

        var editability = await QueryResultEditability.EvaluateAsync(result, capabilities, (_, _) => Keys("id"));

        Assert.False(editability.IsEditable);
        Assert.NotNull(editability.ReadOnlyReason);
    }

    [Fact]
    public async Task SameTableNameInTwoSchemas_IsReadOnly()
    {
        var result = Result(Column("id", "items", schema: "sales"), Column("name", "items", schema: "archive"));

        var editability = await QueryResultEditability.EvaluateAsync(result, SchemaReporting, (_, _) => Keys("id"));

        Assert.False(editability.IsEditable);
    }

    [Fact]
    public async Task SchemaQualifiedSource_LooksUpKeysAndTargetsThatSchema()
    {
        var result = Result(Column("id", "items", schema: "sales"));
        string? askedSchema = null;

        var editability = await QueryResultEditability.EvaluateAsync(result, SchemaReporting, (schema, _) =>
        {
            askedSchema = schema;
            return Keys("id");
        });

        Assert.Equal("sales", askedSchema);
        Assert.Equal("sales", editability.Target!.Schema);
    }

    [Fact]
    public async Task ProviderWithoutSchemas_IgnoresTheReportedSchema()
    {
        // SQLite reports its "main" database as the schema; its catalog and SQL take the bare table name.
        var result = Result(Column("id", "items", schema: "main"));
        string? askedSchema = "unset";

        var editability = await QueryResultEditability.EvaluateAsync(result, Reporting, (schema, _) =>
        {
            askedSchema = schema;
            return Keys("id");
        });

        Assert.Null(askedSchema);
        Assert.Null(editability.Target!.Schema);
    }

    [Fact]
    public async Task KeyNames_MatchCaseInsensitively_UsingTheResultsSpelling()
    {
        var result = Result(Column("ID", "orders"));

        var editability = await QueryResultEditability.EvaluateAsync(result, Reporting, (_, _) => Keys("id"));

        Assert.True(editability.IsEditable);
        Assert.Equal(["ID"], editability.Target!.KeyColumns);
    }

    [Fact]
    public async Task FailedOrEmptyResult_IsNotEditable()
    {
        var failed = new QueryResult { Error = QueryErrorInfo.Failure("SQLite", "boom", "boom") };

        Assert.False((await QueryResultEditability.EvaluateAsync(failed, Reporting, (_, _) => Keys("id"))).IsEditable);
        Assert.False((await QueryResultEditability.EvaluateAsync(new QueryResult(), Reporting, (_, _) => Keys("id"))).IsEditable);
    }
}
