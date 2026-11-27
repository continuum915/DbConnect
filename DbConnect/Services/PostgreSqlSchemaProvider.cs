using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class PostgreSqlSchemaProvider : IDatabaseSchemaProvider, IStructuredSchemaProvider
{
    private readonly PostgreSqlTableMetadata _tableMetadata = new();

    public Task<string?> GetTableCommentAsync(string connectionString, string tableName, CancellationToken ct = default) =>
        _tableMetadata.GetTableCommentAsync(connectionString, tableName, ct);

    public Task<Dictionary<string, string>> GetColumnCommentsAsync(string connectionString, string tableName, CancellationToken ct = default) =>
        _tableMetadata.GetColumnCommentsAsync(connectionString, tableName, ct);

    public Task<Dictionary<string, EnumColumnInfo>> GetEnumColumnsAsync(string connectionString, string tableName, CancellationToken ct = default) =>
        _tableMetadata.GetEnumColumnsAsync(connectionString, tableName, ct);

    public Task SetTableCommentAsync(string connectionString, string tableName, string? comment, CancellationToken ct = default) =>
        _tableMetadata.SetTableCommentAsync(connectionString, tableName, comment, ct);

    public Task SetColumnCommentAsync(string connectionString, string tableName, string columnName, string? comment, CancellationToken ct = default) =>
        _tableMetadata.SetColumnCommentAsync(connectionString, tableName, columnName, comment, ct);

    public async Task<List<SchemaNode>> GetSchemaAsync(string connectionString)
    {
        var root = new SchemaNode { Name = "PostgreSQL", RawName = "postgresql", Type = SchemaNodeType.Database };

        await using var connection = new NpgsqlConnection(connectionString);
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
            if (ProviderCapabilityDeclarations.PostgreSql.SupportsFunctions)
                schemaNode.Children.Add(MakeFolderNode("Functions", SchemaNodeType.FunctionFolder));
            if (ProviderCapabilityDeclarations.PostgreSql.SupportsProcedures)
                schemaNode.Children.Add(MakeFolderNode("Procedures", SchemaNodeType.ProcedureFolder));
            schemaNode.Children.Add(MakeFolderNode("Materialized Views", SchemaNodeType.MaterializedViewFolder));
            schemaNode.Children.Add(MakeFolderNode("Sequences", SchemaNodeType.SequenceFolder));

            // Extensions and Roles are database-level objects, only add once for the "public" schema
            // (or any schema — they are not schema-scoped but logically live at db root)
            if (schemaName == "public")
            {
                schemaNode.Children.Add(MakeFolderNode("Extensions", SchemaNodeType.ExtensionFolder));
                schemaNode.Children.Add(MakeFolderNode("Roles", SchemaNodeType.RoleFolder));
            }

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
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);

        switch (type)
        {
            case SchemaNodeType.Table:
                {
                    var tableNames = await GetTableNamesAsync(connection, "BASE TABLE", schema);
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
                        if (ProviderCapabilityDeclarations.PostgreSql.SupportsIndexes)
                            tableNode.Children.Add(MakeFolderNode("Indexes", SchemaNodeType.IndexFolder));
                        if (ProviderCapabilityDeclarations.PostgreSql.SupportsTriggers)
                            tableNode.Children.Add(MakeFolderNode("Triggers", SchemaNodeType.TriggerFolder));
                        if (ProviderCapabilityDeclarations.PostgreSql.SupportsForeignKeys)
                            tableNode.Children.Add(MakeFolderNode("Foreign Keys", SchemaNodeType.ForeignKeyFolder));
                        nodes.Add(tableNode);
                    }
                    break;
                }

            case SchemaNodeType.View:
                {
                    var viewNames = await GetTableNamesAsync(connection, "VIEW", schema);
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
                    await using var cmd = new NpgsqlCommand(
                        "SELECT indexname FROM pg_indexes WHERE tablename=@table AND schemaname=@schema ORDER BY indexname;",
                        connection);
                    cmd.Parameters.AddWithValue("table", tableName);
                    cmd.Parameters.AddWithValue("schema", schema);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode { Name = reader.GetString(0), RawName = reader.GetString(0), Type = SchemaNodeType.Index });
                    break;
                }

            case SchemaNodeType.Trigger:
                {
                    await using var cmd = new NpgsqlCommand(
                        "SELECT trigger_name FROM information_schema.triggers " +
                        "WHERE event_object_table=@table AND trigger_schema=@schema ORDER BY trigger_name;",
                        connection);
                    cmd.Parameters.AddWithValue("table", tableName);
                    cmd.Parameters.AddWithValue("schema", schema);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode { Name = reader.GetString(0), RawName = reader.GetString(0), Type = SchemaNodeType.Trigger });
                    break;
                }

            case SchemaNodeType.Function:
                {
                    await using var cmd = new NpgsqlCommand(
                        "SELECT p.proname, pg_get_function_identity_arguments(p.oid) FROM pg_proc p " +
                        "JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname=@schema AND p.prokind='f' ORDER BY p.proname, p.oid;",
                        connection);
                    cmd.Parameters.AddWithValue("schema", schema);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode
                        {
                            Name = reader.GetString(0),
                            RawName = reader.GetString(0),
                            Type = SchemaNodeType.Function,
                            RoutineSignature = reader.GetString(1),
                            SchemaPath = schema
                        });
                    break;
                }

            case SchemaNodeType.Procedure:
                {
                    await using var cmd = new NpgsqlCommand(
                        "SELECT p.proname, pg_get_function_identity_arguments(p.oid) FROM pg_proc p " +
                        "JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname=@schema AND p.prokind='p' ORDER BY p.proname, p.oid;",
                        connection);
                    cmd.Parameters.AddWithValue("schema", schema);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode
                        {
                            Name = reader.GetString(0),
                            RawName = reader.GetString(0),
                            Type = SchemaNodeType.Procedure,
                            RoutineSignature = reader.GetString(1),
                            SchemaPath = schema
                        });
                    break;
                }

            case SchemaNodeType.MaterializedView:
                {
                    await using var cmd = new NpgsqlCommand(
                        "SELECT matviewname FROM pg_matviews WHERE schemaname=@schema ORDER BY matviewname;",
                        connection);
                    cmd.Parameters.AddWithValue("schema", schema);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode
                        {
                            Name = reader.GetString(0),
                            RawName = reader.GetString(0),
                            Type = SchemaNodeType.MaterializedView,
                            SchemaPath = schema
                        });
                    break;
                }

            case SchemaNodeType.Sequence:
                {
                    await using var cmd = new NpgsqlCommand(
                        "SELECT sequence_name FROM information_schema.sequences WHERE sequence_schema=@schema ORDER BY sequence_name;",
                        connection);
                    cmd.Parameters.AddWithValue("schema", schema);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode
                        {
                            Name = reader.GetString(0),
                            RawName = reader.GetString(0),
                            Type = SchemaNodeType.Sequence,
                            SchemaPath = schema
                        });
                    break;
                }

            case SchemaNodeType.Extension:
                {
                    // Extensions are database-level, not schema-scoped
                    await using var cmd = new NpgsqlCommand(
                        "SELECT extname FROM pg_extension ORDER BY extname;",
                        connection);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode
                        {
                            Name = reader.GetString(0),
                            RawName = reader.GetString(0),
                            Type = SchemaNodeType.Extension
                        });
                    break;
                }

            case SchemaNodeType.Role:
                {
                    // Roles are cluster-level, not schema-scoped
                    await using var cmd = new NpgsqlCommand(
                        "SELECT rolname FROM pg_roles WHERE rolname NOT LIKE 'pg_%' ORDER BY rolname;",
                        connection);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode
                        {
                            Name = reader.GetString(0),
                            RawName = reader.GetString(0),
                            Type = SchemaNodeType.Role
                        });
                    break;
                }

            case SchemaNodeType.ForeignKey:
                {
                    await using var cmd = new NpgsqlCommand(
                        @"SELECT tc.constraint_name,
                             ccu.table_name AS foreign_table
                      FROM information_schema.table_constraints tc
                      JOIN information_schema.referential_constraints rc
                           ON rc.constraint_name = tc.constraint_name AND rc.constraint_schema = tc.constraint_schema
                      JOIN information_schema.constraint_column_usage ccu
                           ON ccu.constraint_name = rc.unique_constraint_name AND ccu.constraint_schema = rc.unique_constraint_schema
                      WHERE tc.constraint_type = 'FOREIGN KEY'
                        AND tc.table_name = @table
                        AND tc.table_schema = @schema
                      ORDER BY tc.constraint_name;",
                        connection);
                    cmd.Parameters.AddWithValue("table", tableName);
                    cmd.Parameters.AddWithValue("schema", schema);
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SchemaNode MakeFolderNode(string name, SchemaNodeType type)
    {
        var node = new SchemaNode { Name = name, Type = type, IsLazyLoadable = true };
        node.Children.Add(new SchemaNode { Name = "Loading…", Type = SchemaNodeType.Column });
        return node;
    }

    public async Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName)
    {
        var indexes = new List<IndexDefinition>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);

        await using var cmd = new NpgsqlCommand(
            @"SELECT i.relname as index_name, ix.indisunique as is_unique, a.attname as column_name
              FROM pg_class t, pg_class i, pg_index ix, pg_attribute a
              WHERE t.oid = ix.indrelid AND i.oid = ix.indexrelid AND a.attrelid = t.oid
                AND a.attnum = ANY(ix.indkey) AND t.relkind = 'r' AND t.relname = @tn
              ORDER BY i.relname;", connection);
        cmd.Parameters.AddWithValue("tn", tableName);

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
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(MetadataReadContext.Token);
            await using var cmd = new NpgsqlCommand(
                "SELECT view_definition FROM information_schema.views " +
                "WHERE table_schema = @schema AND table_name = @name;", connection);
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("name", rawName);
            var body = await cmd.ExecuteScalarAsync(MetadataReadContext.Token) as string;
            if (body == null) return null;
            return $"CREATE OR REPLACE VIEW \"{schema}\".\"{rawName}\" AS\n{body.TrimEnd(';')};";
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
            await using var connection = new NpgsqlConnection(connectionString);
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

    private async Task<string?> GetTriggerDefinitionInternalAsync(string connectionString, string schema, string triggerName)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(MetadataReadContext.Token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
            SELECT pg_get_functiondef(t.tgfoid)
            FROM pg_trigger t
            JOIN pg_class c ON t.tgrelid = c.oid
            JOIN pg_namespace n ON c.relnamespace = n.oid
            WHERE t.tgname = @name
              AND n.nspname = COALESCE(@schema, 'public')
            LIMIT 1";
            cmd.Parameters.AddWithValue("name", triggerName);
            cmd.Parameters.AddWithValue("schema", (object?)schema ?? DBNull.Value);
            var result = await cmd.ExecuteScalarAsync(MetadataReadContext.Token);
            return result as string;
        }
        catch { return null; }
    }

    public List<string> GetSupportedDataTypes() =>
        new() { "integer", "bigint", "text", "varchar", "numeric", "boolean", "date", "timestamp", "uuid", "jsonb" };

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a possibly-qualified name: "schema.table" → (schema, table);
    /// bare "tablename" → ("public", "tablename").
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
            schema = "public";
            tableName = parentName;
        }
    }

    private static async Task<List<string>> GetSchemaListAsync(NpgsqlConnection connection)
    {
        var schemas = new List<string>();
        await using var cmd = new NpgsqlCommand(
            "SELECT schema_name FROM information_schema.schemata " +
            "WHERE schema_name NOT IN ('pg_catalog', 'information_schema') " +
            "  AND schema_name NOT LIKE 'pg_toast%' AND schema_name NOT LIKE 'pg_temp%' " +
            "ORDER BY schema_name;", connection);
        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await reader.ReadAsync(MetadataReadContext.Token))
            schemas.Add(reader.GetString(0));
        return schemas;
    }

    private static async Task<List<string>> GetTableNamesAsync(NpgsqlConnection connection, string tableType, string schema = "public")
    {
        var names = new List<string>();
        await using var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = @schema AND table_type = @tt ORDER BY table_name;", connection);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("tt", tableType);
        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await reader.ReadAsync(MetadataReadContext.Token))
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task PopulateColumnsAsync(NpgsqlConnection connection, string tableName, string schema, SchemaNode parent)
    {
        await using var cmd = new NpgsqlCommand(
            @"SELECT c.column_name, c.data_type,
                     (kcu.column_name IS NOT NULL) AS is_pk
              FROM information_schema.columns c
              LEFT JOIN information_schema.table_constraints tc
                     ON tc.table_schema = c.table_schema
                    AND tc.table_name   = c.table_name
                    AND tc.constraint_type = 'PRIMARY KEY'
              LEFT JOIN information_schema.key_column_usage kcu
                     ON kcu.constraint_name  = tc.constraint_name
                    AND kcu.table_schema     = c.table_schema
                    AND kcu.table_name       = c.table_name
                    AND kcu.column_name      = c.column_name
              WHERE c.table_schema = @schema AND c.table_name = @tn
              ORDER BY c.ordinal_position;", connection);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("tn", tableName);
        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await reader.ReadAsync(MetadataReadContext.Token))
        {
            var colName = reader.GetString(0);
            var colType = reader.GetString(1);
            var isPk = reader.GetBoolean(2);
            parent.Children.Add(new SchemaNode
            {
                Name = $"{colName} ({colType})",
                RawName = colName,
                DataType = colType,
                Type = SchemaNodeType.Column,
                IsPrimaryKey = isPk
            });
        }
    }
}
