using System;
using System.Collections.Generic;

namespace DbConnect.Models;

public enum SchemaDiffObjectType
{
    Table,
    View,
    Function,
    Procedure,
    Trigger,
    Index,
    Sequence,
    Unknown
}

public enum SchemaDiffChangeType
{
    None,
    Add,
    Modify,
    Remove
}

public static class SchemaDiffObjectTypeExtensions
{
    public static SchemaDiffObjectType Parse(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "TABLE" or "BASE TABLE" or "VIRTUAL TABLE" => SchemaDiffObjectType.Table,
        "VIEW" => SchemaDiffObjectType.View,
        "FUNCTION" => SchemaDiffObjectType.Function,
        "PROCEDURE" => SchemaDiffObjectType.Procedure,
        "TRIGGER" => SchemaDiffObjectType.Trigger,
        "INDEX" => SchemaDiffObjectType.Index,
        "SEQUENCE" => SchemaDiffObjectType.Sequence,
        _ => SchemaDiffObjectType.Unknown
    };

    public static string ToObjectTypeName(this SchemaDiffObjectType value) => value switch
    {
        SchemaDiffObjectType.Table => "table",
        SchemaDiffObjectType.View => "view",
        SchemaDiffObjectType.Function => "function",
        SchemaDiffObjectType.Procedure => "procedure",
        SchemaDiffObjectType.Trigger => "trigger",
        SchemaDiffObjectType.Index => "index",
        SchemaDiffObjectType.Sequence => "sequence",
        _ => "unknown"
    };

    public static string ToSqlKeyword(this SchemaDiffObjectType value) => value switch
    {
        SchemaDiffObjectType.Table => "TABLE",
        SchemaDiffObjectType.View => "VIEW",
        SchemaDiffObjectType.Function => "FUNCTION",
        SchemaDiffObjectType.Procedure => "PROCEDURE",
        SchemaDiffObjectType.Trigger => "TRIGGER",
        SchemaDiffObjectType.Index => "INDEX",
        SchemaDiffObjectType.Sequence => "SEQUENCE",
        _ => "OBJECT"
    };
}

public class SortedDiffResult
{
    public List<SchemaDiffItem> SortedItems { get; set; } = new();
    public List<List<string>> CircularGroups { get; set; } = new();
    public ImpactSummary ImpactSummary { get; set; } = new();
}

public class ImpactSummary
{
    public int AffectedViewCount { get; set; }
    public int AffectedProcedureCount { get; set; }
    public bool HasImpact => AffectedViewCount + AffectedProcedureCount > 0;

    public string FormatMessage() => HasImpact
        ? $"This change will invalidate {AffectedViewCount} view(s) and {AffectedProcedureCount} procedure/function(s) — they will be included in the script for recompilation."
        : string.Empty;
}
