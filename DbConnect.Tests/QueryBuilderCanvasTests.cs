using System;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class QueryBuilderCanvasTests
{
    [Fact]
    public void AddTableCard_AddsToCollection()
    {
        var vm = new QueryBuilderViewModel();

        vm.AddTableToCanvas("orders");

        Assert.Single(vm.TableCards);
        Assert.Equal("orders", vm.TableCards[0].TableName);
    }

    [Fact]
    public void AddTableCard_MultipleCards_Staggered()
    {
        var vm = new QueryBuilderViewModel();

        vm.AddTableToCanvas("orders");
        vm.AddTableToCanvas("customers");

        Assert.Equal(2, vm.TableCards.Count);
        Assert.NotEqual(vm.TableCards[0].X, vm.TableCards[1].X);
    }

    [Fact]
    public void GeneratedSql_NoTables_IsEmpty()
    {
        var vm = new QueryBuilderViewModel();

        Assert.True(string.IsNullOrEmpty(vm.GeneratedSql));
    }

    [Fact]
    public void GeneratedSql_OneTable_HasSelectFrom()
    {
        var vm = new QueryBuilderViewModel();

        vm.AddTableToCanvas("orders");

        Assert.Contains("SELECT", vm.GeneratedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FROM orders", vm.GeneratedSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddJoin_IncreasesJoinCount()
    {
        var vm = new QueryBuilderViewModel();
        vm.AddTableToCanvas("orders");
        vm.AddTableToCanvas("customers");

        vm.AddJoin(new JoinDefinition
        {
            LeftTable = "orders",
            LeftColumn = "customer_id",
            RightTable = "customers",
            RightColumn = "id"
        });

        Assert.Single(vm.Joins);
    }

    [Fact]
    public void GeneratedSql_WithJoin_ContainsJoinKeyword()
    {
        var vm = new QueryBuilderViewModel();
        vm.AddTableToCanvas("orders");
        vm.AddTableToCanvas("customers");

        vm.AddJoin(new JoinDefinition
        {
            LeftTable = "orders",
            LeftColumn = "customer_id",
            RightTable = "customers",
            RightColumn = "id",
            JoinType = "INNER"
        });

        Assert.Contains("JOIN customers", vm.GeneratedSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoveTableCard_RemovesFromCollection()
    {
        var vm = new QueryBuilderViewModel();
        vm.AddTableToCanvas("orders");
        var card = vm.TableCards[0];

        vm.RemoveTableCard(card);

        Assert.Empty(vm.TableCards);
    }

    [Fact]
    public void GeneratedSql_WithCriteria_ContainsClauses()
    {
        var vm = new QueryBuilderViewModel();
        vm.AddTableToCanvas("orders");
        vm.WhereConditions.Add(new WhereCondition { Expression = "orders.total > 100" });
        vm.GroupByColumns.Add(new GroupByColumn { Column = "orders.customer_id" });
        vm.HavingConditions.Add(new HavingCondition { Expression = "COUNT(*) > 1" });
        vm.OrderByColumns.Add(new OrderByColumn { Column = "orders.total", Descending = true });
        vm.GenerateSql();

        Assert.Contains("WHERE", vm.GeneratedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", vm.GeneratedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HAVING", vm.GeneratedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", vm.GeneratedSql, StringComparison.OrdinalIgnoreCase);
    }
}
