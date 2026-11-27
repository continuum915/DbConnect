using System;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Keeps a catalog from retaining closed or abandoned table tabs.</summary>
internal sealed class TableMetadataSubscription : IDisposable
{
    private readonly MetadataCatalog _catalog;
    private readonly WeakReference<TableDataTabViewModel> _tab;
    private readonly Func<TableDataTabViewModel, MetadataScope, Task> _apply;

    public TableMetadataSubscription(TableDataTabViewModel tab, MetadataCatalog catalog,
        Func<TableDataTabViewModel, MetadataScope, Task> apply)
    {
        _catalog = catalog;
        _tab = new(tab);
        _apply = apply;
        catalog.Refreshed += RefreshAsync;
    }

    private Task RefreshAsync(MetadataScope scope)
    {
        if (_tab.TryGetTarget(out var tab)) return _apply(tab, scope);
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _catalog.Refreshed -= RefreshAsync;
}
