using System.Globalization;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class StreamingMarkdownTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-markdown-{Guid.NewGuid():N}");
    public StreamingMarkdownTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task PreservesInvariantValuesAndDuplicateColumns()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new("fr-FR");
            var path = Path.Combine(_directory, "values.md");
            var outcome = await new ResultExporter(new Picker(path)).ExportAsync(
                new LoadedExportSource([new("id", 0), new("id", 1)],
                    [[1.25m, true], [null, DBNull.Value], ["", "NULL"], [new DateOnly(2026, 10, 18), new byte[] { 0, 255 }]]),
                new() { Format = ExportFormat.Markdown, NullRepresentation = NullRepresentation.NullLiteral }, ExportScope.CurrentTablePage);
            Assert.Equal(ExportStatus.Completed, outcome.Status);
            Assert.Equal(4, outcome.RowsWritten);
            Assert.Equal("| id | id |\n| --- | --- |\n| 1.25 | true |\n| NULL | NULL |\n|  | NULL |\n| 2026-10-18 | AP8= |\n", await File.ReadAllTextAsync(path));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(true, "| a\\|b |\n| --- |\n")]
    [InlineData(false, "|  |\n| --- |\n")]
    public async Task EmptyResultsRespectHeaderOption(bool headers, string expected)
    {
        var path = Path.Combine(_directory, "empty.md");
        var outcome = await new ResultExporter(new Picker(path)).ExportAsync(
            new LoadedExportSource([new("a|b", 0)], []),
            new() { Format = ExportFormat.Markdown, IncludeHeaders = headers }, ExportScope.FullQuery);
        Assert.Equal(ExportStatus.Completed, outcome.Status);
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EscapesLiteralMarkupBackslashesAndEveryLineEnding()
    {
        var path = Path.Combine(_directory, "escape.md");
        var outcome = await new ResultExporter(new Picker(path)).ExportAsync(
            new LoadedExportSource([new("value", 0)], [["\\|\r\n*hi*\r<br>\n&`_[]"]]),
            new() { Format = ExportFormat.Markdown, IncludeHeaders = false }, ExportScope.DisplayedQueryResults);
        Assert.Equal(ExportStatus.Completed, outcome.Status);
        Assert.Equal("|  |\n| --- |\n| " + "\\\\" + "\\|<br>\\*hi\\*<br>&lt;br&gt;<br>&amp;\\`\\_\\[\\] |\n", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(NullRepresentation.Empty, "")]
    [InlineData(NullRepresentation.NullLiteral, "NULL")]
    [InlineData(NullRepresentation.BackslashN, "\\\\N")]
    public async Task NullOptionOnlyChangesDatabaseNulls(NullRepresentation nulls, string expected)
    {
        var path = Path.Combine(_directory, "null.md");
        var outcome = await new ResultExporter(new Picker(path)).ExportAsync(
            new LoadedExportSource([new("v", 0)], [[null], [DBNull.Value], [""], ["NULL"]]),
            new() { Format = ExportFormat.Markdown, NullRepresentation = nulls }, ExportScope.CurrentTablePage);
        Assert.Equal(ExportStatus.Completed, outcome.Status);
        Assert.Equal($"| v |\n| --- |\n| {expected} |\n| {expected} |\n|  |\n| NULL |\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SerializationFailurePreservesDestinationAndRedactsDiagnostics()
    {
        var path = Path.Combine(_directory, "invalid.md");
        await File.WriteAllTextAsync(path, "previous");
        var outcome = await new ResultExporter(new Picker(path)).ExportAsync(
            new LoadedExportSource([new("v", 0)], [[1], [new object()]]),
            new() { Format = ExportFormat.Markdown }, ExportScope.CurrentTablePage);
        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Null(outcome.FinalPath);
        Assert.Equal("previous", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ZeroColumnsProduceEmptyDocument()
    {
        var path = Path.Combine(_directory, "zero.md");
        var outcome = await new ResultExporter(new Picker(path)).ExportAsync(new LoadedExportSource([], []),
            new() { Format = ExportFormat.Markdown }, ExportScope.DisplayedQueryResults);
        Assert.Equal(ExportStatus.Completed, outcome.Status);
        Assert.Equal("", await File.ReadAllTextAsync(path));
    }

    private sealed class Picker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(path);
    }
}
