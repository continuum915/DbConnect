using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

/// <summary>
/// RAIN-P2-16 — The query tab's real results grid opens for editing on a single keyed table, and is
/// read-only with the reason as its tooltip otherwise.
/// </summary>
public sealed class QueryResultEditingHeadlessTests : IDisposable
{
    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"rain-grid-edit-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_databaseFile); } catch (IOException) { }
    }

    private async Task<(QueryTabView View, QueryTabViewModel Tab)> ShowResultAsync(string sql, bool largeValue = false)
    {
        var connection = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "grid-edit",
            Provider = "SQLite",
            ConnectionString = $"Data Source={_databaseFile}"
        });
        var tab = new QueryTabViewModel("Query", new ObservableCollection<DatabaseSession> { connection });
        var session = await tab.GetOrOpenDbSessionAsync();
        await session.ExecuteQueryAsync("""
            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER, status TEXT);
            CREATE TABLE customers (id INTEGER PRIMARY KEY, email TEXT);
            INSERT INTO orders VALUES (1, 1, 'new');
            INSERT INTO customers VALUES (1, 'ada@example.com');
            """);
        if (largeValue)
            await session.ExecuteQueryAsync("UPDATE orders SET status = replace(hex(zeroblob(2621440)), '0', 'x')");

        var view = new QueryTabView { DataContext = tab };
        tab.SetExecutionResults([await session.ExecuteQueryAsync(sql)]);
        await tab.ResultEditor.PendingLoad;
        return (view, tab);
    }

    [AvaloniaFact]
    public async Task SingleKeyedTableResult_OpensTheGridForEditing()
    {
        var (view, tab) = await ShowResultAsync("SELECT * FROM orders WHERE id < 10");

        var grid = view.FindControl<DataGrid>("ResultsGrid")!;
        Assert.False(grid.IsReadOnly);
        Assert.All(grid.Columns, column => Assert.IsType<DataGridTemplateColumn>(column));
        Assert.Null(ToolTip.GetTip(grid));
        Assert.NotNull(tab.ResultEditor.DiscardConfirmation); // replacing pending edits asks, rather than being refused silently
        await tab.ReleaseDbSessionAsync();
    }

    [AvaloniaFact]
    public async Task JoinResult_KeepsTheGridReadOnly_WithTheReasonAsItsTooltip()
    {
        var (view, tab) = await ShowResultAsync("SELECT o.id, c.email FROM orders o JOIN customers c ON c.id = o.customer_id");

        var grid = view.FindControl<DataGrid>("ResultsGrid")!;
        Assert.True(grid.IsReadOnly);
        Assert.All(grid.Columns, column => Assert.True(column.IsReadOnly));
        var tip = Assert.IsType<string>(ToolTip.GetTip(grid));
        Assert.Contains("customers", tip);
        await tab.ReleaseDbSessionAsync();
    }

    [AvaloniaFact]
    public async Task EditableResult_LargeTextDisplaysAPrefix_AndEditorUsesFullValue()
    {
        var (view, tab) = await ShowResultAsync("SELECT * FROM orders", largeValue: true);
        var grid = view.FindControl<DataGrid>("ResultsGrid")!;
        ((Panel)grid.Parent!).Children.Remove(grid);
        var window = new Window { Content = grid, Width = 800, Height = 400 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var row = grid.ItemsSource!.Cast<System.Collections.Generic.IReadOnlyList<string>>().First();
            var column = grid.Columns[2];
            var cell = Assert.IsType<ContentControl>(column.GetCellContent(row));
            Assert.Equal(new string('x', 2048) + "…", Assert.IsType<TextBlock>(cell.Content).Text);
            grid.SelectedItem = row;
            grid.CurrentColumn = column;
            Assert.True(grid.BeginEdit());
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(new string('x', 5 * 1024 * 1024), Assert.IsType<TextBox>(column.GetCellContent(row)).Text);
            Assert.True(grid.CommitEdit());
            Assert.Equal(new string('x', 5 * 1024 * 1024), tab.ResultEditor.GetValue(tab.ResultEditor.RowFor(row)!, 2));
        }
        finally
        {
            window.Close();
            await tab.ReleaseDbSessionAsync();
        }
    }
}
