using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class MySqlSchemaProvider
{
    private static string WithCatalog(string connectionString, string? catalog) =>
        catalog is null ? connectionString : new MySqlConnectionStringBuilder(connectionString) { Database = catalog }.ConnectionString;

    public Task<List<ColumnDefinition>> GetColumnsAsync(string connectionString, DatabaseObjectIdentity relation) =>
        GetColumnsCoreAsync(WithCatalog(connectionString, relation.Catalog), relation.Catalog ?? relation.Schema ?? "", relation.Name);

    public Task<List<SchemaNode>> GetChildrenAsync(string connectionString, DatabaseObjectIdentity parent, SchemaNodeType kind) =>
        GetObjectsAsync(WithCatalog(connectionString, parent.Catalog), parent.Name, kind);

    public Task<Dictionary<string, EnumColumnInfo>> GetEnumColumnsAsync(string connectionString, DatabaseObjectIdentity relation) =>
        GetEnumColumnsAsync(WithCatalog(connectionString, relation.Catalog), relation.Name);

    private async Task<List<ColumnDefinition>> GetColumnsCoreAsync(string connectionString, string schema, string rawTable)
    {
        var columns = new List<ColumnDefinition>();
        await using var connection = MySqlConnections.Create(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);
        var database = string.IsNullOrEmpty(schema) ? connection.Database : schema;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT, COLUMN_KEY, EXTRA,
                   CHARACTER_MAXIMUM_LENGTH
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION;";
        cmd.Parameters.AddWithValue("@db", database);
        cmd.Parameters.AddWithValue("@table", rawTable);

        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await reader.ReadAsync(MetadataReadContext.Token))
        {
            columns.Add(new ColumnDefinition
            {
                Name = reader.GetString(0),
                DataType = reader.GetString(1),
                IsNullable = reader.GetString(2).Equals("YES", StringComparison.OrdinalIgnoreCase),
                DefaultValue = reader.IsDBNull(3) ? null : reader.GetString(3),
                IsPrimaryKey = reader.GetString(4).Equals("PRI", StringComparison.OrdinalIgnoreCase),
                IdentityKind = reader.GetString(5).Contains("auto_increment", StringComparison.OrdinalIgnoreCase) ? TableDesignIdentityKind.AutoIncrement : null,
                MaxLength = reader.IsDBNull(6) ? null : (int?)reader.GetInt64(6)
            });
        }
        return columns;
    }
}
