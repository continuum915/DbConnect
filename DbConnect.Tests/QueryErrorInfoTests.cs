using DbConnect.Models;

namespace DbConnect.Tests;

public class QueryErrorInfoTests
{
    [Fact]
    public void ToDisplaySummary_TimeoutFlag_StartsWithTimedOut()
    {
        var info = new QueryErrorInfo { IsTimeout = true, Summary = "timeout" };
        Assert.StartsWith("Timed out", info.ToDisplaySummary());
    }

    [Fact]
    public void ToDisplaySummary_CancelledFlag_StartsWithCancelled()
    {
        var info = new QueryErrorInfo { IsCanceled = true, Summary = "cancelled" };
        Assert.StartsWith("Cancelled", info.ToDisplaySummary());
    }

    [Fact]
    public void ToDisplaySummary_ErrorWithCode_IncludesCodeInParens()
    {
        var info = new QueryErrorInfo { Summary = "relation not found", Code = "42P01" };
        Assert.Contains("(42P01)", info.ToDisplaySummary());
    }

    [Fact]
    public void ToDisplaySummary_ErrorWithDistinctDetail_AppendsDetail()
    {
        var info = new QueryErrorInfo { Summary = "error occurred", Detail = "detail text" };
        Assert.Contains("detail text", info.ToDisplaySummary());
    }

    [Fact]
    public void ToDisplaySummary_ErrorWithMatchingDetail_DoesNotDuplicateDetail()
    {
        var summary = "column does not exist";
        var info = new QueryErrorInfo { Summary = summary, Detail = summary };
        var result = info.ToDisplaySummary();
        // The summary text should appear only once in the output
        Assert.Equal(1, CountOccurrences(result, summary));
    }

    [Fact]
    public void ToDisplaySummary_PlainError_StartsWithFailed()
    {
        var info = new QueryErrorInfo { Summary = "something went wrong" };
        Assert.StartsWith("Failed", info.ToDisplaySummary());
    }

    [Fact]
    public void QueryResult_StructuredErrorSummary_UsesToDisplaySummaryWhenErrorSet()
    {
        var error = new QueryErrorInfo { IsTimeout = true, Summary = "timeout" };
        var result = new QueryResult { Error = error };
        Assert.Equal(error.ToDisplaySummary(), result.StructuredErrorSummary);
    }

    [Fact]
    public void QueryResult_StructuredErrorSummary_FallsBackToErrorMessageString()
    {
        var result = new QueryResult { ErrorMessage = "plain error" };
        Assert.Equal("plain error", result.StructuredErrorSummary);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
