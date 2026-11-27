using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class QueryTabViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResultTabs))]
    private System.Collections.Generic.IReadOnlyList<QueryResult> _resultTabs = [];

    [ObservableProperty]
    private string _resultTabsSummary = string.Empty;

    public bool ShowResultTabs => ResultTabs.Count > 1;

    /// <summary>The result strip: Messages, a pane per result set, Profile, Status, Explain, Chart.</summary>
    public QueryResultPanes Panes => field ??= new(this);

    partial void OnResultsChanged(ObservableCollection<QueryResult> value)
    {
        ResultTabs = ResultTabSelection.Select(value);
        ResultTabsSummary = ResultTabs.Count < value.Count
            ? $"Showing {ResultTabs.Count:N0} of {value.Count:N0} results. See Messages for every statement."
            : string.Empty;
    }
}
