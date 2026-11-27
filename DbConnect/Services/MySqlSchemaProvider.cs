using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class MySqlSchemaProvider : IDatabaseSchemaProvider, IStructuredSchemaProvider
{
    public async Task<List<SchemaNode>> GetSchemaAsync(string connectionString)
    {
        var root = new SchemaNode { Name = "MySQL", RawName = "mysql", Type = SchemaNodeType.Database };

        await using var connection = MySqlConnections.Create(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);
        root.CatalogName = connection.Database;
        var database = connection.Database;

        // Tables and views
        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = @"
            SELECT TABLE_NAME, TABLE_TYPE
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = @db
            ORDER BY TABLE_TYPE DESC, TABLE_NAME;";
        tableCmd.Parameters.AddWithValue("@db", database);

        var tables = new List<(string Name, string Type)>();
        await using (var tableReader = await tableCmd.ExecuteReaderAsync(MetadataReadContext.Token))
        {
            while (await tableReader.ReadAsync(MetadataReadContext.Token))
                tables.Add((tableReader.GetString(0), tableReader.GetString(1)));
        }

        foreach (var (tableName, tableType) in tables)
        {
            if (tableType == "VIEW")
            {
                var viewNode = new SchemaNode { Name = tableName, RawName = tableName, Type = SchemaNodeType.View, IsLazyLoadable = true };
                root.Children.Add(viewNode);
            }
            else
            {
                var tableNode = new SchemaNode { Name = tableName, RawName = tableName, Type = SchemaNodeType.Table, IsLazyLoadable = true };
                if (ProviderCapabilityDeclarations.MySql.SupportsIndexes)
                    tableNode.Children.Add(MakeFolderNode("Indexes", SchemaNodeType.IndexFolder));
                if (ProviderCapabilityDeclarations.MySql.SupportsTriggers)
                    tableNode.Children.Add(MakeFolderNode("Triggers", SchemaNodeType.TriggerFolder));
                root.Children.Add(tableNode);
            }
        }

        if (ProviderCapabilityDeclarations.MySql.SupportsFunctions)
            root.Children.Add(MakeFolderNode("Functions", SchemaNodeType.FunctionFolder));
        if (ProviderCapabilityDeclarations.MySql.SupportsProcedures)
            root.Children.Add(MakeFolderNode("Procedures", SchemaNodeType.ProcedureFolder));

        return new List<SchemaNode> { root };
    }

    public async Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type)
    {
        var nodes = new List<SchemaNode>();
        await using var connection = MySqlConnections.Create(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);
        var database = connection.Database;

        switch (type)
        {
            case SchemaNodeType.Index:
            case SchemaNodeType.IndexFolder:
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                    SELECT DISTINCT INDEX_NAME
                    FROM INFORMATION_SCHEMA.STATISTICS
                    WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table AND INDEX_NAME <> 'PRIMARY'
                    ORDER BY INDEX_NAME;";
                    cmd.Parameters.AddWithValue("@db", database);
                    cmd.Parameters.AddWithValue("@table", parentName);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode
                        {
                            Name = reader.GetString(0),
                            RawName = reader.GetString(0),
                            Type = SchemaNodeType.Index
                        });
                    break;
                }

            case SchemaNodeType.Trigger:
            case SchemaNodeType.TriggerFolder:
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                    SELECT TRIGGER_NAME
                    FROM INFORMATION_SCHEMA.TRIGGERS
                    WHERE TRIGGER_SCHEMA = @db AND EVENT_OBJECT_TABLE = @table
                    ORDER BY TRIGGER_NAME;";
                    cmd.Parameters.AddWithValue("@db", database);
                    cmd.Parameters.AddWithValue("@table", parentName);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode
                        {
                            Name = reader.GetString(0),
                            RawName = reader.GetString(0),
                            Type = SchemaNodeType.Trigger
                        });
                    break;
                }

            case SchemaNodeType.Function:
            case SchemaNodeType.FunctionFolder:
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                    SELECT ROUTINE_NAME
                    FROM INFORMATION_SCHEMA.ROUTINES
                    WHERE ROUTINE_SCHEMA = @db AND ROUTINE_TYPE = 'FUNCTION'
                    ORDER BY ROUTINE_NAME;";
                    cmd.Parameters.AddWithValue("@db", database);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode
                        {
                            Name = reader.GetString(0),
                            RawName = reader.GetString(0),
                            Type = SchemaNodeType.Function
                        });
                    break;
                }

            case SchemaNodeType.Procedure:
            case SchemaNodeType.ProcedureFolder:
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                    SELECT ROUTINE_NAME
                    FROM INFORMATION_SCHEMA.ROUTINES
                    WHERE ROUTINE_SCHEMA = @db AND ROUTINE_TYPE = 'PROCEDURE'
                    ORDER BY ROUTINE_NAME;";
                    cmd.Parameters.AddWithValue("@db", database);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode
                        {
                            Name = reader.GetString(0),
                            RawName = reader.GetString(0),
                            Type = SchemaNodeType.Procedure
                        });
                    break;
                }
        }

        return nodes;
    }

    public async Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName)
    {
        var indexes = new List<IndexDefinition>();
        await using var connection = MySqlConnections.Create(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);
        var database = connection.Database;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT INDEX_NAME, NON_UNIQUE, COALESCE(COLUMN_NAME, CONCAT('(', EXPRESSION, ')'))
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table AND INDEX_NAME <> 'PRIMARY'
            ORDER BY INDEX_NAME, SEQ_IN_INDEX;";
        cmd.Parameters.AddWithValue("@db", database);
        cmd.Parameters.AddWithValue("@table", tableName);

        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        IndexDefinition? current = null;
        while (await reader.ReadAsync(MetadataReadContext.Token))
        {
            var idxName = reader.GetString(0);
            var nonUnique = reader.GetInt32(1);
            if (current == null || current.Name != idxName)
            {
                current = new IndexDefinition
                {
                    Name = idxName,
                    TableName = tableName,
                    IsUnique = nonUnique == 0
                };
                indexes.Add(current);
            }
            current.Columns.Add(reader.GetString(2));
        }
        return indexes;
    }

    public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName)
    {
        return GetColumnsCoreAsync(connectionString, "", tableName);
    }

    public async Task<string?> GetViewDefinitionAsync(string connectionString, string viewName)
    {
        try
        {
            await using var connection = MySqlConnections.Create(connectionString);
            await connection.OpenAsync(MetadataReadContext.Token);
            await using var cmd = connection.CreateCommand();
            // SHOW CREATE VIEW returns: View | Create View | character_set_client | collation_connection
            cmd.CommandText = $"SHOW CREATE VIEW `{viewName.Replace("`", "``")}`;";
            await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
            if (await reader.ReadAsync(MetadataReadContext.Token))
                return reader.IsDBNull(1) ? null : reader.GetString(1);
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GetTriggerDefinitionAsync(string connectionString, string schema, string triggerName)
    {
        try
        {
            await using var conn = new MySqlConnector.MySqlConnection(connectionString);
            await conn.OpenAsync(MetadataReadContext.Token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SHOW CREATE TRIGGER `{triggerName}`";
            await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
            if (await reader.ReadAsync(MetadataReadContext.Token))
            {
                return reader.IsDBNull(2) ? null : reader.GetString(2);
            }
            return null;
        }
        catch { return null; }
    }

    public List<string> GetSupportedDataTypes() => new()
    {
        "INT", "BIGINT", "TINYINT", "SMALLINT", "MEDIUMINT",
        "DECIMAL(10,2)", "FLOAT", "DOUBLE",
        "VARCHAR(255)", "TEXT", "MEDIUMTEXT", "LONGTEXT", "CHAR(1)",
        "ENUM('a','b')",
        "DATE", "DATETIME", "TIMESTAMP", "TIME", "YEAR",
        "BOOLEAN",
        "BLOB", "MEDIUMBLOB", "LONGBLOB",
        "JSON"
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SchemaNode MakeFolderNode(string name, SchemaNodeType type)
    {
        var node = new SchemaNode { Name = name, Type = type, IsLazyLoadable = true };
        node.Children.Add(new SchemaNode { Name = "Loading…", Type = SchemaNodeType.Column });
        return node;
    }

    public async Task<string?> GetTableCommentAsync(string connectionString, string tableName, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync(ct);
            var database = conn.Database;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT TABLE_COMMENT FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tn";
            cmd.Parameters.AddWithValue("@db", database);
            cmd.Parameters.AddWithValue("@tn", tableName);
            var result = await cmd.ExecuteScalarAsync(ct);
            var comment = result as string;
            return string.IsNullOrEmpty(comment) ? null : comment;
        }
        catch { return null; }
    }

    public async Task<Dictionary<string, string>> GetColumnCommentsAsync(string connectionString, string tableName, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync(ct);
            var database = conn.Database;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COLUMN_NAME, COLUMN_COMMENT FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tn AND COLUMN_COMMENT <> ''";
            cmd.Parameters.AddWithValue("@db", database);
            cmd.Parameters.AddWithValue("@tn", tableName);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result[reader.GetString(0)] = reader.GetString(1);
        }
        catch { /* non-fatal */ }
        return result;
    }

    public async Task<Dictionary<string, EnumColumnInfo>> GetEnumColumnsAsync(string connectionString, string tableName, CancellationToken ct = default)
    {
        var result = new Dictionary<string, EnumColumnInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync(ct);
            var database = conn.Database;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COLUMN_NAME, DATA_TYPE, COLUMN_TYPE FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tn AND DATA_TYPE IN ('enum', 'set')";
            cmd.Parameters.AddWithValue("@db", database);
            cmd.Parameters.AddWithValue("@tn", tableName);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var columnName = reader.GetString(0);
                var dataType = reader.GetString(1);
                var columnType = reader.GetString(2);
                var values = MySqlEnumSetLiteralParser.Parse(columnType);
                if (values.Count > 0)
                    result[columnName] = new EnumColumnInfo(values, AllowsMultipleValues: dataType.Equals("set", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { /* non-fatal */ }
        return result;
    }

    public async Task SetTableCommentAsync(string connectionString, string tableName, string? comment, CancellationToken ct = default)
    {
        try
        {
            var quoted = ProviderCapabilityDeclarations.MySql.QuoteIdentifier(tableName);
            var escaped = (comment ?? "").Replace("'", "\\'");
            await using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {quoted} COMMENT = '{escaped}'";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { /* non-fatal */ }
    }

    public async Task SetColumnCommentAsync(string connectionString, string tableName, string columnName, string? comment, CancellationToken ct = default)
    {
        try
        {
            var cols = await GetColumnDefinitionsAsync(connectionString, tableName);
            var col = cols.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
            if (col == null) return;

            var quotedTable = ProviderCapabilityDeclarations.MySql.QuoteIdentifier(tableName);
            var quotedCol = ProviderCapabilityDeclarations.MySql.QuoteIdentifier(columnName);
            var type = col.MaxLength.HasValue ? $"{col.DataType}({col.MaxLength})" : col.DataType;
            var nullable = col.IsNullable ? "NULL" : "NOT NULL";
            var escaped = (comment ?? "").Replace("'", "\\'");
            await using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {quotedTable} MODIFY COLUMN {quotedCol} {type} {nullable} COMMENT '{escaped}'";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { /* non-fatal */ }
    }
}
