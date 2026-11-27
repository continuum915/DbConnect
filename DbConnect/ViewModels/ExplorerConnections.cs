using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// Puts established connections in the explorer and takes them out again. A pinned connection is one root
/// with one session, as before. A connection that shows all databases lists the server's databases under its
/// node (system databases in a folder); each database opens its own session and catalog on first expand, and
/// the connection node acts on its default database. Database sessions stay open until closed or disconnected.
/// </summary>
public sealed class ExplorerConnections
{
    private readonly MainWindowViewModel _host;
    private readonly DatabaseService _databases;

    public ExplorerConnections(MainWindowViewModel host, DatabaseService databases)
    {
        _host = host;
        _databases = databases;
        host.SchemaTree.OpenDatabase = OpenDatabaseAsync;
        host.SchemaTree.RefreshServer = RefreshServerAsync;
    }

    public async Task RegisterAsync(EstablishedConnection established)
    {
        var info = established.Settings;
        var session = established.Session;
        var root = new SchemaNode
        {
            Name = info.Name,
            RawName = info.Name,
            Type = SchemaNodeType.Database,
            ProviderIcon = _databases.GetProviderIcon(info.Provider),
            ConnectionMeta = session.ProviderName,
            TagColor = string.IsNullOrEmpty(info.LabelColor) ? null : info.LabelColor
        };
        _ = ShowServerVersionAsync(root, session);
        var catalog = info.ShowAllDatabases ? ServerDatabaseCatalogs.For(info.Provider) : null;
        IReadOnlyList<ServerDatabase>? databases = null;
        if (catalog != null)
        {
            try { databases = await catalog.ListAsync(session.ConnectionString); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Without the list the connection still works on its own database.
                _host.StatusMessage = $"Could not list databases on {info.Name}; showing its default database only. {DiagnosticRedactor.Redact(ex.Message)}";
            }
        }

        _host.ActiveConnections[root] = info;
        _host.EstablishedConnections[root] = established;
        var schemaChildren = established.Schema.Count > 0 ? established.Schema[0].Children.ToList() : [];
        if (databases == null)
        {
            foreach (var child in schemaChildren) root.Children.Add(child);
            AddSession(root, info, session);
        }
        else
        {
            var defaultName = catalog!.DatabaseOf(session.ConnectionString) ?? established.Schema.FirstOrDefault()?.CatalogName;
            var defaultNode = BuildDatabaseNodes(root, databases, defaultName);
            defaultNode.Children = new(schemaChildren);
            defaultNode.IsLoaded = true;
            defaultNode.IsExpanded = true;
            _host.SchemaTree.ServerDefaults[root] = defaultNode;
            _host.ActiveConnections[defaultNode] = info;
            AddSession(defaultNode, info, session);
        }

        _host.StatusConnectionText = $"Connected: {ConnectionDisplayName.Format(info.Name, session.ProviderName)} [{info.EnvironmentTier}]";
        _host.StatusHasError = false;
        _host.SnippetPanel.ActiveConnectionName = info.Name;
        _host.SnippetPanel.ActiveDatabaseName = info.Name;
        Dispatcher.UIThread.Invoke(() => _host.SchemaNodes.Add(root));
    }

    /// <summary>One node per database; system databases go in a collapsed folder. Returns the default database node.</summary>
    private static SchemaNode BuildDatabaseNodes(SchemaNode root, IReadOnlyList<ServerDatabase> databases, string? defaultName)
    {
        SchemaNode? defaultNode = null;
        var system = new SchemaNode { Name = "System Databases", Type = SchemaNodeType.DatabaseFolder };
        foreach (var database in databases)
        {
            var node = DatabaseNode(database);
            if (defaultName != null && database.Name == defaultName) defaultNode = node;
            (database.IsSystem && node != defaultNode ? system : root).Children.Add(node);
        }
        if (defaultNode == null)
        {
            defaultNode = DatabaseNode(new ServerDatabase(defaultName ?? "(default)", false, true));
            root.Children.Insert(0, defaultNode);
        }
        if (system.Children.Count > 0) root.Children.Add(system);
        root.IsExpanded = true;
        return defaultNode;
    }

    private static SchemaNode DatabaseNode(ServerDatabase database)
    {
        var node = new SchemaNode
        {
            Name = database.Note == null ? database.Name : $"{database.Name} ({database.Note})",
            RawName = database.Name,
            Type = SchemaNodeType.Database,
            IsLazyLoadable = database.IsAccessible,
            Description = database.Note
        };
        if (database.IsAccessible) node.Children.Add(new SchemaNode { Name = "Loading…", Type = SchemaNodeType.Column });
        return node;
    }

    private void AddSession(SchemaNode node, ConnectionInfo info, DatabaseSession session)
    {
        if (ServerOf(node) is { } server && server != node)
            session.Server = new ServerDatabaseDirectory(() => DatabasesFor(session),
                database => SessionForDatabaseAsync(session, database),
                ServerDatabaseCatalogs.For(info.Provider)?.CurrentDatabaseSql);
        _host.ActiveSessions[node] = session;
        _host.ActiveSessionOptions.Add(session);
        _ = Task.Run(() =>
        {
            try
            {
                var index = new SchemaIndexer().BuildIndex(node.Children, session.Capabilities);
                index.Provider = info.Provider;
                Dispatcher.UIThread.Invoke(() =>
                {
                    _host.ActiveIndexes[node] = index;
                    foreach (var tab in _host.Tabs.OfType<QueryTabViewModel>())
                    {
                        tab.SetSessionIndex(session, index);
                        if (tab.SelectedSession == null && tab.WaitingForConnection?.Matches(session.Connection) != false)
                            tab.SelectedSession = session;
                    }
                });
            }
            catch { /* completion works without the index */ }
        });
    }

    private SchemaNode? ServerOf(SchemaNode node) =>
        _host.SchemaTree.ServerDefaults.Keys.FirstOrDefault(root => SchemaTreeViewModel.IsDescendant(root, node));

    /// <summary>Opens a server database's session and loads its schema tree under its node.</summary>
    public async Task OpenDatabaseAsync(SchemaNode node)
    {
        if (_host.ActiveSessions.ContainsKey(node) || ServerOf(node) is not { } root ||
            !_host.ActiveConnections.TryGetValue(root, out var server) ||
            ServerDatabaseCatalogs.For(server.Provider) is not { } catalog) return;
        var info = server.Clone();
        info.ConnectionString = catalog.WithDatabase(server.ConnectionString, node.RawName ?? node.Name);
        var session = _databases.CreateSession(info);
        node.MetadataState = MetadataLoadState.Loading;
        try
        {
            var schema = await session.GetSchemaAsync();
            node.Children = new(schema.Count > 0 ? schema[0].Children : []);
            node.IsLoaded = true;
            node.MetadataState = MetadataLoadState.Loaded;
            _host.ActiveConnections[node] = info;
            AddSession(node, info, session);
            _host.SchemaTree.UpdateFilteredSchemaNodes();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            session.Catalog.Dispose();
            node.MetadataState = MetadataLoadState.Failed;
            node.MetadataError = DiagnosticRedactor.Redact(ex.Message);
            _host.SchemaTree.ReportError?.Invoke($"Could not open database {node.RawName}: {node.MetadataError}");
        }
    }

    /// <summary>New Query Tab on an explorer node: the tab targets that node's database, opening it if needed.</summary>
    public async Task NewQueryTabAsync(SchemaNode? node)
    {
        node = node?.RealNode ?? node;
        if (node is { Type: SchemaNodeType.Database } && !_host.ActiveSessions.ContainsKey(node) &&
            ServerOf(node) is { } server && server != node)
            await OpenDatabaseAsync(node);
        _host.NewQueryTabWithSession(node == null ? null : _host.SchemaTree.FindSessionForNode(node));
    }

    /// <summary>The session of <paramref name="database"/> on the server behind <paramref name="session"/>, opening it if needed.</summary>
    public async Task<DatabaseSession?> SessionForDatabaseAsync(DatabaseSession session, string database)
    {
        var owner = _host.SchemaTree.FindRootNodeForSession(session);
        var root = owner == null ? null : ServerOf(owner);
        var node = root == null ? null : AllDatabaseNodes(root).FirstOrDefault(n => n.RawName == database);
        if (node == null) return null;
        if (!_host.ActiveSessions.ContainsKey(node)) await OpenDatabaseAsync(node);
        return _host.ActiveSessions.GetValueOrDefault(node);
    }

    /// <summary>The databases on the server behind <paramref name="session"/>, or empty for a pinned connection.</summary>
    public IReadOnlyList<string> DatabasesFor(DatabaseSession session)
    {
        var owner = _host.SchemaTree.FindRootNodeForSession(session);
        var root = owner == null ? null : ServerOf(owner);
        return root == null ? [] : AllDatabaseNodes(root).Where(n => n.IsLazyLoadable || n.IsLoaded)
            .Select(n => n.RawName ?? n.Name).ToList();
    }

    private static IEnumerable<SchemaNode> AllDatabaseNodes(SchemaNode root) =>
        root.Children.SelectMany(c => c.Type == SchemaNodeType.DatabaseFolder ? c.Children : [c])
            .Where(c => c.Type == SchemaNodeType.Database);

    /// <summary>Reloads a server's database list, keeping open databases and their trees.</summary>
    private async Task RefreshServerAsync(SchemaNode root)
    {
        if (!_host.ActiveConnections.TryGetValue(root, out var server) || ServerDatabaseCatalogs.For(server.Provider) is not { } catalog)
            return;
        var databases = await catalog.ListAsync(server.ConnectionString);
        var open = AllDatabaseNodes(root).Where(_host.ActiveSessions.ContainsKey).ToDictionary(n => n.RawName ?? n.Name);
        var defaultName = _host.SchemaTree.ServerDefaults.GetValueOrDefault(root)?.RawName;
        root.Children.Clear();
        var rebuilt = BuildDatabaseNodes(root, databases, defaultName);
        foreach (var node in AllDatabaseNodes(root).ToList())
        {
            if (!open.TryGetValue(node.RawName ?? node.Name, out var existing)) continue;
            var parent = node == rebuilt ? root : SchemaTreeViewModel.FindParentOf(root, node)!;
            parent.Children[parent.Children.IndexOf(node)] = existing;
            if (node == rebuilt) _host.SchemaTree.ServerDefaults[root] = existing;
        }
        _host.SchemaTree.UpdateFilteredSchemaNodes();
        foreach (var node in open.Values) await _host.SchemaTree.RefreshSchemaAsync(node);
    }

    /// <summary>
    /// Disconnects a connection, or on a server database closes just that database: its session, tabs and tree
    /// are released and the node can be opened again.
    /// </summary>
    /// <summary>Shows "MySQL 8.0.36" (the provider's version string) as the connection row's meta once it is read.</summary>
    private static async Task ShowServerVersionAsync(SchemaNode root, DatabaseSession session)
    {
        try
        {
            var version = await session.Provider.GetServerVersionAsync(session.ConnectionString);
            if (!string.IsNullOrWhiteSpace(version)) root.ConnectionMeta = version.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The provider name stays as the meta.
        }
    }

    public void Disconnect(SchemaNode node)
    {
        var target = _host.SchemaTree.FindRootNode(node);
        var server = ServerOf(target);
        if (server != null && server != target)
        {
            CloseSession(target);
            target.Children = new([new SchemaNode { Name = "Loading…", Type = SchemaNodeType.Column }]);
            target.IsLoaded = false;
            target.IsExpanded = false;
            _host.SchemaTree.UpdateFilteredSchemaNodes();
            return;
        }
        var name = _host.ActiveConnections.GetValueOrDefault(target)?.Name
                   ?? _host.ActiveSessions.GetValueOrDefault(target)?.Name ?? target.Name;
        if (server != null)
        {
            foreach (var database in AllDatabaseNodes(server).Where(_host.ActiveSessions.ContainsKey).ToList())
                CloseSession(database);
            _host.SchemaTree.ServerDefaults.Remove(server);
        }
        else if (!_host.ActiveSessions.ContainsKey(target)) return;
        else CloseSession(target);

        _host.ActiveConnections.Remove(target);
        _host.SchemaNodes.Remove(target);
        if (_host.EstablishedConnections.Remove(target, out var established))
            established.Dispose();
        _host.SchemaTree.UpdateFilteredSchemaNodes();
        if (_host.SelectedTab != null && !_host.Tabs.Contains(_host.SelectedTab))
            _host.SelectedTab = _host.Tabs.FirstOrDefault();
        _host.StatusMessage = $"Disconnected {name}.";
        _host.StatusConnectionText = $"Disconnected {name}.";
        _host.StatusHasError = false;
    }

    private void CloseSession(SchemaNode node)
    {
        if (!_host.ActiveSessions.TryGetValue(node, out var session)) return;
        _host.SchemaTree.RemoveSession(node);
        if (!_host.SchemaTree.ServerDefaults.ContainsKey(node)) _host.ActiveConnections.Remove(node);
        _host.ActiveIndexes.Remove(node);
        _host.ActiveSessionOptions.Remove(session);
        foreach (var tab in _host.Tabs.OfType<QueryTabViewModel>())
            tab.RemoveSession(session);
        foreach (var tab in _host.Tabs.Where(tab => MainWindowViewModel.IsTabOwnedBySession(tab, session)).ToList())
        {
            _host.Tabs.Remove(tab);
            _ = TabStripActions.ReleaseSessionAsync(tab);
        }
        if (_host.SelectedTab != null && !_host.Tabs.Contains(_host.SelectedTab))
            _host.SelectedTab = _host.Tabs.FirstOrDefault();
    }
}
