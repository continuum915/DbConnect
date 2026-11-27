using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Tests;

public class IndexDefinitionTests
{
    [Fact]
    public void SqliteBuilder_GenerateCreateIndex_WithDirection_EmitsAscDesc()
    {
        var builder = new SqliteDdlBuilder();
        var index = new IndexDefinition
        {
            Name = "idx_test",
            TableName = "Orders",
            Columns = new() { "CreatedAt", "Total" },
            ColumnDirections = new() { IndexColumnDirection.Desc, IndexColumnDirection.Asc }
        };

        var sql = builder.GenerateCreateIndex(index);

        Assert.Contains("\"CreatedAt\" DESC", sql);
        Assert.Contains("\"Total\" ASC", sql);
    }

    [Fact]
    public void SqliteBuilder_GenerateCreateIndex_WithWhere_EmitsPredicateClause()
    {
        var builder = new SqliteDdlBuilder();
        var index = new IndexDefinition
        {
            Name = "idx_active",
            TableName = "Users",
            Columns = new() { "Email" },
            WherePredicate = "IsActive = 1"
        };

        var sql = builder.GenerateCreateIndex(index);

        Assert.Contains("WHERE IsActive = 1", sql);
    }

    [Fact]
    public void SqliteBuilder_GenerateCreateIndex_NoInclude_DoesNotEmitInclude()
    {
        var builder = new SqliteDdlBuilder();
        var index = new IndexDefinition
        {
            Name = "idx_test",
            TableName = "Orders",
            Columns = new() { "Id" },
            IncludedColumns = new() { "SomeCol" }
        };

        var sql = builder.GenerateCreateIndex(index);

        Assert.DoesNotContain("INCLUDE", sql);
    }

    [Fact]
    public void PostgreSqlBuilder_GenerateCreateIndex_WithDirection_EmitsAscDesc()
    {
        var builder = new PostgreSqlDdlBuilder();
        var index = new IndexDefinition
        {
            Name = "idx_pg",
            TableName = "Events",
            Columns = new() { "EventDate", "UserId" },
            ColumnDirections = new() { IndexColumnDirection.Desc, IndexColumnDirection.Asc }
        };

        var sql = builder.GenerateCreateIndex(index);

        Assert.Contains("\"EventDate\" DESC", sql);
        Assert.Contains("\"UserId\" ASC", sql);
    }

    [Fact]
    public void PostgreSqlBuilder_GenerateCreateIndex_WithWhere_EmitsPredicateClause()
    {
        var builder = new PostgreSqlDdlBuilder();
        var index = new IndexDefinition
        {
            Name = "idx_partial",
            TableName = "Products",
            Columns = new() { "Sku" },
            WherePredicate = "deleted_at IS NULL"
        };

        var sql = builder.GenerateCreateIndex(index);

        Assert.Contains("WHERE deleted_at IS NULL", sql);
    }

    [Fact]
    public void SqlServerBuilder_GenerateCreateIndex_WithIncludedColumns_EmitsIncludeClause()
    {
        var builder = new SqlServerDdlBuilder();
        var index = new IndexDefinition
        {
            Name = "idx_ss",
            TableName = "Sales",
            Columns = new() { "CustomerId" },
            IncludedColumns = new() { "OrderDate", "Amount" }
        };

        var sql = builder.GenerateCreateIndex(index);

        Assert.Contains("INCLUDE ([OrderDate], [Amount])", sql);
    }

    [Fact]
    public void SqlServerBuilder_GenerateCreateIndex_WithDirection_EmitsAscDesc()
    {
        var builder = new SqlServerDdlBuilder();
        var index = new IndexDefinition
        {
            Name = "idx_ss_dir",
            TableName = "Sales",
            Columns = new() { "SaleDate", "Region" },
            ColumnDirections = new() { IndexColumnDirection.Desc, IndexColumnDirection.Asc }
        };

        var sql = builder.GenerateCreateIndex(index);

        Assert.Contains("[SaleDate] DESC", sql);
        Assert.Contains("[Region] ASC", sql);
    }

    [Fact]
    public void SqlServerBuilder_GenerateCreateIndex_NoWhere_DoesNotEmitWhere()
    {
        var builder = new SqlServerDdlBuilder();
        var index = new IndexDefinition
        {
            Name = "idx_ss_nowhere",
            TableName = "Sales",
            Columns = new() { "Id" },
            WherePredicate = "Status = 1"
        };

        var sql = builder.GenerateCreateIndex(index);

        Assert.DoesNotContain("WHERE", sql);
    }

    [Fact]
    public void BuilderColumnsDisplay_DefaultsToAsc_WhenDirectionsEmpty()
    {
        var builder = new SqliteDdlBuilder();
        var index = new IndexDefinition
        {
            Name = "idx_nodir",
            TableName = "Foo",
            Columns = new() { "A", "B" },
            // ColumnDirections is empty — should default to ASC
        };

        var sql = builder.GenerateCreateIndex(index);

        Assert.Contains("\"A\" ASC", sql);
        Assert.Contains("\"B\" ASC", sql);
    }

    [Fact]
    public void GenericBuilder_GenerateCreateIndex_WithDirection_EmitsAscDesc()
    {
        var builder = new GenericDdlBuilder("Generic");
        var index = new IndexDefinition
        {
            Name = "idx_gen",
            TableName = "Items",
            Columns = new() { "Name", "Price" },
            ColumnDirections = new() { IndexColumnDirection.Asc, IndexColumnDirection.Desc }
        };

        var sql = builder.GenerateCreateIndex(index);

        Assert.Contains("\"Name\" ASC", sql);
        Assert.Contains("\"Price\" DESC", sql);
    }
}
