using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.ViewModels;

internal static class ErRelationshipVisualFactory
{
    private const double HeaderHeight = 28;
    private const double ColumnHeight = 20;

    internal static ErRelationshipVisual? CreateRelationshipVisual(IEnumerable<ErTable> VisibleTables, string fromTableName, string fromColumnName, string toTableName, string toColumnName, string name)
    {
        var fromTable = VisibleTables.FirstOrDefault(table => string.Equals(table.TableName, fromTableName, StringComparison.OrdinalIgnoreCase));
        var toTable = VisibleTables.FirstOrDefault(table => string.Equals(table.TableName, toTableName, StringComparison.OrdinalIgnoreCase));
        if (fromTable == null || toTable == null)
            return null;

        var fromIndex = IndexOfColumn(fromTable, fromColumnName);
        var toIndex = IndexOfColumn(toTable, toColumnName);

        var fromCenter = fromTable.X + (fromTable.Width / 2);
        var toCenter = toTable.X + (toTable.Width / 2);
        var leftToRight = fromCenter <= toCenter;

        var sourceColumn = fromTable.Columns.ElementAtOrDefault(Math.Max(0, fromIndex));
        var sourceCardinality = sourceColumn?.IsNullable == true
            ? ErRelationshipCardinality.ZeroOrMany
            : ErRelationshipCardinality.OneOrMany;

        return new ErRelationshipVisual
        {
            Name = name,
            FromTable = fromTableName,
            FromColumn = fromColumnName,
            ToTable = toTableName,
            ToColumn = toColumnName,
            StartX = leftToRight ? fromTable.X + fromTable.Width : fromTable.X,
            EndX = leftToRight ? toTable.X : toTable.X + toTable.Width,
            StartY = fromTable.Y + HeaderHeight + (Math.Max(0, fromIndex) + 0.5) * ColumnHeight,
            EndY = toTable.Y + HeaderHeight + (Math.Max(0, toIndex) + 0.5) * ColumnHeight,
            StartCardinality = sourceCardinality,
            EndCardinality = ErRelationshipCardinality.One
        };
    }

    private static int IndexOfColumn(ErTable table, string columnName)
    {
        var index = table.Columns.FindIndex(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : Math.Max(0, table.Columns.Count - 1);
    }

}
