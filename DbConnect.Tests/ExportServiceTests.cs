using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ClosedXML.Excel;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class ExportServiceTests
{
    private static readonly List<string> Columns = new() { "Id", "Name", "Value" };

    private static List<List<string>> MakeRows() =>
        new()
        {
            new() { "1", "Alice", "NULL" },
            new() { "2", "Bob, Jr.", "42" }
        };

    private static QueryResult MakeDuplicateColumnResult()
    {
        var result = new QueryResult();
        result.ColumnInfos.Add(new ColumnInfo { Name = "id", Ordinal = 0 });
        result.ColumnInfos.Add(new ColumnInfo { Name = "id", Ordinal = 1 });
        result.ObjectRows.Add(new object?[] { 1, 2 });
        return result;
    }

    [Fact]
    public void BuildCsv_WithOptions_UsesCommaDelimiter()
    {
        var opts = new ExportOptions { Delimiter = CsvDelimiter.Comma };
        var result = QueryResultFormattingService.BuildCsv(Columns, MakeRows(), opts);
        Assert.Contains("Id,Name,Value", result);
        Assert.Contains("\"Bob, Jr.\"", result);
    }

    [Fact]
    public void BuildCsv_WithTabDelimiter_UsesTabs()
    {
        var opts = new ExportOptions { Delimiter = CsvDelimiter.Tab };
        var result = QueryResultFormattingService.BuildCsv(Columns, MakeRows(), opts);
        Assert.Contains("Id\tName\tValue", result);
    }

    [Fact]
    public void BuildCsv_NullAsEmpty_OutputsEmptyString()
    {
        var opts = new ExportOptions { NullRepresentation = NullRepresentation.Empty };
        var result = QueryResultFormattingService.BuildCsv(Columns, MakeRows(), opts);
        var lines = result.Split('\n');
        // first data row: 1,Alice,  (empty for NULL)
        Assert.Contains("1,Alice,", lines[1].Trim());
    }

    [Fact]
    public void BuildCsv_NullAsBackslashN_OutputsBackslashN()
    {
        var opts = new ExportOptions { NullRepresentation = NullRepresentation.BackslashN };
        var result = QueryResultFormattingService.BuildCsv(Columns, MakeRows(), opts);
        Assert.Contains(@"\N", result);
    }

    [Fact]
    public void BuildJson_ProducesValidJsonArray()
    {
        var opts = new ExportOptions();
        var result = QueryResultFormattingService.BuildJson(Columns, MakeRows(), opts);
        Assert.StartsWith("[", result);
        Assert.EndsWith("]", result);
        Assert.Contains("\"Id\"", result);
        Assert.Contains("\"Alice\"", result);
    }

    [Fact]
    public void BuildJson_NullAsJsonNull_OutputsNull()
    {
        var opts = new ExportOptions { NullRepresentation = NullRepresentation.Empty };
        var result = QueryResultFormattingService.BuildJson(Columns, MakeRows(), opts);
        Assert.Contains(":null", result);
    }

    [Fact]
    public void BuildJson_QuotesBackslashesAndControlCharacters_ProduceValidParsableJson()
    {
        // A raw \b (0x08) is JSON-illegal unescaped and was not covered by the old hand-rolled
        // encoder, which only escaped \n/\r/\t alongside quotes and backslashes.
        var rows = new List<List<string>> { new() { "1", "she said \"hi\"\\bye\b", "x" } };

        var result = QueryResultFormattingService.BuildJson(Columns, rows, new ExportOptions());

        using var doc = JsonDocument.Parse(result);
        var name = doc.RootElement[0].GetProperty("Name").GetString();
        Assert.Equal("she said \"hi\"\\bye\b", name);
    }

    [Fact]
    public void BuildCsv_NoHeaders_WhenIncludeHeadersFalse()
    {
        var opts = new ExportOptions { IncludeHeaders = false };
        var result = QueryResultFormattingService.BuildCsv(Columns, MakeRows(), opts);
        Assert.DoesNotContain("Id,Name", result);
        Assert.Contains("1,Alice", result);
    }

    [Fact]
    public async Task ExportExcelAsync_DuplicateColumnNames_KeepsDistinctValuesByOrdinal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-export-{Guid.NewGuid():N}.xlsx");
        try
        {
            await ExportService.ExportExcelAsync(MakeDuplicateColumnResult(), path);

            using var workbook = new XLWorkbook(path);
            var ws = workbook.Worksheet(1);
            Assert.Equal("id", ws.Cell(1, 1).GetString());
            Assert.Equal("id", ws.Cell(1, 2).GetString());
            Assert.Equal("1", ws.Cell(2, 1).GetString());
            Assert.Equal("2", ws.Cell(2, 2).GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportMarkdownAsync_DuplicateColumnNames_KeepsDistinctValuesByOrdinal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-export-{Guid.NewGuid():N}.md");
        try
        {
            await ExportService.ExportMarkdownAsync(MakeDuplicateColumnResult(), path);

            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("| id | id |", content);
            Assert.Contains("| 1 | 2 |", content);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportJsonLinesAsync_DuplicateColumnNames_SuffixesRepeatedKeysToKeepBothValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-export-{Guid.NewGuid():N}.jsonl");
        try
        {
            await ExportService.ExportJsonLinesAsync(MakeDuplicateColumnResult(), path);

            var line = (await File.ReadAllLinesAsync(path))[0];
            using var doc = JsonDocument.Parse(line);
            Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(2, doc.RootElement.GetProperty("id (2)").GetInt32());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
