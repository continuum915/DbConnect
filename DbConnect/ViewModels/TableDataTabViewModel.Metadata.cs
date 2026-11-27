using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class TableDataTabViewModel
{
    private TableMetadataSubscription? _metadataSubscription;
    private bool _metadataRequiresReload;

    private void InitializeMetadata()
    {
        var tableColumns = _tableNode?.Children
            .Where(child => child.Type == SchemaNodeType.Column)
            .Select(child => child.RawName ?? child.Name)
            .ToList() ?? [];

        _primaryKeyColumns = _tableNode?.Children
            .Where(child => child.Type == SchemaNodeType.Column && child.IsPrimaryKey)
            .Select(child => child.RawName ?? child.Name)
            .ToList() ?? [];

        _stableOrderColumns = _primaryKeyColumns.Count > 0 ? _primaryKeyColumns : tableColumns;
        if (_session == null) return;
        _metadataSubscription = new(this, _session.Catalog, static async (tab, scope) =>
        {
            if (tab.ObjectSelection.Identity is not { } identity || !scope.Contains(new(identity, SchemaNodeType.Column))) return;
            if (tab._dbSessionClosed || tab._session == null || tab._session.Catalog.IsDisposed) return;
            await tab._session.Catalog.LoadAsync(new(identity, SchemaNodeType.Column));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (tab._dbSessionClosed || tab._session == null || tab._session.Catalog.IsDisposed) return;
                var snapshot = tab._session.Catalog.GetSnapshot(new(identity, SchemaNodeType.Column));
                tab._enumColumnsLoadedForTable = null;
                tab._enumColumns.Clear();
                if (snapshot.State is MetadataLoadState.Loaded or MetadataLoadState.LoadedEmpty)
                {
                    (tab._primaryKeyColumns, tab._stableOrderColumns) = TableMetadataOrder.FromSnapshot(snapshot);
                }
                tab._metadataRequiresReload = true;
                tab.Editing.Tracker = null;
                tab.SetBaseStatus(snapshot.Error ?? tab._session.Catalog.GetSnapshot(scope).Error ?? "Table metadata refreshed. Refresh table data to use the current columns; pending edits are retained.");
                tab.OnPropertyChanged(nameof(HasReliableIdentity));
                tab.OnPropertyChanged(nameof(CanEditExistingRows));
                tab.OnPropertyChanged(nameof(CanAddRows));
                tab.OnPropertyChanged(nameof(IdentityModeLabel));
                tab.OnPropertyChanged(nameof(IdentityModeSummary));
                tab.RefreshDirtyState();
            });
        });
    }
    private async Task EnsureWorkspaceLoadedAsync()
    {
        if (_workspaceInitialized || _workspaceService == null)
            return;

        PageSize = await _workspaceService.LoadTablePageSizeAsync();
        if (_session != null)
            await LoadColumnLayoutAsync(_workspaceService, _session);
        _workspaceInitialized = true;
    }

    /// <summary>
    /// RAIN-P2-09 — Fetches ENUM/SET (MySQL) or native enum (PostgreSQL) column metadata for the
    /// current table, once per table: this is a metadata catalog query, not part of the row data,
    /// so it need not re-run on every page/refresh. A column the resolver already classified as
    /// Text (e.g. a PostgreSQL column backed by a custom enum type, which reports its own type
    /// name rather than a recognizable "ENUM") is reclassified here once the provider confirms it.
    /// </summary>
    private async Task EnsureEnumColumnsLoadedAsync()
    {
        if (_session == null || _enumColumnsLoadedForTable == TableObjectName)
            return;

        var revision = ObjectSelection.MetadataRevision;
        var columns = await ObjectSelection.GetEnumsAsync(_session, TableObjectName);
        if (_session.Catalog.GetSnapshot(new(ObjectSelection.Identity, SchemaNodeType.Column)).Revision != revision) return;
        _enumColumns = columns;
        _enumColumnsLoadedForTable = TableObjectName;

        foreach (var (columnName, info) in _enumColumns)
        {
            if (_columnTypes.ContainsKey(columnName))
                _columnTypes[columnName] = info.AllowsMultipleValues ? ColumnDataType.Set : ColumnDataType.Enum;
        }
    }

}

