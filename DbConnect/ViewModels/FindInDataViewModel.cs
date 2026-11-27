using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class FindInDataViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _mainViewModel;
    private readonly FindInDataService _svc = new();
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private DatabaseSession? _selectedSession;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _searchTerm = string.Empty;

    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _exactMatch;
    [ObservableProperty] private bool _textColumnsOnly = true;
    [ObservableProperty] private int _rowLimitPerTable = 100;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _isSearching;

    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<DatabaseSession> AvailableSessions { get; }
    public ObservableCollection<FindInDataTableGroup> Results { get; } = new();

    public override Symbol? TabIcon => Symbol.Search;

    public FindInDataViewModel(ObservableCollection<DatabaseSession> sessions, MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        AvailableSessions = sessions;
        SelectedSession = sessions.FirstOrDefault();
        Title = "Find in Data";
    }

    public FindInDataViewModel() : this(new ObservableCollection<DatabaseSession>(), new MainWindowViewModel()) { }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        if (SelectedSession == null)
        {
            StatusMessage = "Select a session first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchTerm))
        {
            StatusMessage = "Enter a search term.";
            return;
        }

        IsSearching = true;
        StatusMessage = "Searching database...";
        ProgressText = "Initializing search...";
        ProgressFraction = 0;
        ProgressPercent = 0;
        Results.Clear();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var session = SelectedSession;
        var term = SearchTerm.Trim();

        try
        {
            var options = new FindInDataOptions(
                CaseSensitive,
                ExactMatch,
                TextColumnsOnly,
                RowLimitPerTable);

            var progressReporter = new Progress<FindInDataProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ProgressText = $"Searching table {p.Current} of {p.Total}: {p.CurrentTable}";
                    ProgressFraction = p.Total > 0 ? (double)p.Current / p.Total : 0;
                    ProgressPercent = ProgressFraction * 100;
                });
            });

            int matchCount = 0;
            int errorCount = 0;

            var channel = Channel.CreateUnbounded<FindInDataMatch>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var searchTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var match in _svc.SearchAsync(session, term, options, progressReporter, ct))
                    {
                        await channel.Writer.WriteAsync(match, ct);
                    }
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct);

            await foreach (var match in channel.Reader.ReadAllAsync(ct))
            {
                if (match.IsError)
                {
                    AddErrorMatch(match);
                    errorCount++;
                    StatusMessage = errorCount == 1
                        ? $"Warning: {match.TableName} — {match.ErrorMessage}"
                        : $"Found {matchCount} matches; {errorCount} table(s) skipped with errors.";
                }
                else
                {
                    AddDataMatch(match);
                    matchCount++;
                    StatusMessage = errorCount == 0
                        ? $"Found {matchCount} matches."
                        : $"Found {matchCount} matches; {errorCount} table(s) skipped with errors.";
                }
            }

            await searchTask;

            if (matchCount == 0 && errorCount == 0)
            {
                StatusMessage = "No matches found.";
            }
            else if (matchCount == 0 && errorCount > 0)
            {
                StatusMessage = $"No matches found; {errorCount} table(s) had errors.";
            }
            else
            {
                StatusMessage = errorCount == 0
                    ? $"Search completed. Found {matchCount} matches."
                    : $"Search completed. Found {matchCount} matches; {errorCount} table(s) had errors.";
            }
            ProgressText = "Completed.";
            ProgressFraction = 1.0;
            ProgressPercent = 100;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Search cancelled.";
            ProgressText = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search error: {ex.Message}";
            ProgressText = "Error.";
        }
        finally
        {
            IsSearching = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanSearch() => !IsSearching && SelectedSession != null && !string.IsNullOrWhiteSpace(SearchTerm);

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task NavigateToRowAsync(FindInDataRowResult rowResult)
    {
        if (rowResult?.TableNode != null && rowResult.Row != null)
        {
            var val = rowResult.Row.TryGetValue(rowResult.ColumnName, out var v) ? v?.ToString() : null;
            if (val != null)
            {
                await _mainViewModel.OpenTableDataTabWithFilter(rowResult.TableNode, rowResult.ColumnName, val);
            }
            else
            {
                _mainViewModel.OpenTableDataTab(rowResult.TableNode);
            }
        }
    }

    private void AddDataMatch(FindInDataMatch match)
    {
        var tableGroup = Results.FirstOrDefault(tg => tg.TableName == match.TableName && !tg.IsError);
        if (tableGroup == null)
        {
            tableGroup = new FindInDataTableGroup(match.TableName, match.TableNode);
            Results.Add(tableGroup);
        }

        var colGroup = tableGroup.Columns.FirstOrDefault(cg => cg.ColumnName == match.ColumnName);
        if (colGroup == null)
        {
            colGroup = new FindInDataColumnGroup(match.ColumnName ?? "Unknown");
            tableGroup.Columns.Add(colGroup);
        }

        colGroup.Rows.Add(new FindInDataRowResult
        {
            TableName = match.TableName,
            ColumnName = match.ColumnName ?? "Unknown",
            Row = match.Row ?? new(),
            TableNode = match.TableNode,
            Preview = match.Preview ?? string.Empty
        });
    }

    private void AddErrorMatch(FindInDataMatch match)
    {
        var existing = Results.FirstOrDefault(tg => tg.TableName == match.TableName && tg.IsError);
        if (existing != null)
            return;

        Results.Add(new FindInDataTableGroup(match.TableName, match.TableNode, match.ErrorMessage));
    }
}

public class FindInDataTableGroup : ObservableObject
{
    public string TableName { get; }
    public SchemaNode? TableNode { get; }
    public string? ErrorMessage { get; }
    public bool IsError => ErrorMessage is not null;
    public ObservableCollection<FindInDataColumnGroup> Columns { get; } = new();

    public int MatchCount => Columns.Sum(c => c.Rows.Count);
    public string HeaderText => IsError
        ? $"{TableName} — {ErrorMessage}"
        : $"{TableName} ({MatchCount} match{(MatchCount == 1 ? "" : "es")})";

    public FindInDataTableGroup(string tableName, SchemaNode? tableNode, string? errorMessage = null)
    {
        TableName = tableName;
        TableNode = tableNode;
        ErrorMessage = errorMessage;
        Columns.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(MatchCount));
            OnPropertyChanged(nameof(HeaderText));
        };
    }
}

public class FindInDataColumnGroup : ObservableObject
{
    public string ColumnName { get; }
    public ObservableCollection<FindInDataRowResult> Rows { get; } = new();

    public string HeaderText => $"{ColumnName} ({Rows.Count})";

    public FindInDataColumnGroup(string columnName)
    {
        ColumnName = columnName;
        Rows.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(HeaderText));
        };
    }
}

public class FindInDataRowResult
{
    public string TableName { get; init; } = "";
    public string ColumnName { get; init; } = "";
    public string Preview { get; init; } = "";
    public Dictionary<string, object?> Row { get; init; } = new();
    public SchemaNode? TableNode { get; init; }
}
