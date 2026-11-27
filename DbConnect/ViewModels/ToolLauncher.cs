using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Views;

namespace DbConnect.ViewModels;

public partial class ToolLauncher : ObservableObject
{
    private readonly ObservableCollection<ViewModelBase> _tabs;
    private readonly ObservableCollection<DatabaseSession> _sessions;
    private readonly SchemaTreeViewModel _schemaTree;
    private readonly Dictionary<SchemaNode, DatabaseSession> _activeSessions;
    private readonly Dictionary<SchemaNode, SchemaIndex> _indexes;
    private readonly QueryWorkspaceService _queryWorkspaceService;
    private readonly Action<ViewModelBase> _selectTab;
    private readonly Func<FindInDataViewModel> _createFindInData;

    public Action<string, string> ReportError { get; set; } = (_, _) => { };
    public Action<string> ReportStatus { get; set; } = _ => { };
    public Func<string, Task>? CopyToClipboardAsync { get; set; }
    /// <summary>Asks where to save a generated script; null when the user cancels.</summary>
    public Func<string, Task<string?>>? PickScriptPathAsync { get; set; }
    public Func<Window, Task>? ShowDialogAsync { get; set; }
    public Action<ConnectionMonitorViewModel>? ConfigureConnectionMonitor { get; set; }

    public ToolLauncher(
        ObservableCollection<ViewModelBase> tabs,
        ObservableCollection<DatabaseSession> sessions,
        SchemaTreeViewModel schemaTree,
        Dictionary<SchemaNode, DatabaseSession> activeSessions,
        Dictionary<SchemaNode, SchemaIndex> indexes,
        QueryWorkspaceService queryWorkspaceService,
        Action<ViewModelBase> selectTab,
        Func<FindInDataViewModel> createFindInData)
    {
        _tabs = tabs;
        _sessions = sessions;
        _schemaTree = schemaTree;
        _activeSessions = activeSessions;
        _indexes = indexes;
        _queryWorkspaceService = queryWorkspaceService;
        _selectTab = selectTab;
        _createFindInData = createFindInData;
    }

    public void OpenTableDataTab(SchemaNode tableNode)
    {
        tableNode = tableNode.RealNode ?? tableNode;
        if (tableNode.Type is not (SchemaNodeType.Table or SchemaNodeType.VirtualTable or SchemaNodeType.View or SchemaNodeType.MaterializedView)) return;

        var tableName = tableNode.RawName ?? tableNode.Name;

        // Resolve the connection for this node (find the root ancestor in ActiveConnections)
        DatabaseSession? session = _schemaTree.FindSessionForNode(tableNode);
        if (session == null) return;
        var identity = _schemaTree.GetIdentity(tableNode, session);

        // Switch to existing tab only when the same session/object is already open.
        var existing = _tabs.OfType<TableDataTabViewModel>()
                           .FirstOrDefault(t =>
                               ReferenceEquals(t.Session, session) &&
                               session.Objects.Same(t.ObjectSelection.Identity, identity));
        if (existing != null)
        {
            _selectTab(existing);
            return;
        }

        var vm = new TableDataTabViewModel(tableName, session, _queryWorkspaceService, tableNode);

        _tabs.Add(vm);
        _selectTab(vm);
    }

    public async Task OpenTableDataTabWithFilter(SchemaNode tableNode, string filterColumn, string filterValue)
    {
        tableNode = tableNode.RealNode ?? tableNode;
        if (tableNode.Type is not (SchemaNodeType.Table or SchemaNodeType.VirtualTable or SchemaNodeType.View or SchemaNodeType.MaterializedView)) return;

        var tableName = tableNode.RawName ?? tableNode.Name;
        DatabaseSession? session = _schemaTree.FindSessionForNode(tableNode);
        if (session == null) return;
        var identity = _schemaTree.GetIdentity(tableNode, session);

        // Switch to existing tab if same session/object.
        var existing = _tabs.OfType<TableDataTabViewModel>()
                           .FirstOrDefault(t =>
                               ReferenceEquals(t.Session, session) &&
                               session.Objects.Same(t.ObjectSelection.Identity, identity));
        if (existing != null)
        {
            _selectTab(existing);
            await existing.ApplyColumnFilterAsync(filterColumn, filterValue);
            return;
        }

        var vm = new TableDataTabViewModel(tableName, session, _queryWorkspaceService, tableNode);
        vm.PresetFilter(filterColumn, TableFilterOperator.Equals, filterValue);

        _tabs.Add(vm);
        _selectTab(vm);
    }

    public void OpenTableDesignerTab(SchemaNode node)
    {
        node = node.RealNode ?? node;
        DatabaseSession? session = _schemaTree.FindSessionForNode(node);
        if (session == null) return;

        TableDesignerViewModel vm;

        if (node.Type == SchemaNodeType.Table)
        {
            var tableName = node.RawName ?? node.Name;
            var identity = _schemaTree.GetIdentity(node, session);
            var existing = _tabs.OfType<TableDesignerViewModel>()
                               .FirstOrDefault(t => ReferenceEquals(t.Session, session) && t.Design.Table is { } open && session.Objects.Same(open, identity));
            if (existing != null)
            {
                _selectTab(existing);
                return;
            }
            vm = new TableDesignerViewModel(tableName, session, node, identity);
        }
        else
        {
            // Database node - Create NEW table
            vm = new TableDesignerViewModel("new_table", session);
        }

        // Another tab's open transaction may hold locks the schema change needs; the designer waits rather than end it.
        vm.OpenTransactions = () => _tabs.OfType<QueryTabViewModel>()
            .Where(t => t.IsInTransaction && t.SelectedSession is { } s
                && (ReferenceEquals(s, session) || s.ConnectionString == session.ConnectionString))
            .Select(t => t.Title).ToList();
        _tabs.Add(vm);
        _selectTab(vm);
    }

    public void OpenIndexManagerTab(SchemaNode tableNode)
    {
        tableNode = tableNode.RealNode ?? tableNode;
        if (tableNode.Type != SchemaNodeType.Table) return;

        DatabaseSession? session = _schemaTree.FindSessionForNode(tableNode);
        if (session == null) return;
        if (!session.Capabilities.SupportsIndexes)
        {
            ReportError("⚠️ This provider does not support index management in the current workflow.", "Index management not supported for this provider.");
            return;
        }

        var vm = new IndexManagerViewModel(tableNode, session);
        _tabs.Add(vm);
        _selectTab(vm);
    }

    public void OpenViewDesignerTab(SchemaNode node)
    {
        node = node.RealNode ?? node;
        DatabaseSession? session = _schemaTree.FindSessionForNode(node);
        if (session == null) return;
        if (!session.Capabilities.SupportsViews)
        {
            ReportError("⚠️ This provider does not support view operations in the current workflow.", "View operations not supported for this provider.");
            return;
        }

        var vm = new ViewDesignerViewModel(session);
        _tabs.Add(vm);
        _selectTab(vm);
    }

    public void OpenEditViewTab(SchemaNode node)
    {
        node = node.RealNode ?? node;
        DatabaseSession? session = _schemaTree.FindSessionForNode(node);
        if (session == null) return;
        if (!session.Capabilities.SupportsViews)
        {
            ReportError("⚠️ This provider does not support view operations in the current workflow.", "View operations not supported for this provider.");
            return;
        }

        var vmTitle = $"Edit View: {node.Name}";
        var existing = _tabs.OfType<ViewDesignerViewModel>().FirstOrDefault(t => t.Title == vmTitle);
        if (existing != null)
        {
            _selectTab(existing);
            return;
        }

        var vm = new ViewDesignerViewModel(node, session);
        _tabs.Add(vm);
        _selectTab(vm);
    }

    public async Task OpenProcedureEditorAsync(SchemaNode node)
    {
        node = node.RealNode ?? node;
        var session = _schemaTree.FindSessionForNode(node);
        if (session == null)
            return;

        if (node.Type == SchemaNodeType.Function && !session.Capabilities.SupportsFunctions)
        {
            ReportError("⚠️ This provider does not support function editing in the current workflow.", "Function editing not supported for this provider.");
            return;
        }

        if (node.Type == SchemaNodeType.Procedure && !session.Capabilities.SupportsProcedures)
        {
            ReportError("⚠️ This provider does not support procedure editing in the current workflow.", "Procedure editing not supported for this provider.");
            return;
        }

        var routineName = node.RawName ?? node.Name;
        var schema = node.SchemaPath ?? node.SchemaName ?? string.Empty;
        var title = string.IsNullOrWhiteSpace(schema) ? node.Name : $"{schema}.{node.Name}";
        var existing = _tabs.OfType<ProcedureEditorViewModel>()
            .FirstOrDefault(tab => tab.Session == session && string.Equals(tab.Title, title, StringComparison.Ordinal));
        if (existing != null)
        {
            _selectTab(existing);
            return;
        }

        var vm = new ProcedureEditorViewModel(session, node.Type, title, routineName, schema);
        vm.ShowRunnerFunc = async (rvm) =>
        {
            var dialog = new ProcedureRunnerDialog { DataContext = rvm };
            await ShowDialogAsync!(dialog);
        };
        _tabs.Add(vm);
        _selectTab(vm);
        await vm.LoadAsync();
    }

    public async Task OpenNewRoutineEditorAsync(SchemaNode node)
    {
        node = node.RealNode ?? node;
        var session = _schemaTree.FindSessionForNode(node);
        if (session == null)
            return;

        if (!session.Capabilities.SupportsProcedures && !session.Capabilities.SupportsFunctions)
        {
            ReportError("⚠️ This provider does not support routine editing in the current workflow.", "Routine editing not supported for this provider.");
            return;
        }

        var schema = ResolveRoutineSchema(node, session);
        var routineType = session.Capabilities.SupportsProcedures ? SchemaNodeType.Procedure : SchemaNodeType.Function;
        var routineName = "new_routine";
        var title = string.IsNullOrWhiteSpace(schema) ? routineName : $"{schema}.{routineName}";
        var existing = _tabs.OfType<ProcedureEditorViewModel>()
            .FirstOrDefault(tab => tab.Session == session && string.Equals(tab.Title, title, StringComparison.Ordinal));
        if (existing != null)
        {
            _selectTab(existing);
            return;
        }

        var vm = new ProcedureEditorViewModel(session, routineType, title, routineName, schema, isNewRoutine: true);
        vm.ShowRunnerFunc = async (rvm) =>
        {
            var dialog = new ProcedureRunnerDialog { DataContext = rvm };
            await ShowDialogAsync!(dialog);
        };
        _tabs.Add(vm);
        _selectTab(vm);
        await vm.LoadAsync();
    }

    private string ResolveRoutineSchema(SchemaNode node, DatabaseSession session)
    {
        var target = node.RealNode ?? node;
        if (!string.IsNullOrWhiteSpace(target.SchemaPath))
            return target.SchemaPath!;

        if (target.Type == SchemaNodeType.Schema)
            return target.RawName ?? target.Name;

        foreach (var root in _activeSessions.Keys)
        {
            if (!SchemaTreeViewModel.IsDescendant(root, target))
                continue;

            SchemaNode? current = target;
            while (current != null)
            {
                if (current.Type == SchemaNodeType.Schema)
                    return current.RawName ?? current.Name;

                current = SchemaTreeViewModel.FindParentOf(root, current);
            }
        }

        return session.Capabilities.DefaultSchema;
    }
}
