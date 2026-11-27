using Avalonia.Controls;
using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Query-result SQL INSERT export from the query tab: target defaults and the session's dialect (ticket sql-dump-and-insert-export/01).</summary>
public sealed class SqlInsertQueryTabExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-sqlinsert-tab-{Guid.NewGuid():N}");
    public SqlInsertQueryTabExportTests() => Directory.CreateDirectory(_directory);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    [Theory]
    [InlineData("SELECT id, value FROM \"my items\" ORDER BY id", "INSERT INTO \"my items\" (\"id\", \"value\") VALUES (1, 'a');\n")]
    [InlineData("SELECT 1 AS id, 'a' AS value", "INSERT INTO \"exported_result\" (\"id\", \"value\") VALUES (1, 'a');\n")]
    [InlineData("SELECT i.id, o.value FROM \"my items\" i JOIN other o ON o.id = i.id", "INSERT INTO \"exported_result\" (\"id\", \"value\") VALUES (1, 'b');\n")]
    public async Task BlankTargetDefaultsToTheSingleSourceTableOrExportedResult(string query, string expected)
    {
        var output = await ExportAsync(query, tableName: "");
        Assert.Equal(expected, output);
    }

    [Fact]
    public async Task EnteredTargetIsWrittenVerbatim()
    {
        var output = await ExportAsync("SELECT id, value FROM \"my items\" ORDER BY id", tableName: "archive.items_copy");
        Assert.Equal("INSERT INTO archive.items_copy (\"id\", \"value\") VALUES (1, 'a');\n", output);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void ChoosingSqlInsertShowsItsOptionsInsteadOfTextOptions()
    {
        var tab = new QueryTabViewModel { ShowExportOptionsPanel = true };
        var view = new DbConnect.Views.QueryTabView { DataContext = tab };
        var table = view.FindControl<Avalonia.Controls.TextBox>("SqlInsertTableName")!;
        var rows = view.FindControl<Avalonia.Controls.NumericUpDown>("SqlInsertRowsPerStatement")!;
        var columnList = view.FindControl<Avalonia.Controls.CheckBox>("SqlInsertColumnList")!;
        Assert.False(table.IsVisible);

        tab.ExportOptions.Format = ExportFormat.SqlInsert;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(table.IsVisible && rows.IsVisible && columnList.IsVisible);
        Assert.False(tab.ExportOptions.UsesTextOptions);

        table.Text = "\"archive\"";
        rows.Value = 250;
        columnList.IsChecked = false;
        Assert.Equal("\"archive\"", tab.ExportOptions.SqlTableName);
        Assert.Equal(250, tab.ExportOptions.RowsPerStatement);
        Assert.False(tab.ExportOptions.IncludeColumnList);
    }

    private async Task<string> ExportAsync(string query, string tableName)
    {
        var connection = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "sql-insert",
            Provider = "SQLite",
            ConnectionString = $"Data Source={Path.Combine(_directory, "query.sqlite")}"
        });
        var tab = new QueryTabViewModel("Query", new ObservableCollection<DatabaseSession> { connection });
        var session = await tab.GetOrOpenDbSessionAsync();
        try
        {
            await session.ExecuteQueryAsync("CREATE TABLE IF NOT EXISTS \"my items\" (id INTEGER PRIMARY KEY, value TEXT); CREATE TABLE IF NOT EXISTS other (id INTEGER PRIMARY KEY, value TEXT); INSERT OR IGNORE INTO \"my items\" VALUES (1, 'a'); INSERT OR IGNORE INTO other VALUES (1, 'b');");
            tab.SetExecutionResults([await session.ExecuteQueryAsync(query)]);
            var path = Path.Combine(_directory, "rows.sql");
            tab.ExportPathPicker = new Picker(path);
            tab.ExportOptions.Format = ExportFormat.SqlInsert;
            tab.ExportOptions.SqlTableName = tableName;
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.True(tab.ExportState.Outcome?.Status == ExportStatus.Completed, tab.ExportState.Outcome?.Diagnostic);
            return await File.ReadAllTextAsync(path);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private sealed class Picker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(path);
    }
}
