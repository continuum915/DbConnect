using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Updates explorer projections on the UI thread after DbConnect-owned schema execution.</summary>
internal sealed class SchemaChangeProjection(SchemaTreeViewModel tree)
{
    private readonly Dictionary<DatabaseSession, Func<MetadataScope, Task>> _subscriptions = new();

    public void Synchronize()
    {
        var sessions = tree.SchemaNodes.Select(tree.FindSessionForNode).OfType<DatabaseSession>().ToHashSet();
        foreach (var old in _subscriptions.Keys.Where(session => !sessions.Contains(session)).ToArray())
        {
            old.SchemaChanges.Changed -= _subscriptions[old];
            _subscriptions.Remove(old);
        }
        foreach (var session in sessions.Where(session => !_subscriptions.ContainsKey(session)))
        {
            Task Changed(MetadataScope scope) => ApplyAsync(session, scope);
            _subscriptions.Add(session, Changed);
            session.SchemaChanges.Changed += Changed;
        }
    }

    private async Task ApplyAsync(DatabaseSession session, MetadataScope scope)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var root = tree.FindRootNodeForSession(session);
            if (root == null || session.Catalog.IsDisposed) return;
            if (session.Catalog.GetSnapshot(scope).State == MetadataLoadState.Pending)
            {
                MarkPending(root, scope, session.Catalog.GetSnapshot(scope).Error);
                tree.UpdateFilteredSchemaNodes();
                return;
            }
            var target = scope == MetadataScope.Root ? root : Find(root, scope.Parent);
            if (target == null) return;
            if (scope.Kind is not (SchemaNodeType.Database or SchemaNodeType.Schema or SchemaNodeType.Column))
                target = target.Children.FirstOrDefault(child => ExplorerCatalogLoader.ChildKind(child.Type) == scope.Kind) ?? target;
            await tree.RefreshNodeAsync(target);
        });
    }

    private static SchemaNode? Find(SchemaNode node, DatabaseObjectIdentity? identity) => node.Identity == identity
        ? node : node.Children.Select(child => Find(child, identity)).FirstOrDefault(found => found != null);

    private static void MarkPending(SchemaNode node, MetadataScope scope, string? message)
    {
        if (node.Type != SchemaNodeType.Column && MetadataScopeCoverage.Contains(scope, new(node.Identity, SchemaNodeType.Column)))
        {
            node.MetadataError = message;
            node.MetadataState = MetadataLoadState.Pending;
        }
        foreach (var child in node.Children) MarkPending(child, scope, message);
    }
}
