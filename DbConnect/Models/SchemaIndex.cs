using System;
using System.Collections.Generic;

namespace DbConnect.Models;

public class SchemaIndex(bool caseSensitive = false)
{
    public string Provider { get; set; } = string.Empty;

    // Fast lookup for tables/views
    public HashSet<string> Tables { get; } = new(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    // Mapping table names (bare, and "schema.table" when the table lives in a schema) to a fast lookup of their columns
    public Dictionary<string, HashSet<string>> TableColumns { get; } = new(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    // Global fast lookup for all distinct column names regardless of table
    public HashSet<string> AllColumns { get; } = new(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    // Built-in functions from the provider's capabilities plus user-defined functions found in the schema
    public HashSet<string> Functions { get; } = new(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    // Schema name → the tables, views and routines in it, by object name
    public Dictionary<string, Dictionary<string, SchemaNodeType>> SchemaObjects { get; } = new(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    // Schemas whose objects have been fetched for completion (schema trees are lazy-loaded)
    public HashSet<string> LoadedSchemas { get; } = new(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
}
