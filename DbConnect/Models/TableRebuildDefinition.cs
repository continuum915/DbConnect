using System.Collections.Generic;

namespace DbConnect.Models;

/// <summary>The stored definition needed to rebuild a table without losing schema semantics.</summary>
public sealed record TableRebuildDefinition(string CreateSql, IReadOnlySet<string> ObjectNames,
    IReadOnlySet<string> GeneratedColumns)
{
    public IReadOnlyList<SqliteSchemaObject> SchemaObjects { get; init; } = [];

    /// <summary>Keys the rebuild removes, adds or replaces in the table's stored definition.</summary>
    public IReadOnlyList<TableKeyChange> KeyChanges { get; init; } = [];

    /// <summary>Foreign keys the rebuild removes, adds or replaces in the table's stored definition.</summary>
    public IReadOnlyList<TableForeignKeyChange> ForeignKeyChanges { get; init; } = [];

    /// <summary>Checks the rebuild removes, adds or replaces in the table's stored definition.</summary>
    public IReadOnlyList<TableCheckChange> CheckChanges { get; init; } = [];
}
