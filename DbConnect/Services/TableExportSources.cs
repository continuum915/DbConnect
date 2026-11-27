using System;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// What Export Data reads from a table and how it writes it as SQL INSERT: every row, sorted by the primary key (or
/// every column without one), paged on a reserved session; inserts target the table itself, with its CREATE script
/// available for Include CREATE TABLE.
/// </summary>
internal static class TableExportSources
{
    private const int PageSize = 5000;

    public static async Task<TableExportSource> ForTableAsync(DatabaseSession session, DatabaseObjectIdentity table, IDbSession reserved)
    {
        var columns = await session.Objects.GetColumnsAsync(table);
        if (columns.Count == 0) throw new InvalidOperationException("The table has no readable columns.");
        var keys = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        var order = (keys.Count > 0 ? keys : columns.Select(c => c.Name)).Select(c => new TableSortKey(c)).ToList();
        var query = new TableQuerySpec(order, 0, PageSize) { Object = table };
        return new TableExportSource(_ => Task.FromResult(reserved), table.Name, query,
            columns.Select((c, i) => new ExportColumn(c.Name, i, c.DataType)));
    }

    /// <summary>A copy of the chosen options in <paramref name="format"/>, set up for this table.</summary>
    public static ExportOptions OptionsFor(ExportOptions chosen, ExportFormat format, DatabaseSession session, DatabaseObjectIdentity table)
    {
        var options = chosen.Copy();
        options.Format = format;
        options.SqlDialect = session.Capabilities;
        options.SqlTableName = SqlInsertTarget.ForTable(table, session.Capabilities);
        options.CreateTableScript = token => TableScripting.ScriptCreateAsync(session, table, token);
        return options;
    }
}
