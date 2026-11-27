using System.Diagnostics;
using System.Runtime.CompilerServices;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.PerformanceTests;

public sealed record ExecutionObservations(double FirstSubmissionMilliseconds, int CommandCount,
    long SubmittedCharacters, ulong SubmissionHash, int VerifiedInserts, int ResultCount,
    long HeapWithResultsBytes, long HeapWithoutResultsBytes, long RetainedResultBytes,
    string[] ExecutionUnitDiscrepancies, int VerifiedMerges = 0, int VerifiedDeletes = 0);

public sealed class DirectBatchScenario
{
    public const string Id = "execute.direct";
    private readonly ImportFixture _fixture;
    private readonly ImmediateDbSession _fake = new();
    private QueryBatchExecutor.BatchExecutionResult? _result;

    public DirectBatchScenario(ImportFixture fixture) => _fixture = fixture;

    public async Task ExecuteAsync()
    {
        _fake.StartTimestamp = Stopwatch.GetTimestamp();
        _result = await QueryBatchExecutor.ExecuteAsync(
            new DatabaseSession(new ConnectionInfo(), _fake.Provider), _fake, _fixture.Sql,
            QueryExecutionScope.FullEditor, 30, 100, _ => Task.CompletedTask, default);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public ExecutionObservations Validate()
    {
        var result = _result ?? throw new InvalidOperationException("Execute first.");
        return ValidateResults(_fixture, _fake, result.Results, result.DisplayResult);
    }

    internal static ExecutionObservations ValidateResults(ImportFixture fixture, ImmediateDbSession fake,
        IReadOnlyList<QueryResult> results, QueryResult displayResult)
    {
        var units = ExpectedBatches(fixture);
        if (results.Count != units.Count ||
            displayResult.StatementCount != results.Count ||
            !ReferenceEquals(displayResult, results[^1]))
            throw new InvalidDataException("Executor omitted work or returned inconsistent metadata.");
        ulong hash = ImmediateDbSession.InitialHash;
        long characters = 0;
        for (var i = 0; i < results.Count; i++)
        {
            var item = results[i];
            var expected = fixture.Sql.AsSpan(units[i].Start, units[i].Length);
            if (!expected.SequenceEqual(item.ExecutedSql) || item.HasError || item.HasResultSet ||
                item.RecordsAffected != 1 || item.ResultIndex != i || item.StatementCount != results.Count ||
                item.Statements[i].Sql != item.ExecutedSql || item.StatementStart != units[i].Start ||
                !fixture.Sql.AsSpan(item.StatementStart, item.ExecutedSql.Length).SequenceEqual(expected))
                throw new InvalidDataException($"Submission/result {i} differs from the generator oracle.");
            hash = ImmediateDbSession.AppendHash(hash, item.ExecutedSql);
            characters += item.ExecutedSql.Length;
        }
        if (fake.CommandCount != results.Count || fake.Hash != hash || fake.Characters != characters)
            throw new InvalidDataException("Compact submission evidence differs from verified results.");
        return new(fake.FirstSubmissionMilliseconds, fake.CommandCount, characters, hash,
            fixture.Expected.Count, results.Count, 0, 0, 0,
            [$"SQL Server submitted {fake.CommandCount} GO batches for {fixture.Metadata.Inserts} INSERTs; semicolons remain within batches and trailing comment padding is submitted. The fake returns one affected row per batch even for padding; this is not database execution evidence."]);
    }

    // Use generator locations, not the production SQL parser, as the execution oracle.
    private static List<StatementSpan> ExpectedBatches(ImportFixture fixture)
    {
        if (fixture.Expected.Count != fixture.Metadata.Inserts)
            throw new InvalidDataException("Generator INSERT count differs from its metadata.");
        var units = new List<StatementSpan>();
        var start = 0;
        var previousEnd = 0;
        foreach (var insert in fixture.Expected)
        {
            var end = insert.Start + insert.Length + 1;
            if (insert.Start < previousEnd || end > fixture.Sql.Length || fixture.Sql[end - 1] != ';')
                throw new InvalidDataException("Generator INSERT locations are inconsistent.");
            previousEnd = end;
            var separator = end;
            while (separator < fixture.Sql.Length && char.IsWhiteSpace(fixture.Sql[separator])) separator++;
            if (!fixture.Sql.AsSpan(separator).StartsWith("GO\r\n", StringComparison.Ordinal)) continue;
            units.Add(new(start, end - start));
            start = separator + 4;
        }
        var padding = fixture.Sql.AsSpan(start).TrimEnd();
        if (!padding.StartsWith("/*", StringComparison.Ordinal))
            throw new InvalidDataException("Generator trailing padding is missing.");
        units.Add(new(start, padding.Length));
        return units;
    }

    // Separate call frames ensure validation/task temporaries cannot root production output during GC.
    public ExecutionObservations MeasureRetention(ExecutionObservations observations)
    {
        var withResults = GC.GetTotalMemory(true);
        ReleaseResults();
        var withoutResults = GC.GetTotalMemory(true);
        GC.KeepAlive(_fixture);
        GC.KeepAlive(_fake);
        return observations with
        {
            HeapWithResultsBytes = withResults,
            HeapWithoutResultsBytes = withoutResults,
            RetainedResultBytes = withResults - withoutResults
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReleaseResults() => _result = null;
}
