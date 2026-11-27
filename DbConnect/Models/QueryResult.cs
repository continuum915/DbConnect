using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DbConnect.Models;

public class QueryResult
{
    /// <summary>Evidence from an explicit session COMMIT or ROLLBACK statement.</summary>
    public TransactionResult? TransactionResult { get; set; }
    /// <summary>
    /// Columnar result shape: an ordered column-info array told apart by <see cref="ColumnInfo.Ordinal"/>
    /// (so duplicate names, e.g. <c>SELECT a.id, b.id</c>, stay distinct) alongside object-array rows
    /// indexed the same way. This replaced the old name-keyed dictionary rows as of ticket 10.
    /// </summary>
    public List<ColumnInfo> ColumnInfos { get; set; } = new();
    public List<object?[]> ObjectRows { get; set; } = new();
    private string? _errorMessage;
    public QueryErrorInfo? Error { get; set; }
    public string? ErrorMessage
    {
        get => Error?.Summary ?? _errorMessage;
        set => _errorMessage = value;
    }
    /// <summary>Rows changed by a statement without a result set; negative when the driver does not report a count.</summary>
    public int RecordsAffected { get; set; }
    public string ExecutedSql
    {
        get => ExecutedSqlSource.ToString();
        set => ExecutedSqlSource = new ScriptText(value);
    }
    /// <summary>Where <see cref="ExecutedSql"/> comes from, so a large batch does not keep a copy per statement.</summary>
    [JsonIgnore]
    public ScriptText ExecutedSqlSource { get; set; }
    public QueryExecutionScope ExecutionScope { get; set; }
    public TimeSpan Duration { get; set; }
    public List<QueryStatementExecution> Statements { get; set; } = new();
    public List<string> Messages { get; set; } = new();
    public int StatementCount { get; set; }
    public int ReturnedStatementIndex { get; set; } = -1;
    /// <summary>Start of the SQL sent to the driver within the submitted batch.</summary>
    public int StatementStart { get; set; }
    public bool IsTruncated { get; set; }
    public int RowCap { get; set; }
    /// <summary>Label for result tab when multiple results are present. Auto-set by executor.</summary>
    public string TabLabel { get; set; } = "Results";
    /// <summary>Zero-based index among sibling results from the same execution.</summary>
    public int ResultIndex { get; set; }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasRows => ObjectRows.Count > 0;
    public bool HasResultSet => ColumnInfos.Count > 0;

    public string StructuredErrorSummary =>
        Error?.ToDisplaySummary() ?? ErrorMessage ?? string.Empty;

    /// <summary>Ordinal of the first column matching <paramref name="name"/> (case-insensitive), or -1.</summary>
    public int IndexOfColumn(string name) =>
        ColumnInfos.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Value of the first non-null column among <paramref name="names"/> (case-insensitive) in
    /// <paramref name="row"/>, or null when none match or are populated. Mirrors the fallback-name
    /// lookup providers use when a column's name varies across databases (e.g. "name" vs "table_name").
    /// </summary>
    public object? ValueOrDefault(object?[] row, params string[] names)
    {
        foreach (var name in names)
        {
            var index = IndexOfColumn(name);
            if (index >= 0 && index < row.Length && row[index] != null)
                return row[index];
        }

        return null;
    }

    /// <summary>
    /// Expands one <see cref="ObjectRows"/> entry back into a name-keyed dictionary for consumers that
    /// still need name-based access (e.g. adapting into a model built around named fields). Duplicate
    /// column names collapse to their last occurrence, same as the old dictionary-rows shape did.
    /// </summary>
    public Dictionary<string, object?> ToRowDictionary(object?[] row)
    {
        var values = new Dictionary<string, object?>();
        for (var i = 0; i < ColumnInfos.Count && i < row.Length; i++)
            values[ColumnInfos[i].Name] = row[i];

        return values;
    }
}
