using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Views;

namespace DbConnect.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly DatabaseService _dbService;
    private readonly QueryWorkspaceService _queryWorkspaceService;
    private readonly QueryParamContextService _queryParamContextService = new();
    private readonly SettingsService? _settingsService;
    private readonly SupportBundleService _supportBundleService = new();
    private AppSettings _lastSettings => _workspacePersistence.Settings;

    public SchemaTreeViewModel SchemaTree { get; }
    public ObservableCollection<SchemaNode> SchemaNodes => SchemaTree.SchemaNodes;
    public ObservableCollection<SchemaNode> FilteredSchemaNodes => SchemaTree.FilteredSchemaNodes;
    public string SchemaSearchText
    {
        get => SchemaTree.SchemaSearchText;
        set => SchemaTree.SchemaSearchText = value;
    }
    public string SchemaFilterMatchCount => SchemaTree.SchemaFilterMatchCount;

    [ObservableProperty]
    private SchemaNode? _selectedSchemaNode;

    // ── Context-menu visibility helpers ──────────────────────────────────────

    public bool CtxIsDatabase => SelectedSchemaNode?.Type == SchemaNodeType.Database;
    public bool CtxIsTable => SelectedSchemaNode?.Type is SchemaNodeType.Table or SchemaNodeType.VirtualTable;
    public bool CtxIsView => SelectedSchemaNode?.Type is SchemaNodeType.View or SchemaNodeType.MaterializedView;
    public bool CtxIsColumn => SelectedSchemaNode?.Type == SchemaNodeType.Column;
    public bool CtxIsTableOrView => CtxIsTable || CtxIsView;
    public bool CtxIsRoutine => SelectedSchemaNode?.Type is SchemaNodeType.Function
                                        or SchemaNodeType.Procedure or SchemaNodeType.Trigger;
    public bool CtxIsFolder => SelectedSchemaNode?.Type is SchemaNodeType.TableFolder
                                        or SchemaNodeType.ViewFolder or SchemaNodeType.IndexFolder
                                        or SchemaNodeType.TriggerFolder or SchemaNodeType.FunctionFolder
                                        or SchemaNodeType.ProcedureFolder or SchemaNodeType.MaterializedViewFolder
                                        or SchemaNodeType.SequenceFolder or SchemaNodeType.ExtensionFolder
                                        or SchemaNodeType.RoleFolder or SchemaNodeType.ForeignKeyFolder
                                        or SchemaNodeType.DatabaseInfoFolder;
    public bool CtxIsConnectionGroup => SelectedSchemaNode?.Type == SchemaNodeType.ConnectionGroup;
    public bool CtxCanScript => CtxIsTableOrView || CtxIsRoutine;

    /// <summary>
    /// Holds both <see cref="QueryTabViewModel"/> and <see cref="TableDataTabViewModel"/> instances.
    /// The ViewLocator resolves the correct view for each at render time.
    /// </summary>
    public ObservableCollection<ViewModelBase> Tabs { get; } = new();
    public TabStripActions TabActions { get; }
    public SqlFileCommands Files { get; }
    /// <summary>Main toolbar, status bar, Favorites and Window menus and the window title.</summary>
    public WindowFrame Frame { get; }

    [ObservableProperty]
    private ViewModelBase? _selectedTab;

    [ObservableProperty]
    private bool _isExecuting;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _statusConnectionText = "No connection";

    [ObservableProperty]
    private string _statusQueryText = "Idle";

    [ObservableProperty]
    private string _statusErrorText = string.Empty;

    [ObservableProperty]
    private bool _statusHasError;

    // ── Environment Tagging ───────────────────────────────────────────────────

    [ObservableProperty]
    private string _activeEnvironmentTier = "Development";

    [ObservableProperty]
    private bool _isProductionActive;

    [ObservableProperty]
    private string _environmentIndicatorText = string.Empty;

    // ── Update banner ─────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isUpdateBannerVisible;

    [ObservableProperty]
    private string _updateBannerText = string.Empty;

    [ObservableProperty]
    private string _updateBannerUrl = string.Empty;

    /// <summary>The sidebar's Properties/Snippets region: its height and which sections are expanded.</summary>
    public SidebarState Sidebar { get; } = new();

    // ── Theme ─────────────────────────────────────────────────────────────────

    public string[] ThemeOptions { get; } = ["Light", "Dark", "System"];

    [ObservableProperty]
    private string _selectedTheme = "Light";

    partial void OnSelectedThemeChanged(string value)
    {
        ApplyTheme(value);
        _ = SaveSettingsAsync();
    }

    [RelayCommand]
    private void DismissUpdateBanner()
    {
        IsUpdateBannerVisible = false;
    }

    private static void ApplyTheme(string theme)
    {
        Application.Current!.RequestedThemeVariant = theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Light
        };
    }

    // ── Close-tab confirmation delegate ──────────────────────────────────────
    public Func<ViewModelBase, Task<bool>>? RequestCloseConfirmation { get; set; }

    /// <summary>
    /// Asked before closing a query tab that has an open transaction; closing rolls it back.
    /// With no handler the tab stays open, since there is no way to ask.
    /// </summary>
    public Func<QueryTabViewModel, Task<bool>>? RequestTransactionCloseConfirmation { get; set; }

    /// <summary>Status-bar text for the selected query tab's open transaction; empty when there is none.</summary>
    public string StatusTransactionText => (SelectedTab as QueryTabViewModel)?.TransactionStatusText ?? string.Empty;

    // ── CSV Import dialog delegate ────────────────────────────────────────────
    public Func<CsvImportWizardViewModel, Task>? ShowCsvImportDialog { get; set; }

    public Func<SupportBundleDraft, Task<bool>>? ShowSupportBundleDialog { get; set; }

    // ── Production confirmation delegate ─────────────────────────────────────
    /// <summary>Called with the object name; returns true when confirmed.</summary>
    public Func<string, Task<bool>>? ShowProductionConfirmDialog { get; set; }
    public Func<DestructiveActionPreview, Task<bool>>? ShowDestructiveActionConfirmDialog { get; set; }

    // ── Drop with dependencies confirmation delegate ──────────────────────────
    /// <summary>Called with objectName and dependency list; returns true when confirmed.</summary>
    public Func<string, IReadOnlyList<ObjectDependency>, Task<bool>>? ShowDropConfirmationWithDependencies { get; set; }

    // ── Global Search delegate ────────────────────────────────────────────────
    public Action? ShowGlobalSearch { get; set; }

    // ── Generic dialog delegate ──────────────────────────────────────────────
    public Func<Window, Task>? ShowDialogFunc { get; set; }

    // ── Parameter dialog delegate ────────────────────────────────────────────
    public Func<QueryParamDialogViewModel, Task<QueryParamDialogResult?>>? ShowQueryParamDialogFunc { get; set; }

    // ── Editor operation callbacks (injected from MainWindow code-behind) ─────
    public Action? UndoAction { get; set; }
    public Action? RedoAction { get; set; }
    public Action? FindReplaceAction { get; set; }
    public Func<Task>? GoToLineFunc { get; set; }
    public Action? ToggleCommentAction { get; set; }

    [RelayCommand]
    private void UndoActiveTab() => UndoAction?.Invoke();

    [RelayCommand]
    private void RedoActiveTab() => RedoAction?.Invoke();

    [RelayCommand]
    private void FindReplaceActiveTab() => FindReplaceAction?.Invoke();

    [RelayCommand]
    private async Task GoToLineActiveTab() => await (GoToLineFunc?.Invoke() ?? Task.CompletedTask);

    [RelayCommand]
    private void ToggleCommentActiveTab() => ToggleCommentAction?.Invoke();

    [RelayCommand]
    private void FormatSqlActiveTab()
    {
        if (SelectedTab is QueryTabViewModel qt)
            qt.FormatSqlCommand.Execute(null);
    }

    // Active connection dictionary mapping root DB node to ConnectionInfo
    public System.Collections.Generic.Dictionary<SchemaNode, ConnectionInfo> ActiveConnections { get; } = new();

    public System.Collections.Generic.Dictionary<SchemaNode, DatabaseSession> ActiveSessions { get; } = new();

    public ObservableCollection<DatabaseSession> ActiveSessionOptions { get; } = new();

    // Background indexed mapping of schemas
    public System.Collections.Generic.Dictionary<SchemaNode, SchemaIndex> ActiveIndexes { get; } = new();

    // Established connections keyed by root node; each owns its tunnel and is disposed on disconnect
    public System.Collections.Generic.Dictionary<SchemaNode, EstablishedConnection> EstablishedConnections { get; } = new();

    private readonly WorkspacePersistence _workspacePersistence;

    public ToolLauncher Tools { get; }
    public ExplorerObjectActions Explorer { get; }
    public ExplorerConnections Connections { get; }

    // ── Snippet Panel ─────────────────────────────────────────────────────────
    public SnippetPanelViewModel SnippetPanel { get; } = new SnippetPanelViewModel();

    public bool HasNoConnections => !SchemaNodes.Any();

    /// <summary>No document is open: the tab area shows how to start one instead of an empty query.</summary>
    public bool HasNoTabs => Tabs.Count == 0;

    public MainWindowViewModel(DatabaseService dbService, QueryWorkspaceService? queryWorkspaceService = null, SettingsService? settingsService = null)
    {
        _dbService = dbService;
        _queryWorkspaceService = queryWorkspaceService ?? new QueryWorkspaceService();
        _settingsService = settingsService;
        _workspacePersistence = new WorkspacePersistence(
            Tabs, ActiveSessionOptions, () => SelectedTab, _queryWorkspaceService, settingsService);
        SchemaTree = new SchemaTreeViewModel(ActiveConnections, ActiveSessions, ActiveIndexes, Tabs)
        {
            ReportError = message =>
            {
                StatusMessage = $"❌ {message}";
                StatusErrorText = message;
                StatusHasError = true;
            }
        };
        SchemaTree.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        TabActions = new TabStripActions(Tabs, tab => CloseTabCommand.ExecuteAsync(tab), () => SelectedTab, tab => SelectedTab = tab);
        Files = new SqlFileCommands(Tabs, () => SelectedTab, tab => SelectedTab = tab, sql => NewQueryTabWithContent(sql));
        Frame = new WindowFrame(this, Tabs, () => SelectedTab, tab => SelectedTab = tab, GetActiveTabSession,
            () => SelectedSchemaNode, sql => NewQueryTabWithContent(sql), _queryWorkspaceService);
        Explorer = new ExplorerObjectActions(this);
        Connections = new ExplorerConnections(this, dbService);
        Tools = new ToolLauncher(Tabs, ActiveSessionOptions, SchemaTree, ActiveSessions, ActiveIndexes,
            _queryWorkspaceService, tab => SelectedTab = tab,
            () => new FindInDataViewModel(ActiveSessionOptions, this))
        {
            ReportError = (message, error) =>
            {
                StatusMessage = message;
                StatusErrorText = error;
                StatusHasError = true;
            },
            ShowDialogAsync = dialog => ShowDialogFunc!(dialog),
            ConfigureConnectionMonitor = vm => ShowConnectionMonitorConfirmDialog?.Invoke(vm)
        };

        // Wire snippet insert → active query tab
        SnippetPanel.OnSnippetInsertRequested += text =>
        {
            if (SelectedTab is QueryTabViewModel qt)
                qt.RequestInsertText(text);
        };

        // Debounced recovery snapshot on tab collection changes
        Tabs.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (QueryTabViewModel tab in e.NewItems.OfType<QueryTabViewModel>())
                    tab.PropertyChanged += OnTabPropertyChanged;
            }
            if (e.OldItems != null)
            {
                foreach (QueryTabViewModel tab in e.OldItems.OfType<QueryTabViewModel>())
                    tab.PropertyChanged -= OnTabPropertyChanged;
            }
            _workspacePersistence.ScheduleSnapshotWrite();
            OnPropertyChanged(nameof(HasNoTabs));
        };

        SchemaNodes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoConnections));
        };

        SchemaTree.UpdateFilteredSchemaNodes();
    }

    public MainWindowViewModel() : this(new DatabaseService()) { } // Designer

    // ── Tab Management ────────────────────────────────────────────────────────

    [RelayCommand]
    private void NewQueryTab() => NewQueryTabWithSession();

    /// <summary>Opens a new query tab optionally bound to an existing session.</summary>
    public QueryTabViewModel NewQueryTabWithSession(DatabaseSession? session = null)
    {
        var tab = new QueryTabViewModel(
            $"Query {Tabs.Count + 1}",
            ActiveSessionOptions,
            _queryWorkspaceService,
            SlowQueryLogService.Default);

        SchemaTree.ApplyKnownSessionIndexes(tab);

        if (session != null)
        {
            tab.SelectedSession = session;

            var rootNode = SchemaTree.FindRootNodeForSession(session);
            if (rootNode != null && ActiveIndexes.TryGetValue(rootNode, out var index))
                tab.SetSessionIndex(session, index);
        }

        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    public QueryTabViewModel NewQueryTabWithContent(string sql, DatabaseSession? session = null)
    {
        var tab = NewQueryTabWithSession(session);
        tab.QueryText = sql;
        tab.ClearDirty();
        return tab;
    }

    public TableDataTabViewModel NewTableDataTabWithSession(DatabaseSession session, string tableName)
    {
        var tab = new TableDataTabViewModel(tableName, session, _queryWorkspaceService, null);
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    [RelayCommand]
    private async Task CloseSelectedTab()
    {
        if (SelectedTab != null)
            await CloseTab(SelectedTab);
    }

    [RelayCommand]
    private async Task CloseTab(ViewModelBase tab)
    {
        if (tab.IsDirty && RequestCloseConfirmation != null)
        {
            var confirmed = await RequestCloseConfirmation(tab);
            if (!confirmed) return;
        }

        if (!await ConfirmDiscardingTransactionAsync(tab))
            return;
        if (tab is QueryTabViewModel { ResultEditor: var resultEditor } && !await resultEditor.ConfirmDiscardingChangesAsync())
            return;

        if (tab is ErDiagramViewModel diagramViewModel)
            await diagramViewModel.SaveWorkspaceAsync();

        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        Tabs.RemoveAt(index);
        await TabStripActions.ReleaseSessionAsync(tab);

        if (ReferenceEquals(SelectedTab, tab))
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    /// <summary>True when the tab can close: it has no open transaction, or the user agreed to roll it back.</summary>
    private async Task<bool> ConfirmDiscardingTransactionAsync(ViewModelBase tab)
    {
        if (tab is not QueryTabViewModel { IsInTransaction: true } queryTab)
            return true;

        return RequestTransactionCloseConfirmation != null &&
               await RequestTransactionCloseConfirmation(queryTab);
    }

    // ── Settings / Layout ─────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        if (_settingsService == null) return;
        var settings = await _settingsService.LoadAsync();
        _workspacePersistence.Attach(settings, Files.Recent);
        // Set via property to fire notification, but guard against double-save
        // by directly applying theme after setting the backing value.
        SelectedTheme = settings.ThemeVariant;
        IsSidebarCollapsed = settings.SidebarCollapsed;
        Sidebar.Attach(settings, () => _ = _settingsService.SaveAsync(settings));
        // Fire-and-forget update check — must not block UI startup
        _ = CheckForUpdatesAsync(settings.UpdateCheckUrl);

        // Load snippets
        await SnippetPanel.LoadAsync();

        // Reopen the last session's tabs, then any unsaved work from an instance that did not close cleanly.
        WorkspaceSessionRestorer.Restore(this, await _workspacePersistence.LoadSavedSessionAsync());
        var recovered = await _workspacePersistence.RecoverCrashedSessionsAsync(s => CrashRecoveryRestorer.Apply(this, s));
        if (recovered > 0)
            StatusMessage = $"Recovered {recovered} unsaved tab{(recovered == 1 ? "" : "s")} from a session that did not close cleanly.";
    }

    private async Task CheckForUpdatesAsync(string updateUrl)
    {
        try
        {
            var svc = new UpdateCheckService();
            var update = await svc.CheckAsync(updateUrl);
            if (update == null) return;

            var current = UpdateCheckService.CurrentVersion();
            if (System.Version.TryParse(UpdateCheckService.StripPreRelease(update.Version), out var available) &&
                System.Version.TryParse(UpdateCheckService.StripPreRelease(current), out var running) &&
                available > running)
            {
                UpdateBannerText = $"DbConnect {update.Version} is available.";
                UpdateBannerUrl = update.ReleaseUrl;
                IsUpdateBannerVisible = true;
            }
        }
        catch { /* update check must never crash the app */ }
    }

    private async Task SaveSettingsAsync()
    {
        if (_settingsService == null) return;
        _lastSettings.ThemeVariant = SelectedTheme;
        await _settingsService.SaveAsync(_lastSettings);
    }

    public void SaveLayout(double width, double height, double x, double y, double splitterWidth)
        => _workspacePersistence.SaveLayout(width, height, x, y, splitterWidth);

    public (double Width, double Height, double X, double Y, double SplitterWidth) GetSavedLayout()
        => _workspacePersistence.GetSavedLayout();

    public string UpdateCheckUrl => string.IsNullOrEmpty(_lastSettings.UpdateCheckUrl)
        ? "https://github.com/continuum915/DbConnect/releases"
        : _lastSettings.UpdateCheckUrl;

    public Task SaveSidebarStateAsync(bool collapsed)
        => _workspacePersistence.SaveSidebarStateAsync(collapsed);

    public bool GetSidebarCollapsed() => _workspacePersistence.GetSidebarCollapsed();

    /// <summary>
    /// Opens (or switches to an existing) Table Data tab for the given schema node.
    /// Called from MainWindow.axaml.cs when a table-like node is double-clicked.
    /// </summary>
    public void OpenTableDataTab(SchemaNode tableNode) => Tools.OpenTableDataTab(tableNode);

    public Task OpenTableDataTabWithFilter(SchemaNode tableNode, string filterColumn, string filterValue) => Tools.OpenTableDataTabWithFilter(tableNode, filterColumn, filterValue);

    public void OpenTableDesignerTab(SchemaNode node) => Tools.OpenTableDesignerTab(node);

    public void OpenIndexManagerTab(SchemaNode tableNode) => Tools.OpenIndexManagerTab(tableNode);

    public void OpenViewDesignerTab(SchemaNode node) => Tools.OpenViewDesignerTab(node);

    public void OpenEditViewTab(SchemaNode node) => Tools.OpenEditViewTab(node);

    public Task OpenProcedureEditorAsync(SchemaNode node) => Tools.OpenProcedureEditorAsync(node);

    public Task OpenNewRoutineEditorAsync(SchemaNode node) => Tools.OpenNewRoutineEditorAsync(node);

    public Task OpenDdlTabAsync(SchemaNode node) => Tools.OpenDdlTabAsync(node);

    public void ScriptTableAsSelect(SchemaNode node) => Tools.ScriptTableAsSelect(node);

    public Task ScriptObjectAsCreate(SchemaNode node) => Tools.ScriptObjectAsCreate(node);

    public Task ScriptObjectAsDrop(SchemaNode node) => Tools.ScriptObjectAsDrop(node);

    public void ScriptPragma(SchemaNode node) => Tools.ScriptPragma(node);

    // ── Dependency Analysis ───────────────────────────────────────────────────

    public void OpenDependencyAnalysisTab(SchemaNode node) => Tools.OpenDependencyAnalysisTab(node);

    // ── Find in Data ───────────────────────────────────────────────────────────

    public IRelayCommand OpenFindInDataCommand => Tools.OpenFindInDataCommand;

    public void OpenFindInDataForNode(SchemaNode node) => Tools.OpenFindInDataForNode(node);

    // ── Schema Diff ───────────────────────────────────────────────────────────

    public IRelayCommand OpenSchemaDiffCommand => Tools.OpenSchemaDiffCommand;

    // ── Schema History ────────────────────────────────────────────────────────

    public IRelayCommand OpenSchemaHistoryCommand => Tools.OpenSchemaHistoryCommand;

    [RelayCommand]
    private async Task TakeSchemaSnapshot()
    {
        var session = ActiveSessionOptions.FirstOrDefault();
        if (session == null) { StatusMessage = "No active connection."; return; }
        var svc = new DbConnect.Services.SchemaSnapshotService();
        try
        {
            var snap = await svc.TakeSnapshotAsync(session, $"Quick snapshot {DateTimeOffset.UtcNow:HH:mm:ss}");
            StatusMessage = $"Snapshot taken: {snap.ObjectCount} objects.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Snapshot error: {ex.Message}";
        }
    }

    // ── Audit Log ─────────────────────────────────────────────────────────────

    public IRelayCommand OpenAuditLogCommand => Tools.OpenAuditLogCommand;

    [RelayCommand]
    private async Task ExportSupportBundle()
    {
        try
        {
            var draft = await _supportBundleService.BuildDraftAsync(new SupportBundleOptions
            {
                SettingsSummary = new Dictionary<string, string?>
                {
                    ["themeVariant"] = _lastSettings.ThemeVariant,
                    ["slowQueryThresholdMs"] = _lastSettings.SlowQueryThresholdMs.ToString(),
                    ["auditLogEnabled"] = _lastSettings.AuditLogEnabled.ToString(),
                    ["restorePreviousSession"] = _lastSettings.RestorePreviousSession.ToString()
                },
                Sessions = ActiveSessionOptions.Select(session => new DatabaseSessionSummary(
                    session.Name,
                    session.ProviderName,
                    session.Connection.EnvironmentTier,
                    session.DatabaseLabel)).ToArray()
            });

            if (ShowSupportBundleDialog == null)
            {
                StatusMessage = "Support bundle draft is ready, but no save dialog is configured.";
                return;
            }

            StatusMessage = await ShowSupportBundleDialog(draft)
                ? "Support bundle saved."
                : "Support bundle preview closed without saving.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Support bundle error: {DiagnosticRedactor.Redact(ex.Message)}";
        }
    }

    // ── Data Compare ──────────────────────────────────────────────────────────

    public IRelayCommand OpenDataCompareCommand => Tools.OpenDataCompareCommand;

    // ── Backup / Restore ──────────────────────────────────────────────────────

    public IRelayCommand OpenBackupRestoreCommand => Tools.OpenBackupRestoreCommand;

    public void OpenScriptInNewTab(string script) => Tools.OpenScriptInNewTab(script);

    public Task DropObjectAsync(SchemaNode node) => Explorer.DropAsync(node);

    public async Task ImportCsvAsync(SchemaNode node)
    {
        var session = SchemaTree.FindSessionForNode(node);
        if (session == null) return;

        var wizardVm = new CsvImportWizardViewModel(session);
        if (ShowCsvImportDialog != null)
            await ShowCsvImportDialog(wizardVm);

        if (wizardVm.ImportSucceeded)
            await RefreshSchemaAsync(node);
    }

    public async Task ImportCsvIntoTableAsync(SchemaNode node)
    {
        node = node.RealNode ?? node;
        var session = SchemaTree.FindSessionForNode(node);
        if (session == null) return;

        var tableName = node.RawName ?? node.Name;
        var wizardVm = new CsvImportWizardViewModel(session)
        {
            ImportMode = DbConnect.Models.ImportMode.ExistingTable,
            ExistingTableName = tableName
        };
        if (ShowCsvImportDialog != null)
            await ShowCsvImportDialog(wizardVm);

        if (wizardVm.ImportSucceeded)
            await RefreshSchemaAsync(node);
    }

    public Task RefreshSchemaAsync(SchemaNode node) => SchemaTree.RefreshSchemaAsync(node);
    public Task RefreshConnectionAsync(SchemaNode node) => SchemaTree.RefreshConnectionAsync(node);
    public Task RefreshNodeAsync(SchemaNode node) => SchemaTree.RefreshNodeAsync(node);
    public Task LoadSchemaNodeChildrenAsync(SchemaNode node) => SchemaTree.LoadSchemaNodeChildrenAsync(node);
    public void RenameActiveGroup(string oldGroupName, string newGroupName) => SchemaTree.RenameActiveGroup(oldGroupName, newGroupName);
    public void DeleteActiveGroup(string groupName) => SchemaTree.DeleteActiveGroup(groupName);
    public void MoveConnectionToGroup(SchemaNode node, string newGroupName) => SchemaTree.MoveConnectionToGroup(node, newGroupName);

    [RelayCommand]
    private Task RefreshSchema() => SchemaTree.RefreshSchemaAsync();

    public void Disconnect(SchemaNode node) => Connections.Disconnect(node);

    public DatabaseSession? GetSessionForNode(SchemaNode node) => SchemaTree.FindSessionForNode(node);

    public SchemaActionAvailability GetSchemaActionAvailability(SchemaNode? node, SchemaContextAction action)
    {
        if (node == null)
            return new SchemaActionAvailability(false, false);

        var session = SchemaTree.FindSessionForNode(node);
        var hasSession = session != null;
        var capabilities = session?.Capabilities;

        return action switch
        {
            SchemaContextAction.CreateTable => new(node.Type == SchemaNodeType.Database, hasSession, hasSession ? null : "Connect before creating schema objects."),
            SchemaContextAction.DesignTable => new(node.Type == SchemaNodeType.Table, hasSession, hasSession ? null : "Connect before designing tables."),
            SchemaContextAction.DropObject => new(
                node.Type == SchemaNodeType.Table || node.Type == SchemaNodeType.View,
                hasSession && (node.Type != SchemaNodeType.View || capabilities?.SupportsViews == true),
                !hasSession
                    ? "Connect before dropping schema objects."
                    : node.Type == SchemaNodeType.View && capabilities?.SupportsViews != true
                    ? "This provider does not support view operations in the current workflow."
                    : null),
            SchemaContextAction.CopyName => new(true, true),
            SchemaContextAction.ScriptSelect => new(
                node.Type == SchemaNodeType.Table || node.Type == SchemaNodeType.View,
                hasSession,
                hasSession ? null : "Connect before scripting SQL for this object."),
            SchemaContextAction.ManageIndexes => new(
                node.Type == SchemaNodeType.Table,
                capabilities?.SupportsIndexes == true,
                capabilities == null
                    ? "Connect before managing indexes."
                    : "This provider does not support index management in the current workflow."),
            SchemaContextAction.CreateView => new(
                node.Type == SchemaNodeType.Database,
                capabilities?.SupportsViews == true,
                capabilities == null
                    ? "Connect before creating views."
                    : "This provider does not support view operations in the current workflow."),
            SchemaContextAction.EditView => new(
                node.Type == SchemaNodeType.View,
                capabilities?.SupportsViews == true,
                capabilities == null
                    ? "Connect before editing views."
                    : "This provider does not support view operations in the current workflow."),
            SchemaContextAction.CreateRoutine => new(
                node.Type == SchemaNodeType.Database
                    || node.Type == SchemaNodeType.Schema
                    || node.Type == SchemaNodeType.FunctionFolder
                    || node.Type == SchemaNodeType.ProcedureFolder,
                capabilities?.SupportsProcedures == true || capabilities?.SupportsFunctions == true,
                capabilities == null
                    ? "Connect before creating routines."
                    : "This provider does not support procedure or function editing in the current workflow."),
            SchemaContextAction.RefreshNode => new(
                node.Type != SchemaNodeType.Database,
                hasSession,
                hasSession ? null : "Connect before refreshing objects."),
            SchemaContextAction.RefreshConnection => new(
                node.Type == SchemaNodeType.Database,
                hasSession,
                hasSession ? null : "No active session to refresh."),
            SchemaContextAction.ScriptCreate => new(
                node.Type == SchemaNodeType.Table
                    || node.Type == SchemaNodeType.View
                    || node.Type == SchemaNodeType.Function
                    || node.Type == SchemaNodeType.Procedure
                    || node.Type == SchemaNodeType.MaterializedView
                    || node.Type == SchemaNodeType.Sequence
                    || node.Type == SchemaNodeType.VirtualTable,
                hasSession,
                hasSession ? null : "Connect before scripting CREATE statements."),
            SchemaContextAction.ScriptDrop => new(
                node.Type == SchemaNodeType.Table
                    || node.Type == SchemaNodeType.View
                    || node.Type == SchemaNodeType.MaterializedView,
                hasSession,
                hasSession ? null : "Connect before scripting DROP statements."),
            SchemaContextAction.ImportCsv => new(
                node.Type == SchemaNodeType.Database,
                hasSession,
                hasSession ? null : "Connect before importing CSV."),
            SchemaContextAction.ImportCsvIntoTable => new(
                node.Type == SchemaNodeType.Table,
                hasSession,
                hasSession ? null : "Connect before importing CSV into table."),
            SchemaContextAction.ScriptPragma => new(
                node.Type == SchemaNodeType.DatabaseInfoItem,
                hasSession,
                hasSession ? null : "Connect before scripting PRAGMA."),
            _ => new SchemaActionAvailability(false, false)
        };
    }

    // ── Phase 3: Automation, User Management, Query Builder, ER Diagram ──────

    public IRelayCommand OpenAutomationCommand => Tools.OpenAutomationCommand;

    public IRelayCommand OpenUserManagementCommand => Tools.OpenUserManagementCommand;

    public IRelayCommand OpenQueryBuilderCommand => Tools.OpenQueryBuilderCommand;

    public IRelayCommand OpenErDiagramCommand => Tools.OpenErDiagramCommand;

    // ── Phase 4: Connection Monitor, Slow Query Dashboard ────────────────────

    public IRelayCommand<DatabaseSession?> OpenConnectionMonitorCommand => Tools.OpenConnectionMonitorCommand;

    public IRelayCommand OpenSlowQueryDashboardCommand => Tools.OpenSlowQueryDashboardCommand;

    /// <summary>Set by MainWindow.axaml.cs to wire the kill-confirmation dialog.</summary>
    public Action<ConnectionMonitorViewModel>? ShowConnectionMonitorConfirmDialog { get; set; }

    // ── Query Execution ───────────────────────────────────────────────────────

    [RelayCommand]
    private Task ExecuteQueryAsync() => ExecuteQueryCoreAsync(useSelectionOnly: false);

    [RelayCommand]
    private Task ExecuteSelectionAsync() => ExecuteQueryCoreAsync(useSelectionOnly: true);

    [RelayCommand]
    private void CancelQuery()
    {
        if (SelectedTab is QueryTabViewModel queryTab)
            queryTab.CancelExecutionCommand.Execute(null);
    }

    private Task ExecuteQueryCoreAsync(bool useSelectionOnly)
        => QueryRunCoordinator.ExecuteAsync(this, useSelectionOnly, _queryParamContextService);

    [RelayCommand]
    private async Task ExplainQueryAsync()
    {
        if (SelectedTab is not QueryTabViewModel queryTab || queryTab.IsExecuting) return;

        var textToRun = string.IsNullOrWhiteSpace(queryTab.SelectedQueryText)
            ? queryTab.QueryText
            : queryTab.SelectedQueryText;

        if (string.IsNullOrWhiteSpace(textToRun)) return;

        if (queryTab.SelectedSession == null) return;

        var session = queryTab.SelectedSession;
        var cancellationToken = queryTab.BeginExecution();

        IsExecuting = true;
        queryTab.IsExecuting = true;
        queryTab.IsExplaining = true;
        try
        {
            bool useAnalyze = session.Capabilities.SupportsAnalyzeExplain
                && queryTab.AllowAnalyze
                && (textToRun.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                 || textToRun.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase));

            var prefix = useAnalyze
                ? "EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT)"
                : session.Provider.ExplainPrefix;

            IDbSession dbSession;
            try
            {
                dbSession = await queryTab.GetOrOpenDbSessionAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                queryTab.ExplainResult = new QueryResult
                {
                    ExecutedSql = textToRun,
                    Error = QueryErrorInfo.Failure(session.ProviderName, "Could not open a session on this connection.", ex.Message)
                };
                queryTab.ShowExplainPane = false;
                return;
            }

            var explainSql = $"{prefix}\n{textToRun}";
            QueryResult raw;
            try
            {
                raw = await dbSession.ExecuteQueryAsync(
                    explainSql,
                    new QueryExecutionOptions
                    {
                        TimeoutSeconds = queryTab.TimeoutSeconds,
                        MaxResultRows = queryTab.ResultRowCap,
                        CancellationToken = cancellationToken
                    });
            }
            finally
            {
                // SHOWPLAN mode is a session setting; leaving it on would turn the tab's later runs into plans.
                if (session.Capabilities.UsesShowPlanExplain)
                    await dbSession.ExecuteQueryAsync("SET SHOWPLAN_ALL OFF;");
            }
            raw.ExecutionScope = QueryExecutionScope.Explain;
            raw.StatementCount = 1;
            raw.ReturnedStatementIndex = 0;
            raw.Statements =
            [
                new QueryStatementExecution
                {
                    Index = 0,
                    Sql = explainSql,
                    Duration = raw.Duration,
                    RecordsAffected = raw.RecordsAffected,
                    RowCount = raw.ObjectRows.Count,
                    HasResultSet = raw.HasResultSet,
                    Error = raw.Error
                }
            ];

            queryTab.ExplainResult = session.NormalizeExplainResult(raw);
            queryTab.ShowExplainPane = !queryTab.ExplainResult.HasError;

            // Update structured explain plan view
            if (queryTab.ExplainPlan == null)
                queryTab.ExplainPlan = new ExplainPlanViewModel();
            queryTab.ExplainPlan.LoadFromResult(queryTab.ExplainResult, session.ProviderName);
        }
        finally
        {
            IsExecuting = false;
            queryTab.IsExecuting = false;
            queryTab.IsExplaining = false;
            queryTab.EndExecution();
        }
    }

    // ── Connection Management ─────────────────────────────────────────────────

    public async Task AddConnectionAsync(ConnectionInfo saved, SshTrustPrompt? trustPrompt = null)
    {
        var established = await new ConnectionEstablisher(_dbService).EstablishAsync(saved, trustPrompt: trustPrompt);
        try { await Connections.RegisterAsync(established); }
        catch { established.Dispose(); throw; }
    }

    // ── Search / Filter ───────────────────────────────────────────────────────

    partial void OnSelectedSchemaNodeChanged(SchemaNode? value)
    {
        // Fire Ctx* property notifications for context menu binding
        OnPropertyChanged(nameof(CtxIsDatabase));
        OnPropertyChanged(nameof(CtxIsTable));
        OnPropertyChanged(nameof(CtxIsView));
        OnPropertyChanged(nameof(CtxIsColumn));
        OnPropertyChanged(nameof(CtxIsTableOrView));
        OnPropertyChanged(nameof(CtxIsRoutine));
        OnPropertyChanged(nameof(CtxIsFolder));
        OnPropertyChanged(nameof(CtxCanScript));
        OnPropertyChanged(nameof(CtxIsConnectionGroup));
    }

    internal static bool IsTabOwnedBySession(ViewModelBase tab, DatabaseSession session) =>
        tab switch
        {
            TableDataTabViewModel tableData => tableData.Session == session,
            TableDesignerViewModel tableDesigner => tableDesigner.Session == session,
            IndexManagerViewModel indexManager => indexManager.Session == session,
            ViewDesignerViewModel viewDesigner => viewDesigner.Session == session,
            ProcedureEditorViewModel procedureEditor => procedureEditor.Session == session,
            DependencyAnalysisViewModel => false,
            SchemaDiffViewModel => false,
            DataCompareViewModel => false,
            BackupRestoreViewModel => false,
            _ => false
        };

    partial void OnSelectedTabChanged(ViewModelBase? value)
    {
        // Unsubscribe from the previous tab's PropertyChanged
        if (_previousTab is QueryTabViewModel prevQt)
            prevQt.PropertyChanged -= OnActiveTabPropertyChanged;

        _previousTab = value;

        // Subscribe to the new tab's PropertyChanged
        if (value is QueryTabViewModel nextQt)
            nextQt.PropertyChanged += OnActiveTabPropertyChanged;

        OnPropertyChanged(nameof(StatusTransactionText));
        UpdateActiveEnvironmentTier();
        _workspacePersistence.ScheduleSnapshotWrite();
    }

    private ViewModelBase? _previousTab;

    private void OnActiveTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryTabViewModel.SelectedSession))
        {
            UpdateActiveEnvironmentTier();
            OnPropertyChanged(nameof(ActiveSessionLabelColor));
        }
        else if (e.PropertyName == nameof(QueryTabViewModel.TransactionStatusText))
        {
            OnPropertyChanged(nameof(StatusTransactionText));
        }
    }

    private void UpdateActiveEnvironmentTier()
    {
        string tier = "Development";
        var session = GetActiveTabSession();
        if (session != null)
        {
            var conn = ActiveConnections.Values.FirstOrDefault(c => c.Name == session.Name);
            tier = conn?.EnvironmentTier ?? "Development";
        }
        ActiveEnvironmentTier = tier;
        IsProductionActive = tier == "Production";
        EnvironmentIndicatorText = IsProductionActive ? "⚠️ PRODUCTION" : string.Empty;
        UpdateStatusConnectionText();
        OnPropertyChanged(nameof(ActiveSessionLabelColor));
    }

    private DatabaseSession? GetActiveTabSession() => SelectedTab switch
    {
        QueryTabViewModel qt => qt.SelectedSession,
        TableDataTabViewModel td => td.Session,
        ProcedureEditorViewModel procedureEditor => procedureEditor.Session,
        _ => null
    };

    public string? ActiveSessionLabelColor => GetActiveTabSession()?.Connection?.LabelColor;

    private void UpdateStatusConnectionText()
    {
        var session = GetActiveTabSession();
        if (session is null)
        {
            if (SelectedTab is null)
                StatusConnectionText = "No connection";
            return;
        }

        var conn = ActiveConnections.Values.FirstOrDefault(c => c.Name == session.Name);
        var tier = conn?.EnvironmentTier ?? "Development";
        var name = ConnectionDisplayName.Format(session.Name, session.ProviderName, session.DatabaseLabel);
        StatusConnectionText = $"Connected: {name} [{(tier == "Production" ? "PRODUCTION" : tier)}]";
    }

    public bool ShouldConfirmDestructiveAction(DatabaseSession session)
    {
        var conn = ActiveConnections.Values.FirstOrDefault(c => c.Name == session.Name);
        return conn?.EnvironmentTier == "Production";
    }

    [RelayCommand]
    private async Task SaveSelectionAsSnippet()
    {
        if (SelectedTab is not QueryTabViewModel qt) return;
        var body = string.IsNullOrWhiteSpace(qt.SelectedQueryText)
            ? qt.QueryText
            : qt.SelectedQueryText;

        if (string.IsNullOrWhiteSpace(body)) return;

        var snippet = new Snippet
        {
            Name = $"Snippet {DateTimeOffset.Now:HH:mm:ss}",
            Body = body,
            Scope = SnippetScope.Global
        };

        if (SnippetPanel.ShowSnippetEditorDialog != null)
        {
            var result = await SnippetPanel.ShowSnippetEditorDialog(snippet);
            if (result != null)
            {
                var service = new SnippetService();
                await service.SaveAsync(result);
                await SnippetPanel.LoadAsync();
            }
        }
    }

    [RelayCommand]
    private void ShowGlobalSearchOverlay()
    {
        ShowGlobalSearch?.Invoke();
    }

    // ── Session Recovery helpers ──────────────────────────────────────────────

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryTabViewModel.QueryText)
            || e.PropertyName == nameof(QueryTabViewModel.SelectedSession))
        {
            _workspacePersistence.ScheduleSnapshotWrite();
            if (e.PropertyName == nameof(QueryTabViewModel.SelectedSession))
                UpdateActiveEnvironmentTier();
        }
    }

    public Task SaveSessionAsync(bool wasCleanShutdown)
        => _workspacePersistence.SaveSessionAsync(wasCleanShutdown);

    /// <summary>Called on clean app exit to remove the recovery snapshot.</summary>
    public Task OnCleanExitAsync() => _workspacePersistence.OnCleanExitAsync(async () =>
    {
        foreach (var established in EstablishedConnections.Values)
            established.Dispose();
        EstablishedConnections.Clear();

        foreach (var diagramViewModel in Tabs.OfType<ErDiagramViewModel>())
            await diagramViewModel.SaveWorkspaceAsync();

        foreach (var tab in Tabs.ToList())
            await TabStripActions.ReleaseSessionAsync(tab);
        Files.Monitor.Dispose();
    });

}
