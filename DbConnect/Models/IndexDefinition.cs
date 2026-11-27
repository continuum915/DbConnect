using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Models;

public enum IndexColumnDirection { Asc, Desc }

public class IndexDefinition
{
    // Stored SQL preserves expression keys, collations, and predicates during rebuilds.
    public string? CreateSql { get; set; }
    public string Name { get; set; } = "";
    public string TableName { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public bool IsUnique { get; set; }
    public bool IsNew { get; set; }
    public bool IsDropped { get; set; }

    public string ColumnsDisplay => string.Join(", ", Columns);

    public string StatusDisplay =>
        IsNew ? "(new)" : IsDropped ? "(dropped)" : "";

    // Per-column sort direction — parallel to Columns list; Asc = default
    public List<IndexColumnDirection> ColumnDirections { get; set; } = new();
    // SQL Server non-clustered INCLUDE columns
    public List<string> IncludedColumns { get; set; } = new();
    // Partial index WHERE predicate (PostgreSQL / SQLite)
    public string? WherePredicate { get; set; }

    /// <summary>The index as the loaded table design has it; its properties are shown read-only.</summary>
    public TableDesignIndex? Loaded { get; set; }

    /// <summary>Shown from the loaded design only, e.g. an index backing a key; never created, changed or dropped here.</summary>
    public bool IsReadOnly { get; set; }

    public bool CanDrop => !IsReadOnly;
    public string KeyPartsDisplay => Loaded == null ? ColumnsDisplay : string.Join(", ", Loaded.Parts.Select(TableDesignText.Describe));
    public string? IncludedDisplay => Loaded is { IncludedColumns.Count: > 0 } loaded ? string.Join(", ", loaded.IncludedColumns) : null;
    public string? FilterDisplay => Loaded?.Filter ?? WherePredicate;
    public string? MethodDisplay => Loaded?.Method;
    public string? CommentDisplay => Loaded?.Comment;
    public string? PropertiesDisplay => Loaded is { Properties.Count: > 0 } loaded ? string.Join("; ", loaded.Properties) : null;

    public string BacksDisplay => Loaded?.Origin switch
    {
        TableDesignIndexOrigin.PrimaryKey => "Primary key",
        TableDesignIndexOrigin.UniqueKey => "Unique key",
        _ => ""
    };
}
