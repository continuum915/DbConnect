using System.Collections.Generic;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class TableDataTabViewModel
{
    /// <summary>One row at a time as name/value pairs (Form View).</summary>
    public RecordViewModel Record => field ??= new(new TableRecordSource(this));

    /// <summary>The record pane beside the grid: the current row and the focused cell.</summary>
    public RecordPaneState RecordPane => field ??= new(this);

    /// <summary>The filter bar's Builder / SQL modes.</summary>
    public TableFilterBarState FilterBar => field ??= new(this);

    /// <summary>The sort keys, replaceable by the filter bar's ORDER BY.</summary>
    internal IReadOnlyList<TableSortKey> FilterSortKeys
    {
        get => _sortKeys;
        set
        {
            _sortKeys.Clear();
            _sortKeys.AddRange(value);
            OnPropertyChanged(nameof(SortSummary));
        }
    }

    /// <summary>
    /// Shown when the server or network dropped this tab's session. Reconnect drops the dead session without
    /// closing the tab, opens a new one and reloads the current page.
    /// </summary>
    public ConnectionLossViewModel ConnectionLoss => field ??= new(async () =>
    {
        IDbSession? dead;
        lock (_dbSessionLock)
        {
            _dbSessionGeneration++;
            dead = _dbSession;
            _dbSession = null;
        }
        if (dead != null)
        {
            try { await dead.DisposeAsync(); }
            catch { /* the connection is already gone */ }
        }
        await GetOrOpenDbSessionAsync();
        await RefreshInternalAsync(bypassDirtyCheck: true);
    });
}
