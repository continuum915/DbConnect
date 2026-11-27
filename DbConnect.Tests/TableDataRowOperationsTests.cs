using System.Collections.Generic;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class DuplicateRowTests
{
    [Fact]
    public void DuplicateRow_CopiesValues()
    {
        var vm = new TableDataTabViewModel();
        vm.Columns.Add("Id");
        vm.Columns.Add("Name");
        var original = new EditableRow(new Dictionary<string, object?> { ["Id"] = 1L, ["Name"] = "Alice" });
        vm.Rows.Add(original);
        vm.SelectedRow = original;

        vm.DuplicateSelectedRowCommand.Execute(null);

        Assert.Equal(2, vm.Rows.Count);
        var dupe = vm.Rows[1];
        Assert.Equal("Alice", dupe.Values["Name"]?.ToString());
    }

    [Fact]
    public void DuplicateRow_SetsEditStateInserted()
    {
        var vm = new TableDataTabViewModel();
        vm.Columns.Add("Id");
        vm.Columns.Add("Name");
        var original = new EditableRow(new Dictionary<string, object?> { ["Id"] = 1L, ["Name"] = "Bob" });
        vm.Rows.Add(original);
        vm.SelectedRow = original;

        vm.DuplicateSelectedRowCommand.Execute(null);

        Assert.Equal(EditState.Inserted, vm.Rows[1].State);
    }
}

public class CopyAsInsertTests
{
    [Fact]
    public void BuildInsertSql_NullValue_EmitsNullKeyword()
    {
        var vm = new TableDataTabViewModel();
        vm.Columns.Add("Id");
        vm.Columns.Add("Name");
        var row = new EditableRow(new Dictionary<string, object?> { ["Id"] = null, ["Name"] = "Alice" });

        var sql = vm.BuildInsertSql(row);

        Assert.Contains("NULL", sql);
        Assert.Contains("'Alice'", sql);
    }

    [Fact]
    public void BuildInsertSql_StringWithQuote_EscapesCorrectly()
    {
        var vm = new TableDataTabViewModel();
        vm.Columns.Add("Name");
        var row = new EditableRow(new Dictionary<string, object?> { ["Name"] = "O'Brien" });

        var sql = vm.BuildInsertSql(row);

        Assert.Contains("'O''Brien'", sql);
    }

    [Fact]
    public void BuildInsertSql_ContainsTableName()
    {
        var vm = new TableDataTabViewModel();
        // TableName defaults to empty but we can set it via property (it's ObservableProperty)
        vm.TableName = "Orders";
        vm.Columns.Add("Id");
        var row = new EditableRow(new Dictionary<string, object?> { ["Id"] = 1L });

        var sql = vm.BuildInsertSql(row);

        Assert.Contains("\"Orders\"", sql);
        Assert.StartsWith("INSERT INTO", sql);
    }
}
