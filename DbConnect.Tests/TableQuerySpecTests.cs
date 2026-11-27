using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class TableQuerySpecTests
{
    public static TheoryData<IDatabaseProvider> LocalProviders => new()
    {
        new SqliteProvider(),
        new DuckDbProvider()
    };

    [Theory]
    [MemberData(nameof(LocalProviders))]
    public void BuildTablePageQuery_MultiColumnSort_RendersOrderedKeysWithDirections(IDatabaseProvider provider)
    {
        var spec = new TableQuerySpec(
            [new TableSortKey("Name", Descending: true), new TableSortKey("Id")],
            PageIndex: 2,
            PageSize: 10);

        var sql = provider.BuildTablePageQuery("Customers", spec).Sql;

        Assert.Equal("SELECT * FROM \"Customers\" ORDER BY \"Name\" DESC, \"Id\" ASC LIMIT 11 OFFSET 20;", sql);
    }

    [Theory]
    [MemberData(nameof(LocalProviders))]
    public void BuildTablePageQuery_NoSort_OmitsOrderBy(IDatabaseProvider provider)
    {
        var sql = provider.BuildTablePageQuery("main.Customers", new TableQuerySpec([], PageIndex: 0, PageSize: 5)).Sql;

        Assert.Equal("SELECT * FROM \"main\".\"Customers\" LIMIT 6 OFFSET 0;", sql);
    }

    [Theory]
    [MemberData(nameof(LocalProviders))]
    public void BuildTablePageQuery_SortColumnWithQuote_IsEscaped(IDatabaseProvider provider)
    {
        var sql = provider.BuildTablePageQuery("T", new TableQuerySpec([new TableSortKey("a\"b")], 0, 5)).Sql;

        Assert.Contains("ORDER BY \"a\"\"b\" ASC", sql);
    }

    // ── Filters ───────────────────────────────────────────────────────────────

    public static TheoryData<IDatabaseProvider, string, string> FilterDialects => new()
    {
        { new SqliteProvider(), "@p", "LIKE" },
        { new DuckDbProvider(), "?", "ILIKE" }
    };

    public static TheoryData<IDatabaseProvider, string> PlaceholderDialects => new()
    {
        { new SqliteProvider(), "@p" },
        { new DuckDbProvider(), "?" }
    };

    private static string Ph(string prefix, int i) => prefix == "?" ? "?" : $"{prefix}{i}";

    [Theory]
    [MemberData(nameof(PlaceholderDialects))]
    public void BuildTablePageQuery_Filters_AreAndedParametersInOrder(IDatabaseProvider provider, string prefix)
    {
        var spec = new TableQuerySpec([new TableSortKey("Id")], 0, 10)
        {
            Filters =
            [
                new TableFilter("Status", TableFilterOperator.Equals, "shipped"),
                new TableFilter("Total", TableFilterOperator.GreaterThan, "100")
            ]
        };

        var query = provider.BuildTablePageQuery("Orders", spec);

        Assert.Equal(
            $"SELECT * FROM \"Orders\" WHERE \"Status\" = {Ph(prefix, 0)} AND \"Total\" > {Ph(prefix, 1)} ORDER BY \"Id\" ASC LIMIT 11 OFFSET 0;",
            query.Sql);
        Assert.Equal(["shipped", "100"], query.Parameters);
    }

    [Theory]
    [MemberData(nameof(FilterDialects))]
    public void BuildTablePageQuery_QuotesAndWildcardsInValues_TravelAsParametersNotSql(IDatabaseProvider provider, string prefix, string like)
    {
        var spec = new TableQuerySpec([], 0, 5)
        {
            Filters =
            [
                new TableFilter("Name", TableFilterOperator.Equals, "O'Brien; DROP TABLE x"),
                new TableFilter("Note", TableFilterOperator.Contains, "50%_!\\")
            ]
        };

        var query = provider.BuildTablePageQuery("T", spec);

        Assert.DoesNotContain("Brien", query.Sql);
        Assert.Equal(
            $"SELECT * FROM \"T\" WHERE \"Name\" = {Ph(prefix, 0)} AND CAST(\"Note\" AS TEXT) {like} {Ph(prefix, 1)} ESCAPE '!' LIMIT 6 OFFSET 0;",
            query.Sql);
        Assert.Equal(["O'Brien; DROP TABLE x", "%50!%!_!!\\%"], query.Parameters);
    }

    [Theory]
    [MemberData(nameof(LocalProviders))]
    public void BuildTablePageQuery_StartsWithEndsWith_AnchorThePattern(IDatabaseProvider provider)
    {
        var spec = new TableQuerySpec([], 0, 5)
        {
            Filters =
            [
                new TableFilter("A", TableFilterOperator.StartsWith, "x"),
                new TableFilter("B", TableFilterOperator.EndsWith, "y")
            ]
        };

        Assert.Equal(["x%", "%y"], provider.BuildTablePageQuery("T", spec).Parameters);
    }

    [Theory]
    [MemberData(nameof(PlaceholderDialects))]
    public void BuildTablePageQuery_NullOperators_TakeNoParameter(IDatabaseProvider provider, string prefix)
    {
        var spec = new TableQuerySpec([], 0, 5)
        {
            Filters =
            [
                new TableFilter("A", TableFilterOperator.IsNull),
                new TableFilter("B", TableFilterOperator.IsNotNull),
                new TableFilter("C", TableFilterOperator.NotEquals, "z")
            ]
        };

        var query = provider.BuildTablePageQuery("T", spec);

        Assert.Equal(
            $"SELECT * FROM \"T\" WHERE \"A\" IS NULL AND \"B\" IS NOT NULL AND \"C\" <> {Ph(prefix, 0)} LIMIT 6 OFFSET 0;",
            query.Sql);
        Assert.Equal(["z"], query.Parameters);
    }

    // ── Raw WHERE ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(LocalProviders))]
    public void BuildTablePageQuery_RawWhereAlone_IsEmbeddedVerbatimInParentheses(IDatabaseProvider provider)
    {
        var spec = new TableQuerySpec([], 0, 5) { RawWhere = "total > 100 OR status = 'x'" };

        var query = provider.BuildTablePageQuery("T", spec);

        Assert.Equal("SELECT * FROM \"T\" WHERE (total > 100 OR status = 'x'\n) LIMIT 6 OFFSET 0;", query.Sql);
        Assert.Empty(query.Parameters);
    }

    [Theory]
    [MemberData(nameof(PlaceholderDialects))]
    public void BuildTablePageQuery_RawWhereWithFilters_AndsThemWithRawFirst(IDatabaseProvider provider, string prefix)
    {
        var spec = new TableQuerySpec([], 0, 5)
        {
            RawWhere = "a = 1 OR b = 2",
            Filters = [new TableFilter("C", TableFilterOperator.Equals, "z")]
        };

        var query = provider.BuildTablePageQuery("T", spec);

        Assert.Equal(
            $"SELECT * FROM \"T\" WHERE (a = 1 OR b = 2\n) AND \"C\" = {Ph(prefix, 0)} LIMIT 6 OFFSET 0;",
            query.Sql);
        Assert.Equal(["z"], query.Parameters);
    }

    [Theory]
    [MemberData(nameof(LocalProviders))]
    public void BuildTablePageQuery_BlankRawWhere_IsIgnored(IDatabaseProvider provider)
    {
        var spec = new TableQuerySpec([], 0, 5) { RawWhere = "   " };

        Assert.Equal("SELECT * FROM \"T\" LIMIT 6 OFFSET 0;", provider.BuildTablePageQuery("T", spec).Sql);
    }

    // ── Count (ticket 05) ────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(LocalProviders))]
    public void BuildTableCountQuery_NoFilters_IsPlainCount(IDatabaseProvider provider)
    {
        // Sort and paging never affect a row count.
        var spec = new TableQuerySpec([new TableSortKey("Name", Descending: true)], PageIndex: 3, PageSize: 10);

        var query = provider.BuildTableCountQuery("Customers", spec);

        Assert.Equal("SELECT COUNT(*) FROM \"Customers\";", query.Sql);
        Assert.Empty(query.Parameters);
    }

    [Theory]
    [MemberData(nameof(PlaceholderDialects))]
    public void BuildTableCountQuery_Filters_AreAndedParametersInOrder(IDatabaseProvider provider, string prefix)
    {
        var spec = new TableQuerySpec([], 0, 10)
        {
            Filters =
            [
                new TableFilter("Status", TableFilterOperator.Equals, "shipped"),
                new TableFilter("Total", TableFilterOperator.GreaterThan, "100")
            ]
        };

        var query = provider.BuildTableCountQuery("Orders", spec);

        Assert.Equal(
            $"SELECT COUNT(*) FROM \"Orders\" WHERE \"Status\" = {Ph(prefix, 0)} AND \"Total\" > {Ph(prefix, 1)};",
            query.Sql);
        Assert.Equal(["shipped", "100"], query.Parameters);
    }

    [Theory]
    [MemberData(nameof(LocalProviders))]
    public void BuildTableCountQuery_RawWhereWithFilters_AndsThemWithRawFirst(IDatabaseProvider provider)
    {
        var spec = new TableQuerySpec([], 0, 5)
        {
            RawWhere = "a = 1 OR b = 2",
            Filters = [new TableFilter("C", TableFilterOperator.Equals, "z")]
        };

        var query = provider.BuildTableCountQuery("T", spec);

        Assert.StartsWith("SELECT COUNT(*) FROM \"T\" WHERE (a = 1 OR b = 2\n) AND \"C\" =", query.Sql);
        Assert.Equal(["z"], query.Parameters);
    }
}
