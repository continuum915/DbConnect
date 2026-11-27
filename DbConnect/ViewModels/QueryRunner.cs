using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Runs a query tab's SQL. Scanning a large script stays off the UI thread and can be cancelled like execution.</summary>
internal sealed class QueryRunner
{
    private readonly MainWindowViewModel _main;
    private readonly QueryParamContextService _queryParamContextService = new();

    public QueryRunner(MainWindowViewModel main) => _main = main;

    public async Task RunAsync(QueryTabViewModel queryTab, bool useSelectionOnly)
    {
        var editorText = queryTab.QueryText;
        var textToRun = useSelectionOnly ? queryTab.SelectedQueryText : editorText;
        var sourceOffset = useSelectionOnly ? queryTab.SelectedQueryStart : 0;
        var session = queryTab.SelectedSession;
        var findAtCaret = useSelectionOnly && textToRun.Length == 0 && session != null;

        if (!findAtCaret && (textToRun.Length == 0 || (!useSelectionOnly && string.IsNullOrWhiteSpace(textToRun))))
        {
            if (useSelectionOnly)
                ShowEmptySelection(queryTab);
            return;
        }

        if (session == null)
        {
            var noSessionResult = new QueryResult { ErrorMessage = "No active connection selected." };
            queryTab.SetExecutionResults([noSessionResult], noSessionResult);
            return;
        }

        var caretOffset = queryTab.CaretOffset;
        var dialect = session.Capabilities.BatchDialect;
        var scope = useSelectionOnly ? QueryExecutionScope.Selection : QueryExecutionScope.FullEditor;
        RunningStatementHighlighter? highlighter = null;
        var cancellationToken = queryTab.BeginExecution();
        _main.IsExecuting = true;
        queryTab.IsExecuting = true;
        try
        {
            if (findAtCaret)
            {
                var atCaret = await Task.Run(() => SqlBatchParser.FindStatementAt(editorText, caretOffset, dialect, cancellationToken), cancellationToken);
                if (atCaret is not { } span)
                {
                    ShowEmptySelection(queryTab);
                    return;
                }
                textToRun = editorText.Substring(span.Start, span.Length);
                sourceOffset = span.Start;
            }

            var sourceSql = textToRun;
            var substitution = await SubstituteParametersAsync(queryTab, session, sourceSql, useSelectionOnly, cancellationToken);
            if (substitution.Declined)
                return;
            textToRun = substitution.Sql;

            var inDriverTransaction = queryTab.IsManualCommit;
            var sourceSpans = await Task.Run(() => SqlBatchParser.GetExecutionSpans(sourceSql, dialect, inDriverTransaction, cancellationToken), cancellationToken);
            var executionSql = textToRun;
            var executionSpans = substitution.Offsets == null
                ? sourceSpans
                : await Task.Run(() => SqlBatchParser.GetExecutionSpans(executionSql, dialect, inDriverTransaction, cancellationToken), cancellationToken);
            highlighter = new RunningStatementHighlighter(queryTab, editorText, sourceSpans.Select(span => new StatementSpan(span.Start + sourceOffset, span.Length)).ToArray(), throttleUpdates: true);

            IDbSession dbSession;
            try
            {
                dbSession = await queryTab.GetOrOpenDbSessionAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                var openFailed = new QueryResult
                {
                    ExecutedSql = textToRun,
                    Error = QueryErrorInfo.Failure(session.ProviderName, "Could not open a session on this connection.", ex.Message)
                };
                queryTab.SetExecutionResults([openFailed], openFailed);
                return;
            }

            if (queryTab.IsManualCommit && !dbSession.IsInTransaction)
            {
                try
                {
                    await dbSession.BeginTransactionAsync(cancellationToken);
                    queryTab.RefreshTransactionState();
                }
                catch (Exception ex)
                {
                    var beginFailed = new QueryResult
                    {
                        ExecutedSql = textToRun,
                        Error = QueryErrorInfo.Failure(session.ProviderName, "Could not start a transaction for manual-commit mode.", ex.Message)
                    };
                    queryTab.SetExecutionResults([beginFailed], beginFailed);
                    return;
                }
            }
            // Some providers complete ReadAsync synchronously, so run materialization off the UI thread.
            var batchResult = await Task.Run(() => QueryBatchExecutor.ExecuteAsync(
                session,
                dbSession,
                executionSql,
                executionSpans,
                scope,
                queryTab.TimeoutSeconds,
                queryTab.ResultRowCap,
                highlighter.ShowAsync,
                cancellationToken));
            queryTab.SetExecutionResults(batchResult.Results, batchResult.DisplayResult);
            queryTab.NavigateToError(sourceSql, textToRun, sourceOffset,
                batchResult.DisplayResult, substitution.Offsets);
            await queryTab.RegisterExecutedQueryAsync(
                textToRun,
                scope,
                queryTab.CurrentResult ?? batchResult.DisplayResult);
        }
        catch (TooManyRepeatedCommandsException ex)
        {
            var refused = new QueryResult
            {
                ExecutedSql = textToRun,
                ExecutionScope = scope,
                Error = QueryErrorInfo.Failure(session.ProviderName, ex.Message, null)
            };
            queryTab.SetExecutionResults([refused], refused);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = new QueryResult
            {
                ExecutedSql = textToRun,
                ExecutionScope = scope,
                Error = QueryCancellation.CancelledError(session.ProviderName)
            };
            queryTab.SetExecutionResults([cancelled], cancelled);
        }
        finally
        {
            highlighter?.Clear();
            _main.IsExecuting = false;
            queryTab.IsExecuting = false;
            queryTab.EndExecution();
            queryTab.RefreshTransactionState();
        }
    }

    private readonly record struct ParameterSubstitution(string Sql, IReadOnlyList<int>? Offsets, bool Declined);

    private async Task<ParameterSubstitution> SubstituteParametersAsync(QueryTabViewModel queryTab, DatabaseSession session,
        string sourceSql, bool useSelectionOnly, CancellationToken cancellationToken)
    {
        var detected = await Task.Run(() => SqlParameterDetector.Detect(sourceSql, cancellationToken), cancellationToken);
        if (detected.Count == 0 || _main.ShowQueryParamDialogFunc == null)
            return new(sourceSql, null, false);

        var vm = new QueryParamDialogViewModel(
            sourceSql,
            detected,
            _queryParamContextService,
            session.ProviderName,
            session.Connection.Name,
            session.DatabaseLabel);

        var result = await _main.ShowQueryParamDialogFunc(vm);
        if (result == null || !result.Confirmed)
            return new(sourceSql, null, true);

        var substitution = await Task.Run(() => SqlParameterDetector.SubstituteWithOffsets(sourceSql, result.Parameters, cancellationToken), cancellationToken);
        var textToRun = substitution.Sql;

        // Handle "Save Param Context"
        if (result.SaveParamContext && !string.IsNullOrWhiteSpace(result.ContextName))
        {
            var context = new QueryParamContext
            {
                Name = result.ContextName,
                SqlFingerprint = _queryParamContextService.GetFingerprint(useSelectionOnly ? textToRun : queryTab.QueryText),
                ProviderName = session.ProviderName,
                ConnectionName = session.Connection.Name,
                DatabaseName = session.DatabaseLabel,
                Parameters = result.Parameters
            };
            await _queryParamContextService.SaveAsync(context);
        }

        // Handle "Save as Snippet"
        if (result.SaveAsSnippet && _main.SnippetPanel.ShowSnippetEditorDialog != null)
        {
            var snippet = new Snippet
            {
                Name = $"Snippet {DateTimeOffset.Now:HH:mm:ss}",
                Body = textToRun,
                Scope = SnippetScope.Global
            };
            var snippetResult = await _main.SnippetPanel.ShowSnippetEditorDialog(snippet);
            if (snippetResult != null)
            {
                var snippetSvc = new SnippetService();
                await snippetSvc.SaveAsync(snippetResult);
                await _main.SnippetPanel.LoadAsync();
            }
        }

        return new(textToRun, substitution.SourceOffsets, false);
    }

    private static void ShowEmptySelection(QueryTabViewModel queryTab)
    {
        var emptySelectionResult = new QueryResult
        {
            Error = new QueryErrorInfo
            {
                Provider = queryTab.SelectedSession?.ProviderName ?? string.Empty,
                Summary = "No SQL selection is active.",
                Detail = "Select SQL text, or place the caret in a statement, before running the selection."
            },
            ExecutionScope = QueryExecutionScope.Selection
        };
        queryTab.SetExecutionResults([emptySelectionResult], emptySelectionResult);
    }
}
