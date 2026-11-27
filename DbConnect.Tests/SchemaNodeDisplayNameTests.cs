using Xunit;
using DbConnect.Models;

namespace DbConnect.Tests;

public class SchemaNodeDisplayNameTests
{
    [Fact]
    public void DisplayName_NullChildCount_ReturnsName()
    {
        var node = new SchemaNode { Name = "Tables" };
        Assert.Equal("Tables", node.DisplayName);
    }

    [Fact]
    public void DisplayName_WithChildCount_ReturnsNameWithCount()
    {
        var node = new SchemaNode { Name = "Tables" };
        node.ChildCount = 42;
        Assert.Equal("Tables (42)", node.DisplayName);
    }

    [Fact]
    public void ChildCount_Change_RaisesPropertyChanged()
    {
        var node = new SchemaNode { Name = "Tables" };
        var raised = new System.Collections.Generic.List<string>();
        node.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        node.ChildCount = 5;
        Assert.Contains("ChildCount", raised);
        Assert.Contains("DisplayName", raised);
    }

    [Fact]
    public void ChildCount_Zero_ShowsZeroInName()
    {
        var node = new SchemaNode { Name = "Views" };
        node.ChildCount = 0;
        Assert.Equal("Views (0)", node.DisplayName);
    }
}
