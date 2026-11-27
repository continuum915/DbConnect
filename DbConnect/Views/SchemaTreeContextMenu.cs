using System.Linq;
using Avalonia.Controls;
using DbConnect.Models;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>Applies schema action availability to the window context menu.</summary>
internal static class SchemaTreeContextMenu
{
    public static void Update(Window window, MainWindowViewModel vm, SchemaNode node)
    {
        // Ensure VM has the current right-clicked node
        vm.SelectedSchemaNode = node;
        bool isDb = node.Type == SchemaNodeType.Database;
        bool isTable = node.Type is SchemaNodeType.Table or SchemaNodeType.VirtualTable;
        bool isView = node.Type is SchemaNodeType.View or SchemaNodeType.MaterializedView;
        bool isColumn = node.Type == SchemaNodeType.Column;
        bool isRoutine = node.Type is SchemaNodeType.Function or SchemaNodeType.Procedure or SchemaNodeType.Trigger;
        bool isGroup = node.Type == SchemaNodeType.ConnectionGroup;
        bool hasSession = vm.GetSessionForNode(node) != null;

        var createTable = window.FindControl<MenuItem>("CtxCreateTable")!;
        var designTable = window.FindControl<MenuItem>("CtxDesignTable")!;
        var dropTable = window.FindControl<MenuItem>("CtxDropTable")!;
        var manageIndexes = window.FindControl<MenuItem>("CtxManageIndexes")!;
        var createView = window.FindControl<MenuItem>("CtxCreateView")!;
        var editView = window.FindControl<MenuItem>("CtxEditView")!;
        var createRoutine = window.FindControl<MenuItem>("CtxCreateRoutine")!;
        var scriptAs = window.FindControl<MenuItem>("CtxScriptAs")!;
        var refreshNode = window.FindControl<MenuItem>("CtxRefreshNode")!;
        var refreshConn = window.FindControl<MenuItem>("CtxRefreshConnection")!;
        var importCsv = window.FindControl<MenuItem>("CtxImportCsv")!;
        var importCsvTable = window.FindControl<MenuItem>("CtxImportCsvIntoTable")!;
        var viewDeps = window.FindControl<MenuItem>("CtxViewDependencies")!;
        var compareData = window.FindControl<MenuItem>("CtxCompareData")!;
        var backupRestore = window.FindControl<MenuItem>("CtxBackupRestore")!;
        var findInData = window.FindControl<MenuItem>("CtxFindInData")!;
        var disconnect = window.FindControl<MenuItem>("CtxDisconnect")!;
        var erDiagram = window.FindControl<MenuItem>("CtxErDiagram")!;
        var connMonitor = window.FindControl<MenuItem>("CtxConnectionMonitor")!;
        var renameGroup = window.FindControl<MenuItem>("CtxRenameGroup");
        var deleteGroup = window.FindControl<MenuItem>("CtxDeleteGroup");
        var moveToGroup = window.FindControl<MenuItem>("CtxMoveToGroup");
        var newQueryTab = window.FindControl<MenuItem>("CtxNewQueryTab")!;
        var openData = window.FindControl<MenuItem>("CtxOpenData")!;
        var copyName = window.FindControl<MenuItem>("CtxCopyName")!;

        ApplyActionAvailability(createTable, vm.GetSchemaActionAvailability(node, SchemaContextAction.CreateTable));
        ApplyActionAvailability(designTable, vm.GetSchemaActionAvailability(node, SchemaContextAction.DesignTable));
        ApplyActionAvailability(dropTable, vm.GetSchemaActionAvailability(node, SchemaContextAction.DropObject));
        ApplyActionAvailability(manageIndexes, vm.GetSchemaActionAvailability(node, SchemaContextAction.ManageIndexes));
        ApplyActionAvailability(createView, vm.GetSchemaActionAvailability(node, SchemaContextAction.CreateView));
        ApplyActionAvailability(editView, vm.GetSchemaActionAvailability(node, SchemaContextAction.EditView));
        ApplyActionAvailability(createRoutine, vm.GetSchemaActionAvailability(node, SchemaContextAction.CreateRoutine));
        var scriptCreate = vm.GetSchemaActionAvailability(node, SchemaContextAction.ScriptCreate);
        ExplorerScriptMenu.Populate(scriptAs, vm, node, scriptCreate.IsVisible
            ? scriptCreate : vm.GetSchemaActionAvailability(node, SchemaContextAction.ScriptSelect));
        ApplyActionAvailability(refreshNode, vm.GetSchemaActionAvailability(node, SchemaContextAction.RefreshNode));
        ApplyActionAvailability(refreshConn, vm.GetSchemaActionAvailability(node, SchemaContextAction.RefreshConnection));
        ApplyActionAvailability(importCsv, vm.GetSchemaActionAvailability(node, SchemaContextAction.ImportCsv));
        ApplyActionAvailability(importCsvTable, vm.GetSchemaActionAvailability(node, SchemaContextAction.ImportCsvIntoTable));

        ExplorerActionsMenu.Update(window, vm, node);

        // Items not covered by GetSchemaActionAvailability
        copyName.IsVisible = isColumn || isTable || isView;
        copyName.IsEnabled = true;

        viewDeps.IsVisible = isTable || isView || isRoutine;
        viewDeps.IsEnabled = hasSession;

        compareData.IsVisible = isTable;
        compareData.IsEnabled = hasSession;

        backupRestore.IsVisible = isDb;
        backupRestore.IsEnabled = hasSession;

        findInData.IsVisible = isDb;
        findInData.IsEnabled = hasSession;

        openData.IsVisible = isTable;
        openData.IsEnabled = true;

        newQueryTab.IsVisible = isDb;
        newQueryTab.IsEnabled = true;

        connMonitor.IsVisible = isDb;
        connMonitor.IsEnabled = true;

        erDiagram.IsVisible = isDb;
        erDiagram.IsEnabled = hasSession;

        disconnect.IsVisible = isDb;
        disconnect.IsEnabled = true;
        ToolTip.SetTip(disconnect, null);

        if (renameGroup != null) { renameGroup.IsVisible = isGroup; renameGroup.IsEnabled = true; }
        if (deleteGroup != null) { deleteGroup.IsVisible = isGroup; deleteGroup.IsEnabled = true; }
        if (moveToGroup != null) { moveToGroup.IsVisible = isDb; moveToGroup.IsEnabled = true; }

        // Dynamic header for drop action
        dropTable.Header = isView ? "Drop View…" : "Drop Object…";

        if (window.FindControl<TreeView>("SchemaTree")?.ContextMenu is { } menu)
            CollapseSeparators(menu);
    }

    /// <summary>
    /// Shows a separator only between two visible groups: leading and trailing separators are hidden, and of a run of
    /// separators (groups with nothing visible between them) only the last is kept.
    /// </summary>
    internal static void CollapseSeparators(ContextMenu menu)
    {
        var children = menu.Items.OfType<Control>().ToList();
        foreach (var separator in children.OfType<Separator>())
            separator.IsVisible = true;

        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is not Separator separator) continue;
            var itemBefore = children.Take(i).Any(c => c is not Separator && c.IsVisible);
            var next = children.Skip(i + 1).FirstOrDefault(c => c.IsVisible);
            separator.IsVisible = itemBefore && next is not null and not Separator;
        }
    }

    private static void ApplyActionAvailability(MenuItem menuItem, SchemaActionAvailability availability)
    {
        menuItem.IsVisible = availability.IsVisible;
        menuItem.IsEnabled = availability.IsEnabled;
        ToolTip.SetTip(menuItem, availability.DisabledReason);
    }

}
