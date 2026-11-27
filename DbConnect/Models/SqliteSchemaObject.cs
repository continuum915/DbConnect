using System.Collections.Generic;

namespace DbConnect.Models;

/// <summary>Stored SQL and resolved dependencies of a SQLite trigger or view.</summary>
public sealed record SqliteSchemaObject(string Type, string Name, string TableName, string CreateSql,
    IReadOnlySet<string> ReferencedObjects, IReadOnlySet<string> ReferencedColumns);
