using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class SqliteSchemaProvider : IDatabaseSchemaProvider, IStructuredSchemaProvider
{
    public async Task<List<SchemaNode>> GetSchemaAsync(string connectionString)
    {
        var root = new SchemaNode { Name = "Main", RawName = "Main", Type = SchemaNodeType.Database, SchemaPath = "main" };

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);
        using var nativeCancellation = new SqliteExportCancellation(connection, MetadataReadContext.Token);

        // Tables (regular + virtual)
        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        await using var tableReader = await tableCmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await tableReader.ReadAsync(MetadataReadContext.Token))
        {
            var tableName = tableReader.GetString(0);
            var tableSql = tableReader.IsDBNull(1) ? null : tableReader.GetString(1);
            var isVirtual = tableSql != null && tableSql.TrimStart().StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase);

            string? moduleHint = null;
            if (isVirtual && tableSql != null)
            {
                // Extract module name: CREATE VIRTUAL TABLE name USING <module>(...)
                var usingIdx = tableSql.IndexOf(" USING ", StringComparison.OrdinalIgnoreCase);
                if (usingIdx >= 0)
                {
                    var afterUsing = tableSql[(usingIdx + 7)..].TrimStart();
                    var end = afterUsing.IndexOfAny(new[] { '(', ' ', ';', '\n' });
                    moduleHint = end > 0 ? afterUsing[..end] : afterUsing;
                }
            }

            var tableNode = new SchemaNode
            {
                Name = tableName,
                RawName = tableName,
                Type = isVirtual ? SchemaNodeType.VirtualTable : SchemaNodeType.Table,
                DataType = moduleHint
            };

            if (!isVirtual)
                await PopulateColumnsAsync(connection, tableName, tableNode);

            if (!isVirtual && ProviderCapabilityDeclarations.Sqlite.SupportsIndexes)
                tableNode.Children.Add(MakeFolderNode("Indexes", SchemaNodeType.IndexFolder));
            if (!isVirtual && ProviderCapabilityDeclarations.Sqlite.SupportsTriggers)
                tableNode.Children.Add(MakeFolderNode("Triggers", SchemaNodeType.TriggerFolder));
            if (!isVirtual && ProviderCapabilityDeclarations.Sqlite.SupportsForeignKeys)
                tableNode.Children.Add(MakeFolderNode("Foreign Keys", SchemaNodeType.ForeignKeyFolder));

            root.Children.Add(tableNode);
        }

        // Views
        await using var viewCmd = connection.CreateCommand();
        viewCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='view' ORDER BY name;";
        await using var viewReader = await viewCmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await viewReader.ReadAsync(MetadataReadContext.Token))
        {
            var viewName = viewReader.GetString(0);
            var viewNode = new SchemaNode { Name = viewName, RawName = viewName, Type = SchemaNodeType.View };
            await PopulateColumnsAsync(connection, viewName, viewNode);
            root.Children.Add(viewNode);
        }

        // Database info folder (PRAGMAs)
        root.Children.Add(MakeFolderNode("Database Info", SchemaNodeType.DatabaseInfoFolder));

        return new List<SchemaNode> { root };
    }

    public async Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type)
    {
        var nodes = new List<SchemaNode>();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);
        using var nativeCancellation = new SqliteExportCancellation(connection, MetadataReadContext.Token);

        switch (type)
        {
            case SchemaNodeType.Index:
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name=@table ORDER BY name;";
                    cmd.Parameters.AddWithValue("@table", parentName);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode { Name = reader.GetString(0), RawName = reader.GetString(0), Type = SchemaNodeType.Index });
                    break;
                }
            case SchemaNodeType.Trigger:
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' AND tbl_name=@table ORDER BY name;";
                    cmd.Parameters.AddWithValue("@table", parentName);
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                        nodes.Add(new SchemaNode { Name = reader.GetString(0), RawName = reader.GetString(0), Type = SchemaNodeType.Trigger });
                    break;
                }
            case SchemaNodeType.ForeignKey:
                {
                    // PRAGMA foreign_key_list(tablename) returns: id, seq, table, from, to, ...
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"PRAGMA foreign_key_list(\"{parentName}\");";
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                    {
                        var fromCol = reader.GetString(3);
                        var toTable = reader.GetString(2);
                        var toCol = reader.GetString(4);
                        nodes.Add(new SchemaNode
                        {
                            Name = $"{fromCol} → {toTable}.{toCol}",
                            RawName = fromCol,
                            Type = SchemaNodeType.ForeignKey
                        });
                    }
                    break;
                }
            case SchemaNodeType.DatabaseInfoItem:
                {
                    // Surface useful PRAGMA values as info items
                    var pragmas = new[] { "page_count", "freelist_count", "page_size", "journal_mode", "synchronous", "foreign_keys", "user_version" };
                    foreach (var pragma in pragmas)
                    {
                        try
                        {
                            await using var cmd = connection.CreateCommand();
                            cmd.CommandText = $"PRAGMA {pragma};";
                            var value = await cmd.ExecuteScalarAsync(MetadataReadContext.Token);
                            nodes.Add(new SchemaNode
                            {
                                Name = $"{pragma} = {value}",
                                RawName = pragma,
                                Type = SchemaNodeType.DatabaseInfoItem
                            });
                        }
                        catch { /* skip unsupported pragma */ }
                    }
                    break;
                }
            case SchemaNodeType.Function:
                // SQLite does not support user-defined functions in the schema
                break;
            default:
                {
                    var sqlType = type == SchemaNodeType.View ? "view" : "table";
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='{sqlType}' ORDER BY name;";
                    await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
                    while (await reader.ReadAsync(MetadataReadContext.Token))
                    {
                        var name = reader.GetString(0);
                        var node = new SchemaNode { Name = name, RawName = name, Type = type };
                        await PopulateColumnsAsync(connection, name, node);
                        nodes.Add(node);
                    }
                    break;
                }
        }
        return nodes;
    }

    public Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName) =>
        SqliteSchemaMetadata.LoadIndexesAsync(connectionString, tableName);

    public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName)
    {
        return GetColumnsCoreAsync(connectionString, "main", tableName);
    }

    public async Task<string?> GetViewDefinitionAsync(string connectionString, string viewName)
    {
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(MetadataReadContext.Token);
            using var nativeCancellation = new SqliteExportCancellation(connection, MetadataReadContext.Token);
            await using var cmd = connection.CreateCommand();
            // Check views first; also check tables to support VirtualTable scripting
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name=@name AND type IN ('view','table','trigger');";
            cmd.Parameters.AddWithValue("@name", viewName);
            var result = await cmd.ExecuteScalarAsync(MetadataReadContext.Token);
            return result as string;
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
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(MetadataReadContext.Token);
            using var nativeCancellation = new SqliteExportCancellation(connection, MetadataReadContext.Token);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name=@name AND type='trigger';";
            cmd.Parameters.AddWithValue("@name", triggerName);
            var result = await cmd.ExecuteScalarAsync(MetadataReadContext.Token);
            return result as string;
        }
        catch
        {
            return null;
        }
    }

    public List<string> GetSupportedDataTypes() =>
        new() { "INTEGER", "REAL", "TEXT", "BLOB", "NUMERIC" };

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SchemaNode MakeFolderNode(string name, SchemaNodeType type)
    {
        var node = new SchemaNode { Name = name, Type = type, IsLazyLoadable = true };
        node.Children.Add(new SchemaNode { Name = "Loading…", Type = SchemaNodeType.Column });
        return node;
    }

    private static async Task PopulateColumnsAsync(SqliteConnection connection, string objectName, SchemaNode parent)
    {
        await using var colCmd = connection.CreateCommand();
        colCmd.CommandText = $"PRAGMA table_info('{objectName.Replace("'", "''")}');";
        await using var colReader = await colCmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await colReader.ReadAsync(MetadataReadContext.Token))
        {
            var colName = colReader.GetString(1);
            var colType = colReader.GetString(2);
            var pkSeq = colReader.GetInt32(5); // > 0 = part of PK
            parent.Children.Add(new SchemaNode
            {
                Name = $"{colName} ({colType})",
                RawName = colName,
                DataType = colType,
                Type = SchemaNodeType.Column,
                IsPrimaryKey = pkSeq > 0
            });
        }
    }
}
