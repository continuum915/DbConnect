using System.Text;
using DbConnect.Models;
using DbConnect.PerformanceTests;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class PreparationAllocationTests
{
    private const string Insert = "INSERT INTO dbo.Orders (Id, Name, Note) VALUES (1, 'semi;colon', N'text'); -- trailing; comment\n";

    private static string Script(int statements)
    {
        var script = new StringBuilder();
        for (var i = 0; i < statements; i++)
        {
            script.Append(Insert);
            if (i % 50 == 49) script.Append("GO\n");
        }
        return script.ToString();
    }

    private static long AllocatedBy(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void ScanningStatementsDoesNotCopyTheScript()
    {
        var script = Script(10_000);
        SqlBatchParser.GetStatementSpans(script, SqlBatchDialect.SqlServer);

        var allocated = AllocatedBy(() => SqlBatchParser.GetStatementSpans(script, SqlBatchDialect.SqlServer));

        // Only the span list after doubling, plus the token enumerator. The script itself is about 2 MB of UTF-16.
        Assert.InRange(allocated, 0, 512 * 1024);
    }

    [Fact]
    public void CheckingForSchemaChangesDoesNotCopyTokens()
    {
        SchemaChangeProbe.MayChangeSchema(Insert, SqlBatchDialect.SqlServer);

        var allocated = AllocatedBy(() =>
        {
            for (var i = 0; i < 1_000; i++)
                SchemaChangeProbe.MayChangeSchema(Insert, SqlBatchDialect.SqlServer);
        });

        Assert.InRange(allocated, 0, 1_000 * 256);
    }

    [Fact]
    public void DetectingParametersDoesNotKeepMatchesInsideLiterals()
    {
        var script = string.Concat(Enumerable.Repeat("INSERT INTO t VALUES ('a@b.com', 'x:y', 'z?', '$1');\n", 10_000));
        SqlParameterDetector.Detect(script);

        var allocated = AllocatedBy(() => Assert.Empty(SqlParameterDetector.Detect(script)));

        Assert.InRange(allocated, 0, 2 * 1024 * 1024);
    }

    [Fact]
    public void DetectingParametersStillFindsEveryStyle()
    {
        var found = SqlParameterDetector.Detect("SELECT @name, :other, $2, ?1, ? FROM t WHERE a = 'skip @me' -- @nor_me\nAND b::int = @name");

        Assert.Equal(["@name", ":other", "$2", "?", "?1"], found.Select(parameter => parameter.Placeholder));
        Assert.Equal(2, found[2].OrdinalIndex);
        Assert.Equal(1, found[4].OrdinalIndex);
    }

    [Theory]
    [InlineData("CREATE TABLE t (id int)", SqlBatchDialect.Standard, true)]
    [InlineData("insert into t values (1); drop table t", SqlBatchDialect.Standard, true)]
    [InlineData("SELECT 1 AS create_me", SqlBatchDialect.Standard, false)]
    [InlineData("SELECT 1 AS drop", SqlBatchDialect.Standard, false)]
    [InlineData("SELECT 1 AS /* note */ drop", SqlBatchDialect.Standard, false)]
    [InlineData("SELECT 'DROP TABLE t'", SqlBatchDialect.Standard, false)]
    [InlineData("-- DROP TABLE t\nSELECT 1", SqlBatchDialect.Standard, false)]
    [InlineData("/*!50001 CREATE VIEW v AS SELECT 1 */", SqlBatchDialect.MySql, true)]
    [InlineData("SELECT 1 INTO t", SqlBatchDialect.SqlServer, true)]
    [InlineData("SELECT 1 INTO t", SqlBatchDialect.Sqlite, false)]
    [InlineData("SELECT comment FROM t", SqlBatchDialect.PostgreSql, false)]
    [InlineData("COMMENT ON TABLE t IS 'x'", SqlBatchDialect.PostgreSql, true)]
    [InlineData("CALL refresh()", SqlBatchDialect.MySql, true)]
    [InlineData("UPDATE t SET a = 1", SqlBatchDialect.Standard, false)]
    [InlineData("", SqlBatchDialect.Standard, false)]
    public void SchemaChecksKeepTheirMeaning(string sql, SqlBatchDialect dialect, bool expected) =>
        Assert.Equal(expected, SchemaChangeProbe.MayChangeSchema(sql, dialect));

    [Fact]
    public async Task ResultsReadTheirSqlFromTheScriptWithoutRetainingACopy()
    {
        var session = new WeakQuerySession();

        var (display, results) = await ExecutionStopChild.ExecuteScriptAsync(session, CancellationToken.None);

        Assert.Equal(results.Select(result => result.ExecutedSql), results[0].Statements.Select(statement => statement.Sql));
        Assert.Equal("SELECT 101", results[0].ExecutedSql);
        Assert.Equal(results[^1].ExecutedSql, display.ExecutedSql);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.NotEmpty(session.Submitted);
        Assert.All(session.Submitted, submitted => Assert.False(submitted.TryGetTarget(out _)));
        GC.KeepAlive(results);
    }

    [Fact]
    public void SqlCanStillBeAssignedDirectly()
    {
        var result = new QueryResult();
        Assert.Equal(string.Empty, result.ExecutedSql);

        var sql = "SELECT 1";
        result.ExecutedSql = sql;

        Assert.Same(sql, result.ExecutedSql);
        Assert.Equal(string.Empty, new QueryStatementExecution().Sql);
    }

    private sealed class WeakQuerySession : IDbSession
    {
        public List<WeakReference<string>> Submitted { get; } = [];
        public IDatabaseProvider Provider { get; } = new SqlServerProvider();
        public bool IsInTransaction => false;

        public async IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(string query, QueryExecutionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Submitted.Add(new(query));
            yield return new QueryResult { RecordsAffected = 1, ExecutedSql = query };
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task BeginTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RollbackAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelAsync() => throw new NotSupportedException();
        public Task<QueryResult> ExecuteQueryAsync(string query, QueryExecutionOptions? options = null) => throw new NotSupportedException();
        public Task<TablePageResult> LoadTablePageAsync(string tableName, IReadOnlyList<string> orderByColumns,
            int pageSize, int pageIndex, QueryExecutionOptions? options = null) => throw new NotSupportedException();
        public Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<SqlCommandSpec> commands) => throw new NotSupportedException();
    }
}
