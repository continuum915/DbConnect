using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Search-only projections of relation names; browsing and shared loads retain their ownership.</summary>
public partial class ExplorerSearchViewModel(SchemaTreeViewModel tree) : ViewModelBase
{
    private readonly Dictionary<SchemaNode, IReadOnlyList<SchemaNode>> _children = new();
    private int _revision;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isIncomplete;
    public Task Completion { get; private set; } = Task.CompletedTask;

    internal IEnumerable<SchemaNode> Children(SchemaNode node) =>
        _children.TryGetValue(node, out var children) ? children : node.Children;

    internal bool HasProjection(SchemaNode node) => _children.ContainsKey(node) || node.Children.Any(HasProjection);

    internal void Begin()
    {
        var revision = ++_revision;
        _children.Clear();
        IsLoading = false;
        IsIncomplete = false;
        if (string.IsNullOrWhiteSpace(tree.SchemaSearchText))
        {
            Completion = Task.CompletedTask;
            tree.UpdateFilteredSchemaNodes();
            return;
        }
        var scopes = tree.SchemaNodes.SelectMany(root => Folders(root)
            .Select(folder => (Folder: folder, Session: tree.FindSessionForNode(root))))
            .Where(item => item.Session != null && !item.Session.Catalog.IsDisposed).ToList();
        foreach (var item in scopes)
        {
            var identity = tree.GetIdentity(item.Folder, item.Session!);
            var snapshot = item.Session!.Catalog.GetSnapshot(new(identity.Parent ?? identity, ExplorerCatalogLoader.ChildKind(item.Folder.Type)));
            Project(item.Folder, snapshot);
        }
        IsLoading = scopes.Count > 0;
        tree.UpdateFilteredSchemaNodes();
        Completion = LoadAsync();

        async Task LoadAsync()
        {
            await Task.WhenAll(scopes.Select(async item =>
            {
                var session = item.Session!;
                var identity = tree.GetIdentity(item.Folder, session);
                var scope = new MetadataScope(identity.Parent ?? identity, ExplorerCatalogLoader.ChildKind(item.Folder.Type));
                try
                {
                    await session.Catalog.LoadAsync(scope);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (revision != _revision || session.Catalog.IsDisposed || tree.FindSessionForNode(item.Folder) != session) return;
                        var snapshot = session.Catalog.GetSnapshot(scope);
                        IsIncomplete |= snapshot.State is not (MetadataLoadState.Loaded or MetadataLoadState.LoadedEmpty);
                        Project(item.Folder, snapshot);
                        tree.UpdateFilteredSchemaNodes();
                    });
                }
                catch (ObjectDisposedException) { /* A disconnected session no longer contributes coverage. */ }
            }));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (revision != _revision) return;
                IsLoading = false;
                tree.UpdateFilteredSchemaNodes();
            });
        }
    }

    private void Project(SchemaNode folder, MetadataSnapshot snapshot)
    {
        if (!snapshot.HasSnapshot) return;
        _children[folder] = snapshot.Records.Select(record =>
        {
            var original = folder.Children.FirstOrDefault(child => child.Identity == record.Identity);
            if (original != null) return original;
            var node = ProviderMetadataCatalogSource.Project(record);
            if (tree.FindSessionForNode(folder) is { } session) ExplorerMetadataProjection.ApplyColumns(node, session);
            if (snapshot.State is MetadataLoadState.Stale or MetadataLoadState.Failed or MetadataLoadState.Loading or MetadataLoadState.Pending)
                node.MetadataState = snapshot.State;
            node.MetadataError = snapshot.Error;
            return node;
        }).ToList();
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        foreach (var root in tree.SchemaNodes.ToArray())
        {
            var session = tree.FindSessionForNode(root);
            if (session == null || session.Catalog.IsDisposed) continue;
            if (session.Catalog.GetSnapshot(MetadataScope.Root).Error != null)
                await tree.RefreshSchemaAsync(root);
            else
                foreach (var schema in root.Children.Where(node => node.Type == SchemaNodeType.Schema).ToArray())
                    if (session.Catalog.GetSnapshot(new(schema.Identity, SchemaNodeType.Schema)).Error != null)
                        await tree.RefreshNodeAsync(schema);
            foreach (var folder in Folders(root))
            {
                var identity = tree.GetIdentity(folder, session);
                var scope = new MetadataScope(identity.Parent ?? identity, ExplorerCatalogLoader.ChildKind(folder.Type));
                if (session.Catalog.GetSnapshot(scope).Error != null) session.Catalog.Invalidate(scope);
            }
        }
        Begin();
        await Completion;
    }

    private static IEnumerable<SchemaNode> Folders(SchemaNode node)
    {
        if (node.IsLazyLoadable && node.Type is SchemaNodeType.TableFolder or SchemaNodeType.ViewFolder or SchemaNodeType.MaterializedViewFolder)
            yield return node;
        // Relation details (columns, indexes, triggers) are never part of search coverage.
        if (node.Type is SchemaNodeType.Database or SchemaNodeType.Schema)
            foreach (var child in node.Children)
                foreach (var folder in Folders(child)) yield return folder;
    }
}
