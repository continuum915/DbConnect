using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class PostgreSqlSchemaProvider
{
    private static string WithCatalog(string connectionString, string? catalog) =>
        catalog is null ? connectionString : new NpgsqlConnectionStringBuilder(connectionString) { Database = catalog }.ConnectionString;

    public Task<List<ColumnDefinition>> GetColumnsAsync(string connectionString, DatabaseObjectIdentity relation) =>
        GetColumnsCoreAsync(WithCatalog(connectionString, relation.Catalog), relation.Schema ?? "public", relation.Name);

    public Task<List<SchemaNode>> GetChildrenAsync(string connectionString, DatabaseObjectIdentity parent, SchemaNodeType kind) =>
        GetChildrenCoreAsync(WithCatalog(connectionString, parent.Catalog), parent.Kind == SchemaNodeType.Schema ? parent.Name : parent.Schema ?? "public", parent.Name, kind);

    public Task<Dictionary<string, EnumColumnInfo>> GetEnumColumnsAsync(string connectionString, DatabaseObjectIdentity relation) =>
        _tableMetadata.GetEnumColumnsAsync(WithCatalog(connectionString, relation.Catalog), relation.Schema ?? "public", relation.Name, default);

    private async Task<List<ColumnDefinition>> GetColumnsCoreAsync(string connectionString, string schema, string rawTable)
    {
        var columns = new List<ColumnDefinition>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);

        await using var cmd = new NpgsqlCommand(
            @"SELECT c.column_name, c.data_type, c.is_nullable, c.column_default,
                     (kcu.column_name IS NOT NULL) AS is_pk,
                     c.character_maximum_length
              FROM information_schema.columns c
              LEFT JOIN information_schema.table_constraints tc
                     ON tc.table_schema = c.table_schema AND tc.table_name = c.table_name AND tc.constraint_type = 'PRIMARY KEY'
              LEFT JOIN information_schema.key_column_usage kcu
                     ON kcu.constraint_name = tc.constraint_name AND kcu.table_schema = c.table_schema AND kcu.table_name = c.table_name AND kcu.column_name = c.column_name
              WHERE c.table_schema = @schema AND c.table_name = @tn
              ORDER BY c.ordinal_position;", connection);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("tn", rawTable);

        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await reader.ReadAsync(MetadataReadContext.Token))
        {
            columns.Add(new ColumnDefinition
            {
                Name = reader.GetString(0),
                DataType = reader.GetString(1),
                IsNullable = reader.GetString(2).Equals("YES", StringComparison.OrdinalIgnoreCase),
                DefaultValue = reader.IsDBNull(3) ? null : reader.GetString(3),
                IsPrimaryKey = reader.GetBoolean(4),
                MaxLength = reader.IsDBNull(5) ? null : reader.GetInt32(5)
            });
        }
        return columns;
    }
}
