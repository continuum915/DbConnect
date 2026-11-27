using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed class PostgreSqlTableMetadata
{
    public async Task<string?> GetTableCommentAsync(string connectionString, string tableName, CancellationToken ct = default)
    {
        try
        {
            ParseSchemaTable(tableName, out var schema, out var table);
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT obj_description(c.oid, 'pg_class')
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relname = @table";
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("table", table);
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
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT a.attname, col_description(a.attrelid, a.attnum)
                FROM pg_attribute a
                JOIN pg_class c ON c.oid = a.attrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relname = @table AND a.attnum > 0 AND NOT a.attisdropped";
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("table", table);
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

    public Task<Dictionary<string, EnumColumnInfo>> GetEnumColumnsAsync(string connectionString, string tableName, CancellationToken ct = default)
    {
        ParseSchemaTable(tableName, out var schema, out var table);
        return GetEnumColumnsAsync(connectionString, schema, table, ct);
    }

    public async Task<Dictionary<string, EnumColumnInfo>> GetEnumColumnsAsync(string connectionString, string schema, string table, CancellationToken ct)
    {
        var result = new Dictionary<string, EnumColumnInfo>(StringComparer.Ordinal);
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            // PostgreSQL has no SET type — every enum column here is single-valued.
            cmd.CommandText = @"
                SELECT a.attname, e.enumlabel
                FROM pg_attribute a
                JOIN pg_class c ON c.oid = a.attrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_type t ON t.oid = a.atttypid
                JOIN pg_enum e ON e.enumtypid = t.oid
                WHERE n.nspname = @schema AND c.relname = @table AND a.attnum > 0 AND NOT a.attisdropped
                ORDER BY a.attname, e.enumsortorder";
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("table", table);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            while (await reader.ReadAsync(ct))
            {
                var columnName = reader.GetString(0);
                if (!values.TryGetValue(columnName, out var list))
                    values[columnName] = list = new List<string>();
                list.Add(reader.GetString(1));
            }

            foreach (var (columnName, list) in values)
                result[columnName] = new EnumColumnInfo(list, AllowsMultipleValues: false);
        }
        catch { /* non-fatal */ }
        return result;
    }

    public async Task SetTableCommentAsync(string connectionString, string tableName, string? comment, CancellationToken ct = default)
    {
        try
        {
            var quoted = QuoteQualifiedIdentifier(tableName);
            var commentSql = comment is null
                ? $"COMMENT ON TABLE {quoted} IS NULL"
                : $"COMMENT ON TABLE {quoted} IS '{comment.Replace("'", "''")}'";
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = commentSql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { /* non-fatal */ }
    }

    public async Task SetColumnCommentAsync(string connectionString, string tableName, string columnName, string? comment, CancellationToken ct = default)
    {
        try
        {
            var quotedTable = QuoteQualifiedIdentifier(tableName);
            var quotedCol = ProviderCapabilityDeclarations.PostgreSql.QuoteIdentifier(columnName);
            var commentSql = comment is null
                ? $"COMMENT ON COLUMN {quotedTable}.{quotedCol} IS NULL"
                : $"COMMENT ON COLUMN {quotedTable}.{quotedCol} IS '{comment.Replace("'", "''")}'";
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = commentSql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { /* non-fatal */ }
    }

    private static void ParseSchemaTable(string tableName, out string schema, out string table)
    {
        var parts = tableName.Split('.', 2);
        schema = parts.Length == 2 ? parts[0] : "public";
        table = parts.Length == 2 ? parts[1] : tableName;
    }

    private string QuoteQualifiedIdentifier(string identifier) =>
        string.Join(".", identifier.Split('.').Select(ProviderCapabilityDeclarations.PostgreSql.QuoteIdentifier));
}
