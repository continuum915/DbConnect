using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Schema explorer state, connection grouping, filtering and node loading.</summary>
public partial class SchemaTreeViewModel : ViewModelBase
{
    private readonly Dictionary<SchemaNode, ConnectionInfo> _connections;
    private readonly Dictionary<SchemaNode, DatabaseSession> _sessions;
    private readonly Dictionary<SchemaNode, SchemaIndex> _indexes;
    private readonly IEnumerable<ViewModelBase> _tabs;
    private readonly Dictionary<string, SchemaNode> _groupNodes = new();

    public ObservableCollection<SchemaNode> SchemaNodes { get; } = new();
    public ObservableCollection<SchemaNode> FilteredSchemaNodes { get; } = new();

    /// <summary>Collapses every node in the navigator (the header's Collapse all button).</summary>
    [RelayCommand]
    private void CollapseAll()
    {
        static void Collapse(SchemaNode node)
        {
            node.IsExpanded = false;
            foreach (var child in node.Children) Collapse(child);
        }

        foreach (var node in SchemaNodes.Concat(FilteredSchemaNodes)) Collapse(node);
    }

    [ObservableProperty]
    private string _schemaSearchText = string.Empty;

    [ObservableProperty]
    private string _schemaFilterMatchCount = string.Empty;

    public ConnectionGroupActions Groups { get; }
    public ExplorerSearchViewModel Search { get; }

    public Action<string>? ReportError { get; set; }

    /// <summary>Server connections that list their databases: connection node → its default database node.</summary>
    public Dictionary<SchemaNode, SchemaNode> ServerDefaults { get; } = new();

    /// <summary>Opens the session of a server database node on first expand.</summary>
    public Func<SchemaNode, Task>? OpenDatabase { get; set; }

    /// <summary>Reloads a server connection's database list.</summary>
    public Func<SchemaNode, Task>? RefreshServer { get; set; }

    public SchemaTreeViewModel(
        Dictionary<SchemaNode, ConnectionInfo> connections,
        Dictionary<SchemaNode, DatabaseSession> sessions,
        Dictionary<SchemaNode, SchemaIndex> indexes,
        IEnumerable<ViewModelBase>? tabs = null)
    {
        _connections = connections;
        _tabs = tabs ?? [];
        Search = new ExplorerSearchViewModel(this);
        Groups = new ConnectionGroupActions(this, connections);
        _sessions = sessions;
        _indexes = indexes;
        var schemaChanges = new SchemaChangeProjection(this);
        SchemaNodes.CollectionChanged += (_, _) => { schemaChanges.Synchronize(); Search.Begin(); };
    }

    partial void OnSchemaSearchTextChanged(string value) => Search.Begin();

    public DatabaseObjectIdentity GetIdentity(SchemaNode node, DatabaseSession session)
    {
        node = node.RealNode ?? node;
        if (node.Identity != null) return node.Identity;
        var root = FindRootNodeForSession(session);
        if (root != null) session.Objects.Bind(root, null);
        return node.Identity ?? session.Objects.Bind(node, null);
    }

    public Task RefreshSchemaAsync(SchemaNode node)
    {
        node = node.RealNode ?? node;
        var session = FindSessionForNode(node);
        if (ServerDefaults.ContainsKey(node) && RefreshServer != null) return RefreshServer(node);
        if (session == null) return Task.CompletedTask;
        if (node.Type == SchemaNodeType.Schema)
            return LoadCatalogAsync(node, session, new(GetIdentity(node, session), SchemaNodeType.Schema), true);
        var root = FindRootNode(node);
        return LoadCatalogAsync(root, session, MetadataScope.Root, true);
    }

    public Task RefreshConnectionAsync(SchemaNode node) => RefreshSchemaAsync(node);

    public Task RefreshSchemaAsync() => SchemaNodes.FirstOrDefault() is { } root
        ? RefreshSchemaAsync(root) : Task.CompletedTask;

    public Task RefreshNodeAsync(SchemaNode node) => (node.RealNode ?? node).Type is SchemaNodeType.Database or SchemaNodeType.Schema
        ? RefreshSchemaAsync(node) : LoadNodeAsync(node, true);
    public Task LoadSchemaNodeChildrenAsync(SchemaNode node) => LoadNodeAsync(node, false);

    private Task LoadNodeAsync(SchemaNode node, bool refresh)
    {
        node = node.RealNode ?? node;
        if (!refresh && node.IsLoaded) return Task.CompletedTask;
        if (node.Type == SchemaNodeType.Database && !_sessions.ContainsKey(node) && OpenDatabase != null)
            return OpenDatabase(node);
        var session = FindSessionForNode(node);
        if (session == null) return Task.CompletedTask;
        var identity = GetIdentity(node, session);
        var kind = ExplorerCatalogLoader.ChildKind(node.Type);
        var parent = kind != SchemaNodeType.Column ? identity.Parent ?? identity : identity;
        return LoadCatalogAsync(node, session, new(parent, kind), refresh);
    }

    private async Task LoadCatalogAsync(SchemaNode node, DatabaseSession session, MetadataScope scope, bool refresh)
    {
        await ExplorerCatalogLoader.LoadAsync(node, session, scope, refresh,
            () => FindSessionForNode(node) == session, UpdateFilteredSchemaNodes, ReportError);
        if (refresh && scope.Kind is SchemaNodeType.Column or SchemaNodeType.Schema && scope.Parent is { } identity &&
            FindRootNodeForSession(session) is { } root && ExplorerCatalogProjection.FindListingOwner(root, identity) is { } owner)
            await ExplorerCatalogLoader.LoadAsync(owner, session, MetadataScope.ListingFor(identity), false,
                () => FindSessionForNode(owner) == session, UpdateFilteredSchemaNodes, ReportError);
        if (refresh && !session.Catalog.IsDisposed)
            await OpenTableMetadataRefresh.ApplyAsync(_tabs, session, scope);
        Search.Begin();
    }

    public void RemoveSession(SchemaNode root)
    {
        if (_sessions.Remove(root, out var session)) session.Catalog.Dispose();
    }

    public ConnectionInfo? FindConnectionForNode(SchemaNode node) =>
        Nearest(_connections.Keys, node.RealNode ?? node) is { } root ? _connections[root] : null;

    /// <summary>The deepest registered root holding <paramref name="target"/>: a server database before its connection.</summary>
    private static SchemaNode? Nearest(IEnumerable<SchemaNode> roots, SchemaNode target)
    {
        var matches = roots.Where(root => BelongsToRoot(root, target)).ToList();
        return matches.FirstOrDefault(m => !matches.Any(other => other != m && IsDescendant(m, other)));
    }

    public DatabaseSession? FindSessionForNode(SchemaNode node)
    {
        // If this is a filtered display copy, use the real node for lookup
        var target = node.RealNode ?? node;
        foreach (var (rootNode, session) in _sessions)
        {
            if (BelongsToRoot(rootNode, target))
                return session;
        }
        // A server connection node acts on its default database.
        return ServerDefaults.TryGetValue(target, out var database) && _sessions.TryGetValue(database, out var fallback)
            ? fallback : null;
    }

    public SchemaNode FindRootNode(SchemaNode node)
    {
        var target = node.RealNode ?? node;
        return Nearest(_connections.Keys, target) ?? target;
    }

    public static SchemaNode? FindParentOf(SchemaNode root, SchemaNode target)
    {
        foreach (var child in root.Children)
        {
            if (child == target) return root;
            var found = FindParentOf(child, target);
            if (found != null) return found;
        }
        return null;
    }

    public void RenameActiveGroup(string oldGroupName, string newGroupName)
    {
        foreach (var (_, ci) in _connections)
        {
            if ((ci.Group ?? string.Empty) == oldGroupName)
                ci.Group = newGroupName;
        }
        UpdateFilteredSchemaNodes();
    }

    public void DeleteActiveGroup(string groupName)
    {
        foreach (var (_, ci) in _connections)
        {
            if ((ci.Group ?? string.Empty) == groupName)
                ci.Group = string.Empty;
        }
        UpdateFilteredSchemaNodes();
    }

    public void MoveConnectionToGroup(SchemaNode node, string newGroupName)
    {
        var root = FindRootNode(node);
        if (_connections.TryGetValue(root, out var ci))
        {
            ci.Group = newGroupName;
            UpdateFilteredSchemaNodes();
        }
    }

    public void UpdateFilteredSchemaNodes()
    {
        FilteredSchemaNodes.Clear();
        var text = SchemaSearchText?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(text))
        {
            BuildGroupedNodes(FilteredSchemaNodes);
            SchemaFilterMatchCount = string.Empty;
            return;
        }

        var filteredByGroup = SchemaNodes
            .Select(n => new
            {
                Node = n,
                Group = _connections.TryGetValue(n, out var ci) ? (ci.Group ?? string.Empty) : string.Empty,
            })
            .Select(x => new
            {
                x.Node,
                x.Group,
                Filtered = !string.IsNullOrEmpty(x.Group) && x.Group.Contains(text, StringComparison.OrdinalIgnoreCase)
                    ? Search.HasProjection(x.Node) ? FilterNode(x.Node, string.Empty) : x.Node
                    : FilterNode(x.Node, text)
            })
            .Where(x => x.Filtered != null)
            .GroupBy(x => x.Group)
            .OrderBy(g => g.Key == string.Empty ? "\uFFFF" : g.Key);

        foreach (var grp in filteredByGroup)
        {
            if (!string.IsNullOrEmpty(grp.Key))
            {
                var folder = new SchemaNode { Name = grp.Key, Type = SchemaNodeType.ConnectionGroup, IsExpanded = true };
                foreach (var item in grp.OrderBy(x => x.Node.Name))
                    folder.Children.Add(item.Filtered!);
                FilteredSchemaNodes.Add(folder);
            }
            else
            {
                foreach (var item in grp.OrderBy(x => x.Node.Name))
                    FilteredSchemaNodes.Add(item.Filtered!);
            }
        }

        int count = FilteredSchemaNodes.Sum(n => CountRelations(n));
        SchemaFilterMatchCount = count == 0 ? "No matches" : $"{count} match{(count == 1 ? "" : "es")}";
        if (Search.IsLoading || Search.IsIncomplete)
            SchemaFilterMatchCount = $"{count} match{(count == 1 ? "" : "es")} · " +
                (Search.IsLoading ? "Searching…" : "Incomplete coverage · metadata unavailable or stale");
    }

    private void BuildGroupedNodes(ObservableCollection<SchemaNode> target)
    {
        target.Clear();
        var grouped = SchemaNodes
            .Select(n => new { Node = n, Group = _connections.TryGetValue(n, out var ci) ? (ci.Group ?? string.Empty) : string.Empty })
            .GroupBy(x => x.Group)
            .OrderBy(g => g.Key == string.Empty ? "\uFFFF" : g.Key);

        foreach (var grp in grouped)
        {
            if (!string.IsNullOrEmpty(grp.Key))
            {
                if (!_groupNodes.TryGetValue(grp.Key, out var folder))
                    _groupNodes[grp.Key] = folder = new SchemaNode { Name = grp.Key, Type = SchemaNodeType.ConnectionGroup };
                folder.Children.Clear();
                foreach (var item in grp.OrderBy(x => x.Node.Name))
                    folder.Children.Add(item.Node);
                target.Add(folder);
            }
            else
            {
                foreach (var item in grp.OrderBy(x => x.Node.Name))
                    target.Add(item.Node);
            }
        }
    }

    private static bool IsRelation(SchemaNode node) => node.Type is
        SchemaNodeType.Table or SchemaNodeType.View or SchemaNodeType.VirtualTable or SchemaNodeType.MaterializedView;

    private static int CountRelations(SchemaNode node) => IsRelation(node)
        ? 1 : node.Children.Sum(CountRelations);

    private SchemaNode? FilterNode(SchemaNode node, string text)
    {
        if (node.Type == SchemaNodeType.ConnectionGroup)
        {
            bool groupNameMatches = node.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
            var matchingChildren = new List<SchemaNode>();
            foreach (var child in node.Children)
            {
                var fc = FilterNode(child, text);
                if (fc != null) matchingChildren.Add(fc);
            }
            if (matchingChildren.Count == 0 && !groupNameMatches) return null;
            var copy = new SchemaNode { Name = node.Name ?? string.Empty, Type = node.Type };
            IEnumerable<SchemaNode> childrenToCopy = matchingChildren.Count > 0 ? matchingChildren : node.Children;
            foreach (var child in childrenToCopy)
                copy.Children.Add(child);
            return copy;
        }

        if (IsRelation(node))
        {
            bool nameMatches = (node.RawName ?? node.Name).Contains(text, StringComparison.OrdinalIgnoreCase);
            return nameMatches ? node : null;
        }

        var children = Search.Children(node).ToList();
        if (children.Count > 0)
        {
            var matchingChildren = new List<SchemaNode>();
            foreach (var child in children)
            {
                var fc = FilterNode(child, text);
                if (fc != null) matchingChildren.Add(fc);
            }
            if (matchingChildren.Count == 0) return null;

            var copy = new SchemaNode
            {
                Name = node.Name,
                RawName = node.RawName,
                IsExpanded = true,
                ChildCount = node.ChildCount,
                CatalogName = node.CatalogName,
                Type = node.Type,
                ProviderIcon = node.ProviderIcon,
                ConnectionMeta = node.ConnectionMeta,
                TagColor = node.TagColor,
                Identity = node.Identity,
                SchemaPath = node.SchemaPath,
                SchemaName = node.SchemaName,
                IsLazyLoadable = node.IsLazyLoadable,
                IsLoaded = node.IsLoaded,
                MetadataState = node.MetadataState,
                MetadataError = node.MetadataError,
                RealNode = node
            };
            foreach (var child in matchingChildren)
                copy.Children.Add(child);
            return copy;
        }

        return null;
    }

    public void ApplyKnownSessionIndexes(QueryTabViewModel tab)
    {
        foreach (var (rootNode, index) in _indexes)
        {
            if (_sessions.TryGetValue(rootNode, out var session))
                tab.SetSessionIndex(session, index);
        }
    }

    public SchemaNode? FindRootNodeForSession(DatabaseSession session)
    {
        foreach (var (rootNode, activeSession) in _sessions)
        {
            if (ReferenceEquals(activeSession, session))
                return rootNode;
        }

        return null;
    }

    public static bool IsDescendant(SchemaNode root, SchemaNode target)
    {
        if (root == target) return true;
        foreach (var child in root.Children)
            if (IsDescendant(child, target)) return true;
        return false;
    }

    private static bool BelongsToRoot(SchemaNode root, SchemaNode node) => IsDescendant(root, node) ||
        node.Identity is { } identity && identity.ConnectionId == root.Identity?.ConnectionId &&
        identity.Generation == root.Identity?.Generation;

    public static void TagChildrenWithRoot(SchemaNode root, SchemaNode node)
    {
        foreach (var child in node.Children)
            TagChildrenWithRoot(root, child);
    }
}
