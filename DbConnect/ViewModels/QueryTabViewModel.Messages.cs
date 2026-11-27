using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.ViewModels;

public partial class QueryTabViewModel
{
    private sealed record ErrorNavigationContext(string SourceSql, string SubmittedSql,
        int SourceOffset, IReadOnlyList<int>? SourceOffsets);

    private ErrorNavigationContext? _errorNavigation;

    public string BatchStatementSummary =>
        CurrentResult?.Statements.Count > 0
            ? string.Join(Environment.NewLine, CurrentResult.Statements.Select(statement => statement.Summary))
            : string.Empty;

    public IReadOnlyList<QueryMessageRow> MessagesRows
    {
        get
        {
            if (CurrentResult == null)
                return [];

            if (CurrentResult.Statements.Count == 0)
                return CurrentResult.HasError
                    ? [new QueryMessageRow(new QueryStatementExecution
                    {
                        Index = 0,
                        Error = CurrentResult.Error ?? QueryErrorInfo.Failure(string.Empty,
                            CurrentResult.ErrorMessage!, null),
                        Duration = CurrentResult.Duration, RecordsAffected = CurrentResult.RecordsAffected,
                        Messages = CurrentResult.Messages
                    }, () => NavigateMessageError(CurrentResult))]
                    : [];

            return CurrentResult.Statements.Select(statement => new QueryMessageRow(statement,
                () => NavigateMessageError(Results.FirstOrDefault(result =>
                    result.ReturnedStatementIndex == statement.Index && result.HasError)))).ToArray();
        }
    }

    private void NavigateMessageError(QueryResult? result)
    {
        if (result?.HasError != true || _errorNavigation is not { } context)
            return;

        NavigateToError(context.SourceSql, context.SubmittedSql, context.SourceOffset, result, context.SourceOffsets);
        if (ErrorSpan != null || result.Error?.Position != null ||
            context.SourceOffset < 0 || context.SourceOffset + context.SourceSql.Length > QueryText.Length ||
            !QueryText.AsSpan(context.SourceOffset, context.SourceSql.Length).SequenceEqual(context.SourceSql))
            return;

        var submittedIndex = result.StatementStart;
        if (submittedIndex < 0)
            return;
        while (submittedIndex < context.SubmittedSql.Length && char.IsWhiteSpace(context.SubmittedSql[submittedIndex]))
            submittedIndex++;
        if (submittedIndex >= context.SubmittedSql.Length ||
            context.SourceOffsets != null && context.SourceOffsets.Count != context.SubmittedSql.Length)
            return;

        var sourceIndex = context.SourceOffsets == null ? submittedIndex : context.SourceOffsets[submittedIndex];
        if (sourceIndex >= 0 && sourceIndex < context.SourceSql.Length)
            ErrorSpan = new DbConnect.Services.StatementSpan(context.SourceOffset + sourceIndex, 1);
    }

    private void RememberErrorNavigation(string sourceSql, string submittedSql, int sourceOffset,
        IReadOnlyList<int>? sourceOffsets)
    {
        _errorNavigation = new ErrorNavigationContext(sourceSql, submittedSql, sourceOffset, sourceOffsets);
    }
}
