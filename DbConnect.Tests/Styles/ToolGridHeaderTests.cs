using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.Tests.UiSnapshots;
using DbConnect.Views;

namespace DbConnect.Tests.Styles;

/// <summary>
/// Ticket 08: every column header in every tool tab is at least as wide as its text, with each tool opened from the
/// main window on the seeded database, as a user would open it. Grids hidden behind an empty state are not measured.
/// </summary>
public sealed class ToolGridHeaderTests : MainWindowTestBase
{
    [AvaloniaFact]
    public async Task ToolTabColumnHeaders_ShowTheirWholeText()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"rain-headers-{Guid.NewGuid():N}.db");
        File.Copy(UiSnapshotDialogs.FindSeededDatabase(), dbPath);
        var vm = CreateMainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        try
        {
            await vm.AddConnectionAsync(new ConnectionInfo { Name = "Shop", Provider = "SQLite", ConnectionString = $"Data Source={dbPath}" });
            var table = await UiSnapshotDialogs.ExpandToFirstTableAsync(vm, vm.SchemaNodes[0]);
            Assert.NotNull(table);
            vm.SelectedSchemaNode = table;
            HeadlessFrames.Pump(window);

            var trimmed = new List<string>();
            var measured = 0;
            foreach (var name in UiSnapshotCapture.ToolCommands)
            {
                Assert.True(await UiSnapshotCapture.OpenToolAsync(vm, name), $"{name} not found");
                measured += Measure(window, name, trimmed);
            }

            vm.Tools.OpenDependencyAnalysisTab(table);
            measured += Measure(window, "Dependencies", trimmed);

            Assert.True(measured > 0, "No column headers were found in the tool tabs.");
            Assert.True(trimmed.Count == 0, "Trimmed tool-tab headers:\n" + string.Join("\n", trimmed));
        }
        finally
        {
            window.Close();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    /// <summary>Adds the open tab's trimmed headers to <paramref name="trimmed"/> and returns how many it measured.</summary>
    private static int Measure(Window window, string tool, List<string> trimmed)
    {
        HeadlessFrames.Pump(window);
        var headers = window.GetVisualDescendants().OfType<DataGrid>()
            .Where(grid => grid.IsEffectivelyVisible && grid.Bounds.Width > 0)
            .SelectMany(DataGridHeaders.Texts)
            .Where(header => header.IsEffectivelyVisible)
            .ToList();
        trimmed.AddRange(headers.Where(DataGridHeaders.IsTrimmed).Select(header => $"{tool}: \"{header.Text}\" ({header.Bounds.Width:0} px)"));
        return headers.Count;
    }
}
