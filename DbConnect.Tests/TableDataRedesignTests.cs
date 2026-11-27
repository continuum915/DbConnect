using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;
using static DbConnect.Tests.TableDataTestFixtures;

namespace DbConnect.Tests;

/// <summary>UI redesign 07: the table data tab's filter bar SQL mode, record pane, pending-changes chip and cell menu.</summary>
public sealed class TableDataRedesignTests
{
    [Fact]
    public void Rules_RenderAsLiteralWhereText_WithTheProvidersQuoting()
    {
        var capabilities = new SqliteProvider().Capabilities;
        var where = TableFilterSql.ToWhere(capabilities,
            [
                new TableFilter("Name", TableFilterOperator.NotEquals, "o'brien"),
                new TableFilter("Note", TableFilterOperator.IsNull),
                new TableFilter("Code", TableFilterOperator.StartsWith, "50%"),
            ],
            "Id < 4");

        Assert.Equal(
            "(Id < 4) AND \"Name\" <> 'o''brien' AND \"Note\" IS NULL AND CAST(\"Code\" AS TEXT) LIKE '50!%%' ESCAPE '!'",
            where);
    }

    [Fact]
    public void OrderBy_MapsToTheTablesColumns_AndRefusesOthers()
    {
        var (keys, error) = TableFilterSql.ParseOrderBy("\"name\" DESC, id", ["Id", "Name"]);
        Assert.Null(error);
        Assert.Equal([new TableSortKey("Name", true), new TableSortKey("Id")], keys);

        var (_, refused) = TableFilterSql.ParseOrderBy("total DESC", ["Id", "Name"]);
        Assert.Contains("\"total\"", refused);
    }

    [Fact]
    public async Task ABuilderFilterSwitchedToSql_ShowsTheEquivalentWhere_AndReturnsTheSameRows()
    {
        var (tab, dispose) = await OpenCustomersAsync();
        try
        {
            tab.RawWhere = "Id < 5";
            tab.AddFilterRow("Name", TableFilterOperator.NotEquals, "drop");
            await tab.ApplyFiltersAsync();
            var builderRows = Ids(tab);
            Assert.Equal([1, 2, 3], builderRows);

            tab.FilterBar.ShowSqlCommand.Execute(null);
            Assert.True(tab.FilterBar.IsSqlMode);
            Assert.Equal("(Id < 5) AND \"Name\" <> 'drop'", tab.FilterBar.Where);
            Assert.Empty(tab.FilterRows);

            tab.FilterBar.OrderBy = "Name DESC";
            await tab.FilterBar.ApplySqlAsync();
            Assert.Equal(builderRows.OrderBy(id => id), Ids(tab).OrderBy(id => id));
            Assert.Equal([new TableSortKey("Name", true)], tab.SortKeys);

            tab.FilterBar.ShowBuilderCommand.Execute(null);
            Assert.False(tab.FilterBar.IsSqlMode);
            Assert.Equal("(Id < 5) AND \"Name\" <> 'drop'", tab.RawWhere);
        }
        finally
        {
            await dispose();
        }
    }

    [AvaloniaFact]
    public async Task EditingACell_ShowsOnePendingChange_TheRecordPaneFollowsTheRow_AndDiscardClears()
    {
        var (tab, dispose) = await OpenCustomersAsync();
        var view = new TableDataTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1300, Height = 800 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var chip = Named<Border>(view, "PendingChip");
            Assert.False(chip.IsVisible);

            tab.Rows[0].Values["Name"] = "edited";
            tab.MarkRowModified(tab.Rows[0]);
            window.UpdateLayout();
            Assert.True(chip.IsVisible);
            Assert.Equal("1 pending changes", ((TextBlock)chip.Child!).Text);

            tab.RecordPane.IsVisible = true;
            var grid = Named<DataGrid>(view, "DataGrid");
            grid.SelectedIndex = 1;
            window.UpdateLayout();
            Assert.True(Named<RecordPaneView>(view, "RecordPane").IsVisible);
            Assert.Equal($"Record 2 of {tab.Rows.Count}", tab.RecordPane.PositionText);

            tab.RevertCommand.Execute(null);
            window.UpdateLayout();
            Assert.False(chip.IsVisible);
        }
        finally
        {
            window.Close();
            await dispose();
        }
    }

    [AvaloniaFact]
    public async Task TheFormToggle_ShowsTheForm_AndTheCellMenuKeepsEveryCommand()
    {
        var (tab, dispose) = await OpenCustomersAsync();
        var view = new TableDataTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 1300, Height = 800 };
        try
        {
            window.Show();
            Named<ToggleButton>(view, "FormToggle").Command!.Execute(null);
            window.UpdateLayout();
            Assert.True(tab.Record.IsActive);
            Assert.True(view.GetVisualDescendants().OfType<RecordView>().First().IsVisible);

            var menu = Named<DataGrid>(view, "DataGrid").ContextMenu!;
            var headers = menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToList();
            string[] expected =
            [
                "Copy Row", "Copy as INSERT", "Paste Rows", "Revert Cell", "Revert Row", "Duplicate Row", "View Value…",
                "View Binary…", "Look up referenced value…", "Open referenced row", "Open referencing rows", "Hide Column",
                "Freeze Up To This Column", "Unfreeze Columns",
            ];
            Assert.Empty(expected.Except(headers!));
        }
        finally
        {
            window.Close();
            await dispose();
        }
    }

    private static async Task<(TableDataTabViewModel Tab, Func<Task> Dispose)> OpenCustomersAsync()
    {
        var connection = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "redesign-07",
            Provider = "SQLite",
            ConnectionString = "Data Source=:memory:",
        });
        var tab = new TableDataTabViewModel("Customers", connection, CreateTableNode(true)) { SkipExactCount = true };
        var session = await tab.GetOrOpenDbSessionAsync();
        await session.ExecuteQueryAsync(
            "CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT); INSERT INTO Customers VALUES (1,'B'),(2,'A'),(3,'C'),(4,'drop'),(6,'Z');");
        await tab.RefreshInternalAsync(bypassDirtyCheck: true);
        return (tab, () => tab.ReleaseDbSessionAsync());
    }

    private static List<long> Ids(TableDataTabViewModel tab) =>
        tab.Rows.Select(r => Convert.ToInt64(r.Values["Id"])).ToList();

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
}
