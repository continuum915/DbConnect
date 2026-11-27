using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class SqlServerSchemaProvider : IDatabaseSchemaProvider, IStructuredSchemaProvider
{
    public async Task<List<SchemaNode>> GetSchemaAsync(string connectionString)
    {
        var root = new SchemaNode { Name = "SQL Server", RawName = "sqlserver", Type = SchemaNodeType.Database };

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);
        root.CatalogName = connection.Database;

        var schemas = await GetSchemaListAsync(connection);
        foreach (var schemaName in schemas)
        {
            var schemaNode = new SchemaNode
            {
                Name = schemaName,
                RawName = schemaName,
                Type = SchemaNodeType.Schema,
                SchemaPath = schemaName
            };

            schemaNode.Children.Add(MakeFolderNode("Tables", SchemaNodeType.TableFolder));
            schemaNode.Children.Add(MakeFolderNode("Views", SchemaNodeType.ViewFolder));
            if (ProviderCapabilityDeclarations.SqlServer.SupportsFunctions)
                schemaNode.Children.Add(MakeFolderNode("Functions", SchemaNodeType.FunctionFolder));
            if (ProviderCapabilityDeclarations.SqlServer.SupportsProcedures)
                schemaNode.Children.Add(MakeFolderNode("Procedures", SchemaNodeType.ProcedureFolder));

            root.Children.Add(schemaNode);
        }

        return new List<SchemaNode> { root };
    }

    public Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type)
    {
        ParseParentName(parentName, out var schema, out var tableName);
        return GetChildrenCoreAsync(connectionString, schema, tableName, type);
    }

    private async Task<List<SchemaNode>> GetChildrenCoreAsync(string connectionString, string schema, string tableName, SchemaNodeType type)
    {
        var nodes = new List<SchemaNode>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);

        switch (type)
        {
            case SchemaNodeType.Table:
                {
                    var tableNames = await GetObjectNamesAsync(connection, "U", schema);
                    foreach (var name in tableNames)
                    {
                        var tableNode = new SchemaNode
                        {
                            Name = name,
                            RawName = name,
                            Type = SchemaNodeType.Table,
                            SchemaName = schema,
                            SchemaPath = schema
                        };
                        await PopulateColumnsAsync(connection, name, schema, tableNode);
                        if (ProviderCapabilityDeclarations.SqlServer.SupportsIndexes)
                            tableNode.Children.Add(MakeFolderNode("Indexes", SchemaNodeType.IndexFolder));
                        if (ProviderCapabilityDeclarations.SqlServer.SupportsTriggers)
                            tableNode.Children.Add(MakeFolderNode("Triggers", SchemaNodeType.TriggerFolder));
                        if (ProviderCapabilityDeclarations.SqlServer.SupportsForeignKeys)
                            tableNode.Children.Add(MakeFolderNode("Foreign Keys", SchemaNodeType.ForeignKeyFolder));
                        nodes.Add(tableNode);
                    }
                    break;
                }

            case SchemaNodeType.View:
                {
                    var viewNames = await GetObjectNamesAsync(connection, "V", schema);
                    foreach (var name in viewNames)
                    {
                        var viewNode = new SchemaNode
                        {
                            Name = name,
                            RawName = name,
                            Type = SchemaNodeType.View,
                            SchemaName = schema,
                            SchemaPath = schema
                        };
                        await PopulateColumnsAsync(connection, name, schema, viewNode);
                        nodes.Add(viewNode);
                    }
                    break;
                }

            case SchemaNodeType.Index:
                {
                    await using var cmd = new SqlCommand(
                        @"SELECT i.name FROM sys.indexes i
                      JOIN sys.objects o ON i.object_id = o.object_id
                      WHERE o.name = @table AND SCHEMA_NAME(o.schema_id) = @schema
                        AND i.name IS NOT NULL
                      ORDER BY i.name;", connection);
                    cmd.Parameters.AddWithValue("@table", tableName);
                    cmd.Parameters.AddWithValue("@schema", schema);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode { Name = reader.GetString(0), RawName = reader.GetString(0), Type = SchemaNodeType.Index });
                    break;
                }

            case SchemaNodeType.Trigger:
                {
                    await using var cmd = new SqlCommand(
                        @"SELECT t.name FROM sys.triggers t
                      JOIN sys.objects o ON t.parent_id = o.object_id
                      WHERE o.name = @table AND SCHEMA_NAME(o.schema_id) = @schema
                      ORDER BY t.name;", connection);
                    cmd.Parameters.AddWithValue("@table", tableName);
                    cmd.Parameters.AddWithValue("@schema", schema);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode { Name = reader.GetString(0), RawName = reader.GetString(0), Type = SchemaNodeType.Trigger });
                    break;
                }

            case SchemaNodeType.Function:
                {
                    await using var cmd = new SqlCommand(
                        @"SELECT o.name FROM sys.objects o
                      WHERE o.type IN ('FN','TF','IF') AND SCHEMA_NAME(o.schema_id) = @schema
                      ORDER BY o.name;", connection);
                    cmd.Parameters.AddWithValue("@schema", schema);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode
                        {
                            Name = reader.GetString(0),
                            RawName = reader.GetString(0),
                            Type = SchemaNodeType.Function,
                            SchemaPath = schema
                        });
                    break;
                }

            case SchemaNodeType.Procedure:
                {
                    // Load all procedures for the schema
                    await using var cmd = new SqlCommand(
                        @"SELECT o.name FROM sys.objects o
                      WHERE o.type = 'P' AND SCHEMA_NAME(o.schema_id) = @schema
                      ORDER BY o.name;", connection);
                    cmd.Parameters.AddWithValue("@schema", schema);

                    var procNodes = new List<SchemaNode>();
                    await using (var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token))
                    {
                        while (await reader.ReadAsync(MetadataReadContext.Token))
                            procNodes.Add(new SchemaNode
                            {
                                Name = reader.GetString(0),
                                RawName = reader.GetString(0),
                                Type = SchemaNodeType.Procedure,
                                SchemaPath = schema
                            });
                    }

                    // Load all parameters for this schema's procedures in one query
                    await using var paramCmd = new SqlCommand(
                        @"SELECT o.name AS proc_name,
                             pp.name AS param_name,
                             tp.name AS type_name,
                             pp.is_output
                      FROM sys.objects o
                      JOIN sys.parameters pp ON pp.object_id = o.object_id AND pp.parameter_id > 0
                      JOIN sys.types tp ON pp.system_type_id = tp.system_type_id
                                       AND tp.user_type_id = tp.system_type_id
                      WHERE o.type = 'P' AND SCHEMA_NAME(o.schema_id) = @schema
                      ORDER BY o.name, pp.parameter_id;", connection);
                    paramCmd.Parameters.AddWithValue("@schema", schema);

                    var paramsByProc = new Dictionary<string, List<SchemaNode>>(StringComparer.OrdinalIgnoreCase);
                    await using (var pReader = await paramCmd.ExecuteReaderAsync(MetadataReadContext.Token))
                    {
                        while (await pReader.ReadAsync(MetadataReadContext.Token))
                        {
                            var procName = pReader.GetString(0);
                            var paramName = pReader.GetString(1);
                            var typeName = pReader.GetString(2);
                            var isOutput = pReader.GetBoolean(3);
                            var direction = isOutput ? " OUT" : "";
                            if (!paramsByProc.TryGetValue(procName, out var list))
                                paramsByProc[procName] = list = [];
                            list.Add(new SchemaNode
                            {
                                Name = $"{paramName} ({typeName}{direction})",
                                RawName = paramName,
                                Type = SchemaNodeType.Column
                            });
                        }
                    }

                    foreach (var proc in procNodes)
                    {
                        if (!string.IsNullOrEmpty(proc.RawName) && paramsByProc.TryGetValue(proc.RawName, out var paramList))
                            foreach (var p in paramList)
                                proc.Children.Add(p);
                        nodes.Add(proc);
                    }
                    break;
                }

            case SchemaNodeType.ForeignKey:
                {
                    await using var cmd = new SqlCommand(
                        @"SELECT fk.name, OBJECT_NAME(fk.referenced_object_id) AS ref_table
                      FROM sys.foreign_keys fk
                      JOIN sys.objects o ON fk.parent_object_id = o.object_id
                      WHERE o.name = @table AND SCHEMA_NAME(o.schema_id) = @schema
                      ORDER BY fk.name;", connection);
                    cmd.Parameters.AddWithValue("@table", tableName);
                    cmd.Parameters.AddWithValue("@schema", schema);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                    {
                        var fkName = reader.GetString(0);
                        var refTable = reader.GetString(1);
                        nodes.Add(new SchemaNode
                        {
                            Name = $"{fkName} → {refTable}",
                            RawName = fkName,
                            Type = SchemaNodeType.ForeignKey
                        });
                    }
                    break;
                }
        }

        return nodes;
    }

    public async Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName)
    {
        var indexes = new List<IndexDefinition>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);

        await using var cmd = new SqlCommand(
            @"SELECT i.name AS index_name, i.is_unique, c.name AS column_name, ic.is_included_column
              FROM sys.indexes i
              INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
              INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
              WHERE i.object_id = OBJECT_ID(@tn) AND i.is_primary_key = 0
              ORDER BY i.name, ic.key_ordinal, ic.is_included_column;", connection);
        cmd.Parameters.AddWithValue("@tn", tableName);

        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        IndexDefinition? current = null;
        while (await reader.ReadAsync(MetadataReadContext.Token))
        {
            var idxName = reader.GetString(0);
            if (current == null || current.Name != idxName)
            {
                current = new IndexDefinition
                {
                    Name = idxName,
                    TableName = tableName,
                    IsUnique = reader.GetBoolean(1)
                };
                indexes.Add(current);
            }
            var isIncluded = reader.GetBoolean(3);
            if (isIncluded)
                current.IncludedColumns.Add(reader.GetString(2));
            else
                current.Columns.Add(reader.GetString(2));
        }
        return indexes;
    }

    public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName)
    {
        ParseParentName(tableName, out var schema, out var rawTable);
        return GetColumnsCoreAsync(connectionString, schema, rawTable);
    }

    public async Task<string?> GetViewDefinitionAsync(string connectionString, string viewName)
    {
        ParseParentName(viewName, out var schema, out var rawName);
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(MetadataReadContext.Token);
            await using var cmd = new SqlCommand(
                @"SELECT m.definition
                  FROM sys.sql_modules m
                  JOIN sys.objects o ON m.object_id = o.object_id
                  WHERE o.name = @name AND o.type = 'V'
                    AND SCHEMA_NAME(o.schema_id) = @schema;", connection);
            cmd.Parameters.AddWithValue("@name", rawName);
            cmd.Parameters.AddWithValue("@schema", schema);
            var result = await cmd.ExecuteScalarAsync(MetadataReadContext.Token);
            return result as string;
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<string>> GetSchemasAsync(string connectionString)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(MetadataReadContext.Token);
            return await GetSchemaListAsync(connection);
        }
        catch
        {
            return new List<string>();
        }
    }

    public Task<string?> GetTriggerDefinitionAsync(string connectionString, string schema, string triggerName) =>
        GetTriggerDefinitionInternalAsync(connectionString, schema, triggerName);

    /// <summary>A trigger belongs to its table's schema; without one, the user's default schema is searched.</summary>
    private async Task<string?> GetTriggerDefinitionInternalAsync(string connectionString, string schema, string triggerName)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(MetadataReadContext.Token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = string.IsNullOrEmpty(schema)
                ? "SELECT OBJECT_DEFINITION(OBJECT_ID(QUOTENAME(@name)))"
                : "SELECT OBJECT_DEFINITION(OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@name)))";
            cmd.Parameters.AddWithValue("@schema", schema ?? string.Empty);
            cmd.Parameters.AddWithValue("@name", triggerName);
            var result = await cmd.ExecuteScalarAsync(MetadataReadContext.Token);
            return result as string;
        }
        catch { return null; }
    }

    public List<string> GetSupportedDataTypes() =>
        new() { "int", "bigint", "nvarchar", "varchar", "decimal", "bit", "datetime2", "uniqueidentifier" };

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a possibly-qualified name: "schema.table" → (schema, table);
    /// bare "tablename" → ("dbo", "tablename").
    /// </summary>
    private static void ParseParentName(string parentName, out string schema, out string tableName)
    {
        var dot = parentName.IndexOf('.');
        if (dot > 0)
        {
            schema = parentName[..dot];
            tableName = parentName[(dot + 1)..];
        }
        else
        {
            schema = "dbo";
            tableName = parentName;
        }
    }

    private static async Task<List<string>> GetSchemaListAsync(SqlConnection connection)
    {
        var schemas = new List<string>();
        await using var cmd = new SqlCommand(
            @"SELECT s.name FROM sys.schemas s
              JOIN sys.database_principals dp ON s.principal_id = dp.principal_id
              WHERE dp.type IN ('S','U','G') OR s.name = 'dbo'
              ORDER BY s.name;", connection);
        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await reader.ReadAsync(MetadataReadContext.Token))
            schemas.Add(reader.GetString(0));
        return schemas;
    }

    private static SchemaNode MakeFolderNode(string name, SchemaNodeType type)
    {
        var node = new SchemaNode { Name = name, Type = type, IsLazyLoadable = true };
        node.Children.Add(new SchemaNode { Name = "Loading…", Type = SchemaNodeType.Column });
        return node;
    }

    private static async Task<List<string>> GetObjectNamesAsync(SqlConnection connection, string type, string schema = "dbo")
    {
        var names = new List<string>();
        await using var cmd = new SqlCommand(
            @"SELECT o.name
              FROM sys.objects o
              WHERE o.type = @type AND SCHEMA_NAME(o.schema_id) = @schema
              ORDER BY o.name;", connection);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@schema", schema);
        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await reader.ReadAsync(MetadataReadContext.Token))
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task PopulateColumnsAsync(SqlConnection connection, string tableName, string schema, SchemaNode parent)
    {
        var qualifiedName = $"{ProviderCapabilityDeclarations.SqlServer.QuoteIdentifier(schema)}.{ProviderCapabilityDeclarations.SqlServer.QuoteIdentifier(tableName)}";
        await using var cmd = new SqlCommand(
            @"SELECT c.name, tp.name AS data_type,
                     CAST(CASE WHEN ic.column_id IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS is_pk,
                     c.is_identity
              FROM sys.columns c
              JOIN sys.types tp ON c.system_type_id = tp.system_type_id
                               AND tp.user_type_id  = tp.system_type_id
              LEFT JOIN sys.indexes i
                     ON i.object_id      = c.object_id
                    AND i.is_primary_key = 1
              LEFT JOIN sys.index_columns ic
                     ON ic.object_id  = c.object_id
                    AND ic.index_id   = i.index_id
                    AND ic.column_id  = c.column_id
              WHERE c.object_id = OBJECT_ID(@tn)
              ORDER BY c.column_id;", connection);
        cmd.Parameters.AddWithValue("@tn", qualifiedName);
        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await reader.ReadAsync(MetadataReadContext.Token))
        {
            var colName = reader.GetString(0);
            var colType = reader.GetString(1);
            var isPk = reader.GetBoolean(2);
            var isIdentity = reader.GetBoolean(3);
            var displayName = isIdentity ? $"{colName} ({colType}, IDENTITY)" : $"{colName} ({colType})";
            parent.Children.Add(new SchemaNode
            {
                Name = displayName,
                RawName = colName,
                DataType = colType,
                Type = SchemaNodeType.Column,
                IsPrimaryKey = isPk
            });
        }
    }

    public async Task<string?> GetTableCommentAsync(string connectionString, string tableName, CancellationToken ct = default)
    {
        try
        {
            ParseSchemaTable(tableName, out var schema, out var table);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT CAST(ep.value AS NVARCHAR(MAX))
                FROM sys.extended_properties ep
                JOIN sys.tables t ON t.object_id = ep.major_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE ep.name = 'MS_Description' AND ep.minor_id = 0 AND ep.class = 1
                  AND s.name = @schema AND t.name = @table";
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", table);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is DBNull or null ? null : (string?)result;
        }
        catch { return null; }
    }

    public async Task<Dictionary<string, string>> GetColumnCommentsAsync(string connectionString, string tableName, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            ParseSchemaTable(tableName, out var schema, out var table);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT c.name, CAST(ep.value AS NVARCHAR(MAX))
                FROM sys.extended_properties ep
                JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
                JOIN sys.tables t ON t.object_id = ep.major_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE ep.name = 'MS_Description' AND ep.class = 1
                  AND s.name = @schema AND t.name = @table";
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", table);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(1))
                    result[reader.GetString(0)] = reader.GetString(1);
            }
        }
        catch { /* non-fatal */ }
        return result;
    }

    public async Task SetTableCommentAsync(string connectionString, string tableName, string? comment, CancellationToken ct = default)
    {
        try
        {
            ParseSchemaTable(tableName, out var schema, out var table);
            var existing = await GetTableCommentAsync(connectionString, tableName, ct);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            if (existing != null)
            {
                cmd.CommandText = "EXEC sp_updateextendedproperty 'MS_Description', @v, 'SCHEMA', @s, 'TABLE', @t";
            }
            else
            {
                cmd.CommandText = "EXEC sp_addextendedproperty 'MS_Description', @v, 'SCHEMA', @s, 'TABLE', @t";
            }
            cmd.Parameters.AddWithValue("@v", (object?)comment ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@s", schema);
            cmd.Parameters.AddWithValue("@t", table);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { /* non-fatal */ }
    }

    public async Task SetColumnCommentAsync(string connectionString, string tableName, string columnName, string? comment, CancellationToken ct = default)
    {
        try
        {
            ParseSchemaTable(tableName, out var schema, out var table);
            var existing = await GetColumnCommentsAsync(connectionString, tableName, ct);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            if (existing.ContainsKey(columnName))
            {
                cmd.CommandText = "EXEC sp_updateextendedproperty 'MS_Description', @v, 'SCHEMA', @s, 'TABLE', @t, 'COLUMN', @c";
            }
            else
            {
                cmd.CommandText = "EXEC sp_addextendedproperty 'MS_Description', @v, 'SCHEMA', @s, 'TABLE', @t, 'COLUMN', @c";
            }
            cmd.Parameters.AddWithValue("@v", (object?)comment ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@s", schema);
            cmd.Parameters.AddWithValue("@t", table);
            cmd.Parameters.AddWithValue("@c", columnName);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { /* non-fatal */ }
    }

    private static void ParseSchemaTable(string tableName, out string schema, out string table)
    {
        var parts = tableName.Split('.', 2);
        schema = parts.Length == 2 ? parts[0] : "dbo";
        table = parts.Length == 2 ? parts[1] : tableName;
    }
}
