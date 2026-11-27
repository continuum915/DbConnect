using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

public class LazyQueryExecutionHeadlessTests : MainWindowTestBase
{
    [AvaloniaFact]
    public async Task RunUsesTheLatestUnmaterializedEditorRevision()
    {
        var main = CreateMainWindowViewModel();
        var tab = main.NewQueryTabWithSession();
        var session = new DatabaseSession(new ConnectionInfo
        {
            Name = "Snapshot test",
            Provider = "SQLite",
            ConnectionString = "Data Source=:memory:"
        }, new SqliteProvider());
        main.ActiveSessionOptions.Add(session);
        tab.SelectedSession = session;
        tab.QueryText = "SELECT 1;";
        var view = new QueryTabView { DataContext = tab };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            var editor = view.FindControl<TextEditor>("QueryEditor")!;
            editor.Document.Replace(7, 1, "42");
            await main.ExecuteQueryCommand.ExecuteAsync(null);
            Assert.NotNull(tab.CurrentResult);
            Assert.False(tab.CurrentResult.HasError, tab.CurrentResult.Error?.Summary);
            Assert.Equal(42L, Convert.ToInt64(Assert.Single(tab.CurrentResult.ObjectRows)[0]));
            Assert.Equal("SELECT 42", tab.CurrentResult.ExecutedSql.Trim().TrimEnd(';'));
        }
        finally { window.Close(); view.DataContext = null; session.Catalog.Dispose(); }
    }
}
