using System.Globalization;
using System.Text.Json;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class TypedJsonExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-json-{Guid.NewGuid():N}");
    public TypedJsonExportTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.JsonLines)]
    public async Task PreservesTypedValuesAndPrecisionRegardlessOfTextOptions(ExportFormat format)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new("fr-FR");
            object?[] values = [null, DBNull.Value, "NULL", "", true, 12345678901234567890m,
                1.25d, new DateTimeOffset(2026, 10, 18, 1, 2, 3, TimeSpan.FromHours(-4)).AddTicks(1234567),
                new DateOnly(2026, 10, 18), new TimeOnly(1, 2, 3).Add(TimeSpan.FromTicks(1234567)),
                new byte[] { 0, 255 }, "{\"a\":1}", JsonSerializer.SerializeToElement(new { a = 1 })];
            var path = Path.Combine(_directory, "values");
            var outcome = await new ResultExporter(new Picker(path)).ExportAsync(
                new LoadedExportSource(values.Select((_, i) => new ExportColumn($"c{i}", i)), [values]),
                new() { Format = format, IncludeHeaders = false, NullRepresentation = NullRepresentation.BackslashN }, ExportScope.FullQuery);
            Assert.Equal(ExportStatus.Completed, outcome.Status);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var row = format == ExportFormat.Json ? doc.RootElement[0] : doc.RootElement;
            Assert.Equal(JsonValueKind.Null, row.GetProperty("c0").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("c1").ValueKind);
            Assert.Equal("NULL", row.GetProperty("c2").GetString());
            Assert.Equal("", row.GetProperty("c3").GetString());
            Assert.True(row.GetProperty("c4").GetBoolean());
            Assert.Equal(12345678901234567890m, row.GetProperty("c5").GetDecimal());
            Assert.Equal(1.25, row.GetProperty("c6").GetDouble());
            Assert.Equal("2026-10-18T01:02:03.1234567-04:00", row.GetProperty("c7").GetString());
            Assert.Equal("2026-10-18", row.GetProperty("c8").GetString());
            Assert.Equal("01:02:03.1234567", row.GetProperty("c9").GetString());
            Assert.Equal("AP8=", row.GetProperty("c10").GetString());
            Assert.Equal("{\"a\":1}", row.GetProperty("c11").GetString());
            Assert.Equal(1, row.GetProperty("c12").GetProperty("a").GetInt32());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.JsonLines)]
    public async Task KeepsEveryOrdinalWithCollidingAndBlankHeaders(ExportFormat format)
    {
        var path = Path.Combine(_directory, "keys");
        string[] names = ["id", "id", "id (2)", "", "", " (2)"];
        var outcome = await new ResultExporter(new Picker(path)).ExportAsync(
            new LoadedExportSource(names.Select((name, i) => new ExportColumn(name, i)), [[1, 2, 3, 4, 5, 6]]),
            new() { Format = format }, ExportScope.AllMatchingTableRows);
        Assert.Equal(ExportStatus.Completed, outcome.Status);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var row = format == ExportFormat.Json ? doc.RootElement[0] : doc.RootElement;
        Assert.Equal(["id", "id (3)", "id (2)", "", " (3)", " (2)"], row.EnumerateObject().Select(p => p.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6], row.EnumerateObject().Select(p => p.Value.GetInt32()));
    }

    [Theory]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.JsonLines)]
    public async Task InvalidValuesPreserveDestinationAndIdentifyPositionWithoutLeakingValue(ExportFormat format)
    {
        foreach (var invalid in new object[] { double.NaN, double.PositiveInfinity, float.NegativeInfinity, new object(), JsonSerializer.SerializeToElement(1).Clone().ValueKind })
        {
            var path = Path.Combine(_directory, "invalid");
            await File.WriteAllTextAsync(path, "previous");
            var outcome = await new ResultExporter(new Picker(path)).ExportAsync(
                new LoadedExportSource([new("value", 0)], [[1], [invalid]]), new() { Format = format }, ExportScope.FullQuery);
            Assert.Equal(ExportStatus.Failed, outcome.Status);
            Assert.Contains("row 2, column 1", outcome.Diagnostic);
            Assert.Equal("previous", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.GetFiles(_directory));
        }
    }

    [Theory]
    [InlineData(ExportFormat.Json, "[]")]
    [InlineData(ExportFormat.JsonLines, "")]
    public async Task EmptyResultsProduceValidFiles(ExportFormat format, string expected)
    {
        var path = Path.Combine(_directory, "empty");
        var outcome = await new ResultExporter(new Picker(path)).ExportAsync(new LoadedExportSource([], []),
            new() { Format = format }, ExportScope.DisplayedQueryResults);
        Assert.Equal(ExportStatus.Completed, outcome.Status);
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LegacyJsonLinesEntryPointAlsoPreservesDestinationOnSerializationFailure()
    {
        var path = Path.Combine(_directory, "legacy");
        await File.WriteAllTextAsync(path, "previous");
        await Assert.ThrowsAsync<IOException>(() => ExportService.ExportJsonLinesAsync(new QueryResult
        {
            ColumnInfos = [new() { Name = "value" }],
            ObjectRows = [[1], [double.NaN]]
        }, path));
        Assert.Equal("previous", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(ExportFormat.Json)]
    [InlineData(ExportFormat.JsonLines)]
    public async Task PreservesRemainingNumericAndTemporalTypes(ExportFormat format)
    {
        object?[] values = [(sbyte)-1, (byte)255, (short)-32768, (ushort)65535, int.MinValue,
            uint.MaxValue, long.MinValue, ulong.MaxValue, 1.25f, false,
            new DateTime(2026, 10, 18, 1, 2, 3, DateTimeKind.Utc).AddTicks(1234567),
            new DateTime(2026, 10, 18, 1, 2, 3, DateTimeKind.Unspecified).AddTicks(1234567)];
        var path = Path.Combine(_directory, "numbers");
        var outcome = await new ResultExporter(new Picker(path)).ExportAsync(
            new LoadedExportSource(values.Select((_, i) => new ExportColumn($"c{i}", i)), [values]),
            new() { Format = format }, ExportScope.CurrentTablePage);
        Assert.Equal(ExportStatus.Completed, outcome.Status);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var row = format == ExportFormat.Json ? doc.RootElement[0] : doc.RootElement;
        decimal[] expected = [-1, 255, -32768, 65535, -2147483648, 4294967295, -9223372036854775808m, 18446744073709551615m, 1.25m];
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], row.GetProperty($"c{i}").GetDecimal());
        Assert.False(row.GetProperty("c9").GetBoolean());
        Assert.Equal("2026-10-18T01:02:03.1234567Z", row.GetProperty("c10").GetString());
        Assert.Equal("2026-10-18T01:02:03.1234567", row.GetProperty("c11").GetString());
    }

    [Theory]
    [InlineData(false, ExportFormat.Json)]
    [InlineData(true, ExportFormat.Json)]
    [InlineData(false, ExportFormat.JsonLines)]
    [InlineData(true, ExportFormat.JsonLines)]
    [InlineData(false, ExportFormat.Markdown)]
    [InlineData(true, ExportFormat.Markdown)]
    public async Task BothTabsExportRawTypesAndAllColumns(bool table, ExportFormat format)
    {
        var path = Path.Combine(_directory, "tab");
        var result = new QueryResult { ColumnInfos = [new() { Name = "Id", Ordinal = 0 }, new() { Name = "Name", Ordinal = 1 }], ObjectRows = [[42, null]] };
        if (table)
        {
            var provider = new TableDataTestFixtures.TablePagingProvider(new TablePageResult { Result = result });
            var tab = new DbConnect.ViewModels.TableDataTabViewModel("Customers", TableDataTestFixtures.CreateSession(provider), TableDataTestFixtures.CreateTableNode(true));
            await tab.RefreshInternalAsync(true);
            tab.ExportPathPicker = new Picker(path);
            tab.ExportOptions.Format = format;
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Completed, tab.ExportState.Outcome!.Status);
        }
        else
        {
            var tab = new DbConnect.ViewModels.QueryTabViewModel { ExportPathPicker = new Picker(path) };
            tab.SetExecutionResults([result]);
            tab.ExportOptions.Format = format;
            await tab.ExportCommand.ExecuteAsync(null);
            Assert.Equal(ExportStatus.Completed, tab.ExportState.Outcome!.Status);
        }
        var content = await File.ReadAllTextAsync(path);
        if (format == ExportFormat.Markdown)
            Assert.Equal("| Id | Name |\n| --- | --- |\n| 42 |  |\n", content);
        else
        {
            using var doc = JsonDocument.Parse(content);
            var row = format == ExportFormat.Json ? doc.RootElement[0] : doc.RootElement;
            Assert.Equal(42, row.GetProperty("Id").GetInt32());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("Name").ValueKind);
        }
    }

    private sealed class Picker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(path);
    }
}
