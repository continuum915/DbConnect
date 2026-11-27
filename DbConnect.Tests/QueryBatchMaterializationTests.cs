using Moq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class QueryBatchMaterializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyStopDoesNotAllocateSqlForUnexecutedStatements(bool canceled)
    {
        const string first = "SELECT missing";
        const int tailLength = 4 * 1_048_576;
        var sql = first + ";" + new string('x', tailLength) + ";";
        StatementSpan[] spans = [new(0, first.Length), new(first.Length + 1, tailLength)];
        var provider = new SqliteProvider();
        var owner = new DatabaseSession(new ConnectionInfo(), provider);
        var submitted = new List<string>();
        var session = new Mock<IDbSession>();
        session.Setup(session => session.ExecuteQueryMultipleAsync(It.IsAny<string>(),
                It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((string statement, QueryExecutionOptions? _, CancellationToken _) => Stop(statement));
        var dbSession = session.Object;
        try
        {
            var before = GC.GetAllocatedBytesForCurrentThread();

            var executing = QueryBatchExecutor.ExecuteAsync(owner, dbSession, sql,
                QueryExecutionScope.FullEditor, 30, 100, _ => Task.CompletedTask, default, spans);
            Assert.True(executing.IsCompletedSuccessfully); // The fake never switches threads.
            var result = await executing;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(new[] { first }, submitted);
            Assert.Equal(first, Assert.Single(result.Results).ExecutedSql);
            Assert.True(result.DisplayResult.HasError);
            Assert.Equal(canceled, result.DisplayResult.Error!.IsCanceled);
            Assert.Equal(1, result.DisplayResult.StatementCount);
            Assert.True(allocated < 2 * 1_048_576,
                $"Early stop allocated {allocated:N0} bytes before an unexecuted {tailLength:N0}-character statement.");
        }
        finally { owner.Catalog.Dispose(); }

        async IAsyncEnumerable<QueryResult> Stop(string statement)
        {
            submitted.Add(statement);
            yield return new QueryResult { Error = new QueryErrorInfo { Summary = "Stopped", IsCanceled = canceled } };
            await Task.CompletedTask;
        }
    }
}
