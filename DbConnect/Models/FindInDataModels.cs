using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Models;

public record FindInDataOptions(
    bool CaseSensitive = false,
    bool ExactMatch = false,
    bool TextColumnsOnly = true,
    int RowLimitPerTable = 100);

public record FindInDataProgress(int Current, int Total, string CurrentTable);

public class FindInDataMatch
{
    public string TableName { get; init; } = "";
    public string? ColumnName { get; init; }
    public Dictionary<string, object?>? Row { get; init; }
    public SchemaNode? TableNode { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsError => ErrorMessage is not null;

    public string? Preview => Row == null ? null : string.Join(" | ", Row.Values.Select(v => v?.ToString() ?? "NULL"));

    public static FindInDataMatch Error(string table, string message) =>
        new() { TableName = table, ErrorMessage = message };
}
