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

public partial class ToolLauncher
{
    public void OpenDependencyAnalysisTab(SchemaNode node)
    {
        node = node.RealNode ?? node;
        var session = _schemaTree.FindSessionForNode(node);
        if (session == null) return;

        var objectName = node.RawName ?? node.Name;
        var objectType = node.Type.ToString();

        var vm = new DependencyAnalysisViewModel();
        _tabs.Add(vm);
        _selectTab(vm);
        _ = vm.LoadAsync(session, objectName, objectType);
    }

    [RelayCommand]
    private void OpenFindInData()
    {
        var existing = _tabs.OfType<FindInDataViewModel>().FirstOrDefault();
        if (existing != null) { _selectTab(existing); return; }

        var vm = _createFindInData();
        _tabs.Add(vm);
        _selectTab(vm);
    }

    public void OpenFindInDataForNode(SchemaNode node)
    {
        var session = _schemaTree.FindSessionForNode(node);
        if (session == null) return;

        var existing = _tabs.OfType<FindInDataViewModel>().FirstOrDefault();
        if (existing != null)
        {
            existing.SelectedSession = session;
            _selectTab(existing);
            return;
        }

        var vm = _createFindInData();
        vm.SelectedSession = session;
        _tabs.Add(vm);
        _selectTab(vm);
    }

    [RelayCommand]
    private void OpenSchemaDiff()
    {
        var existing = _tabs.OfType<SchemaDiffViewModel>().FirstOrDefault();
        if (existing != null) { _selectTab(existing); return; }

        var vm = new SchemaDiffViewModel(_sessions);
        vm.RequestOpenInTab = script => OpenScriptInNewTab(script);
        _tabs.Add(vm);
        _selectTab(vm);
    }

    [RelayCommand]
    private void OpenSchemaHistory()
    {
        var existing = _tabs.OfType<SchemaHistoryViewModel>().FirstOrDefault();
        if (existing != null) { _selectTab(existing); return; }

        var vm = new SchemaHistoryViewModel();
        vm.Session = _sessions.FirstOrDefault();
        vm.RequestOpenDiffTab = diffVm =>
        {
            _tabs.Add(diffVm);
            _selectTab(diffVm);
        };
        _tabs.Add(vm);
        _selectTab(vm);
    }

    [RelayCommand]
    private void OpenAuditLog()
    {
        var existing = _tabs.OfType<AuditLogViewModel>().FirstOrDefault();
        if (existing != null) { _selectTab(existing); return; }

        var vm = new AuditLogViewModel(_sessions);
        vm.RequestOpenInTab = script => OpenScriptInNewTab(script);
        _tabs.Add(vm);
        _selectTab(vm);
    }

    [RelayCommand]
    private void OpenDataCompare()
    {
        var existing = _tabs.OfType<DataCompareViewModel>().FirstOrDefault();
        if (existing != null) { _selectTab(existing); return; }

        var vm = new DataCompareViewModel(_sessions);
        vm.RequestOpenInTab = script => OpenScriptInNewTab(script);
        _tabs.Add(vm);
        _selectTab(vm);
    }

    [RelayCommand]
    private void OpenDataTransfer()
    {
        var existing = _tabs.OfType<DataTransferViewModel>().FirstOrDefault();
        if (existing != null) { _selectTab(existing); return; }

        var vm = new DataTransferViewModel(_sessions);
        _tabs.Add(vm);
        _selectTab(vm);
    }

    /// <summary>The Data Transfer wizard with <paramref name="source"/> chosen and the tables <paramref name="tick"/> accepts ticked.</summary>
    public void OpenDataTransferFrom(DatabaseSession source, Func<DatabaseObjectIdentity, bool> tick)
    {
        var vm = _tabs.OfType<DataTransferViewModel>().FirstOrDefault(t => !t.IsBusy);
        if (vm == null)
        {
            vm = new DataTransferViewModel(_sessions);
            _tabs.Add(vm);
        }
        _selectTab(vm);
        _ = vm.StartFromAsync(source, tick);
    }

    [RelayCommand]
    private void OpenBackupRestore()
    {
        var existing = _tabs.OfType<BackupRestoreViewModel>().FirstOrDefault();
        if (existing != null) { _selectTab(existing); return; }

        var vm = new BackupRestoreViewModel(_sessions);
        _tabs.Add(vm);
        _selectTab(vm);
    }

    public void OpenScriptInNewTab(string script)
    {
        var tab = new QueryTabViewModel("Script", _sessions, _queryWorkspaceService);
        tab.QueryText = script;
        tab.ClearDirty();
        _tabs.Add(tab);
        _selectTab(tab);
    }

    [RelayCommand]
    private void OpenAutomation()
    {
        var existing = _tabs.OfType<AutomationViewModel>().FirstOrDefault();
        if (existing != null) { _selectTab(existing); return; }
        var vm = new AutomationViewModel(_sessions);
        _tabs.Add(vm);
        _selectTab(vm);
        _ = vm.LoadJobs();
    }

    [RelayCommand]
    private void OpenUserManagement()
    {
        var session = _sessions.FirstOrDefault();
        var existing = _tabs.OfType<UserManagementViewModel>().FirstOrDefault();
        if (existing != null) { _selectTab(existing); return; }
        var vm = new UserManagementViewModel { Session = session };
        _tabs.Add(vm);
        _selectTab(vm);
    }

    [RelayCommand]
    private void OpenQueryBuilder()
    {
        var vm = new QueryBuilderViewModel { Session = _sessions.FirstOrDefault() };
        foreach (var s in _sessions) vm.AvailableSessions.Add(s);
        _tabs.Add(vm);
        _selectTab(vm);
    }

    [RelayCommand]
    private void OpenMaskingRules()
    {
        var vm = _tabs.OfType<MaskingRulesViewModel>().FirstOrDefault();
        if (vm == null)
        {
            vm = new MaskingRulesViewModel();
            _tabs.Add(vm);
        }
        _selectTab(vm);
    }

    [RelayCommand]
    private void OpenErDiagram() => OpenErDiagramTab(_sessions.FirstOrDefault());

    public void OpenErDiagramForNode(SchemaNode node) =>
        OpenErDiagramTab(_schemaTree.FindSessionForNode(node));

    private void OpenErDiagramTab(DatabaseSession? session)
    {
        var vm = new ErDiagramViewModel { Session = session };
        vm.SetAvailableSessions(_sessions);
        vm.RequestOpenDiffTab = diffVm =>
        {
            diffVm.RequestOpenInTab = script => OpenScriptInNewTab(script);
            _tabs.Add(diffVm);
            _selectTab(diffVm);
        };
        _tabs.Add(vm);
        _selectTab(vm);
    }

    [RelayCommand]
    private void OpenConnectionMonitor(DatabaseSession? session = null)
    {
        session ??= _sessions.FirstOrDefault();
        var vm = new ConnectionMonitorViewModel();
        if (session != null) vm.Session = session;
        _tabs.Add(vm);
        _selectTab(vm);
        ConfigureConnectionMonitor?.Invoke(vm);
    }

    [RelayCommand]
    private void OpenSlowQueryDashboard()
    {
        var existing = _tabs.OfType<SlowQueryDashboardViewModel>().FirstOrDefault();
        if (existing != null) { _selectTab(existing); return; }

        var vm = new SlowQueryDashboardViewModel(_sessions);
        vm.RequestOpenInTab = script => OpenScriptInNewTab(script);
        _tabs.Add(vm);
        _selectTab(vm);
    }
}
