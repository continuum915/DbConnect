using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>A two-million-row transfer streams in bounded memory (data-transfer/09).</summary>
[Collection(MemoryMeasurement.Name)]
public sealed class DataTransferStreamingTests : DumpTestDatabases
{
    private const int Rows = 2_000_000;

    // Between engines, so every value also goes through per-column conversion; one pair keeps the suite's time down.
    [Theory]
    [InlineData("DuckDB", "SQLite")]
    public async Task TwoMillionRowsTransferInBoundedMemory(string from, string to)
    {
        var source = await SessionAsync(from, "big", from == "SQLite"
            ? $"""
              CREATE TABLE big (id INTEGER PRIMARY KEY, label TEXT);
              WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM s WHERE i < {Rows}) INSERT INTO big SELECT i, 'row number ' || i FROM s;
              """
            : $"CREATE TABLE big (id BIGINT PRIMARY KEY, label VARCHAR); INSERT INTO big SELECT i, 'row number ' || i FROM range(1, {Rows + 1}) t(i);");
        var target = await SessionAsync(to, "copy", null);
        var memory = new LiveHeapEvery(400_000);

        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, [Table("big")],
            new DataTransferOptions { BatchSize = 5000 }, memory));

        Assert.True(outcome.Status == DataTransferTableStatus.Created, outcome.Diagnostic);
        Assert.Equal(Rows, outcome.RowsCopied);
        Assert.Equal($"{Rows}|{Rows}|row number {Rows}", await QueryAsync(target, "SELECT count(*), max(id), max(label) FILTER (WHERE id = (SELECT max(id) FROM big)) FROM big"));
        // Live heap after a full collection at 400k, 800k … 2M rows: holding the rows would add well over 150 MB by the end.
        Assert.Equal(5, memory.Samples.Count);
        var growth = memory.Samples.Max() - memory.Samples[0];
        Assert.True(growth < 16_000_000, $"live heap grew {growth / 1_000_000} MB: {string.Join(", ", memory.Samples.Select(b => b / 1_000_000))} MB");
    }

    private sealed class LiveHeapEvery(long rows) : IProgress<DataTransferProgress>
    {
        private readonly long _every = rows;
        private long _next = rows;
        public List<long> Samples { get; } = [];

        public void Report(DataTransferProgress value)
        {
            if (value.Rows < _next) return;
            _next += _every;
            Samples.Add(GC.GetTotalMemory(forceFullCollection: true));
        }
    }
}
