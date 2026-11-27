using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>Wires the explorer's object actions (Empty, Truncate, Rename, Duplicate, Copy DDL, Export Data, Dump SQL File) and their dialogs.</summary>
internal static class ExplorerActionsMenu
{
    /// <summary>Each action runs on the node the menu opened for; Dump SQL File also takes every node selected in the tree.</summary>
    private static readonly (string Name, ExplorerObjectAction Action, Func<ExplorerObjectActions, SchemaNode, IReadOnlyList<SchemaNode>, Task> Run)[] Items =
    [
        ("CtxEmptyTable", ExplorerObjectAction.Empty, (a, n, _) => a.EmptyAsync(n)),
        ("CtxTruncateTable", ExplorerObjectAction.Truncate, (a, n, _) => a.TruncateAsync(n)),
        ("CtxRenameObject", ExplorerObjectAction.Rename, (a, n, _) => a.RenameAsync(n)),
        ("CtxDuplicateWithData", ExplorerObjectAction.Duplicate, (a, n, _) => a.DuplicateAsync(n, includeData: true)),
        ("CtxDuplicateStructure", ExplorerObjectAction.Duplicate, (a, n, _) => a.DuplicateAsync(n, includeData: false)),
        ("CtxCopyDdl", ExplorerObjectAction.CopyDdl, (a, n, _) => a.CopyDdlAsync(n)),
        ("CtxExportData", ExplorerObjectAction.ExportData, (a, n, _) => a.ExportDataAsync(n)),
        ("CtxTransferData", ExplorerObjectAction.TransferData, (a, n, s) => { a.TransferData(n, s); return Task.CompletedTask; }),
        ("CtxDumpSqlStructureAndData", ExplorerObjectAction.DumpSql, (a, n, s) => a.DumpSqlAsync(n, DumpMode.StructureAndData, s)),
        ("CtxDumpSqlStructure", ExplorerObjectAction.DumpSql, (a, n, s) => a.DumpSqlAsync(n, DumpMode.StructureOnly, s))
    ];

    public static void Wire(Window owner, MainWindowViewModel vm)
    {
        foreach (var (name, _, run) in Items)
            if (owner.FindControl<MenuItem>(name) is { } item)
                item.Click += async (_, _) =>
                {
                    var selected = owner.FindControl<TreeView>("SchemaTree")?.SelectedItems?.OfType<SchemaNode>().ToList() ?? [];
                    if (vm.SelectedSchemaNode is { } node) await run(vm.Explorer, node, selected);
                };

        vm.Explorer.PromptForName = async (title, prompt, initial) =>
            await new GroupInputDialog(prompt, initial) { Title = title }.ShowDialog<string?>(owner);
        vm.Explorer.ShowExportDialog = async export =>
        {
            export.PickFolder = async () =>
            {
                var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Export to folder",
                    AllowMultiple = false
                });
                return folders.FirstOrDefault()?.TryGetLocalPath();
            };
            await new ExportDataDialog { DataContext = export }.ShowDialog(owner);
        };
        vm.Explorer.DumpPathPicker = new StorageProviderExportPathPicker(owner, "Dump SQL File");
        vm.Explorer.ShowDumpProgress = operation => new DumpSqlFileDialog { DataContext = operation }.ShowDialog(owner);
        vm.Explorer.ShowDumpTables = choices => new DumpTablesDialog { DataContext = choices }.ShowDialog(owner);
    }

    /// <summary>Applies availability each time the context menu opens.</summary>
    public static void Update(Window window, MainWindowViewModel vm, SchemaNode node)
    {
        foreach (var (name, action, _) in Items)
            if (window.FindControl<MenuItem>(name) is { } item) Apply(item, vm.Explorer.Availability(node, action));
        if (window.FindControl<MenuItem>("CtxDuplicateTable") is { } duplicate)
            Apply(duplicate, vm.Explorer.Availability(node, ExplorerObjectAction.Duplicate));
        if (window.FindControl<MenuItem>("CtxDumpSql") is { } dump)
            Apply(dump, vm.Explorer.Availability(node, ExplorerObjectAction.DumpSql));
    }

    private static void Apply(MenuItem item, SchemaActionAvailability availability)
    {
        item.IsVisible = availability.IsVisible;
        item.IsEnabled = availability.IsEnabled;
        ToolTip.SetTip(item, availability.DisabledReason);
    }
}
