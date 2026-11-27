using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using DbConnect.ViewModels;
using DbConnect.PerformanceTests;
using DbConnect.Views;

namespace DbConnect.Tests;

public class QueryTextCopyHeadlessTests
{
    [AvaloniaFact]
    public void EditingDoesNotMaterializeTheWholeQueryUntilAConsumerReadsIt()
    {
        var sql = RepresentativeImport.Generate(1024 * 1024).Sql;
        var offset = sql.IndexOf("Parents", StringComparison.Ordinal) + 3;
        var vm = new QueryTabViewModel { QueryText = sql };
        var view = new QueryTabView { DataContext = vm };
        var editor = view.FindControl<TextEditor>("QueryEditor")!;
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        try
        {
            window.Show();
            vm.ClearDirty();
            var before = GC.GetAllocatedBytesForCurrentThread();
            editor.Document.Insert(offset, "z");
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 1024 * 1024, $"Edit allocated {allocated:N0} bytes on the UI thread.");
            Assert.True(vm.IsDirty);
            Assert.Equal(sql.Insert(offset, "z"), vm.QueryText);
            Assert.Equal(editor.Text, vm.QueryText);
        }
        finally { window.Close(); view.DataContext = null; }
    }
}
