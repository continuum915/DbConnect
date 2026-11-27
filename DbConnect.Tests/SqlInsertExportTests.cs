using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>SQL INSERT export through the shared export workflow (ticket sql-dump-and-insert-export/01).</summary>
public sealed class SqlInsertExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-sqlinsert-{Guid.NewGuid():N}");
    public SqlInsertExportTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private async Task<(ExportOutcome Outcome, string Path)> ExportAsync(IExportSource source, ExportOptions options)
    {
        var path = Path.Combine(_directory, "export.sql");
        options.Format = ExportFormat.SqlInsert;
        var outcome = await new ResultExporter(new Picker(path)).ExportAsync(source, options, ExportScope.DisplayedQueryResults);
        return (outcome, path);
    }

    [Fact]
    public async Task WritesOneQuotedInsertPerRowByDefault()
    {
        var (outcome, path) = await ExportAsync(
            new LoadedExportSource([new("id", 0), new("na\"me", 1)], [[1, "O'Brien"], [2, null]]),
            new() { SqlTableName = "\"people\"", SqlDialect = new SqliteProvider().Capabilities });

        Assert.Equal(ExportStatus.Completed, outcome.Status);
        Assert.Equal(2, outcome.RowsWritten);
        Assert.Equal(
            "INSERT INTO \"people\" (\"id\", \"na\"\"me\") VALUES (1, 'O''Brien');\n" +
            "INSERT INTO \"people\" (\"id\", \"na\"\"me\") VALUES (2, NULL);\n",
            await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task GroupsRowsIntoMultiRowValuesListsWithARemainder()
    {
        var (outcome, path) = await ExportAsync(
            new LoadedExportSource([new("id", 0)], [[1], [2], [3], [4], [5]]),
            new() { SqlTableName = "t", RowsPerStatement = 2, IncludeColumnList = false });

        Assert.Equal(ExportStatus.Completed, outcome.Status);
        Assert.Equal(5, outcome.RowsWritten);
        Assert.Equal(
            "INSERT INTO t VALUES\n(1),\n(2);\n" +
            "INSERT INTO t VALUES\n(3),\n(4);\n" +
            "INSERT INTO t VALUES (5);\n",
            await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SqlServerValuesListsNeverExceedOneThousandRows()
    {
        var rows = Enumerable.Range(1, 2500).Select(i => (IReadOnlyList<object?>)[i]).ToList();
        var (outcome, path) = await ExportAsync(new LoadedExportSource([new("id", 0)], rows),
            new() { SqlTableName = "[t]", RowsPerStatement = 5000, SqlDialect = new SqlServerProvider().Capabilities });

        Assert.Equal(ExportStatus.Completed, outcome.Status);
        var statements = (await File.ReadAllTextAsync(path)).Split("INSERT INTO [t] ([id]) VALUES", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal([1000, 1000, 500], statements.Select(s => s.Count(c => c == '(')));
    }

    [Fact]
    public async Task ValueWithNoLiteralFormFailsNamingTheColumnAndPublishesNothing()
    {
        var path = Path.Combine(_directory, "export.sql");
        await File.WriteAllTextAsync(path, "previous");
        var (outcome, _) = await ExportAsync(
            new LoadedExportSource([new("id", 0), new("shape", 1)], [[1, "ok"], [2, new object()]]),
            new() { SqlTableName = "t" });

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Null(outcome.FinalPath);
        Assert.Contains("\"shape\"", outcome.Diagnostic);
        Assert.Contains("not published", outcome.Diagnostic);
        Assert.Equal("previous", await File.ReadAllTextAsync(path));
        Assert.Equal(["export.sql"], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task BlankTableNameFallsBackToExportedResult()
    {
        var (outcome, path) = await ExportAsync(new LoadedExportSource([new("id", 0)], [[1]]),
            new() { SqlDialect = new MySqlProvider().Capabilities });

        Assert.Equal(ExportStatus.Completed, outcome.Status);
        Assert.Equal("INSERT INTO `exported_result` (`id`) VALUES (1);\n", await File.ReadAllTextAsync(path));
    }

    private sealed class Picker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string suggestedFileName, ExportFileType fileType) => Task.FromResult<string?>(path);
    }
}
