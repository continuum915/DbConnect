using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class DuckDbSchemaProvider : IDatabaseSchemaProvider, IStructuredSchemaProvider
{
    public async Task<List<SchemaNode>> GetSchemaAsync(string connectionString)
    {
        var root = new SchemaNode { Name = "DuckDB", RawName = "duckdb", Type = SchemaNodeType.Database };

        await using var connection = DuckDbConnections.Create(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);

        await using (var catalog = connection.CreateCommand())
        {
            catalog.CommandText = "SELECT current_database()";
            root.CatalogName = (string?)await catalog.ExecuteScalarAsync(MetadataReadContext.Token);
        }

        // Get all user schemas (exclude system schemas)
        var schemas = new List<string>();
        await using (var schemaCmd = connection.CreateCommand())
        {
            schemaCmd.CommandText = @"
                SELECT DISTINCT table_schema
                FROM information_schema.tables
                WHERE table_schema NOT IN ('information_schema', 'pg_catalog')
                ORDER BY table_schema;";
            using var schemaReaderCancellation = MetadataReadContext.Token.Register(schemaCmd.Cancel);
            await using var schemaReader = await schemaCmd.ExecuteReaderAsync(MetadataReadContext.Token);
            while (await schemaReader.ReadAsync(MetadataReadContext.Token))
                schemas.Add(schemaReader.GetString(0));
        }

        // If no schemas found, try "main"
        if (schemas.Count == 0) schemas.Add("main");

        foreach (var schemaName in schemas)
        {
            var schemaNode = new SchemaNode
            {
                Name = schemaName,
                RawName = schemaName,
                Type = SchemaNodeType.Schema,
                SchemaPath = schemaName
            };

            schemaNode.Children.Add(MakeFolderNode("Tables", SchemaNodeType.TableFolder, schemaName));
            schemaNode.Children.Add(MakeFolderNode("Views", SchemaNodeType.ViewFolder, schemaName));
            if (ProviderCapabilityDeclarations.DuckDb.SupportsIndexes)
                schemaNode.Children.Add(MakeFolderNode("Indexes", SchemaNodeType.IndexFolder, schemaName));
            if (ProviderCapabilityDeclarations.DuckDb.SupportsFunctions)
                schemaNode.Children.Add(MakeFolderNode("Functions", SchemaNodeType.FunctionFolder, schemaName));

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
        await using var connection = DuckDbConnections.Create(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);

        switch (type)
        {
            case SchemaNodeType.Table:
            case SchemaNodeType.TableFolder:
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                    SELECT table_name
                    FROM information_schema.tables
                    WHERE table_schema = ? AND table_type = 'BASE TABLE'
                    ORDER BY table_name;";
                    cmd.Parameters.Add(new DuckDBParameter { Value = schema });
                    using var readerCancellation = MetadataReadContext.Token.Register(cmd.Cancel);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                    {
                        var tName = reader.GetString(0);
                        var tableNode = new SchemaNode
                        {
                            Name = tName,
                            RawName = tName,
                            Type = SchemaNodeType.Table,
                            SchemaPath = schema
                        };
                        await PopulateColumnsAsync(connection, tName, schema, tableNode);
                        if (ProviderCapabilityDeclarations.DuckDb.SupportsIndexes)
                            tableNode.Children.Add(MakeFolderNode("Indexes", SchemaNodeType.IndexFolder, $"{schema}.{tName}"));
                        nodes.Add(tableNode);
                    }
                    break;
                }

            case SchemaNodeType.View:
            case SchemaNodeType.ViewFolder:
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                    SELECT table_name
                    FROM information_schema.tables
                    WHERE table_schema = ? AND table_type = 'VIEW'
                    ORDER BY table_name;";
                    cmd.Parameters.Add(new DuckDBParameter { Value = schema });
                    using var readerCancellation = MetadataReadContext.Token.Register(cmd.Cancel);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                    {
                        var vName = reader.GetString(0);
                        var viewNode = new SchemaNode
                        {
                            Name = vName,
                            RawName = vName,
                            Type = SchemaNodeType.View,
                            SchemaPath = schema
                        };
                        await PopulateColumnsAsync(connection, vName, schema, viewNode);
                        nodes.Add(viewNode);
                    }
                    break;
                }

            case SchemaNodeType.Function:
            case SchemaNodeType.FunctionFolder:
                {
                    // DbConnect exposes DuckDB macros as functions. Built-in scalar/aggregate
                    // functions are intentionally omitted from the schema tree.
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                    SELECT DISTINCT function_name
                    FROM duckdb_functions()
                    WHERE schema_name = ? AND function_type IN ('macro', 'table_macro') AND NOT internal
                    ORDER BY function_name;";
                    cmd.Parameters.Add(new DuckDBParameter { Value = schema });
                    using var readerCancellation = MetadataReadContext.Token.Register(cmd.Cancel);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                    {
                        var functionName = reader.GetString(0);
                        nodes.Add(new SchemaNode
                        {
                            Name = functionName,
                            RawName = functionName,
                            Type = SchemaNodeType.Function,
                            SchemaPath = schema
                        });
                    }
                    break;
                }

            case SchemaNodeType.Index:
            case SchemaNodeType.IndexFolder:
                {
                    if (!string.IsNullOrEmpty(tableName))
                    {
                        // Table-level indexes
                        await using var cmd = connection.CreateCommand();
                        cmd.CommandText = @"
                        SELECT index_name, is_unique
                        FROM duckdb_indexes()
                        WHERE schema_name = ? AND table_name = ?
                        ORDER BY index_name;";
                        cmd.Parameters.Add(new DuckDBParameter { Value = schema });
                        cmd.Parameters.Add(new DuckDBParameter { Value = tableName });
                        using var readerCancellation = MetadataReadContext.Token.Register(cmd.Cancel);
                        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                        while (await reader.ReadAsync(MetadataReadContext.Token))
                            nodes.Add(new SchemaNode
                            {
                                Name = reader.GetString(0),
                                RawName = reader.GetString(0),
                                Type = SchemaNodeType.Index
                            });
                    }
                    else
                    {
                        // Schema-level: all indexes
                        await using var cmd = connection.CreateCommand();
                        cmd.CommandText = @"
                        SELECT index_name FROM duckdb_indexes()
                        WHERE schema_name = ? ORDER BY index_name;";
                        cmd.Parameters.Add(new DuckDBParameter { Value = schema });
                        using var readerCancellation = MetadataReadContext.Token.Register(cmd.Cancel);
                        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                        while (await reader.ReadAsync(MetadataReadContext.Token))
                            nodes.Add(new SchemaNode
                            {
                                Name = reader.GetString(0),
                                RawName = reader.GetString(0),
                                Type = SchemaNodeType.Index
                            });
                    }
                    break;
                }

            case SchemaNodeType.Sequence:
            case SchemaNodeType.SequenceFolder:
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                    SELECT sequence_name FROM duckdb_sequences()
                    WHERE schema_name = ? ORDER BY sequence_name;";
                    cmd.Parameters.Add(new DuckDBParameter { Value = schema });
                    using var readerCancellation = MetadataReadContext.Token.Register(cmd.Cancel);
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
        }

        return nodes;
    }

    public async Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName)
    {
        var indexes = new List<IndexDefinition>();
        await using var connection = DuckDbConnections.Create(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);

        ParseParentName(tableName, out var schema, out var rawTable);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT index_name, is_unique, sql
            FROM duckdb_indexes()
            WHERE schema_name = ? AND table_name = ?
            ORDER BY index_name;";
        cmd.Parameters.Add(new DuckDBParameter { Value = schema });
        cmd.Parameters.Add(new DuckDBParameter { Value = rawTable });

        using var readerCancellation = MetadataReadContext.Token.Register(cmd.Cancel);

        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await reader.ReadAsync(MetadataReadContext.Token))
        {
            indexes.Add(new IndexDefinition
            {
                Name = reader.GetString(0),
                TableName = rawTable,
                IsUnique = !reader.IsDBNull(1) && reader.GetBoolean(1)
            });
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
        try
        {
            ParseParentName(viewName, out var schema, out var rawName);
            await using var connection = DuckDbConnections.Create(connectionString);
            await connection.OpenAsync(MetadataReadContext.Token);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT sql FROM duckdb_views() WHERE schema_name = ? AND view_name = ?;";
            cmd.Parameters.Add(new DuckDBParameter { Value = schema });
            cmd.Parameters.Add(new DuckDBParameter { Value = rawName });
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
            await using var connection = DuckDbConnections.Create(connectionString);
            await connection.OpenAsync(MetadataReadContext.Token);
            var schemas = new List<string>();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT table_schema
                FROM information_schema.tables
                WHERE table_schema NOT IN ('information_schema', 'pg_catalog')
                ORDER BY table_schema;";
            using var readerCancellation = MetadataReadContext.Token.Register(cmd.Cancel);
            await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
            while (await reader.ReadAsync(MetadataReadContext.Token))
                schemas.Add(reader.GetString(0));
            return schemas;
        }
        catch
        {
            return new List<string>();
        }
    }

    public List<string> GetSupportedDataTypes() => new()
    {
        "INTEGER", "BIGINT", "SMALLINT", "TINYINT", "HUGEINT",
        "FLOAT", "DOUBLE", "DECIMAL(18,2)",
        "VARCHAR", "TEXT", "BOOLEAN",
        "DATE", "TIMESTAMP", "TIMESTAMP WITH TIME ZONE", "INTERVAL",
        "BLOB", "UUID", "JSON", "LIST", "STRUCT"
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SchemaNode MakeFolderNode(string name, SchemaNodeType type, string? schemaPath = null)
    {
        var node = new SchemaNode
        {
            Name = name,
            Type = type,
            IsLazyLoadable = true,
            SchemaPath = schemaPath
        };
        node.Children.Add(new SchemaNode { Name = "Loading…", Type = SchemaNodeType.Column });
        return node;
    }

    private static async Task PopulateColumnsAsync(DuckDBConnection connection, string tableName, string schema, SchemaNode parent)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = ? AND table_name = ?
            ORDER BY ordinal_position;";
        cmd.Parameters.Add(new DuckDBParameter { Value = schema });
        cmd.Parameters.Add(new DuckDBParameter { Value = tableName });

        using var readerCancellation = MetadataReadContext.Token.Register(cmd.Cancel);

        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await reader.ReadAsync(MetadataReadContext.Token))
        {
            var colName = reader.GetString(0);
            var colType = reader.GetString(1);
            parent.Children.Add(new SchemaNode
            {
                Name = $"{colName} ({colType})",
                RawName = colName,
                DataType = colType,
                Type = SchemaNodeType.Column
            });
        }
    }

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
            schema = "main";
            tableName = parentName;
        }
    }
}
