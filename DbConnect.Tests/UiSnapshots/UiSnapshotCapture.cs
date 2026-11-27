using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests.UiSnapshots;

/// <summary>
/// Opt-in screenshot capture for UI review. Does nothing unless DBCONNECT_UI_SNAPSHOT_DIR names an
/// output directory. Drives the real app against a copy of the seeded SQLite database and saves
/// the main window, every tool tab and every dialog in light and dark. Dialogs get representative
/// content from <see cref="UiSnapshotDialogs"/>; <c>_log.txt</c> lists each capture and any exception.
/// </summary>
public sealed class UiSnapshotCapture : MainWindowTestBase
{
    /// <summary>The tool tabs the main window opens on the selected connection.</summary>
    internal static readonly string[] ToolCommands =
    [
        "OpenQueryBuilderCommand", "OpenErDiagramCommand", "OpenAutomationCommand", "OpenUserManagementCommand",
        "OpenConnectionMonitorCommand", "OpenSlowQueryDashboardCommand", "OpenSchemaHistoryCommand",
        "OpenAuditLogCommand", "OpenFindInDataCommand", "OpenSchemaDiffCommand", "OpenDataCompareCommand",
        "OpenBackupRestoreCommand", "OpenDataTransferCommand",
    ];

    [AvaloniaFact]
    public async Task CaptureAllScreens()
    {
        var output = Environment.GetEnvironmentVariable("DBCONNECT_UI_SNAPSHOT_DIR");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        var log = new List<string>();

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var suffix = variant == ThemeVariant.Light ? "light" : "dark";
            var variantLog = new List<string>();
            await CaptureMainWindowAsync(output, suffix, variantLog);
            log.AddRange(variantLog.Select(line => $"[{suffix}] {line}"));
        }
        File.WriteAllLines(Path.Combine(output, "_log.txt"), log);
    }

    private async Task CaptureMainWindowAsync(string output, string suffix, List<string> log)
    {
        var empty = CreateMainWindowViewModel();
        var emptyWindow = new MainWindow { DataContext = empty, Width = 1400, Height = 900 };
        emptyWindow.Show();
        Save(emptyWindow, output, $"main-empty-{suffix}");
        emptyWindow.Close();

        var dbPath = Path.Combine(Path.GetTempPath(), $"rain-snap-{Guid.NewGuid():N}.db");
        File.Copy(UiSnapshotDialogs.FindSeededDatabase(), dbPath);
        var vm = CreateMainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.AddConnectionAsync(new ConnectionInfo { Name = "Shop (SQLite)", Provider = "SQLite", ConnectionString = $"Data Source={dbPath}" });
        HeadlessFrames.Pump(window);
        Save(window, output, $"main-connected-{suffix}");

        var table = await UiSnapshotDialogs.ExpandToFirstTableAsync(vm, vm.SchemaNodes[0]);
        if (table == null) log.Add("No table node found.");
        else
        {
            vm.SelectedSchemaNode = table;
            window.FindControl<TreeView>("SchemaTree")!.SelectedItem = table;
            HeadlessFrames.Pump(window);
            Save(window, output, $"main-tree-selected-{suffix}");

            await Try(log, "query tab", async () =>
            {
                vm.NewQueryTabCommand.Execute(null);
                HeadlessFrames.Pump(window);
                if (vm.SelectedTab is QueryTabViewModel query)
                {
                    query.QueryText = $"SELECT * FROM {table.Name} LIMIT 200;";
                    await vm.ExecuteQueryCommand.ExecuteAsync(null);
                    await Task.Delay(500);
                }
                HeadlessFrames.Pump(window);
                Save(window, output, $"tab-query-{suffix}");
            });
            await Try(log, "table data", async () =>
            {
                vm.OpenTableDataTab(table);
                await Task.Delay(800);
                HeadlessFrames.Pump(window);
                Save(window, output, $"tab-table-data-{suffix}");
            });
            await Try(log, "table designer", async () =>
            {
                vm.Tools.OpenTableDesignerTab(table);
                await Task.Delay(800);
                HeadlessFrames.Pump(window);
                Save(window, output, $"tab-table-designer-{suffix}");
            });
            await Try(log, "index manager", async () =>
            {
                vm.Tools.OpenIndexManagerTab(table);
                await Task.Delay(500);
                HeadlessFrames.Pump(window);
                Save(window, output, $"tab-index-manager-{suffix}");
            });
            await Try(log, "dependencies", async () =>
            {
                vm.Tools.OpenDependencyAnalysisTab(table);
                await Task.Delay(500);
                HeadlessFrames.Pump(window);
                Save(window, output, $"tab-dependencies-{suffix}");
            });
        }

        foreach (var name in ToolCommands)
        {
            await Try(log, name, async () =>
            {
                if (!await OpenToolAsync(vm, name)) { log.Add($"{name}: not found"); return; }
                await Task.Delay(500);
                HeadlessFrames.Pump(window);
                var label = name.Replace("Open", "").Replace("Command", "");
                Save(window, output, $"tool-{label}-{suffix}");
            });
        }
        var session = table == null ? null : vm.SchemaTree.FindSessionForNode(table);
        var fixtures = session == null
            ? new Dictionary<Type, Func<Task<Window>>>()
            : UiSnapshotDialogs.Build(new UiSnapshotContext(vm, session, table!));
        await CaptureDialogsAsync(output, suffix, fixtures, log);
        window.Close();
    }

    private static async Task CaptureDialogsAsync(string output, string suffix,
        IReadOnlyDictionary<Type, Func<Task<Window>>> fixtures, List<string> log)
    {
        foreach (var type in UiSnapshotDialogs.DialogTypes())
        {
            Window? dialog = null;
            try
            {
                dialog = await UiSnapshotDialogs.CreateAsync(type, fixtures, message => log.Add($"{type.Name}: {message}"));
                if (dialog == null) continue;
                if (double.IsNaN(dialog.Width) && dialog.SizeToContent == SizeToContent.Manual) dialog.Width = 600;
                dialog.Show();
                await Task.Delay(200);
                HeadlessFrames.Pump(dialog);
                Save(dialog, output, $"dialog-{type.Name}-{suffix}");
                log.Add($"{type.Name}: ok ({(fixtures.ContainsKey(type) ? "fixture" : "default")}, DataContext {(dialog.DataContext?.GetType().Name ?? "none")}, {dialog.Bounds.Width}x{dialog.Bounds.Height})");
            }
            catch (Exception e) { log.Add($"{type.Name}: {e.GetBaseException().Message}"); }
            finally { try { dialog?.Close(); } catch { } }
        }
    }

    /// <summary>Runs the main window's (or its Tools') open command <paramref name="name"/>; false if there is none.</summary>
    internal static async Task<bool> OpenToolAsync(MainWindowViewModel vm, string name)
    {
        var command = new object[] { vm, vm.Tools }
            .Select(target => target.GetType().GetProperty(name)?.GetValue(target))
            .OfType<System.Windows.Input.ICommand>()
            .FirstOrDefault();
        if (command == null) return false;
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(null);
        else command.Execute(null);
        return true;
    }

    private static async Task Try(List<string> log, string what, Func<Task> action)
    {
        try { await action(); log.Add($"{what}: ok"); }
        catch (Exception e) { log.Add($"{what}: {e.GetBaseException().Message}"); }
    }


    private static void Save(Window window, string output, string name)
    {
        HeadlessFrames.Pump(window);
        var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(output, name + ".png"));
    }
}
