using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class SqliteSchemaProvider
{
    public Task<List<ColumnDefinition>> GetColumnsAsync(string connectionString, DatabaseObjectIdentity relation) =>
        GetColumnsCoreAsync(connectionString, relation.Schema ?? "main", relation.Name);

    public Task<List<SchemaNode>> GetChildrenAsync(string connectionString, DatabaseObjectIdentity parent, SchemaNodeType kind) =>
        GetObjectsAsync(connectionString, parent.Name, kind);

    private async Task<List<ColumnDefinition>> GetColumnsCoreAsync(string connectionString, string schema, string rawTable)
    {
        var columns = new List<ColumnDefinition>();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);
        using var nativeCancellation = new SqliteExportCancellation(connection, MetadataReadContext.Token);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA {ProviderCapabilityDeclarations.Sqlite.QuoteIdentifier(schema)}.table_xinfo('{rawTable.Replace("'", "''")}');";
        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await reader.ReadAsync(MetadataReadContext.Token))
        {
            columns.Add(new ColumnDefinition
            {
                Name = reader.GetString(1),
                DataType = reader.GetString(2),
                IsNullable = reader.GetInt32(3) == 0,
                DefaultValue = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsPrimaryKey = reader.GetInt32(5) > 0
            });
        }
        return columns;
    }
}
