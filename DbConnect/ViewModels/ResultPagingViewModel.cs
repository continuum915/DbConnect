using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// Continues a query result past the row cap from its still-open reader: Fetch next (one cap-sized page, also
/// used by scrolling to the end) and Fetch all (until the result ends or the Preferences row limit). Rows are
/// appended to the result and the grid is rebuilt once per fetch. Fetching is refused while the result has
/// unsaved edits. Running anything else on the tab's session closes the result.
/// </summary>
public sealed partial class ResultPagingViewModel(QueryTabViewModel tab, Action rebuild) : ObservableObject
{
    private ResultContinuation? _continuation;
    private bool _stopFetchAll;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchNextCommand), nameof(FetchAllCommand))]
    private bool _hasMore;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchNextCommand), nameof(FetchAllCommand))]
    private bool _isFetching;

    [ObservableProperty] private string _summary = string.Empty;

    private QueryResult? Result => _continuation?.Result;

    public void Attach(ResultContinuation? continuation)
    {
        _continuation = continuation;
        HasMore = continuation?.IsOpen == true;
        UpdateSummary(null);
    }

    private bool CanFetch() => HasMore && !IsFetching;

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchNextAsync() => await FetchAsync(Math.Max(1, tab.ResultRowCap), all: false);

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchAllAsync() => await FetchAsync(Math.Max(10_000, tab.ResultRowCap), all: true);

    /// <summary>Stops Fetch all after the page being read; rows already read are kept.</summary>
    [RelayCommand]
    private void StopFetching() => _stopFetchAll = true;

    /// <summary>The grid asks for the next page when it is scrolled to the end.</summary>
    public void OnScrolledToEnd()
    {
        if (FetchNextCommand.CanExecute(null)) FetchNextCommand.Execute(null);
    }

    private async Task FetchAsync(int pageSize, bool all)
    {
        if (_continuation is not { } continuation || Result is not { } result) return;
        if (tab.ResultEditor.HasChanges)
        {
            UpdateSummary("Commit or revert your edits before loading more rows.");
            return;
        }
        IsFetching = true;
        _stopFetchAll = false;
        string? note = null;
        try
        {
            do
            {
                var limit = AppPreferences.Results.MaxResultRows - result.ObjectRows.Count;
                if (limit <= 0)
                {
                    note = $"Reached the {AppPreferences.Results.MaxResultRows:N0}-row limit set in Preferences. Export the result to get every row.";
                    break;
                }
                var fetch = await continuation.FetchAsync(Math.Min(pageSize, limit));
                result.ObjectRows.AddRange(fetch.Rows);
                if (fetch.Error != null) note = $"Reading more rows failed: {fetch.Error}";
                if (fetch.Exhausted)
                {
                    result.IsTruncated = false;
                    if (fetch.LaterResults.Count > 0)
                        tab.SetExecutionResults(tab.Results.Concat(fetch.LaterResults).ToList(), result);
                }
                if (!all) UpdateSummary(note);
            } while (all && continuation.IsOpen && !_stopFetchAll && note == null);
        }
        finally
        {
            // Rebuild while still marked as fetching, so scrolling caused by the rebuild starts no new fetch.
            rebuild();
            HasMore = continuation.IsOpen;
            IsFetching = false;
            UpdateSummary(note);
        }
    }

    private void UpdateSummary(string? note)
    {
        var loaded = Result?.ObjectRows.Count ?? 0;
        var state = HasMore ? $"{loaded:N0} rows loaded · more available (scroll to the end or Fetch)"
            : _continuation != null ? $"All {loaded:N0} rows loaded" : string.Empty;
        Summary = note == null ? state : $"{state} · {note}";
    }
}
