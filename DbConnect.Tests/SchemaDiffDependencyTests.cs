using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services;
using Xunit;

namespace DbConnect.Tests;

public class SchemaDiffDependencyTests
{
    private static SchemaDiffItem Make(
        string name,
        SchemaDiffObjectType type,
        SchemaDiffChangeType change = SchemaDiffChangeType.Add,
        List<string>? deps = null) =>
        new()
        {
            ObjectName = name,
            ObjectKind = type,
            ChangeType = change,
            DependsOn = deps ?? new List<string>()
        };

    [Fact]
    public void Sort_TablesBeforeViews_InAddOrder()
    {
        var svc = new DependencyAnalysisService();
        var items = new List<SchemaDiffItem>
        {
            Make("my_view", SchemaDiffObjectType.View, deps: new List<string> { "orders" }),
            Make("orders", SchemaDiffObjectType.Table)
        };

        var result = svc.Sort(items);
        var names = result.SortedItems.Select(i => i.ObjectName).ToList();

        Assert.True(names.IndexOf("orders") < names.IndexOf("my_view"));
    }

    [Fact]
    public void Sort_RemovesInReverseOrder_TriggersBeforeTables()
    {
        var svc = new DependencyAnalysisService();
        var items = new List<SchemaDiffItem>
        {
            Make("orders", SchemaDiffObjectType.Table, SchemaDiffChangeType.Remove),
            Make("trg_audit", SchemaDiffObjectType.Trigger, SchemaDiffChangeType.Remove)
        };

        var result = svc.Sort(items);
        var names = result.SortedItems.Select(i => i.ObjectName).ToList();

        Assert.True(names.IndexOf("trg_audit") < names.IndexOf("orders"));
    }

    [Fact]
    public void Sort_CircularDependency_DetectedAndIncluded()
    {
        var svc = new DependencyAnalysisService();
        var items = new List<SchemaDiffItem>
        {
            Make("a", SchemaDiffObjectType.View, deps: new List<string> { "b" }),
            Make("b", SchemaDiffObjectType.View, deps: new List<string> { "a" })
        };

        var result = svc.Sort(items);

        Assert.NotEmpty(result.CircularGroups);
        Assert.Equal(2, result.SortedItems.Count);
    }

    [Fact]
    public void NormalizeBody_RemovesCommentsAndNormalizesCase()
    {
        var input = "SELECT -- comment\n  id  FROM  orders";
        var normalized = DependencyAnalysisService.NormalizeBody(input);

        Assert.DoesNotContain("--", normalized);
        Assert.Equal(normalized, normalized.ToUpperInvariant());
    }

    [Fact]
    public void ExtractReferencedObjects_FindsTablesFromJoin()
    {
        var sql = "SELECT * FROM orders o JOIN customers c ON o.customer_id = c.id";
        var refs = DependencyAnalysisService.ExtractReferencedObjects(sql);

        Assert.Contains("orders", refs);
        Assert.Contains("customers", refs);
    }

    [Fact]
    public void ImpactSummary_ModifiedViews_ReportsCorrectCount()
    {
        var svc = new DependencyAnalysisService();
        var items = new List<SchemaDiffItem>
        {
            Make("v1", SchemaDiffObjectType.View, SchemaDiffChangeType.Modify, new List<string> { "orders" }),
            Make("v2", SchemaDiffObjectType.View, SchemaDiffChangeType.Modify),
            Make("p1", SchemaDiffObjectType.Procedure, SchemaDiffChangeType.Modify)
        };

        var result = svc.Sort(items);

        Assert.Equal(2, result.ImpactSummary.AffectedViewCount);
        Assert.Equal(1, result.ImpactSummary.AffectedProcedureCount);
        Assert.True(result.ImpactSummary.HasImpact);
    }
}
