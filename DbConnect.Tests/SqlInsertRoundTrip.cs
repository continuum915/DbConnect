using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>
/// Exports <c>SELECT * FROM source ORDER BY id</c> as SQL INSERT into <c>target</c>, runs the script on the same
/// session and checks both tables hold the same values: in-engine (<c>EXCEPT</c> both ways, which a lossy driver
/// read cannot hide) and value by value as the driver reads them.
/// </summary>
internal static class SqlInsertRoundTrip
{
    public const string Text = "it's \"quoted\"\r\nnext line\ttab \\ backslash \\' \\\\; -- not a comment /* nor this */";
    public const string Unicode = "héllo wörld — 日本語 🌧 Ωμέγα";

    public static string Literal(string text) => $"'{text.Replace("'", "''")}'";

    public static async Task AssertAsync(IDbSession session, string source, string target, int rowsPerStatement, string? exceptSelect = null)
    {
        var caps = session.Provider.Capabilities;
        var (outcome, script, read) = await ExportAsync(session, $"SELECT * FROM {caps.QuoteIdentifier(source)} ORDER BY id", caps.QuoteIdentifier(target), rowsPerStatement);
        Assert.True(outcome.Status == ExportStatus.Completed, outcome.Diagnostic);
        await ExecuteAsync(session, script!);

        var select = exceptSelect ?? "*";
        foreach (var (left, right) in new[] { (source, target), (target, source) })
        {
            var difference = await QueryAsync(session,
                $"SELECT count(*) FROM (SELECT {select} FROM {caps.QuoteIdentifier(left)} EXCEPT SELECT {select} FROM {caps.QuoteIdentifier(right)}) d");
            Assert.True(Convert.ToInt64(difference.ObjectRows[0][0]) == 0, $"{left} has rows missing from {right}");
        }

        var copy = await QueryAsync(session, $"SELECT * FROM {caps.QuoteIdentifier(target)} ORDER BY id");
        Assert.Equal(read.ObjectRows.Count, copy.ObjectRows.Count);
        for (var row = 0; row < read.ObjectRows.Count; row++)
            for (var column = 0; column < read.ColumnInfos.Count; column++)
                Assert.True(SameValueAndKind(read.ObjectRows[row][column], copy.ObjectRows[row][column]),
                    $"row {row + 1}, {read.ColumnInfos[column].Name}: expected {Show(read.ObjectRows[row][column])}, got {Show(copy.ObjectRows[row][column])}");
    }

    /// <summary>Exports the query's result as SQL INSERT into <paramref name="target"/>; the script is null when the export failed.</summary>
    public static async Task<(ExportOutcome Outcome, string? Script, QueryResult Read)> ExportAsync(IDbSession session, string query, string target, int rowsPerStatement = 1)
    {
        var read = await QueryAsync(session, query);
        var directory = Path.Combine(Path.GetTempPath(), $"rain-sqlinsert-rt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "rows.sql");
            var outcome = await new ResultExporter(new Picker(path)).ExportAsync(
                new LoadedExportSource(read.ColumnInfos.Select(c => new ExportColumn(c.Name, c.Ordinal, c.ProviderTypeName, c.ClrType)), read.ObjectRows),
                new()
                {
                    Format = ExportFormat.SqlInsert,
                    SqlTableName = target,
                    RowsPerStatement = rowsPerStatement,
                    SqlDialect = session.Provider.Capabilities
                },
                ExportScope.DisplayedQueryResults);
            return (outcome, File.Exists(path) ? await File.ReadAllTextAsync(path) : null, read);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static bool SameValueAndKind(object? expected, object? actual) => (expected, actual) switch
    {
        // DateTime equality ignores Kind; a UTC instant re-read as local time would otherwise pass.
        (DateTime a, DateTime b) => a == b && a.Kind == b.Kind,
        // DateTimeOffset equality compares instants; the offset must survive too.
        (DateTimeOffset a, DateTimeOffset b) => a == b && a.Offset == b.Offset,
        (byte[] a, byte[] b) => a.SequenceEqual(b),
        _ => Equals(expected, actual)
    };

    private static string Show(object? value) => value switch
    {
        null => "null",
        DateTime dateTime => $"{dateTime:O} ({dateTime.Kind})",
        DateTimeOffset dateTimeOffset => $"{dateTimeOffset:O}",
        byte[] bytes => Convert.ToHexString(bytes),
        _ => $"{value} ({value.GetType().Name})"
    };

    public static async Task ExecuteAsync(IDbSession session, string sql)
    {
        var result = await session.ExecuteQueryAsync(sql);
        Assert.False(result.HasError, result.ErrorMessage);
    }

    public static async Task<QueryResult> QueryAsync(IDbSession session, string sql)
    {
        var result = await session.ExecuteQueryAsync(sql);
        Assert.False(result.HasError, result.ErrorMessage);
        return result;
    }

    private sealed class Picker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string suggestedFileName, ExportFileType fileType) => Task.FromResult<string?>(path);
    }
}
