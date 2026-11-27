using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Database schema operations, independent of other provider roles.</summary>
public interface IDatabaseSchemaProvider
{
    /// <summary>Returns the top-level schema tree (databases → tables/views → columns).</summary>
    Task<List<SchemaNode>> GetSchemaAsync(string connectionString);

    /// <summary>
    /// Lazy-loads a specific class of schema objects beneath a given parent.
    /// Useful for on-demand expansion of large schemas.
    /// </summary>
    Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type);

    /// <summary>Returns all index definitions for a given table.</summary>
    Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName);

    /// <summary>Returns column definitions for the Table Designer (structured form).</summary>
    Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName);

    /// <summary>
    /// Returns a complete provider-correct CREATE VIEW script, or null when the provider
    /// cannot retrieve it (e.g. the view does not exist or metadata access is denied).
    /// </summary>
    Task<string?> GetViewDefinitionAsync(string connectionString, string viewName);

    /// <summary>Returns a list of data type names valid for new columns on this provider.</summary>
    List<string> GetSupportedDataTypes();

    // ── Optional metadata retrieval (default: empty / null) ───────────────────

    /// <summary>
    /// Returns user-visible schema names for multi-schema providers (PostgreSQL, SQL Server).
    /// Default: empty list (SQLite uses a single flat namespace).
    /// </summary>
    Task<List<string>> GetSchemasAsync(string connectionString) =>
        Task.FromResult(new List<string>());

    /// <summary>
    /// Returns the complete CREATE TRIGGER DDL for a named trigger within a schema.
    /// Returns null when the definition cannot be retrieved.
    /// </summary>
    Task<string?> GetTriggerDefinitionAsync(string connectionString, string schema, string triggerName) =>
        Task.FromResult<string?>(null);

    // ── Object comments (PostgreSQL, MySQL, SQL Server) ─────────────────────

    /// <summary>Returns the table-level comment, or null if not set / not supported.</summary>
    Task<string?> GetTableCommentAsync(string connectionString, string tableName, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    /// <summary>Returns a map of column name → comment for the specified table. Empty when not supported.</summary>
    Task<Dictionary<string, string>> GetColumnCommentsAsync(string connectionString, string tableName, CancellationToken ct = default) =>
        Task.FromResult(new Dictionary<string, string>());

    /// <summary>
    /// RAIN-P2-09 — Returns a map of column name → allowed values for every ENUM/SET column
    /// (MySQL) or native enum column (PostgreSQL) on the table, read straight from the
    /// provider's own metadata catalog. Empty for providers with no such column type
    /// (SQLite, SQL Server, DuckDB) or when the table has none.
    /// </summary>
    Task<Dictionary<string, EnumColumnInfo>> GetEnumColumnsAsync(string connectionString, string tableName, CancellationToken ct = default) =>
        Task.FromResult(new Dictionary<string, EnumColumnInfo>());

    /// <summary>Persists a table-level comment. No-op when not supported.</summary>
    Task SetTableCommentAsync(string connectionString, string tableName, string? comment, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>Persists a column-level comment. No-op when not supported.</summary>
    Task SetColumnCommentAsync(string connectionString, string tableName, string columnName, string? comment, CancellationToken ct = default) =>
        Task.CompletedTask;
}
