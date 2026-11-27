using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

internal static class SqliteSchemaMetadata
{
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    public static async Task<TableRebuildDefinition> LoadDefinitionAsync(string connectionString, string tableName)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.AddWithValue("@name", tableName);
        var sql = await cmd.ExecuteScalarAsync() as string
            ?? throw new InvalidOperationException($"Table {tableName} has no stored definition.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cmd.CommandText = "SELECT name FROM sqlite_master UNION SELECT name FROM sqlite_temp_master";
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cmd.CommandText = $"PRAGMA table_xinfo({Literal(tableName)});";
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                if (reader.GetInt32(6) >= 2) generated.Add(reader.GetString(1));
        return new TableRebuildDefinition(sql, names, generated)
        {
            SchemaObjects = await SqliteSchemaObjects.LoadAsync(connection, tableName)
        };
    }

    public static async Task<List<IndexDefinition>> LoadIndexesAsync(string connectionString, string tableName)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA index_list({Literal(tableName)});";
        var indexes = new List<IndexDefinition>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                indexes.Add(new IndexDefinition
                {
                    Name = reader.GetString(1),
                    TableName = tableName,
                    IsUnique = reader.GetBoolean(2)
                });
        foreach (var index in indexes)
        {
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name=@name";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@name", index.Name);
            index.CreateSql = await cmd.ExecuteScalarAsync() as string;
            index.WherePredicate = index.CreateSql == null ? null : SqliteDefinitionSql.IndexPredicate(index.CreateSql);
            var expressions = index.CreateSql == null ? new List<string>() : SqliteDefinitionSql.BodyTerms(index.CreateSql);
            cmd.CommandText = $"PRAGMA index_xinfo({Literal(index.Name)});";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.GetInt32(5) == 0) continue;
                var position = reader.GetInt32(0);
                index.Columns.Add(reader.IsDBNull(2)
                    ? position < expressions.Count ? expressions[position] : "(expression)"
                    : reader.GetString(2));
                index.ColumnDirections.Add(reader.GetInt32(3) == 0 ? IndexColumnDirection.Asc : IndexColumnDirection.Desc);
            }
        }
        return indexes;
    }
}
