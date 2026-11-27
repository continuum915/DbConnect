using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>
/// Window-side dialogs, file pickers and menus that view models ask for: SQL files, unsaved work,
/// and confirming SQL that changes data on sensitive connections.
/// </summary>
internal static class MainWindowDialogs
{
    private static readonly FilePickerFileType SqlFiles = new("SQL Files") { Patterns = ["*.sql", "*.txt"] };
    private static readonly FilePickerFileType AllFiles = new("All Files") { Patterns = ["*"] };

    /// <summary>Supplies the view models' pickers and dialogs, and returns the guard for closing and quitting.</summary>
    public static UnsavedChangesGuard Wire(Window owner, MainWindowViewModel vm)
    {
        vm.Safety.ConfirmDataChange = confirmation => new ConfirmDialog(
            "Confirm data change",
            $"This SQL may change data on the {confirmation.EnvironmentTier} connection \"{confirmation.ConnectionName}\":\n\n" +
            Preview(confirmation.Statement),
            "Run", "Cancel").ShowDialog<bool>(owner);

        var files = vm.Files;
        ExplorerScriptMenu.Wire(owner, vm);
        ExplorerActionsMenu.Wire(owner, vm);
        files.PickOpenPaths = async () =>
        {
            var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open SQL File",
                AllowMultiple = true,
                FileTypeFilter = [SqlFiles, AllFiles]
            });
            return picked.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        };
        files.PickSavePath = async suggestedName =>
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save SQL File",
                SuggestedFileName = suggestedName,
                DefaultExtension = "sql",
                FileTypeChoices = [SqlFiles, AllFiles]
            });
            return file?.TryGetLocalPath();
        };
        files.ConfirmOverwrite = message =>
            new ConfirmDialog("File changed on disk", message, "Overwrite", "Cancel").ShowDialog<bool>(owner);
        files.ReportError = message =>
            new ConfirmDialog("File error", message, "OK", "Close").ShowDialog<bool>(owner);

        owner.Activated += (_, _) => _ = files.Monitor.CheckAllAsync();

        if (owner.FindControl<MenuItem>("OpenRecentMenu") is { } recentMenu)
        {
            files.Recent.Items.CollectionChanged += (_, _) => BuildRecentMenu(recentMenu, files);
            BuildRecentMenu(recentMenu, files);
        }

        return new UnsavedChangesGuard(
            async tabs => await new SaveChangesDialog(tabs.Select(t => t.Title).ToList())
                .ShowDialog<SaveChangesChoice?>(owner) ?? SaveChangesChoice.Cancel,
            files.SaveTabAsync);
    }

    /// <summary>Fills File ▸ Open Recent: one item per file, then Clear Recent Files.</summary>
    internal static void BuildRecentMenu(MenuItem menu, SqlFileCommands files)
    {
        var items = files.Recent.Items.Select(path => (object)new MenuItem
        {
            Header = Path.GetFileName(path),
            Command = files.OpenRecentCommand,
            CommandParameter = path,
            [ToolTip.TipProperty] = path
        }).ToList();
        if (items.Count > 0) items.Add(new Separator());
        items.Add(new MenuItem { Header = "Clear Recent Files", Command = files.ClearRecentCommand, IsEnabled = items.Count > 0 });
        menu.ItemsSource = items;
    }

    private static string Preview(string statement) =>
        statement.Length <= 600 ? statement : statement[..600] + "…";
}
