using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class TableDataServerSideSortTests
{
    [Fact]
    public async Task HeaderClick_SortsWholeTableAcrossPages()
    {
        await using var fixture = await SeededTableAsync();
        var vm = fixture.ViewModel;

        await vm.SortByColumn("Name");
        Assert.Equal(["Alice", "Bob"], Names(vm));

        await vm.SortByColumn("Name"); // descending
        Assert.Equal(["Dave", "Charlie"], Names(vm));

        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(["Bob", "Alice"], Names(vm));
    }

    [Fact]
    public async Task HeaderClick_ResetsToFirstPage()
    {
        await using var fixture = await SeededTableAsync();
        var vm = fixture.ViewModel;
        await vm.NextPageCommand.ExecuteAsync(null);

        await vm.SortByColumn("Name");

        Assert.Equal(0, vm.CurrentPageIndex);
        Assert.Equal(["Alice", "Bob"], Names(vm));
    }

    [Fact]
    public async Task AdditiveHeaderClick_AddsSecondarySortKey()
    {
        await using var fixture = await SeededTableAsync();
        var vm = fixture.ViewModel;

        await vm.SortByColumn("Group");
        await vm.SortByColumn("Name", additive: true);
        await vm.SortByColumn("Name", additive: true); // secondary key descending

        Assert.Equal(
            [new TableSortKey("Group"), new TableSortKey("Name", Descending: true)],
            vm.SortKeys);
        Assert.Equal(["Dave", "Charlie"], Names(vm));
    }

    [Fact]
    public async Task PlainHeaderClick_ReplacesEarlierKeys()
    {
        await using var fixture = await SeededTableAsync();
        var vm = fixture.ViewModel;
        await vm.SortByColumn("Group");
        await vm.SortByColumn("Name", additive: true);

        await vm.SortByColumn("Id");

        Assert.Equal([new TableSortKey("Id")], vm.SortKeys);
    }

    [Fact]
    public async Task ThirdClick_ClearsSortAndFallsBackToKeyOrder()
    {
        await using var fixture = await SeededTableAsync();
        var vm = fixture.ViewModel;

        await vm.SortByColumn("Name");
        await vm.SortByColumn("Name");
        await vm.SortByColumn("Name");

        Assert.Empty(vm.SortKeys);
        Assert.Equal(["Charlie", "Alice"], Names(vm));
    }

    // ── Column filters ────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyFilters_FiltersWholeTableNotJustLoadedPage()
    {
        await using var fixture = await SeededTableAsync();
        var vm = fixture.ViewModel;
        await vm.NextPageCommand.ExecuteAsync(null);

        vm.AddFilterRow(column: "Group", TableFilterOperator.Equals, "2");
        await vm.ApplyFiltersAsync();

        Assert.Equal(0, vm.CurrentPageIndex);
        Assert.Equal(["Alice", "Bob"], Names(vm));
        Assert.False(vm.HasNextPage);
    }

    [Fact]
    public async Task ApplyFilters_QuoteInValue_MatchesLiterally()
    {
        await using var fixture = await SeededTableAsync();
        var vm = fixture.ViewModel;

        vm.AddFilterRow("Name", TableFilterOperator.Equals, "Ali'ce");
        await vm.ApplyFiltersAsync();

        Assert.Empty(vm.Rows);
        Assert.DoesNotContain("❌", vm.StatusMessage);
    }

    [Fact]
    public async Task ClearFilters_RemovesRowsAndReturnsToFullTable()
    {
        await using var fixture = await SeededTableAsync();
        var vm = fixture.ViewModel;
        vm.AddFilterRow("Group", TableFilterOperator.Equals, "2");
        await vm.ApplyFiltersAsync();

        await vm.ClearFiltersAsync();

        Assert.Empty(vm.FilterRows);
        Assert.Equal(["Charlie", "Alice"], Names(vm));
        Assert.True(vm.HasNextPage);
    }

    [Fact]
    public void BuildFilters_MapsRowsToSpecAndSkipsRowsWithoutColumn()
    {
        var vm = new TableDataTabViewModel();
        vm.AddFilterRow("Total", TableFilterOperator.GreaterThan, "100");
        vm.AddFilterRow(null, TableFilterOperator.Equals, "ignored");
        vm.AddFilterRow("Note", TableFilterOperator.IsNull, "leftover");

        Assert.Equal(
            [
                new TableFilter("Total", TableFilterOperator.GreaterThan, "100"),
                new TableFilter("Note", TableFilterOperator.IsNull)
            ],
            vm.BuildFilters());
    }

    // ── Raw WHERE ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RawWhere_CombinesWithColumnFiltersUsingAnd()
    {
        await using var fixture = await SeededTableAsync();
        var vm = fixture.ViewModel;

        vm.RawWhere = "\"Group\" = 1 OR \"Group\" = 2";
        vm.AddFilterRow("Name", TableFilterOperator.StartsWith, "B");
        await vm.ApplyFiltersAsync();

        Assert.Equal(["Bob"], Names(vm));
    }

    [Fact]
    public async Task InvalidRawWhere_ShowsErrorAndKeepsColumnFilters()
    {
        await using var fixture = await SeededTableAsync();
        var vm = fixture.ViewModel;
        vm.AddFilterRow("Name", TableFilterOperator.StartsWith, "B");
        vm.RawWhere = "nonsense((";

        await vm.ApplyFiltersAsync();

        Assert.Contains("❌", vm.StatusMessage);
        var row = Assert.Single(vm.FilterRows);
        Assert.Equal("Name", row.Column);
        Assert.Equal("B", row.Value);
        Assert.Equal(["Id", "Name", "Group"], vm.Columns);

        vm.RawWhere = "\"Group\" = 2";
        await vm.ApplyFiltersAsync();

        Assert.Equal(["Bob"], Names(vm));
    }

    [Fact]
    public async Task ClearFilters_AlsoClearsRawWhere()
    {
        await using var fixture = await SeededTableAsync();
        var vm = fixture.ViewModel;
        vm.RawWhere = "\"Group\" = 2";
        await vm.ApplyFiltersAsync();

        await vm.ClearFiltersAsync();

        Assert.Equal(string.Empty, vm.RawWhere);
        Assert.Equal(["Charlie", "Alice"], Names(vm));
    }

    // ── Jump-to-row navigation (FK lookup, Find in Data) ────────────────────────

    [Fact]
    public async Task PresetFilter_AppliesOnTheFirstLoad()
    {
        var connectionString = $"Data Source=file:sort{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL, "Group" INTEGER NOT NULL);
            INSERT INTO Customers VALUES (1, 'Charlie', 1);
            INSERT INTO Customers VALUES (2, 'Alice', 2);
            """);
        var session = new DatabaseSession(
            new ConnectionInfo { Name = "Local", Provider = provider.ProviderName, ConnectionString = connectionString },
            provider);
        var vm = new TableDataTabViewModel("Customers", session, CreateTableNode());

        vm.PresetFilter("Name", TableFilterOperator.Equals, "Alice");
        await vm.RefreshInternalAsync(bypassDirtyCheck: true);

        Assert.Equal(["Alice"], Names(vm));
        Assert.Equal("Alice", Assert.Single(vm.FilterRows).Value);
    }

    [Fact]
    public async Task ApplyColumnFilterAsync_ReplacesExistingFiltersAndReloads()
    {
        await using var fixture = await SeededTableAsync();
        var vm = fixture.ViewModel;
        vm.AddFilterRow("Group", TableFilterOperator.Equals, "1");
        await vm.ApplyFiltersAsync();

        await vm.ApplyColumnFilterAsync("Name", "Bob");

        Assert.Equal(["Bob"], Names(vm));
        Assert.Equal("Name", Assert.Single(vm.FilterRows).Column);
    }

    private static List<string?> Names(TableDataTabViewModel vm) =>
        vm.Rows.Select(r => r.Values["Name"]?.ToString()).ToList();

    private static async Task<Fixture> SeededTableAsync()
    {
        var connectionString = $"Data Source=file:sort{Guid.NewGuid():N}?mode=memory&cache=shared";
        var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(connectionString, """
            CREATE TABLE Customers (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL, "Group" INTEGER NOT NULL);
            INSERT INTO Customers VALUES (1, 'Charlie', 1);
            INSERT INTO Customers VALUES (2, 'Alice', 2);
            INSERT INTO Customers VALUES (3, 'Dave', 1);
            INSERT INTO Customers VALUES (4, 'Bob', 2);
            """);

        var session = new DatabaseSession(
            new ConnectionInfo { Name = "Local", Provider = provider.ProviderName, ConnectionString = connectionString },
            provider);
        var vm = new TableDataTabViewModel("Customers", session, CreateTableNode());
        vm.PageSize = 2;
        await vm.RefreshInternalAsync(bypassDirtyCheck: true);
        return new Fixture(vm, keepAlive);
    }

    private static SchemaNode CreateTableNode()
    {
        var table = new SchemaNode { Name = "Customers", RawName = "Customers", Type = SchemaNodeType.Table };
        table.Children.Add(new SchemaNode { Name = "Id", RawName = "Id", Type = SchemaNodeType.Column, IsPrimaryKey = true });
        table.Children.Add(new SchemaNode { Name = "Name", RawName = "Name", Type = SchemaNodeType.Column });
        table.Children.Add(new SchemaNode { Name = "Group", RawName = "Group", Type = SchemaNodeType.Column });
        return table;
    }

    private sealed class Fixture(TableDataTabViewModel viewModel, Microsoft.Data.Sqlite.SqliteConnection keepAlive) : IAsyncDisposable
    {
        public TableDataTabViewModel ViewModel { get; } = viewModel;
        public ValueTask DisposeAsync() => keepAlive.DisposeAsync();
    }
}
