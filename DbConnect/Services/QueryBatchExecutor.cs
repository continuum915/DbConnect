using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

internal static class QueryBatchExecutor
{
    internal sealed record BatchExecutionResult(QueryResult DisplayResult, IReadOnlyList<QueryResult> Results);

    internal static Task<BatchExecutionResult> ExecuteAsync(DatabaseSession session, IDbSession dbSession, string sql,
        QueryExecutionScope scope, int timeoutSeconds, int resultRowCap, Func<int, Task> showStatementAsync,
        System.Threading.CancellationToken cancellationToken, IReadOnlyList<StatementSpan> spans) =>
        ExecuteAsync(session, dbSession, sql, spans, scope, timeoutSeconds, resultRowCap, showStatementAsync, cancellationToken);

    internal static Task<BatchExecutionResult> ExecuteAsync(
        DatabaseSession session,
        IDbSession dbSession,
        string sql,
        QueryExecutionScope scope,
        int timeoutSeconds,
        int resultRowCap,
        Func<int, Task> showStatementAsync,
        System.Threading.CancellationToken cancellationToken) =>
        ExecuteAsync(session, dbSession, sql, SqlBatchParser.GetExecutionSpans(sql, session.Capabilities.BatchDialect, dbSession.IsInTransaction, cancellationToken),
            scope, timeoutSeconds, resultRowCap, showStatementAsync, cancellationToken);

    internal static async Task<BatchExecutionResult> ExecuteAsync(
        DatabaseSession session,
        IDbSession dbSession,
        string sql,
        IReadOnlyList<StatementSpan> statementSpans,
        QueryExecutionScope scope,
        int timeoutSeconds,
        int resultRowCap,
        Func<int, Task> showStatementAsync,
        System.Threading.CancellationToken cancellationToken,
        ResultContinuation? continuation = null)
    {
        using var executionMeasurement = QueryMemoryDiagnostics.Begin("execute-statements");
        if (statementSpans.Count == 0)
        {
            var emptyResult = new QueryResult
            {
                ExecutedSql = sql,
                ExecutionScope = scope,
                Error = new QueryErrorInfo
                {
                    Provider = session.ProviderName,
                    Summary = "No executable SQL statements were found.",
                    Detail = "Enter a SQL statement or select a statement to run."
                }
            };

            return new BatchExecutionResult(emptyResult, [emptyResult]);
        }

        var statementResults = new List<QueryStatementExecution>();
        var results = new List<QueryResult>();
        QueryResult? lastResult = null;
        QueryResult? lastRowResult = null;
        var selectedSpan = scope == QueryExecutionScope.Selection && statementSpans.Count == 1
            ? statementSpans[0]
            : (StatementSpan?)null;
        var anyUnitHasSeveralResults = false;
        var runSelectionVerbatim = selectedSpan is { } span
            && sql[..span.Start].All(char.IsWhiteSpace)
            && sql[span.End..].All(ch => char.IsWhiteSpace(ch) || ch == ';');

        for (var i = 0; i < statementSpans.Count; i++)
        {
            await showStatementAsync(i);
            var statementSource = runSelectionVerbatim
                ? new ScriptText(sql)
                : new ScriptText(sql, statementSpans[i].Start, statementSpans[i].Length);
            var statement = statementSource.ToString();
            QueryResult? statementResult = null;
            var statementMessages = new List<string>();
            var statementRowCount = 0;
            var hasStatementResultSet = false;
            var firstResultOfUnit = results.Count;

            await foreach (var result in SchemaChangeExecution.ReadAsync(session, dbSession,
                statement,
                new QueryExecutionOptions
                {
                    TimeoutSeconds = SqlBatchParser.ScaleTimeoutToStatements(timeoutSeconds, statement, session.Capabilities.BatchDialect),
                    MaxResultRows = resultRowCap,
                    CancellationToken = cancellationToken,
                    // Only the last statement may keep a capped reader open: later statements would wait on it.
                    Continuation = i == statementSpans.Count - 1 ? continuation : null
                }))
            {
                statementResult = result;
                lastResult = result;
                result.ExecutedSqlSource = statementSource;
                result.StatementStart = runSelectionVerbatim ? 0 : statementSpans[i].Start;
                result.ReturnedStatementIndex = i;
                statementMessages.AddRange(result.Messages);
                statementRowCount += result.ObjectRows.Count;
                hasStatementResultSet |= result.HasResultSet;
                result.TabLabel = statementSpans.Count > 1 ? $"Statement {i + 1}" : "Results";
                if (result.HasResultSet)
                    lastRowResult = result;

                results.Add(result);
            }

            anyUnitHasSeveralResults |= results.Count - firstResultOfUnit > 1;
            if (statementResult == null)
                continue;

            statementResults.Add(new QueryStatementExecution
            {
                Index = i,
                SqlSource = statementSource,
                Duration = statementResult.Duration,
                RecordsAffected = statementResult.RecordsAffected,
                RowCount = statementRowCount,
                HasResultSet = hasStatementResultSet,
                Error = statementResult.Error ?? (statementResult.HasError
                    ? QueryErrorInfo.Failure(session.ProviderName, statementResult.ErrorMessage!, null)
                    : null),
                Messages = statementMessages
            });

            if (statementResult.HasError)
                break;
        }

        var displayResult = lastResult?.HasError == true
            ? lastResult
            : lastRowResult ?? lastResult ?? new QueryResult();

        var totalDuration = TimeSpan.FromMilliseconds(statementResults.Sum(statement => statement.Duration.TotalMilliseconds));
        var returnedStatementIndex = lastResult?.HasError == true
            ? statementResults.Count - 1
            : lastRowResult != null
                ? statementResults.FindLastIndex(statement => statement.HasResultSet)
                : statementResults.Count - 1;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            result.ExecutionScope = scope;
            result.StatementCount = statementResults.Count;
            result.Statements = statementResults;
            result.Duration = totalDuration;
            result.ResultIndex = i;
            if (results.Count > 1 && (statementSpans.Count == 1 || anyUnitHasSeveralResults))
                result.TabLabel = $"Result {i + 1}";
        }

        if (results.Count == 0)
            displayResult.ExecutedSql = sql;
        displayResult.ExecutionScope = scope;
        displayResult.StatementCount = statementResults.Count;
        displayResult.Statements = statementResults;
        displayResult.ReturnedStatementIndex = returnedStatementIndex;
        displayResult.Duration = totalDuration;

        return new BatchExecutionResult(displayResult, results);
    }
}
