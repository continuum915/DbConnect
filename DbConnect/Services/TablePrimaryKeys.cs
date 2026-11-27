using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>A table's primary key as Data Compare matches rows by it.</summary>
public static class TablePrimaryKeys
{
    /// <summary>
    /// The table's primary key columns: from the column list, or from the table design where the column list does not
    /// mark them (DuckDB).
    /// </summary>
    public static async Task<List<string>> ResolveAsync(DatabaseSession session, string tableName)
    {
        var marked = (await session.GetColumnDefinitionsAsync(tableName)).Where(column => column.IsPrimaryKey).Select(column => column.Name).ToList();
        if (marked.Count > 0 || session.Provider is not ITableDesignSource designs) return marked;
        var parts = tableName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            var design = await designs.GetTableDesignAsync(session.ConnectionString,
                new DatabaseObjectIdentity(null, parts.Length > 1 ? parts[^2] : null, parts[^1], SchemaNodeType.Table));
            return design.PrimaryKey?.Columns.ToList() ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return marked; }
    }
}
