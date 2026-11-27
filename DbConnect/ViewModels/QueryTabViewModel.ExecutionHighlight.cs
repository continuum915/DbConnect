using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class QueryTabViewModel
{
    public int SelectedQueryStart { get; set; }

    [ObservableProperty]
    private StatementSpan? _runningStatementSpan;

    [ObservableProperty]
    private StatementSpan? _errorSpan;

    public void NavigateToError(string sourceSql, string submittedSql, int sourceOffset,
        QueryResult result, System.Collections.Generic.IReadOnlyList<int>? sourceOffsets = null)
    {
        RememberErrorNavigation(sourceSql, submittedSql, sourceOffset, sourceOffsets);
        ErrorSpan = QueryErrorNavigator.FindToken(QueryText, sourceSql, submittedSql, sourceOffset, result, sourceOffsets);
    }
}
