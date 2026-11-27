using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class QueryResultFormattingServiceTests
{
    [Fact]
    public void FormatValue_FormatsCommonDatabaseValues()
    {
        Assert.Equal("NULL", QueryResultFormattingService.FormatValue(null));
        Assert.Equal("TRUE", QueryResultFormattingService.FormatValue(true));
        Assert.Equal("FALSE", QueryResultFormattingService.FormatValue(false));
        Assert.Equal("12.5", QueryResultFormattingService.FormatValue(12.5m));
        Assert.Equal("2025-01-02 03:04:05", QueryResultFormattingService.FormatValue(
            new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc)));
        Assert.Equal("[1,\"two\"]", QueryResultFormattingService.FormatValue(new object?[] { 1, "two" }));
        Assert.Equal("{\"a\":1,\"b\":[true,null]}", QueryResultFormattingService.FormatValue("""
            {
              "a": 1,
              "b": [ true, null ]
            }
            """));
        Assert.Equal("not json", QueryResultFormattingService.FormatValue("not json"));
    }

    [Fact]
    public void BuildCsv_EscapesValues()
    {
        var csv = QueryResultFormattingService.BuildCsv(
            ["Name", "Notes"],
            [
                new List<string> { "Ada", "hello, \"world\"" }
            ]);

        Assert.Contains("Name,Notes", csv);
        Assert.Contains("Ada,\"hello, \"\"world\"\"\"", csv);
    }

    [Fact]
    public void BuildDisplayRows_UsesFormattedCellValues()
    {
        var result = new QueryResult
        {
            ColumnInfos =
            [
                new ColumnInfo { Name = "Id", Ordinal = 0 },
                new ColumnInfo { Name = "IsActive", Ordinal = 1 },
                new ColumnInfo { Name = "Name", Ordinal = 2 },
                new ColumnInfo { Name = "Payload", Ordinal = 3 }
            ],
            ObjectRows =
            [
                new object?[]
                {
                    1,
                    true,
                    null,
                    """
                    {
                      "ok": true
                    }
                    """
                }
            ]
        };

        var row = Assert.Single(QueryResultFormattingService.BuildDisplayRows(result));
        Assert.Equal("1", row.DisplayValues[0]);
        Assert.Equal("TRUE", row.DisplayValues[1]);
        Assert.Equal("NULL", row.DisplayValues[2]);
        Assert.Equal("{\"ok\":true}", row.DisplayValues[3]);
    }

    [Fact]
    public void BuildDisplayRows_PreservesDuplicateColumnNamesByOrdinal()
    {
        // SELECT a.id, b.id: two columns named "id", told apart by ordinal.
        var result = new QueryResult
        {
            ColumnInfos =
            [
                new ColumnInfo { Name = "id", Ordinal = 0 },
                new ColumnInfo { Name = "id", Ordinal = 1 }
            ],
            ObjectRows = [new object?[] { 1, 2 }]
        };

        var row = Assert.Single(QueryResultFormattingService.BuildDisplayRows(result));
        Assert.Equal("1", row.DisplayValues[0]);
        Assert.Equal("2", row.DisplayValues[1]);
    }
}
