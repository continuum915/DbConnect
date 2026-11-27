using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;

namespace DbConnect.ViewModels;

/// <summary>What a query tab's result pane shows.</summary>
public enum ResultPaneKind
{
    Messages,
    Result,
    Profile,
    Status,
    Explain,
    Chart,
}

/// <summary>One tab in the query tab's result strip; a Result pane stands for one result set.</summary>
public sealed record ResultPane(ResultPaneKind Kind, string Header, QueryResult? Result = null);

/// <summary>
/// The result strip of a query tab (the design's QueryEditor artboard): Messages, one Result pane per result set,
/// Profile, Status, Explain and Chart. Choosing a Result pane shows that result in the one results grid; running a
/// query shows its result (or Messages when it returned none); Explain shows the plan.
/// </summary>
public sealed partial class QueryResultPanes : ObservableObject
{
    private readonly QueryTabViewModel _tab;
    private bool _syncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMessages), nameof(IsResult), nameof(IsProfile), nameof(IsStatus), nameof(IsExplain),
        nameof(IsChart))]
    private ResultPane? _selected;

    /// <summary>Explain shows the plan as a diagram (the parsed tree) or as text (the raw rows).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlanDiagram), nameof(ShowPlanText))]
    private bool _isPlanText;

    public QueryResultPanes(QueryTabViewModel tab)
    {
        _tab = tab;
        Rebuild();
        tab.PropertyChanged += OnTabPropertyChanged;
    }

    public ObservableCollection<ResultPane> Items { get; } = [];

    public bool IsMessages => Selected?.Kind == ResultPaneKind.Messages;
    public bool IsResult => Selected?.Kind == ResultPaneKind.Result;
    public bool IsProfile => Selected?.Kind == ResultPaneKind.Profile;
    public bool IsStatus => Selected?.Kind == ResultPaneKind.Status;
    public bool IsExplain => Selected?.Kind == ResultPaneKind.Explain;
    public bool IsChart => Selected?.Kind == ResultPaneKind.Chart;

    /// <summary>The footer's executed SQL, on one line.</summary>
    public string FooterSqlText
    {
        get
        {
            if (_tab.CurrentResult is not { } result) return _tab.ResultSummary;
            var sql = result.ExecutedSql;
            var preview = sql.Length > 300 ? sql[..300] : sql;
            return string.Join(' ', preview.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
        }
    }

    public string FooterTimeText => _tab.CurrentResult is { } result
        ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "Query time: {0:0.000} s", result.Duration.TotalSeconds)
        : string.Empty;

    /// <summary>"N rows", with "· all fetched" once every row of the result is loaded.</summary>
    public string FooterRowsText => _tab.CurrentResult is { HasResultSet: true } result
        ? $"{result.ObjectRows.Count:N0} rows{(result.IsTruncated || _tab.Paging.HasMore ? string.Empty : " · all fetched")}"
        : string.Empty;

    public bool ShowPlanDiagram => !IsPlanText && _tab.ExplainPlan?.HasStructuredPlan == true;
    public bool ShowPlanText => !ShowPlanDiagram;

    [RelayCommand]
    private void ShowDiagram() => IsPlanText = false;

    [RelayCommand]
    private void ShowText() => IsPlanText = true;

    partial void OnSelectedChanged(ResultPane? value)
    {
        if (_syncing || value is not { Kind: ResultPaneKind.Result, Result: { } result }) return;
        _syncing = true;
        try { _tab.SelectedResult = result; }
        finally { _syncing = false; }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(QueryTabViewModel.ResultTabs):
                Rebuild();
                break;
            case nameof(QueryTabViewModel.SelectedResult) when !_syncing:
                SelectResult(_tab.SelectedResult);
                break;
            case nameof(QueryTabViewModel.ShowExplainPane) when _tab.ShowExplainPane:
            case nameof(QueryTabViewModel.IsExplaining) when _tab.IsExplaining:
                Selected = Items.First(p => p.Kind == ResultPaneKind.Explain);
                break;
            case nameof(QueryTabViewModel.CurrentResult) or nameof(QueryTabViewModel.DisplayResultRows)
                or nameof(QueryTabViewModel.ResultSummary):
                OnPropertyChanged(nameof(FooterSqlText));
                OnPropertyChanged(nameof(FooterTimeText));
                OnPropertyChanged(nameof(FooterRowsText));
                break;
            case nameof(QueryTabViewModel.ExplainPlan):
                OnPropertyChanged(nameof(ShowPlanDiagram));
                OnPropertyChanged(nameof(ShowPlanText));
                break;
        }
    }

    private void Rebuild()
    {
        var keep = Selected;
        _syncing = true;
        try
        {
            Items.Clear();
            Items.Add(new ResultPane(ResultPaneKind.Messages, "Messages"));
            var results = _tab.ResultTabs.Where(r => r.HasResultSet).ToList();
            for (var i = 0; i < results.Count; i++)
                Items.Add(new ResultPane(ResultPaneKind.Result, results.Count == 1 ? "Result" : $"Result {i + 1}", results[i]));
            Items.Add(new ResultPane(ResultPaneKind.Profile, "Profile"));
            Items.Add(new ResultPane(ResultPaneKind.Status, "Status"));
            Items.Add(new ResultPane(ResultPaneKind.Explain, "Explain"));
            Items.Add(new ResultPane(ResultPaneKind.Chart, "Chart"));
        }
        finally
        {
            _syncing = false;
        }

        if (keep is { Kind: not ResultPaneKind.Result })
            Selected = Items.First(p => p.Kind == keep.Kind);
        else
            SelectResult(_tab.SelectedResult);
    }

    /// <summary>Shows <paramref name="result"/>'s pane, or Messages when the run returned no result set.</summary>
    private void SelectResult(QueryResult? result)
    {
        _syncing = true;
        try
        {
            Selected = Items.FirstOrDefault(p => p.Kind == ResultPaneKind.Result && ReferenceEquals(p.Result, result))
                ?? Items[0];
        }
        finally
        {
            _syncing = false;
        }
    }
}
