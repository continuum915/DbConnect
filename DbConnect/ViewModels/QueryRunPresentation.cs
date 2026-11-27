using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

internal static class QueryRunPresentation
{
    internal static async Task ApplyAsync(QueryTabViewModel tab,
        QueryBatchExecutor.BatchExecutionResult batch, string sourceSql, string submittedSql,
        int sourceOffset, SqlSubstitution? substitution, QueryExecutionScope scope)
    {
        QueryRunDiagnostics.Measure("present-results", () =>
            tab.SetExecutionResults(batch.Results, batch.DisplayResult));
        QueryRunDiagnostics.Measure("navigate-error", () =>
            tab.NavigateToError(sourceSql, submittedSql, sourceOffset,
                batch.DisplayResult, substitution?.SourceOffsets));
        await QueryRunDiagnostics.MeasureAsync("register-history", () =>
            tab.RegisterExecutedQueryAsync(submittedSql, scope, tab.CurrentResult ?? batch.DisplayResult));
    }
}
