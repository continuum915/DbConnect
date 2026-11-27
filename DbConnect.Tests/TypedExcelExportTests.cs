using System.IO.Compression;
using System.Xml.Linq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class TypedExcelExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-excel-{Guid.NewGuid():N}");
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public TypedExcelExportTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task WritesNativeCellsAndExactFallbacksWithDuplicateHeaders()
    {
        object?[] values = [true, 999999999999999L, 1000000000000001L, 1.25m,
            0.1234567890123456m, new DateOnly(2026, 10, 18),
            new DateTimeOffset(2026, 10, 18, 1, 2, 3, TimeSpan.FromHours(-4)),
            new DateTime(1800, 1, 1), new byte[] { 0, 255 }, "=1+2", null, "", "NULL"];
        var outcome = await Export(values);
        Assert.Equal(ExportStatus.Completed, outcome.Status);
        Assert.Contains("5 Excel cells", outcome.Diagnostic);
        var sheet = ReadSheet();
        Assert.All(sheet.Descendants(Ns + "row").First().Elements(), cell => Assert.Equal("id", cell.Value));
        var cells = sheet.Descendants(Ns + "row").Last().Elements().ToDictionary(c => (string)c.Attribute("r")!);
        Assert.Equal("b", (string?)cells["A2"].Attribute("t"));
        Assert.Equal("n", (string?)cells["B2"].Attribute("t"));
        Assert.Equal("1000000000000001", cells["C2"].Value);
        Assert.Equal("inlineStr", (string?)cells["C2"].Attribute("t"));
        Assert.Equal("1.25", cells["D2"].Value);
        Assert.Equal("0.1234567890123456", cells["E2"].Value);
        Assert.Equal("1", (string?)cells["F2"].Attribute("s"));
        Assert.Equal("2026-10-18T01:02:03.0000000-04:00", cells["G2"].Value);
        Assert.Equal("1800-01-01T00:00:00.0000000", cells["H2"].Value);
        Assert.Equal("AP8=", cells["I2"].Value);
        Assert.Equal("=1+2", cells["J2"].Value);
        Assert.Empty(sheet.Descendants(Ns + "f"));
        Assert.False(cells.ContainsKey("K2"));
        Assert.Equal("inlineStr", (string?)cells["L2"].Attribute("t"));
        Assert.Equal("", cells["L2"].Value);
        Assert.Equal("NULL", cells["M2"].Value);
        using var workbook = new ClosedXML.Excel.XLWorkbook(Path.Combine(_directory, "result.xlsx"));
        Assert.Equal(new DateTime(2026, 10, 18), workbook.Worksheet(1).Cell("F2").GetDateTime());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HonorsHeadersForEmptyAndPopulatedResults(bool headers)
    {
        Assert.Equal(ExportStatus.Completed, (await Export([42], headers)).Status);
        Assert.Equal(headers ? 2 : 1, ReadSheet().Descendants(Ns + "row").Count());
        var source = new LoadedExportSource([new("id", 0)], []);
        Assert.Equal(ExportStatus.Completed, (await Run(source, headers)).Status);
        Assert.Equal(headers ? 1 : 0, ReadSheet().Descendants(Ns + "row").Count());
    }

    [Fact]
    public async Task RejectsLimitsAndUnsupportedValuesWithoutReplacingDestination()
    {
        foreach (var value in new object[] { new string('x', 32768), new object(), new string('\n', 254) })
        {
            await File.WriteAllTextAsync(Path.Combine(_directory, "result.xlsx"), "previous");
            var outcome = await Export([value]);
            Assert.Equal(ExportStatus.Failed, outcome.Status);
            Assert.Contains("row 2, column 1", outcome.Diagnostic);
            Assert.Equal("previous", await File.ReadAllTextAsync(Path.Combine(_directory, "result.xlsx")));
            Assert.Single(Directory.GetFiles(_directory));
        }
        var columns = Enumerable.Range(0, 16385).Select(i => new ExportColumn("x", i));
        var tooWide = await Run(new LoadedExportSource(columns, []));
        Assert.Contains("16384", tooWide.Diagnostic);
    }

    private Task<ExportOutcome> Export(object?[] values, bool headers = true) =>
        Run(new LoadedExportSource(values.Select((_, i) => new ExportColumn("id", i)), [values]), headers);
    private Task<ExportOutcome> Run(IExportSource source, bool headers = true) =>
        new ResultExporter(new Picker(Path.Combine(_directory, "result.xlsx"))).ExportAsync(source,
            new() { Format = ExportFormat.Excel, IncludeHeaders = headers }, ExportScope.FullQuery);
    private XDocument ReadSheet()
    {
        using var zip = ZipFile.OpenRead(Path.Combine(_directory, "result.xlsx"));
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        return XDocument.Load(stream);
    }
    private sealed class Picker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(path);
    }

    [Fact]
    public async Task RejectsRowOverflowAndAcceptsExactLimitWithoutHeaders()
    {
        Assert.Equal(ExportStatus.Completed, (await Run(new ManyRows(), false)).Status);
        var path = Path.Combine(_directory, "result.xlsx");
        await File.WriteAllTextAsync(path, "previous");
        var outcome = await Run(new ManyRows());
        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Contains("1048576", outcome.Diagnostic);
        Assert.Equal("previous", await File.ReadAllTextAsync(path));
    }

    private sealed class ManyRows : IExportSource
    {
        public IReadOnlyList<ExportColumn> Columns { get; } = [new("id", 0)];
        public async IAsyncEnumerable<IReadOnlyList<object?>> ReadRowsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            for (var i = 0; i < 1048576; i++) { token.ThrowIfCancellationRequested(); yield return Array.Empty<object?>(); }
            await Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("finalize")]
    [InlineData("close")]
    [InlineData("publish")]
    public async Task FileFailuresNeverReplaceExistingDestination(string stage)
    {
        var path = Path.Combine(_directory, "result.xlsx");
        await File.WriteAllTextAsync(path, "previous");
        var files = new FaultFiles(stage);
        var outcome = await new ResultExporter(new Picker(path), files).ExportAsync(
            new LoadedExportSource([new("id", 0)], [[42]]), new() { Format = ExportFormat.Excel }, ExportScope.FullQuery);
        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Equal("previous", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
        Assert.Equal(stage == "publish", files.PublicationAttempted);
    }

    private sealed class FaultFiles(string stage) : IExportFileOperations
    {
        public bool PublicationAttempted { get; private set; }
        public Stream CreateTemporary(string path) => new FaultStream(stage);
        public void DeleteTemporary(string path) { }
        public void Publish(string temporaryPath, string destinationPath)
        {
            PublicationAttempted = true;
            throw new IOException("publication failure");
        }
    }

    private sealed class FaultStream(string stage) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (stage == "write" || stage == "finalize" && buffer.Length >= 4
                && buffer[0] == 0x50 && buffer[1] == 0x4b && buffer[2] == 1 && buffer[3] == 2)
                throw new IOException("writer failure");
            var bytes = buffer.ToArray();
            base.Write(bytes, 0, bytes.Length);
        }
        public override ValueTask DisposeAsync()
        {
            base.Dispose();
            return stage == "close" ? ValueTask.FromException(new IOException("close failure")) : ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task PreservesPrecisionExtremesAndEscapedLiteralText()
    {
        object?[] values = [long.MinValue, ulong.MaxValue, double.NaN, double.Epsilon,
            new DateTime(2026, 10, 18).AddTicks(1), new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc),
            "_x0041_\u0001", new string('x', 32767), 1.23456789012345d,
            new DateOnly(1800, 1, 1), DateTime.MaxValue, "a\rb\nc\r\nd"];
        Assert.Equal(ExportStatus.Completed, (await Export(values, false)).Status);
        var cells = ReadSheet().Descendants(Ns + "c").ToArray();
        Assert.Equal("-9223372036854775808", cells[0].Value);
        Assert.Equal("18446744073709551615", cells[1].Value);
        Assert.Equal("NaN", cells[2].Value);
        Assert.Equal("5E-324", cells[3].Value);
        Assert.Equal("2026-10-18T00:00:00.0000001", cells[4].Value);
        Assert.Equal("2026-10-18T00:00:00.0000000Z", cells[5].Value);
        Assert.Equal("_x005F_x0041__x0001_", cells[6].Value);
        Assert.Equal("n", (string?)cells[8].Attribute("t"));
        Assert.Equal("1800-01-01", cells[9].Value);
        Assert.Equal("9999-12-31T23:59:59.9999999", cells[10].Value);
        Assert.Equal("a\rb\nc\r\nd", cells[11].Value);
    }
}
