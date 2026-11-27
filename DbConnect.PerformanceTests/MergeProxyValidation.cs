using DbConnect.Models;

namespace DbConnect.PerformanceTests;

internal static class MergeProxyValidation
{
    internal static ExecutionObservations Validate(ImportFixture fixture, ImmediateDbSession fake,
        IReadOnlyList<QueryResult> results, QueryResult display)
    {
        var expected = fixture.ProxyStatements ?? throw new InvalidDataException("Missing proxy oracle.");
        if (results.Count != expected.Count || !ReferenceEquals(display, results[^1]))
            throw new InvalidDataException("Proxy work was omitted or the final result was not selected.");
        ulong hash = ImmediateDbSession.InitialHash;
        long characters = 0;
        for (var i = 0; i < expected.Count; i++)
        {
            var item = results[i];
            var statement = expected[i];
            // SQL Server batches retain the required statement terminator.
            var sql = fixture.Sql.AsSpan(statement.Start, statement.Length + 1);
            if (!sql.SequenceEqual(item.ExecutedSql) || item.HasError || item.HasResultSet ||
                item.RecordsAffected != 1 || item.ResultIndex != i || item.StatementCount != results.Count ||
                item.StatementStart != statement.Start || item.Statements[i].Sql != item.ExecutedSql)
                throw new InvalidDataException($"Proxy submission/result {i} differs from the generator oracle.");
            hash = ImmediateDbSession.AppendHash(hash, item.ExecutedSql);
            characters += item.ExecutedSql.Length;
        }
        if (fake.CommandCount != expected.Count || fake.Hash != hash || fake.Characters != characters)
            throw new InvalidDataException("Proxy submission evidence differs from verified results.");
        return new(fake.FirstSubmissionMilliseconds, fake.CommandCount, characters, hash,
            expected.Count(item => item.Kind == "INSERT"), results.Count, 0, 0, 0,
            ["Synthetic inline VALUES workload. Fake affected-row counts validate client routing only; SQL Server execution, indexing, locks and logging are not measured."],
            expected.Count(item => item.Kind == "MERGE"), expected.Count(item => item.Kind == "DELETE"));
    }
}
