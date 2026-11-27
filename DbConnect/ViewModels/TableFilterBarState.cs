using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// The table data filter bar's two modes (UI redesign 07). Builder is the tab's column rules plus its raw WHERE box;
/// SQL is a WHERE box and an ORDER BY box. Switching to SQL renders the rules as WHERE text and the sort as ORDER BY
/// text; switching back keeps the WHERE text in the raw WHERE box. Applying SQL mode sends the WHERE text through the
/// raw WHERE plumbing and the ORDER BY as the table's sort keys, so the provider quotes the columns.
/// </summary>
public sealed partial class TableFilterBarState : ObservableObject
{
    private readonly TableDataTabViewModel _tab;

    [ObservableProperty]
    private bool _isSqlMode;

    [ObservableProperty]
    private string _where = string.Empty;

    [ObservableProperty]
    private string _orderBy = string.Empty;

    [ObservableProperty]
    private string? _error;

    /// <summary>Whether the bar is shown (the toolbar's Filter toggle).</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    public TableFilterBarState(TableDataTabViewModel tab) => _tab = tab;

    [RelayCommand]
    private void ShowBuilder()
    {
        if (!IsSqlMode) return;
        _tab.RawWhere = Where.Trim();
        Where = string.Empty;
        Error = null;
        IsSqlMode = false;
    }

    [RelayCommand]
    private void ShowSql()
    {
        if (IsSqlMode || _tab.Session is not { } session) return;
        Where = TableFilterSql.ToWhere(session.Capabilities, _tab.BuildFilters(), _tab.RawWhere);
        OrderBy = TableFilterSql.ToOrderBy(session.Capabilities, _tab.SortKeys);
        _tab.FilterRows.Clear();
        _tab.RawWhere = string.Empty;
        Error = null;
        IsSqlMode = true;
    }

    /// <summary>Applies the bar in whichever mode it is in.</summary>
    [RelayCommand]
    private Task Apply() => IsSqlMode ? ApplySqlAsync() : _tab.ApplyFiltersAsync();

    /// <summary>Sends the WHERE as the raw WHERE and the ORDER BY as the sort keys, then reloads.</summary>
    public async Task ApplySqlAsync()
    {
        var (keys, error) = TableFilterSql.ParseOrderBy(OrderBy, _tab.Columns.ToList());
        Error = error;
        if (error != null) return;

        _tab.FilterSortKeys = keys;
        _tab.FilterRows.Clear();
        _tab.RawWhere = Where.Trim();
        await _tab.ApplyFiltersAsync();
    }

    [RelayCommand]
    private async Task Clear()
    {
        Where = string.Empty;
        OrderBy = string.Empty;
        Error = null;
        await _tab.ClearFiltersAsync();
    }
}
