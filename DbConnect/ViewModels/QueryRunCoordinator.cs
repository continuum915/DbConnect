using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Owns Run's cancellation lifetime; CPU preparation uses immutable editor snapshots.</summary>
internal static class QueryRunCoordinator
{
    internal static async Task ExecuteAsync(MainWindowViewModel owner, bool selectionOnly,
        QueryParamContextService contexts)
    {
        if (owner.SelectedTab is ProcedureEditorViewModel procedure)
        {
            if (!selectionOnly) await procedure.ExecuteCommand.ExecuteAsync(null);
            return;
        }
        if (owner.SelectedTab is not QueryTabViewModel tab || tab.IsExecuting) return;
        if (tab.ConnectionLoss.IsLost)
        {
            var lost = new QueryResult { ErrorMessage = "The connection was lost. Reconnect before running SQL." };
            tab.SetExecutionResults([lost], lost);
            return;
        }
        if (!await tab.ResultEditor.ConfirmDiscardingChangesAsync() || tab.IsExecuting) return;

        var editorSql = tab.QueryText;
        var selection = tab.SelectedQueryText;
        var session = tab.SelectedSession;
        if (session == null)
        {
            if (!selectionOnly && await Task.Run(() => SqlScanCancellation.IsWhiteSpace(editorSql, CancellationToken.None)))
                return;
            if (selectionOnly && selection.Length == 0)
            {
                var empty = new QueryResult
                {
                    ExecutionScope = QueryExecutionScope.Selection,
                    Error = QueryErrorInfo.Failure(string.Empty, "No SQL selection is active.",
                        "Select SQL text, or place the caret in a statement, before running the selection.")
                };
                tab.SetExecutionResults([empty], empty);
                return;
            }
            var failed = new QueryResult { ErrorMessage = "No active connection selected." };
            tab.SetExecutionResults([failed], failed);
            return;
        }
        var selectionOffset = tab.SelectedQueryStart;
        var caretOffset = tab.CaretOffset;
        var timeout = tab.TimeoutSeconds;
        var rowCap = tab.ResultRowCap;
        var scope = selectionOnly ? QueryExecutionScope.Selection : QueryExecutionScope.FullEditor;
        var token = tab.BeginExecution();
        owner.IsExecuting = true;
        tab.IsExecuting = true;
        RunningStatementHighlighter? highlighter = null;
        var submittedSql = selectionOnly ? selection : editorSql;
        try
        {
            var source = await Task.Run(() => QueryRunDiagnostics.Measure("resolve-source", () =>
                QueryScriptPreparation.Resolve(editorSql, selection, selectionOnly, selectionOffset,
                    caretOffset, session.Capabilities.BatchDialect, token)), token);
            if (source.IsEmpty)
            {
                if (selectionOnly)
                {
                    var empty = new QueryResult
                    {
                        ExecutionScope = scope,
                        Error = QueryErrorInfo.Failure(session.ProviderName, "No SQL selection is active.",
                            "Select SQL text, or place the caret in a statement, before running the selection.")
                    };
                    tab.SetExecutionResults([empty], empty);
                }
                return;
            }
            submittedSql = source.Sql;
            var detected = await Task.Run(() => QueryRunDiagnostics.Measure("detect-parameters", () =>
                SqlParameterDetector.Detect(source.Sql, token)), token);
            token.ThrowIfCancellationRequested();
            SqlSubstitution? substitution = null;
            if (detected.Count > 0 && owner.ShowQueryParamDialogFunc != null)
            {
                var dialog = new QueryParamDialogViewModel(source.Sql, detected, contexts,
                    session.ProviderName, session.Connection.Name, session.DatabaseLabel, token);
                var parameters = await owner.ShowQueryParamDialogFunc(dialog);
                token.ThrowIfCancellationRequested();
                if (parameters == null || !parameters.Confirmed) return;
                substitution = await Task.Run(() => QueryRunDiagnostics.Measure("substitute-parameters", () =>
                    SqlParameterDetector.SubstituteWithOffsets(source.Sql, parameters.Parameters, token)), token);
                submittedSql = substitution.Sql;
                await SaveParametersAsync(owner, contexts, session, parameters,
                    selectionOnly ? submittedSql : editorSql, submittedSql, token);
            }

            if (await ExecutionSafety.ReadOnlyRefusalAsync(session, submittedSql, token) is { } refusal)
            {
                PresentFailure(tab, session, submittedSql, scope, refusal,
                    new InvalidOperationException("The connection is marked read-only in its connection options."));
                return;
            }
            if (!await owner.Safety.ConfirmRunAsync(session, submittedSql, token)) return;

            var spans = await Task.Run(() => QueryRunDiagnostics.Measure("prepare-highlighter", () =>
                SqlBatchParser.GetExecutionSpans(source.Sql, session.Capabilities.BatchDialect, tab.IsManualCommit, token)), token);
            token.ThrowIfCancellationRequested();
            highlighter = new RunningStatementHighlighter(tab, editorSql, spans.Select(span => new StatementSpan(span.Start + source.Offset, span.Length)).ToArray(), throttleUpdates: true);
            IDbSession dbSession;
            try { dbSession = await tab.GetOrOpenDbSessionAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                PresentFailure(tab, session, submittedSql, scope, "Could not open a session on this connection.", exception);
                return;
            }
            token.ThrowIfCancellationRequested();
            if (tab.IsManualCommit && !dbSession.IsInTransaction)
            {
                try
                {
                    await dbSession.BeginTransactionAsync(token);
                    tab.RefreshTransactionState();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    PresentFailure(tab, session, submittedSql, scope, "Could not start a transaction for manual-commit mode.", exception);
                    return;
                }
            }
            var executionSpans = await Task.Run(() => QueryMemoryDiagnostics.Measure("prepare-execution-spans", () =>
                substitution == null ? spans : SqlBatchParser.GetExecutionSpans(submittedSql, session.Capabilities.BatchDialect, tab.IsManualCommit, token)), token);
            var continuation = new ResultContinuation();
            var batch = await QueryRunDiagnostics.MeasureAsync("execute-batch", () => Task.Run(() =>
                QueryBatchExecutor.ExecuteAsync(session, dbSession, submittedSql, executionSpans, scope, timeout,
                    rowCap, highlighter.ShowAsync, token, continuation), token));
            await QueryRunPresentation.ApplyAsync(tab, batch, source.Sql, submittedSql,
                source.Offset, substitution, scope);
            tab.Paging.Attach(continuation.IsOpen ? continuation : null);
            if (dbSession.IsConnectionLost) tab.ConnectionLoss.MarkLost(session.Name, dbSession.IsInTransaction);
            else await tab.DatabasePicker.ObserveAsync(dbSession, submittedSql, session.Capabilities.BatchDialect);
        }
        catch (TooManyRepeatedCommandsException exception)
        {
            PresentFailure(tab, session, submittedSql, scope, exception.Message, exception);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var canceled = new QueryResult
            {
                ExecutedSql = submittedSql,
                ExecutionScope = scope,
                Error = new QueryErrorInfo { Provider = session.ProviderName, Summary = "Query execution was cancelled.", IsCanceled = true }
            };
            tab.SetExecutionResults([canceled], canceled);
        }
        finally
        {
            highlighter?.Clear();
            owner.IsExecuting = false;
            tab.IsExecuting = false;
            tab.EndExecution();
            tab.RefreshTransactionState();
        }
    }

    private static void PresentFailure(QueryTabViewModel tab, DatabaseSession session, string sql,
        QueryExecutionScope scope, string summary, Exception exception)
    {
        var failed = new QueryResult
        {
            ExecutedSql = sql,
            ExecutionScope = scope,
            Error = QueryErrorInfo.Failure(session.ProviderName, summary, exception.Message)
        };
        tab.SetExecutionResults([failed], failed);
    }

    private static async Task SaveParametersAsync(MainWindowViewModel owner, QueryParamContextService contexts,
        DatabaseSession session, QueryParamDialogResult parameters, string fingerprintSql, string submittedSql,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (parameters.SaveParamContext && !string.IsNullOrWhiteSpace(parameters.ContextName))
        {
            var fingerprint = await Task.Run(() => contexts.GetFingerprint(fingerprintSql, token), token);
            token.ThrowIfCancellationRequested();
            await contexts.SaveAsync(new QueryParamContext
            {
                Name = parameters.ContextName,
                SqlFingerprint = fingerprint,
                ProviderName = session.ProviderName,
                ConnectionName = session.Connection.Name,
                DatabaseName = session.DatabaseLabel,
                Parameters = parameters.Parameters
            });
        }
        token.ThrowIfCancellationRequested();
        if (parameters.SaveAsSnippet && owner.SnippetPanel.ShowSnippetEditorDialog != null)
        {
            var snippet = await owner.SnippetPanel.ShowSnippetEditorDialog(new Snippet
            { Name = $"Snippet {DateTimeOffset.Now:HH:mm:ss}", Body = submittedSql, Scope = SnippetScope.Global });
            token.ThrowIfCancellationRequested();
            if (snippet != null)
            {
                await new SnippetService().SaveAsync(snippet);
                await owner.SnippetPanel.LoadAsync();
            }
        }
    }
}
