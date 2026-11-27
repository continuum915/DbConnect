using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class DuckDbSchemaProvider
{
    public Task<List<ColumnDefinition>> GetColumnsAsync(string connectionString, DatabaseObjectIdentity relation) =>
        GetColumnsCoreAsync(connectionString, relation.Schema ?? "main", relation.Name, relation.Catalog);

    public Task<List<SchemaNode>> GetChildrenAsync(string connectionString, DatabaseObjectIdentity parent, SchemaNodeType kind) =>
        GetChildrenCoreAsync(connectionString, parent.Kind == SchemaNodeType.Schema ? parent.Name : parent.Schema ?? "main",
            parent.Kind == SchemaNodeType.Schema ? string.Empty : parent.Name, kind);

    private async Task<List<ColumnDefinition>> GetColumnsCoreAsync(string connectionString, string schema, string rawTable, string? catalog = null)
    {
        var columns = new List<ColumnDefinition>();
        await using var connection = DuckDbConnections.Create(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT column_name, data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_catalog = COALESCE(?, current_database()) AND table_schema = ? AND table_name = ?
            ORDER BY ordinal_position;";
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)catalog ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = schema });
        cmd.Parameters.Add(new DuckDBParameter { Value = rawTable });

        using var readerCancellation = MetadataReadContext.Token.Register(cmd.Cancel);

        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await reader.ReadAsync(MetadataReadContext.Token))
        {
            columns.Add(new ColumnDefinition
            {
                Name = reader.GetString(0),
                DataType = reader.GetString(1),
                IsNullable = reader.GetString(2).Equals("YES", StringComparison.OrdinalIgnoreCase),
                DefaultValue = reader.IsDBNull(3) ? null : reader.GetString(3)
            });
        }
        return columns;
    }
}
