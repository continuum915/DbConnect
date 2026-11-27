using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public class ErDiagramLayoutService
{
    private const double TableWidth = 250;
    private const double TableHeight = 200;
    private const double GapX = 50;
    private const double GapY = 50;
    private const int ColumnsPerRow = 4;
    private const double RowHeaderHeight = 28;
    private const double ColumnRowHeight = 22;

    public Dictionary<string, (double X, double Y)> ComputeLayout(IEnumerable<ErTable> tables)
    {
        var result = new Dictionary<string, (double X, double Y)>();
        var list = tables.ToList();

        for (int i = 0; i < list.Count; i++)
        {
            int col = i % ColumnsPerRow;
            int row = i / ColumnsPerRow;
            double x = col * (TableWidth + GapX);
            double y = row * (TableHeight + GapY);
            result[list[i].TableName] = (x, y);
            list[i].X = x;
            list[i].Y = y;
            list[i].Width = TableWidth;
            list[i].Height = RowHeaderHeight + (list[i].Columns.Count * ColumnRowHeight) + 8;
        }

        return result;
    }

    public async Task<ErDiagramModel> BuildErModelAsync(DatabaseSession session)
    {
        var model = new ErDiagramModel();

        try
        {
            var schema = await session.GetSchemaAsync();
            var tables = schema
                .SelectMany(node => node.Children)
                .Where(node => node.Type == SchemaNodeType.Table)
                .ToList();

            foreach (var tableNode in tables)
            {
                var tableName = tableNode.RawName ?? tableNode.Name;
                var erTable = new ErTable
                {
                    TableName = tableName,
                    SchemaName = tableNode.SchemaName ?? string.Empty
                };

                try
                {
                    var cols = await session.GetColumnDefinitionsAsync(tableName);
                    foreach (var col in cols)
                    {
                        erTable.Columns.Add(new ErColumn
                        {
                            Name = col.Name,
                            DataType = col.DataType,
                            IsPrimaryKey = col.IsPrimaryKey,
                            IsNullable = col.IsNullable
                        });
                    }
                }
                catch
                {
                    // Column load failures are non-fatal.
                }

                model.Tables.Add(erTable);
            }

            foreach (var relationship in await GetRelationshipsAsync(session, model.Tables.Select(table => table.TableName).ToList()))
                model.Relationships.Add(relationship);

            var foreignKeyLookup = model.Relationships
                .GroupBy(relationship => relationship.FromTable, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(relationship => relationship.FromColumn).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var table in model.Tables)
            {
                if (!foreignKeyLookup.TryGetValue(table.TableName, out var columns))
                    continue;

                foreach (var column in table.Columns)
                    column.IsForeignKey = columns.Contains(column.Name);
            }
        }
        catch
        {
            // Schema load failures are non-fatal.
        }

        ComputeLayout(model.Tables);
        return model;
    }

    private static async Task<List<ErRelationship>> GetRelationshipsAsync(DatabaseSession session, IReadOnlyList<string> tableNames)
    {
        if (tableNames.Count == 0)
            return [];

        if (session.ProviderName == "SQLite")
        {
            var sqliteRelationships = new List<ErRelationship>();
            foreach (var tableName in tableNames)
            {
                var quotedTable = tableName.Replace("'", "''");
                var pragmaResult = await session.ExecuteQueryAsync($"PRAGMA foreign_key_list('{quotedTable}')");
                foreach (var values in pragmaResult.ObjectRows)
                {
                    var row = pragmaResult.ToRowDictionary(values);
                    sqliteRelationships.Add(new ErRelationship
                    {
                        FromTable = tableName,
                        FromColumn = GetValue(row, "from"),
                        ToTable = GetValue(row, "table"),
                        ToColumn = GetValue(row, "to")
                    });
                }
            }

            return sqliteRelationships;
        }

        var sql = GetForeignKeySql(session.ProviderName);
        if (string.IsNullOrWhiteSpace(sql))
            return [];

        var result = await session.ExecuteQueryAsync(sql);
        return result.ObjectRows.Select(result.ToRowDictionary)
            .Select(row => new ErRelationship
            {
                FromTable = GetValue(row, "from_table", "table_name"),
                FromColumn = GetValue(row, "from_column", "column_name"),
                ToTable = GetValue(row, "to_table", "referenced_table_name"),
                ToColumn = GetValue(row, "to_column", "referenced_column_name")
            })
            .Where(relationship => !string.IsNullOrWhiteSpace(relationship.FromTable)
                && !string.IsNullOrWhiteSpace(relationship.FromColumn)
                && !string.IsNullOrWhiteSpace(relationship.ToTable)
                && !string.IsNullOrWhiteSpace(relationship.ToColumn))
            .ToList();
    }

    private static string GetForeignKeySql(string providerName) => providerName switch
    {
        "PostgreSQL" => @"
            SELECT tc.table_name AS from_table,
                   kcu.column_name AS from_column,
                   ccu.table_name AS to_table,
                   ccu.column_name AS to_column
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name
             AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage ccu
              ON ccu.constraint_name = tc.constraint_name
             AND ccu.constraint_schema = tc.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_schema NOT IN ('pg_catalog', 'information_schema')",
        "SQL Server" => @"
            SELECT tp.name AS from_table,
                   cp.name AS from_column,
                   tr.name AS to_table,
                   cr.name AS to_column
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
            JOIN sys.tables tp ON fkc.parent_object_id = tp.object_id
            JOIN sys.columns cp ON fkc.parent_object_id = cp.object_id AND fkc.parent_column_id = cp.column_id
            JOIN sys.tables tr ON fkc.referenced_object_id = tr.object_id
            JOIN sys.columns cr ON fkc.referenced_object_id = cr.object_id AND fkc.referenced_column_id = cr.column_id",
        "MySQL" => @"
            SELECT kcu.table_name AS from_table,
                   kcu.column_name AS from_column,
                   kcu.referenced_table_name AS to_table,
                   kcu.referenced_column_name AS to_column
            FROM information_schema.key_column_usage kcu
            WHERE kcu.table_schema = DATABASE()
              AND kcu.referenced_table_name IS NOT NULL",
        _ => string.Empty
    };

    private static string GetValue(IReadOnlyDictionary<string, object?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row.TryGetValue(key, out var value) && value != null)
                return value.ToString() ?? string.Empty;
        }

        return string.Empty;
    }
}
