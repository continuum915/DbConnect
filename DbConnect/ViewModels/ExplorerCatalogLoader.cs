using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Projects complete catalog scopes to UI nodes without discarding a usable stale tree.</summary>
internal static class ExplorerCatalogLoader
{
    public static SchemaNodeType ChildKind(SchemaNodeType kind) => kind switch
    {
        SchemaNodeType.TableFolder => SchemaNodeType.Table,
        SchemaNodeType.ViewFolder => SchemaNodeType.View,
        SchemaNodeType.IndexFolder => SchemaNodeType.Index,
        SchemaNodeType.TriggerFolder => SchemaNodeType.Trigger,
        SchemaNodeType.FunctionFolder => SchemaNodeType.Function,
        SchemaNodeType.ProcedureFolder => SchemaNodeType.Procedure,
        SchemaNodeType.MaterializedViewFolder => SchemaNodeType.MaterializedView,
        SchemaNodeType.SequenceFolder => SchemaNodeType.Sequence,
        SchemaNodeType.ExtensionFolder => SchemaNodeType.Extension,
        SchemaNodeType.RoleFolder => SchemaNodeType.Role,
        SchemaNodeType.ForeignKeyFolder => SchemaNodeType.ForeignKey,
        SchemaNodeType.DatabaseInfoFolder => SchemaNodeType.DatabaseInfoItem,
        _ => SchemaNodeType.Column
    };

    public static async Task LoadAsync(SchemaNode node, DatabaseSession session, MetadataScope scope, bool refresh,
        Func<bool> isOwned, Action changed, Action<string>? reportError, bool reloadDescendants = false)
    {
        if (session.Catalog.IsDisposed) return;
        await Dispatcher.UIThread.InvokeAsync(() => { node.MetadataState = MetadataLoadState.Loading; changed(); });
        try
        {
            if (refresh) await session.Catalog.RefreshAsync(scope);
            else await session.Catalog.LoadAsync(scope);
            SchemaNode[] descendants = [];
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!isOwned() || session.Catalog.IsDisposed) return;
                var snapshot = session.Catalog.GetSnapshot(scope);
                node.MetadataState = snapshot.State;
                node.MetadataError = snapshot.Error;
                if (snapshot.State is MetadataLoadState.Loaded or MetadataLoadState.LoadedEmpty)
                {
                    var records = scope == MetadataScope.Root
                        ? snapshot.Records.FirstOrDefault()?.Children ?? [] : snapshot.Records;
                    var children = ExplorerMetadataProjection.Reconcile(node.Children, records, session);
                    if (scope.Kind == SchemaNodeType.Column)
                    {
                        foreach (var folder in node.Children.Where(child => child.IsLazyLoadable))
                        {
                            children.Add(folder);
                        }
                    }
                    node.Children = children;
                    node.ChildCount = children.Count;
                    node.IsLoaded = true;
                    if (refresh || reloadDescendants) descendants = node.Children.ToArray();
                }
                if (snapshot.Error != null && snapshot.State != MetadataLoadState.Pending) reportError?.Invoke(snapshot.Error);
                changed();
            });
            foreach (var child in descendants)
            {
                if (child.Type == SchemaNodeType.Schema || child.IsLazyLoadable && child.IsLoaded ||
                    CatalogSqlCompletion.IsRelation(child.Type) && (child.IsLoaded || child.IsExpanded ||
                        session.Catalog.GetSnapshot(new(child.Identity, SchemaNodeType.Column)).HasSnapshot))
                    await LoadAsync(child, session, ExplorerMetadataProjection.Scope(child), false,
                        isOwned, changed, reportError, reloadDescendants: true);
            }
        }
        catch (ObjectDisposedException) { /* Disconnect owns removal of the explorer projection. */ }
    }
}
