using DbConnect.Services;

namespace DbConnect.Tests;

public class CsvValidationTests
{
    [Theory]
    [InlineData("bad\"quote,2")]
    [InlineData("\"unclosed,2")]
    [InlineData("\"closed\"junk,2")]
    [InlineData("short")]
    [InlineData("too,many,fields")]
    public async Task ChangedFileIsValidatedBeyondPreviewBeforeAnyDatabaseChanges(string invalidRow)
    {
        var path = Path.GetTempFileName();
        var connectionString = $"Data Source=csv_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        try
        {
            string validContent = "Id,Name\n" + string.Concat(Enumerable.Repeat("1,Alice\n", 21));
            await File.WriteAllTextAsync(path, validContent);
            var preview = await CsvImportService.ParsePreviewAsync(path, ',', true);
            Assert.Equal(20, preview.PreviewRows.Count);
            await File.AppendAllTextAsync(path, invalidRow);
            var error = await Assert.ThrowsAsync<FormatException>(() => CsvImportService.ParsePreviewAsync(path, ',', true));
            Assert.Contains("record 23, physical line 23", error.Message);
            var parseError = Assert.Throws<FormatException>(() => CsvImportService.ParseCsv(path, ',', true, out _));
            Assert.Equal(error.Message, parseError.Message);

            var provider = new SqliteProvider();
            var session = new DatabaseSession(new DbConnect.Models.ConnectionInfo
            {
                Name = "csv",
                Provider = "SQLite",
                ConnectionString = connectionString
            }, provider);
            await session.ExecuteNonQueryBatchAsync("CREATE TABLE people (Id TEXT, Name TEXT); INSERT INTO people VALUES ('7', 'Preserved');");
            foreach (var mode in new[] { DbConnect.Models.ImportMode.ExistingTable, DbConnect.Models.ImportMode.NewTable })
            {
                var result = await CsvImportService.ExecuteImportAsync(session, new DbConnect.Models.CsvImportOptions
                {
                    FilePath = path,
                    TableName = mode == DbConnect.Models.ImportMode.NewTable ? "new_people" : "people",
                    HasHeaderRow = true,
                    Mode = mode,
                    TruncateBeforeImport = true
                });
                Assert.False(result.Succeeded);
                Assert.Equal(0, result.RowsImported);
                Assert.Equal(error.Message, result.ErrorMessage);
            }
            var existing = await provider.ExecuteQueryAsync(connectionString, "SELECT Id, Name FROM people");
            Assert.Equal(new object?[] { "7", "Preserved" }, Assert.Single(existing.ObjectRows));
            var created = await provider.ExecuteQueryAsync(connectionString, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'new_people'");
            Assert.Empty(created.ObjectRows);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("a\"b,c", "Unexpected quote", 1, 1, 1)]
    [InlineData(" \"a\",c", "Unexpected quote", 1, 1, 1)]
    [InlineData("\"a\"junk,c", "closing quote", 1, 1, 1)]
    [InlineData("\"a\" ,c", "closing quote", 1, 1, 1)]
    [InlineData("\"a\n", "Unclosed", 1, 2, 1)]
    [InlineData("\"a\r\n", "Unclosed", 1, 2, 1)]
    [InlineData("\"a\r", "Unclosed", 1, 2, 1)]
    [InlineData("a,b\n1", "expected 2 fields but found 1", 2, 2, 2)]
    [InlineData("a,b\n1,2,3", "expected 2 fields but found 3", 2, 2, 3)]
    [InlineData("a,b\r\n1\r\n", "expected 2 fields but found 1", 2, 2, 2)]
    [InlineData("\n\"a\nb\",c\r\n1,ba\"d", "Unexpected quote", 2, 4, 2)]
    public async Task PreviewAndFullParseRejectMalformedInput(string content, string reason, int record, int line, int column)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, content);
            foreach (bool header in new[] { true, false })
            {
                var error = Assert.Throws<FormatException>(() => CsvImportService.ParseCsv(path, ',', header, out _));
                var previewError = await Assert.ThrowsAsync<FormatException>(() => CsvImportService.ParsePreviewAsync(path, ',', header));
                Assert.Equal(error.Message, previewError.Message);
                Assert.Contains(reason, error.Message);
                Assert.Contains($"record {record}, physical line {line}, field column {column}", error.Message);
            }
        }
        finally { File.Delete(path); }
    }
}
