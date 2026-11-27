using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed partial class SqlServerSchemaProvider
{
    private static string WithCatalog(string connectionString, string? catalog) =>
        catalog is null ? connectionString : new SqlConnectionStringBuilder(connectionString) { InitialCatalog = catalog }.ConnectionString;

    public Task<List<ColumnDefinition>> GetColumnsAsync(string connectionString, DatabaseObjectIdentity relation) =>
        GetColumnsCoreAsync(WithCatalog(connectionString, relation.Catalog), relation.Schema ?? "dbo", relation.Name);

    public Task<List<SchemaNode>> GetChildrenAsync(string connectionString, DatabaseObjectIdentity parent, SchemaNodeType kind) =>
        GetChildrenCoreAsync(WithCatalog(connectionString, parent.Catalog), parent.Kind == SchemaNodeType.Schema ? parent.Name : parent.Schema ?? "dbo", parent.Name, kind);

    private async Task<List<ColumnDefinition>> GetColumnsCoreAsync(string connectionString, string schema, string rawTable)
    {
        var qualifiedName = $"[{schema.Replace("]", "]]")}].[{rawTable.Replace("]", "]]")}]";
        var columns = new List<ColumnDefinition>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(MetadataReadContext.Token);

        await using var cmd = new SqlCommand(
            @"SELECT c.name, tp.name AS data_type, c.is_nullable,
                     OBJECT_DEFINITION(c.default_object_id) AS column_default,
                     CAST(CASE WHEN ic.column_id IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS is_pk,
                     c.max_length,
                     c.is_identity
              FROM sys.columns c
              JOIN sys.types tp ON c.system_type_id = tp.system_type_id AND tp.user_type_id = tp.system_type_id
              LEFT JOIN sys.indexes i ON i.object_id = c.object_id AND i.is_primary_key = 1
              LEFT JOIN sys.index_columns ic ON ic.object_id = c.object_id AND ic.index_id = i.index_id AND ic.column_id = c.column_id
              WHERE c.object_id = OBJECT_ID(@tn)
              ORDER BY c.column_id;", connection);
        cmd.Parameters.AddWithValue("@tn", qualifiedName);

        await using var reader = await cmd.ExecuteReaderAsync(MetadataReadContext.Token);
        while (await reader.ReadAsync(MetadataReadContext.Token))
        {
            columns.Add(new ColumnDefinition
            {
                Name = reader.GetString(0),
                DataType = reader.GetString(1),
                IsNullable = reader.GetBoolean(2),
                DefaultValue = reader.IsDBNull(3) ? null : reader.GetString(3),
                IsPrimaryKey = reader.GetBoolean(4),
                MaxLength = reader.GetInt16(5),
                IdentityKind = reader.GetBoolean(6) ? TableDesignIdentityKind.Identity : null
            });
        }
        return columns;
    }
}
