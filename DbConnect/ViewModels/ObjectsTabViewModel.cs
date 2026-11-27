using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>How the Objects tab lays its objects out.</summary>
public enum ObjectsViewMode
{
    List,
    Detail,
}

/// <summary>
/// The permanent Objects tab (the design's Main artboard): the objects of the type chosen in the main toolbar or the
/// navigator, in the database or schema the navigator's selection belongs to. Tables carry provider statistics where
/// the provider supplies them (<see cref="ITableStatisticsSource"/>), "—" otherwise. The selected row feeds the
/// Information pane.
/// </summary>
public sealed partial class ObjectsTabViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _host;
    private readonly MainToolbarState _toolbar;
    private readonly WindowMenuState _menu;
    private readonly List<ObjectRow> _all = [];
    private CancellationTokenSource? _loading;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    private ObjectRow? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView), nameof(IsDetailView))]
    private ObjectsViewMode _viewMode = ObjectsViewMode.Detail;

    [ObservableProperty]
    private string _scopeName = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    public ObjectsTabViewModel(MainWindowViewModel host, MainToolbarState toolbar, WindowMenuState menu)
    {
        _host = host;
        _toolbar = toolbar;
        _menu = menu;
        Title = "Objects";
        toolbar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainToolbarState.ObjectType)) Reload();
        };
        host.PropertyChanged += OnHostPropertyChanged;
        host.SchemaNodes.CollectionChanged += (_, _) => Reload();
    }

    public override Symbol? TabIcon => Symbol.Grid;

    /// <summary>The Objects tab is always there; it has no close button.</summary>
    public override bool ShowsCloseButton => false;

    public ObjectBrowserKind Kind => _toolbar.ObjectType;

    public string KindLabel => Kind.Label();

    /// <summary>The kind in the singular, for the toolbar ("Open Table").</summary>
    public string KindSingular => Kind switch
    {
        ObjectBrowserKind.Queries => "Query",
        ObjectBrowserKind.MaterializedViews => "Materialized View",
        _ => Kind.Label().TrimEnd('s'),
    };

    public ObservableCollection<ObjectRow> Rows { get; } = [];

    public bool IsListView => ViewMode == ObjectsViewMode.List;
    public bool IsDetailView => ViewMode == ObjectsViewMode.Detail;

    public bool IsTables => Kind == ObjectBrowserKind.Tables;
    public bool CanDesign => Kind is ObjectBrowserKind.Tables or ObjectBrowserKind.Views or ObjectBrowserKind.Functions
        or ObjectBrowserKind.Procedures;
    public bool CanCreate => Kind is ObjectBrowserKind.Tables or ObjectBrowserKind.Views or ObjectBrowserKind.Functions
        or ObjectBrowserKind.Procedures;
    public bool HasSelection => SelectedRow != null;

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The session of the database the Objects tab lists.</summary>
    public DatabaseSession? ScopeSession => Scope() is { } scope ? _host.GetSessionForNode(scope) : null;

    /// <summary>The listed row for a navigator node, with its statistics; null when the node is not listed.</summary>
    public ObjectRow? RowFor(SchemaNode node) => _all.FirstOrDefault(r => ReferenceEquals(r.Node, node));

    public string FooterText =>
        $"{Rows.Count} {(Rows.Count == 1 ? KindSingular.ToLowerInvariant() : KindLabel.ToLowerInvariant())} · {(SelectedRow == null ? 0 : 1)} selected";

    /// <summary>Lists the objects again (toolbar type, navigator scope or connections changed).</summary>
    public void Reload() => _ = ReloadAsync();

    public async Task ReloadAsync()
    {
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(KindSingular));
        OnPropertyChanged(nameof(IsTables));
        OnPropertyChanged(nameof(CanDesign));
        OnPropertyChanged(nameof(CanCreate));

        var scope = Scope();
        ScopeName = scope?.RawName ?? scope?.Name ?? string.Empty;
        _all.Clear();
        if (Kind == ObjectBrowserKind.Queries)
        {
            await _menu.RefreshFavoritesCommand.ExecuteAsync(null);
            _all.AddRange(_menu.Favorites.Select(q => new ObjectRow(q.Name, Symbol.DocumentText, "Accent") { Sql = q.Sql }));
        }
        else if (scope != null)
        {
            var nodes = ObjectsIn(scope, Kind).ToList();
            var statistics = await StatisticsAsync(scope, loading.Token);
            if (loading.IsCancellationRequested) return;
            _all.AddRange(nodes.Select(node => new ObjectRow(node.RawName ?? node.Name,
                node.FluentIconSymbol ?? Symbol.Table, node.IconRole)
            {
                Node = node,
                Statistics = statistics.TryGetValue(node.RawName ?? node.Name, out var s) ? s : null,
            }));
        }

        if (loading.IsCancellationRequested) return;
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedRowChanged(ObjectRow? value) => OnPropertyChanged(nameof(HasSelection));

    private void ApplyFilter()
    {
        var selected = SelectedRow;
        Rows.Clear();
        foreach (var row in _all.Where(r => SearchText.Length == 0
                     || r.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)))
            Rows.Add(row);
        SelectedRow = Rows.FirstOrDefault(r => ReferenceEquals(r, selected));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedSchemaNode)) return;
        var scope = Scope();
        if ((scope?.RawName ?? scope?.Name ?? string.Empty) != ScopeName) Reload();
    }

    /// <summary>
    /// The database or schema the navigator's selection belongs to (the deepest Database or Schema node above it), or
    /// the first connection when nothing is selected.
    /// </summary>
    private SchemaNode? Scope()
    {
        var selected = _host.SelectedSchemaNode is { } node ? node.RealNode ?? node : null;
        if (selected != null)
        {
            foreach (var root in _host.SchemaNodes)
            {
                var path = PathTo(root, selected);
                if (path != null)
                    return path.LastOrDefault(n => n.Type is SchemaNodeType.Database or SchemaNodeType.Schema) ?? root;
            }
        }

        return _host.SchemaNodes.FirstOrDefault(n => n.Type == SchemaNodeType.Database)
            ?? _host.SchemaNodes.SelectMany(n => n.Children).FirstOrDefault(n => n.Type == SchemaNodeType.Database);
    }

    private static List<SchemaNode>? PathTo(SchemaNode from, SchemaNode target)
    {
        if (ReferenceEquals(from, target)) return [from];
        foreach (var child in from.Children)
        {
            if (PathTo(child, target) is { } path)
            {
                path.Insert(0, from);
                return path;
            }
        }

        return null;
    }

    /// <summary>The objects of <paramref name="kind"/> under <paramref name="scope"/>, not looking inside objects.</summary>
    public static IEnumerable<SchemaNode> ObjectsIn(SchemaNode scope, ObjectBrowserKind kind)
    {
        foreach (var child in scope.Children)
        {
            if (Matches(child.Type, kind))
            {
                yield return child;
                continue;
            }

            if (IsContainer(child.Type))
            {
                foreach (var inner in ObjectsIn(child, kind)) yield return inner;
            }
        }
    }

    private static bool Matches(SchemaNodeType type, ObjectBrowserKind kind) => kind switch
    {
        ObjectBrowserKind.Tables => type is SchemaNodeType.Table or SchemaNodeType.VirtualTable,
        ObjectBrowserKind.Views => type == SchemaNodeType.View,
        ObjectBrowserKind.MaterializedViews => type == SchemaNodeType.MaterializedView,
        ObjectBrowserKind.Functions => type == SchemaNodeType.Function,
        ObjectBrowserKind.Procedures => type == SchemaNodeType.Procedure,
        ObjectBrowserKind.Triggers => type == SchemaNodeType.Trigger,
        ObjectBrowserKind.Sequences => type == SchemaNodeType.Sequence,
        _ => false,
    };

    private static bool IsContainer(SchemaNodeType type) => type is SchemaNodeType.TableFolder or SchemaNodeType.ViewFolder
        or SchemaNodeType.FunctionFolder or SchemaNodeType.ProcedureFolder or SchemaNodeType.MaterializedViewFolder
        or SchemaNodeType.SequenceFolder or SchemaNodeType.TriggerFolder;

    private async Task<Dictionary<string, TableStatistics>> StatisticsAsync(SchemaNode scope, CancellationToken token)
    {
        var map = new Dictionary<string, TableStatistics>(StringComparer.OrdinalIgnoreCase);
        if (Kind != ObjectBrowserKind.Tables || _host.GetSessionForNode(scope) is not { Provider: ITableStatisticsSource source } session)
            return map;

        IsLoading = true;
        try
        {
            foreach (var statistics in await source.GetTableStatisticsAsync(session.ConnectionString, token))
                map[statistics.Name] = statistics;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Statistics are informational; the list still shows names with "—".
        }
        catch (OperationCanceledException)
        {
            // A newer reload replaced this one.
        }
        finally
        {
            IsLoading = false;
        }

        return map;
    }

    [RelayCommand]
    private void ShowList() => ViewMode = ObjectsViewMode.List;

    [RelayCommand]
    private void ShowDetail() => ViewMode = ObjectsViewMode.Detail;

    /// <summary>The ER view opens the schema's ER diagram.</summary>
    [RelayCommand]
    private void ShowDiagram() => _host.OpenErDiagramCommand.Execute(null);

    /// <summary>Opens a row: table or view data, a routine's definition, or a saved query in a new tab.</summary>
    [RelayCommand]
    private async Task Open(ObjectRow? row)
    {
        row ??= SelectedRow;
        if (row?.Sql is { } sql) { _host.OpenScriptInNewTab(sql); return; }
        if (row?.Node is not { } node) return;
        switch (node.Type)
        {
            case SchemaNodeType.Table or SchemaNodeType.VirtualTable or SchemaNodeType.View or SchemaNodeType.MaterializedView:
                _host.OpenTableDataTab(node);
                break;
            case SchemaNodeType.Function or SchemaNodeType.Procedure:
                await _host.OpenProcedureEditorAsync(node);
                break;
            default:
                await _host.OpenDdlTabAsync(node);
                break;
        }
    }

    [RelayCommand]
    private async Task Design()
    {
        if (SelectedRow?.Node is not { } node) return;
        switch (node.Type)
        {
            case SchemaNodeType.Table or SchemaNodeType.VirtualTable: _host.OpenTableDesignerTab(node); break;
            case SchemaNodeType.View or SchemaNodeType.MaterializedView: _host.OpenEditViewTab(node); break;
            case SchemaNodeType.Function or SchemaNodeType.Procedure: await _host.OpenProcedureEditorAsync(node); break;
        }
    }

    [RelayCommand]
    private async Task Create()
    {
        if (Scope() is not { } scope) return;
        switch (Kind)
        {
            case ObjectBrowserKind.Tables: _host.OpenTableDesignerTab(scope); break;
            case ObjectBrowserKind.Views: _host.OpenViewDesignerTab(scope); break;
            case ObjectBrowserKind.Functions or ObjectBrowserKind.Procedures: await _host.OpenNewRoutineEditorAsync(scope); break;
        }
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (SelectedRow?.Node is { } node)
        {
            await _host.DropObjectAsync(node);
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task Import()
    {
        if (SelectedRow?.Node is { Type: SchemaNodeType.Table } node) await _host.ImportCsvIntoTableAsync(node);
        else if (Scope() is { } scope) await _host.ImportCsvAsync(scope);
    }

    [RelayCommand]
    private async Task Export()
    {
        if (SelectedRow?.Node is { } node) await _host.Explorer.ExportDataAsync(node);
    }
}
