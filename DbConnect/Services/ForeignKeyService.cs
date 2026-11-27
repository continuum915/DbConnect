using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Reads ordered FK column pairs, preserving constraint identity and schema names.</summary>
public static class ForeignKeyService
{
    public static async Task<string> ResolveTableNameAsync(IDbSession session, string table)
    {
        if (table.Contains('.')) return table;
        var sql = session.Provider.ProviderName switch
        {
            "PostgreSQL" => "SELECT current_schema()",
            "SQL Server" => "SELECT SCHEMA_NAME()",
            "MySQL" => "SELECT DATABASE()",
            _ => null
        };
        if (sql == null) return table;
        var result = await session.ExecuteQueryAsync(sql);
        if (result.HasError) throw new InvalidOperationException(result.ErrorMessage);
        var schema = result.ObjectRows.FirstOrDefault()?.FirstOrDefault() as string;
        return string.IsNullOrEmpty(schema) ? table : schema + "." + table;
    }

    public static async Task<IReadOnlyList<ForeignKeyRelationship>> LoadAsync(IDbSession session)
    {
        var sql = session.Provider.ProviderName switch
        {
            "SQLite" => "SELECT m.name AS child, f.id AS name, f.[table] AS parent, f.[from] AS source, f.[to] AS target, f.seq AS ordinal FROM sqlite_master m JOIN pragma_foreign_key_list(m.name) f WHERE m.type = 'table' ORDER BY m.name, f.id, f.seq",
            "PostgreSQL" => "SELECT ns.nspname || '.' || t.relname AS child, c.conname AS name, nr.nspname || '.' || r.relname AS parent, a.attname AS source, b.attname AS target, k.ordinality AS ordinal FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace ns ON ns.oid=t.relnamespace JOIN pg_class r ON r.oid=c.confrelid JOIN pg_namespace nr ON nr.oid=r.relnamespace CROSS JOIN LATERAL unnest(c.conkey,c.confkey) WITH ORDINALITY k(src,dst,ordinality) JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.src JOIN pg_attribute b ON b.attrelid=r.oid AND b.attnum=k.dst WHERE c.contype='f' ORDER BY child,name,ordinal",
            "MySQL" => "SELECT CONCAT(TABLE_SCHEMA,'.',TABLE_NAME) AS child, CONSTRAINT_NAME AS name, CONCAT(REFERENCED_TABLE_SCHEMA,'.',REFERENCED_TABLE_NAME) AS parent, COLUMN_NAME AS source, REFERENCED_COLUMN_NAME AS target, ORDINAL_POSITION AS ordinal FROM information_schema.KEY_COLUMN_USAGE WHERE REFERENCED_TABLE_NAME IS NOT NULL AND TABLE_SCHEMA=DATABASE() ORDER BY child,name,ordinal",
            "SQL Server" => "SELECT SCHEMA_NAME(t.schema_id)+'.'+t.name AS child, fk.name AS name, SCHEMA_NAME(r.schema_id)+'.'+r.name AS parent, a.name AS source, b.name AS target, k.constraint_column_id AS ordinal FROM sys.foreign_keys fk JOIN sys.foreign_key_columns k ON k.constraint_object_id=fk.object_id JOIN sys.tables t ON t.object_id=k.parent_object_id JOIN sys.tables r ON r.object_id=k.referenced_object_id JOIN sys.columns a ON a.object_id=t.object_id AND a.column_id=k.parent_column_id JOIN sys.columns b ON b.object_id=r.object_id AND b.column_id=k.referenced_column_id ORDER BY child,name,ordinal",
            // DuckDB foreign keys reference a table in their own schema; the catalog names only the referenced table.
            "DuckDB" => "SELECT c.schema_name || '.' || c.table_name AS child, COALESCE(c.constraint_name, c.constraint_text) AS name, c.schema_name || '.' || c.referenced_table AS parent, c.constraint_column_names[k.i] AS source, c.referenced_column_names[k.i] AS target, k.i AS ordinal FROM duckdb_constraints() c, LATERAL range(1, len(c.constraint_column_names) + 1) k(i) WHERE c.constraint_type = 'FOREIGN KEY' AND c.database_name = current_database() ORDER BY child, name, ordinal",
            _ => null
        };
        if (sql == null) return [];
        var result = await session.ExecuteQueryAsync(sql);
        if (result.HasError) throw new InvalidOperationException(result.ErrorMessage);
        string Text(object?[] row, string column) => Convert.ToString(result.ValueOrDefault(row, column)) ?? "";
        var relationships = new List<ForeignKeyRelationship>();
        foreach (var group in result.ObjectRows.GroupBy(row => (Child: Text(row, "child"), Name: Text(row, "name"), Parent: Text(row, "parent"))))
        {
            var rows = group.OrderBy(row => Convert.ToInt32(result.ValueOrDefault(row, "ordinal"))).ToList();
            var targets = rows.Select(row => Text(row, "target")).ToList();
            if (targets.Any(string.IsNullOrEmpty) && session.Provider.ProviderName == "SQLite")
            {
                var info = await session.ExecuteQueryAsync("PRAGMA table_info('" + group.Key.Parent.Replace("'", "''") + "')");
                if (info.HasError) throw new InvalidOperationException(info.ErrorMessage);
                targets = info.ObjectRows.Where(row => Convert.ToInt32(info.ValueOrDefault(row, "pk")) > 0)
                    .OrderBy(row => Convert.ToInt32(info.ValueOrDefault(row, "pk")))
                    .Select(row => Convert.ToString(info.ValueOrDefault(row, "name"))!).ToList();
            }
            if (targets.Count == rows.Count && targets.All(s => !string.IsNullOrEmpty(s)))
                relationships.Add(new(group.Key.Name, group.Key.Child, group.Key.Parent,
                    rows.Select(row => Text(row, "source")).ToList(), targets));
        }
        return relationships;
    }
}
