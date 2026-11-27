using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// The window frame around the documents: the main toolbar's state, the status bar and the Favorites and Window menus
/// with the window title. It follows the window's selected tab and that tab's title, dirty and transaction state.
/// </summary>
public sealed class WindowFrame
{
    private readonly Func<ViewModelBase?> _selected;
    private readonly Func<DbConnect.Models.SchemaNode?> _selectedNode;
    private ViewModelBase? _watched;

    public WindowFrame(
        MainWindowViewModel window,
        ObservableCollection<ViewModelBase> tabs,
        Func<ViewModelBase?> selected,
        Action<ViewModelBase> select,
        Func<DatabaseSession?> activeSession,
        Func<DbConnect.Models.SchemaNode?> selectedNode,
        Action<string> openScript,
        QueryWorkspaceService workspace)
    {
        _selected = selected;
        _selectedNode = selectedNode;
        Toolbar = new MainToolbarState();
        // With the Objects tab in front, the status bar shows the connection the Objects tab lists.
        StatusBar = new StatusBarState(() => activeSession() ?? (selected() == null ? Objects?.ScopeSession : null), selected);
        Menu = new WindowMenuState(tabs, selected, select, openScript, workspace);
        Objects = new ObjectsTabViewModel(window, Toolbar, Menu);
        Strip = new DocumentStrip(tabs, Objects, selected, tab => window.SelectedTab = tab);
        Information = new InformationPaneViewModel(window, Strip);
        window.PropertyChanged += OnWindowPropertyChanged;
        Objects.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ObjectsTabViewModel.ScopeName) or nameof(ObjectsTabViewModel.FooterText)) Refresh();
        };
        Refresh();
    }

    public MainToolbarState Toolbar { get; }

    public StatusBarState StatusBar { get; }

    public WindowMenuState Menu { get; }

    /// <summary>The permanent Objects tab.</summary>
    public ObjectsTabViewModel Objects { get; }

    /// <summary>The document tab strip: Objects, then the documents.</summary>
    public DocumentStrip Strip { get; }

    /// <summary>The right-hand Information pane.</summary>
    public InformationPaneViewModel Information { get; }

    private void OnWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "SelectedTab" or "StatusConnectionText" or "ActiveSessionLabelColor" or "StatusTransactionText")
            Refresh();
        else if (e.PropertyName == "SelectedSchemaNode" && ObjectKindOf(_selectedNode()) is { } kind)
            Toolbar.ObjectType = kind;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModelBase.Title) or nameof(ViewModelBase.IsDirty) or "IsInTransaction"
            or "SelectedSession")
            Refresh();
    }

    /// <summary>The Objects tab type a navigator node selects: a folder its contents, an object its own kind.</summary>
    public static ObjectBrowserKind? ObjectKindOf(DbConnect.Models.SchemaNode? node) => node?.Type switch
    {
        DbConnect.Models.SchemaNodeType.TableFolder or DbConnect.Models.SchemaNodeType.Table or DbConnect.Models.SchemaNodeType.VirtualTable
            => ObjectBrowserKind.Tables,
        DbConnect.Models.SchemaNodeType.ViewFolder or DbConnect.Models.SchemaNodeType.View => ObjectBrowserKind.Views,
        DbConnect.Models.SchemaNodeType.FunctionFolder or DbConnect.Models.SchemaNodeType.Function => ObjectBrowserKind.Functions,
        DbConnect.Models.SchemaNodeType.ProcedureFolder or DbConnect.Models.SchemaNodeType.Procedure => ObjectBrowserKind.Procedures,
        DbConnect.Models.SchemaNodeType.TriggerFolder or DbConnect.Models.SchemaNodeType.Trigger => ObjectBrowserKind.Triggers,
        DbConnect.Models.SchemaNodeType.SequenceFolder or DbConnect.Models.SchemaNodeType.Sequence => ObjectBrowserKind.Sequences,
        DbConnect.Models.SchemaNodeType.MaterializedViewFolder or DbConnect.Models.SchemaNodeType.MaterializedView
            => ObjectBrowserKind.MaterializedViews,
        _ => null,
    };

    private void Refresh()
    {
        var tab = _selected();
        if (!ReferenceEquals(tab, _watched))
        {
            if (_watched != null) _watched.PropertyChanged -= OnTabPropertyChanged;
            _watched = tab;
            if (tab != null) tab.PropertyChanged += OnTabPropertyChanged;
        }

        Strip?.SyncSelection();
        StatusBar.Refresh();
        Menu.RefreshTitle();
        if (tab == null && Objects != null)
        {
            StatusBar.Breadcrumb = Objects.ScopeName.Length == 0 ? Objects.KindLabel : $"{Objects.ScopeName} › {Objects.KindLabel}";
            Menu.WindowTitle = $"{Objects.KindLabel} — {WindowMenuState.AppName}";
        }
    }
}
