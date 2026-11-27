using System.Text;
using DbConnect.Services;

namespace DbConnect.Tests;

public class CsvLogicalRecordTests
{
    [Theory]
    [InlineData("\r\n", true, true)]
    [InlineData("\n", false, true)]
    [InlineData("\r", true, false)]
    [InlineData("\r\n", false, false)]
    public async Task PreservesTextEmptyFieldsAndExplicitHeaderChoice(string ending, bool bom, bool header)
    {
        var path = Path.GetTempFileName();
        try
        {
            var content = ending + "001, leading ,NULL,true,2026-10-16,1.20,\"\"," + ending;
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(bom));
            var rows = CsvImportService.ParseCsv(path, ',', header, out var headers);
            var preview = await CsvImportService.ParsePreviewAsync(path, ',', header);
            string[] expected = ["001", " leading ", "NULL", "true", "2026-10-16", "1.20", "", ""];
            if (header)
            {
                Assert.Equal(expected, headers);
                Assert.Equal(expected, preview.Headers);
                Assert.Empty(rows);
                Assert.Empty(preview.PreviewRows);
                Assert.Equal(0, preview.TotalDataRows);
            }
            else
            {
                Assert.Empty(headers);
                Assert.Equal(8, preview.Headers.Count);
                Assert.Equal(expected, Assert.Single(rows));
                Assert.Equal(expected, Assert.Single(preview.PreviewRows));
                Assert.Equal(1, preview.TotalDataRows);
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n\n\r")]
    public async Task EmptyInputHasNoColumnsOrRows(string content)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, content);
            foreach (bool header in new[] { true, false })
            {
                Assert.Empty(CsvImportService.ParseCsv(path, ',', header, out var headers));
                Assert.Empty(headers);
                var preview = await CsvImportService.ParsePreviewAsync(path, ',', header);
                Assert.Empty(preview.Headers);
                Assert.Empty(preview.PreviewRows);
                Assert.Equal(0, preview.TotalDataRows);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SkipsOnlyPhysicallyEmptyRecords()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "\n ,,\r\n\"\",,\r\n,,\r\n\nlast,,");
            var rows = CsvImportService.ParseCsv(path, ',', false, out _);
            var preview = await CsvImportService.ParsePreviewAsync(path, ',', false);
            Assert.Equal(4, rows.Count);
            Assert.Equal(new[] { " ", "", "" }, rows[0]);
            Assert.Equal(new[] { "", "", "" }, rows[1]);
            Assert.Equal(new[] { "", "", "" }, rows[2]);
            Assert.Equal(new[] { "last", "", "" }, rows[3]);
            Assert.Equal(4, preview.TotalDataRows);
            for (int i = 0; i < rows.Count; i++) Assert.Equal(rows[i], preview.PreviewRows[i]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PreviewCountsAllLogicalRecordsButRetainsOnlyTwenty()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "Value\n" + string.Concat(Enumerable.Repeat("\"first\nsecond\"\n", 57)));
            var preview = await CsvImportService.ParsePreviewAsync(path, ',', true);
            Assert.Equal(57, preview.TotalDataRows);
            Assert.Equal(20, preview.PreviewRows.Count);
            Assert.All(preview.PreviewRows, row => Assert.Equal(new[] { "first\nsecond" }, row));
            Assert.Equal(57, CsvImportService.ParseCsv(path, ',', true, out _).Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportPreservesNonemptyMultilineTextInSqlite()
    {
        var path = Path.GetTempFileName();
        var connectionString = $"Data Source=csv_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        try
        {
            await File.WriteAllTextAsync(path, "Value\n\" 001\r\n\nO'Brien \"\"quoted\"\" \"");
            var provider = new SqliteProvider();
            var session = new DatabaseSession(new DbConnect.Models.ConnectionInfo
            {
                Name = "csv",
                Provider = "SQLite",
                ConnectionString = connectionString
            }, provider);
            var result = await CsvImportService.ExecuteImportAsync(session, new DbConnect.Models.CsvImportOptions
            {
                FilePath = path,
                TableName = "records",
                HasHeaderRow = true
            });
            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.Equal(1, result.RowsImported);
            var query = await provider.ExecuteQueryAsync(connectionString, "SELECT Value FROM records");
            Assert.Equal(" 001\r\n\nO'Brien \"quoted\" ", Assert.Single(query.ObjectRows)[0]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(',')]
    [InlineData('\t')]
    [InlineData('|')]
    [InlineData(';')]
    public async Task PreviewAndFullParsePreserveMultilineFields(char delimiter)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path,
                $"Id{delimiter}Text\r\n001{delimiter}\"first{delimiter}\"\"quoted\"\"\r\n\nlast\rline\"\r\n", new UTF8Encoding(false));
            var rows = CsvImportService.ParseCsv(path, delimiter, true, out var headers);
            var preview = await CsvImportService.ParsePreviewAsync(path, delimiter, true);
            Assert.Equal(new[] { "Id", "Text" }, headers);
            Assert.Equal(headers, preview.Headers);
            Assert.Equal(new[] { "001", $"first{delimiter}\"quoted\"\r\n\nlast\rline" }, Assert.Single(rows));
            Assert.Equal(rows[0], Assert.Single(preview.PreviewRows));
            Assert.Equal(1, preview.TotalDataRows);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
