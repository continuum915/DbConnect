using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DbConnect.Models;

public sealed class QueryStatementExecution
{
    public int Index { get; set; }
    public string Sql
    {
        get => SqlSource.ToString();
        set => SqlSource = new ScriptText(value);
    }
    /// <summary>Where <see cref="Sql"/> comes from, so a large batch does not keep a copy per statement.</summary>
    [JsonIgnore]
    public ScriptText SqlSource { get; set; }
    public TimeSpan Duration { get; set; }
    public int RecordsAffected { get; set; }
    public int RowCount { get; set; }
    public bool HasResultSet { get; set; }
    public QueryErrorInfo? Error { get; set; }
    public List<string> Messages { get; set; } = new();

    public string Summary
    {
        get
        {
            var prefix = $"#{Index + 1}";
            if (Error != null)
                return $"{prefix}: {Error.ToDisplaySummary()}";

            if (HasResultSet)
                return $"{prefix}: {RowCount:N0} row(s) returned in {Duration.TotalMilliseconds:N0} ms";

            return RecordsAffected >= 0
                ? $"{prefix}: {RecordsAffected:N0} row(s) affected in {Duration.TotalMilliseconds:N0} ms"
                : $"{prefix}: executed in {Duration.TotalMilliseconds:N0} ms";
        }
    }
}
