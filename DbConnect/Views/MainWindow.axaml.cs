using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class MainWindow : Window
{
    /// <summary>Opens the connection dialog (the sidebar's "No connections" empty state).</summary>
    public System.Windows.Input.ICommand AddConnectionCommand { get; }

    public MainWindow()
    {
        AddConnectionCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => AddConnection_Click(this, new RoutedEventArgs()));
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        var tree = this.FindControl<TreeView>("SchemaTree")!;

        // Wire DoubleTapped with handledEventsToo:true so we receive the event
        // even after the TreeView has internally handled it for node expansion.
        tree.AddHandler(
            DoubleTappedEvent,
            SchemaTree_DoubleTapped,
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        // Wire lazy-loading: when a folder node is expanded, load its children on demand.
        tree.AddHandler(TreeViewItem.ExpandedEvent, OnTreeItemExpanded, RoutingStrategies.Bubble);

        // Update selected node properties panel when selection changes.
        tree.SelectionChanged += (s, e) =>
        {
            if (tree.SelectedItem is SchemaNode node && DataContext is MainWindowViewModel vm)
                vm.SelectedSchemaNode = node;
        };

        tree.ContextMenu!.Opening += (s, e) =>
        {
            if (tree.SelectedItem is SchemaNode node && DataContext is MainWindowViewModel vm)
            {
                SchemaTreeContextMenu.Update(this, vm, node);
            }
        };

        // Ctrl+P opens the command palette
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
    }

    private QueryTabView? ActiveQueryTab =>
        this.FindControl<TabControl>("MainTabControl")?.SelectedContent as QueryTabView;

    private AvaloniaEdit.TextEditor? ActiveEditor => ActiveQueryTab?.QueryEditor;

    private async void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            _unsavedChanges = MainWindowDialogs.Wire(this, vm);
            vm.RequestCloseConfirmation = _unsavedChanges.ConfirmCloseAsync;

            vm.RequestTransactionCloseConfirmation = tab =>
                ConfirmRollingBackTransactionsAsync([tab]);

            vm.ShowCsvImportDialog = async (wizardVm) =>
            {
                var dialog = new CsvImportWizardView { DataContext = wizardVm };
                await dialog.ShowDialog(this);
            };

            vm.ShowProductionConfirmDialog = async (objectName) =>
            {
                var confirmVm = new ProductionConfirmViewModel(objectName);
                var dialog = new ProductionConfirmDialog { DataContext = confirmVm };
                await dialog.ShowDialog(this);
                return confirmVm.Confirmed;
            };

            vm.ShowDestructiveActionConfirmDialog = async preview =>
            {
                var confirmVm = new ProductionConfirmViewModel(preview);
                var dialog = new ProductionConfirmDialog { DataContext = confirmVm };
                await dialog.ShowDialog(this);
                return confirmVm.Confirmed;
            };

            vm.ShowSupportBundleDialog = async draft =>
            {
                var bundleVm = new SupportBundleViewModel(draft);
                var dialog = new SupportBundleDialog { DataContext = bundleVm };
                bundleVm.SaveRequested = async () =>
                {
                    var topLevel = TopLevel.GetTopLevel(dialog);
                    if (topLevel == null)
                        return false;

                    var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                        new Avalonia.Platform.Storage.FilePickerSaveOptions
                        {
                            Title = "Save DbConnect Support Bundle",
                            SuggestedFileName = $"rain-support-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip",
                            FileTypeChoices =
                            [new Avalonia.Platform.Storage.FilePickerFileType("ZIP archive") { Patterns = ["*.zip"] }]
                        });
                    if (file == null)
                        return false;

                    await new SupportBundleService().SaveAsync(draft, file.Path.LocalPath);
                    return true;
                };
                await dialog.ShowDialog(this);
                return bundleVm.Saved;
            };

            vm.ShowGlobalSearch = () =>
            {
                var searchVm = new GlobalSearchViewModel(vm);
                var overlay = new GlobalSearchOverlay { DataContext = searchVm };
                overlay.ShowDialog(this);
            };

            vm.ShowConnectionMonitorConfirmDialog = monitorVm =>
            {
                monitorVm.ShowConfirmDialog = async message =>
                {
                    // Simple confirmation — Avalonia 11 has no built-in dialog.
                    // Return true to allow kill; a proper implementation would show a dialog.
                    return await Task.FromResult(true);
                };
            };

            vm.SnippetPanel.ShowSnippetEditorDialog = async (snippet) =>
            {
                var editorVm = new SnippetEditorDialogViewModel(snippet);
                var dialog = new SnippetEditorDialog { DataContext = editorVm };
                var result = await dialog.ShowDialog<DbConnect.Models.Snippet?>(this);
                return result;
            };

            vm.UndoAction = () => ActiveEditor?.Undo();
            vm.RedoAction = () => ActiveEditor?.Redo();
            vm.FindReplaceAction = () => ActiveQueryTab?.OpenSearch();
            vm.GoToLineFunc = async () =>
            {
                var editor = ActiveEditor;
                if (editor is null) return;
                var dlg = new GoToLineDialog(editor.Document.LineCount);
                var line = await dlg.ShowDialog<int?>(this);
                if (line is > 0)
                {
                    editor.ScrollToLine(line.Value);
                    editor.TextArea.Caret.Line = line.Value;
                    editor.TextArea.Caret.Column = 1;
                }
            };
            vm.ToggleCommentAction = () =>
            {
                if (ActiveEditor is { } editor)
                    SqlEditorCommentBehavior.Toggle(editor);
            };
            vm.ShowDialogFunc = async (dialog) => await dialog.ShowDialog(this);

            vm.ShowQueryParamDialogFunc = async (rvm) =>
            {
                var dialog = new QueryParamDialog { DataContext = rvm };
                return await dialog.ShowDialog<QueryParamDialogResult?>(this);
            };

            await vm.InitializeAsync();
            RestoreLayout(vm);
        }
    }

    /// <summary>Closing a tab with an open transaction rolls its uncommitted changes back, so ask first.</summary>
    private async Task<bool> ConfirmRollingBackTransactionsAsync(IReadOnlyList<QueryTabViewModel> tabs)
    {
        var message = tabs.Count == 1
            ? $"\"{tabs[0].Title}\" has an open transaction with uncommitted changes. Closing it rolls those changes back."
            : $"{tabs.Count} query tabs have open transactions with uncommitted changes. Closing rolls those changes back.";

        var dialog = new ConfirmDialog("Open transaction", message, "Roll back and close", "Keep open");
        return await dialog.ShowDialog<bool>(this);
    }

    private bool _closeConfirmed;
    private UnsavedChangesGuard? _unsavedChanges;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _ = PerformCloseAsync();
    }

    private async Task PerformCloseAsync()
    {
        if (DataContext is MainWindowViewModel vm)
        {
            // Unsaved text first, then open transactions, matching the order when closing one tab.
            if (_unsavedChanges != null && !await _unsavedChanges.ConfirmQuitAsync(vm.Tabs))
                return;

            var openTransactions = vm.Tabs.OfType<QueryTabViewModel>().Where(t => t.IsInTransaction).ToList();
            if (openTransactions.Count > 0 && !await ConfirmRollingBackTransactionsAsync(openTransactions))
                return;

            SaveLayout(vm);
            await vm.OnCleanExitAsync();
            await vm.SaveSessionAsync(wasCleanShutdown: true);
        }

        _closeConfirmed = true;
        Close();
    }

    private void SaveLayout(MainWindowViewModel vm)
    {
        var grid = this.FindControl<Grid>("MainContentGrid");
        double splitterWidth = 250;
        if (grid != null && grid.ColumnDefinitions.Count > 0)
            splitterWidth = grid.ColumnDefinitions[0].ActualWidth;

        vm.SaveLayout(Width, Height, Position.X, Position.Y, splitterWidth);
    }

    private void RestoreLayout(MainWindowViewModel vm)
    {
        var (w, h, x, y, sw) = vm.GetSavedLayout();
        if (!double.IsNaN(x) && !double.IsNaN(y))
            Position = new Avalonia.PixelPoint((int)x, (int)y);
        if (w > 0 && h > 0)
        {
            Width = w;
            Height = h;
        }
        var grid = this.FindControl<Grid>("MainContentGrid");
        if (grid != null && grid.ColumnDefinitions.Count > 0)
        {
            var sidebarCollapsed = vm.GetSidebarCollapsed();
            if (sidebarCollapsed)
                grid.ColumnDefinitions[0].Width = new GridLength(0);
            else if (sw > 0)
                grid.ColumnDefinitions[0].Width = new GridLength(sw);
        }
    }

    private void ToggleSidebar_Click(object? sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar()
    {
        var grid = this.FindControl<Grid>("MainContentGrid");
        if (grid == null) return;
        var col = grid.ColumnDefinitions[0];
        if (col.Width.Value > 0)
        {
            col.Width = new GridLength(0);
            if (DataContext is MainWindowViewModel vm)
            {
                vm.IsSidebarCollapsed = true;
                _ = vm.SaveSidebarStateAsync(true);
            }
        }
        else
        {
            double sw = 250;
            if (DataContext is MainWindowViewModel vm)
            {
                sw = vm.GetSavedLayout().SplitterWidth;
                if (sw <= 0) sw = 250;
                _ = vm.SaveSidebarStateAsync(false);
                vm.IsSidebarCollapsed = false;
            }
            col.Width = new GridLength(sw);
        }
    }

    private void MenuExit_Click(object? sender, RoutedEventArgs e) => Close();

    private void MenuToggleSidebar_Click(object? sender, RoutedEventArgs e) => ToggleSidebar();
    private void MenuToggleSnippetPanel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Sidebar.SnippetsExpanded = !vm.Sidebar.SnippetsExpanded;
    }
    private void MenuThemeLight_Click(object? sender, RoutedEventArgs e) { if (DataContext is MainWindowViewModel vm) vm.SelectedTheme = "Light"; }
    private void MenuThemeDark_Click(object? sender, RoutedEventArgs e) { if (DataContext is MainWindowViewModel vm) vm.SelectedTheme = "Dark"; }
    private void MenuThemeSystem_Click(object? sender, RoutedEventArgs e) { if (DataContext is MainWindowViewModel vm) vm.SelectedTheme = "System"; }
    private void MenuCommandPalette_Click(object? sender, RoutedEventArgs e) { if (DataContext is MainWindowViewModel vm) OpenCommandPalette(vm); }
    private void MenuDisconnect_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            vm.Disconnect(node);
    }
    private async void MenuRefreshSchema_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            var root = vm.SchemaNodes.FirstOrDefault();
            if (root != null) await vm.RefreshSchemaAsync(root);
        }
    }
    private async void MenuImportCsv_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm)
        {
            var node = tree.SelectedItem as SchemaNode ?? vm.SchemaNodes.FirstOrDefault();
            if (node != null) await vm.ImportCsvAsync(node);
        }
    }
    private void MenuDependencyAnalysis_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            vm.OpenDependencyAnalysisTab(node);
    }
    private async void MenuAbout_Click(object? sender, RoutedEventArgs e)
    {
        await new AboutDialog().ShowDialog(this);
    }

    private void MenuCheckUpdates_Click(object? sender, RoutedEventArgs e)
    {
        var url = DataContext is MainWindowViewModel vm ? vm.UpdateCheckUrl : "https://github.com/continuum915/DbConnect/releases";
        OpenUrl(url);
    }

    private void MenuViewOnGitHub_Click(object? sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/continuum915/DbConnect");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Browser launch failures are silently ignored
        }
    }
    private async void CtxNewQueryTab_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.Connections.NewQueryTabAsync(this.FindControl<TreeView>("SchemaTree")!.SelectedItem as SchemaNode);
    }
    private void CtxOpenData_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            vm.OpenTableDataTab(node);
    }
    private void CtxFindInData_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            vm.OpenFindInDataForNode(node);
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            vm.Files.SaveActiveCommand.Execute(null);
        }
        else if (e.Key == Key.OemBackslash && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            ToggleSidebar();
        }
        else if (e.Key == Key.P && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            OpenCommandPalette(vm);
        }
    }

    private void OpenCommandPalette(MainWindowViewModel vm)
    {
        var actions = new[]
        {
            new CommandPaletteViewModel.PaletteAction("New Query Tab", "File", vm.NewQueryTabCommand),
            new CommandPaletteViewModel.PaletteAction("Close Tab", "File", vm.CloseSelectedTabCommand),
            new CommandPaletteViewModel.PaletteAction("Run Query", "Query", vm.ExecuteQueryCommand),
            new CommandPaletteViewModel.PaletteAction("Run Selection", "Query", vm.ExecuteSelectionCommand),
            new CommandPaletteViewModel.PaletteAction("Cancel Execution", "Query", vm.CancelQueryCommand),
            new CommandPaletteViewModel.PaletteAction("Explain Plan", "Query", vm.ExplainQueryCommand),
            new CommandPaletteViewModel.PaletteAction("Schema Diff", "Database", vm.OpenSchemaDiffCommand),
            new CommandPaletteViewModel.PaletteAction("Data Compare", "Database", vm.OpenDataCompareCommand),
            new CommandPaletteViewModel.PaletteAction("Backup / Restore", "Database", vm.OpenBackupRestoreCommand),
            new CommandPaletteViewModel.PaletteAction("Query Builder", "Tools", vm.OpenQueryBuilderCommand),
            new CommandPaletteViewModel.PaletteAction("ER Diagram", "Tools", vm.OpenErDiagramCommand),
            new CommandPaletteViewModel.PaletteAction("Automation", "Tools", vm.OpenAutomationCommand),
            new CommandPaletteViewModel.PaletteAction("User Management", "Tools", vm.OpenUserManagementCommand),
            new CommandPaletteViewModel.PaletteAction("Connection Monitor", "Tools", vm.OpenConnectionMonitorCommand),
            new CommandPaletteViewModel.PaletteAction("Slow Query Dashboard", "Tools", vm.OpenSlowQueryDashboardCommand),
            new CommandPaletteViewModel.PaletteAction("Schema History", "Tools", vm.OpenSchemaHistoryCommand),
            new CommandPaletteViewModel.PaletteAction("Audit Log", "Tools", vm.OpenAuditLogCommand),
            new CommandPaletteViewModel.PaletteAction("Export Support Bundle", "Help", vm.ExportSupportBundleCommand),
        };
        var paletteVm = new CommandPaletteViewModel(actions);
        var paletteWindow = new CommandPaletteView { DataContext = paletteVm };
        paletteWindow.ShowDialog(this);
    }

    private async void OnTreeItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem tvi && tvi.DataContext is SchemaNode node
            && node.IsLazyLoadable && !node.IsLoaded)
        {
            if (DataContext is MainWindowViewModel vm)
                await vm.LoadSchemaNodeChildrenAsync(node);
        }
    }

    private async void AddConnection_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) await ConnectionDialogLauncher.AddConnectionAsync(this, vm);
    }

    private async void SchemaTree_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var tree = this.FindControl<TreeView>("SchemaTree");
        if (tree?.SelectedItem is not SchemaNode node) return;

        switch (node.Type)
        {
            case SchemaNodeType.Table:
            case SchemaNodeType.VirtualTable:
                vm.OpenTableDataTab(node);
                e.Handled = true;
                break;
            case SchemaNodeType.View:
            case SchemaNodeType.MaterializedView:
                vm.OpenEditViewTab(node);
                e.Handled = true;
                break;
            case SchemaNodeType.Procedure:
            case SchemaNodeType.Function:
                await vm.OpenProcedureEditorAsync(node);
                e.Handled = true;
                break;
            case SchemaNodeType.Trigger:
                await vm.OpenDdlTabAsync(node);
                e.Handled = true;
                break;
        }
    }

    private void CtxDesignTable_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            vm.OpenTableDesignerTab(node);
    }

    private async void CtxDropTable_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            await vm.DropObjectAsync(node);
    }

    private async void CtxCopyName_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (tree.SelectedItem is SchemaNode node)
            await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(node.RawName ?? node.Name);
    }

    private void CtxManageIndexes_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            vm.OpenIndexManagerTab(node);
    }

    private void CtxCreateTable_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            vm.OpenTableDesignerTab(node);
    }

    private void CtxCreateView_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            vm.OpenViewDesignerTab(node);
    }

    private void CtxEditView_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            vm.OpenEditViewTab(node);
    }

    private async void CtxCreateRoutine_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            await vm.OpenNewRoutineEditorAsync(node);
    }

    private async void CtxRefreshNode_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            await vm.RefreshNodeAsync(node);
    }

    private async void CtxRefreshConnection_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            await vm.RefreshConnectionAsync(node);
    }

    private void CtxDisconnect_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            vm.Disconnect(node);
    }

    private async void CtxImportCsv_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            await vm.ImportCsvAsync(node);
    }

    private async void CtxImportCsvIntoTable_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            await vm.ImportCsvIntoTableAsync(node);
    }

    private void CtxErDiagram_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            vm.Tools.OpenErDiagramForNode(node);
    }

    private void CtxViewDependencies_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
            vm.OpenDependencyAnalysisTab(node);
    }

    private void CtxCompareData_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.OpenDataCompareCommand.Execute(null);
    }

    private void CtxBackupRestore_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.OpenBackupRestoreCommand.Execute(null);
    }

    private void CtxConnectionMonitor_Click(object? sender, RoutedEventArgs e)
    {
        var tree = this.FindControl<TreeView>("SchemaTree")!;
        if (DataContext is MainWindowViewModel vm && tree.SelectedItem is SchemaNode node)
        {
            var session = vm.GetSessionForNode(node);
            vm.OpenConnectionMonitorCommand.Execute(session);
        }
    }

    private async void CtxRenameGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var tree = this.FindControl<TreeView>("SchemaTree");
        if (tree?.SelectedItem is not SchemaNode { Type: SchemaNodeType.ConnectionGroup } node) return;

        var dialog = new GroupInputDialog("New group name:", node.Name);
        var result = await dialog.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(result) || result == node.Name) return;

        await vm.SchemaTree.Groups.RenameAsync(node.Name, result);
    }

    private async void CtxDeleteGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var tree = this.FindControl<TreeView>("SchemaTree");
        if (tree?.SelectedItem is not SchemaNode { Type: SchemaNodeType.ConnectionGroup } node) return;

        await vm.SchemaTree.Groups.DeleteAsync(node.Name);
    }

    private async void CtxMoveToGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var tree = this.FindControl<TreeView>("SchemaTree");
        if (tree?.SelectedItem is not SchemaNode dbNode) return;

        var groups = vm.SchemaTree.Groups.AvailableGroups;

        var dialog = new GroupPickerDialog(groups);
        var result = await dialog.ShowDialog<string?>(this);
        if (result == null) return;

        await vm.SchemaTree.Groups.MoveAsync(dbNode, result);
    }
}
