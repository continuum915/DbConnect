using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// The change plan that makes a target table match a source table of the same engine, from the table designer's own
/// planner: both tables are opened in designers, as the user would open them, and the target designer is edited to the
/// source's columns, keys, indexes, foreign keys, checks and comment, keeping its loaded baseline. What the target lacks
/// is added, what the source lacks is marked dropped, and matching elements take the source's values, so the plan holds
/// exactly the designer's steps, destructive list and blocked reasons for those edits, SQLite rebuilds included.
/// </summary>
public static class TableSyncEditor
{
    /// <param name="created">Source tables the same synchronization creates before this one, which keys may reference.</param>
    public static async Task<TableChangePlan> PlanAsync(DatabaseSession source, DatabaseObjectIdentity sourceTable, DatabaseSession target,
        DatabaseObjectIdentity targetTable, IReadOnlyList<DatabaseObjectIdentity>? created = null)
    {
        var from = await OpenAsync(source, sourceTable);
        var to = await OpenAsync(target, targetTable);
        SyncColumns(from, to);
        SyncKeys(from, to);
        SyncIndexes(from, to);
        SyncForeignKeys(from, to, targetTable);
        SyncChecks(from, to);
        to.TableComment = from.TableComment;
        // A key to a table created in the same run is checked against that table's source definition.
        foreach (var table in created ?? [])
            if (source.Provider is ITableDesignSource designs)
                to.ForeignKeys.Know(new DatabaseObjectIdentity(targetTable.Catalog, table.Schema, table.Name, SchemaNodeType.Table),
                    ForeignKeyTarget.From(await designs.GetTableDesignAsync(source.ConnectionString, table)));
        await to.ForeignKeys.EnsureTargetsAsync();
        to.UpdatePreview();
        return to.Plan;
    }

    private static async Task<TableDesignerViewModel> OpenAsync(DatabaseSession session, DatabaseObjectIdentity table)
    {
        var designer = new TableDesignerViewModel(table.Name, session,
            new SchemaNode { Name = table.Name, RawName = table.Name, Type = SchemaNodeType.Table, SchemaName = table.Schema }, table);
        await designer.LoadAsync();
        if (!designer.Design.IsLoaded)
            throw new TableDefinitionUnavailableException(designer.Design.Error ?? designer.StatusMessage ?? $"{table.Name} could not be opened in the designer.");
        return designer;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void SyncColumns(TableDesignerViewModel from, TableDesignerViewModel to)
    {
        foreach (var column in to.Columns)
            if (from.Columns.FirstOrDefault(c => Same(c.Name, column.Name)) is { } wanted) Copy(wanted, column);
            else column.IsDropped = true;
        foreach (var wanted in from.Columns.Where(c => !to.Columns.Any(t => Same(t.Name, c.Name))))
        {
            var added = wanted.Clone();
            added.IsNew = true;
            added.Original = null;
            added.Loaded = null;
            to.Columns.Add(added);
        }
    }

    private static void Copy(ColumnDefinition wanted, ColumnDefinition column)
    {
        column.DataType = wanted.DataType;
        column.IsNullable = wanted.IsNullable;
        column.DefaultValue = wanted.DefaultValue;
        column.MaxLength = wanted.MaxLength;
        column.Length = wanted.Length;
        column.Precision = wanted.Precision;
        column.Scale = wanted.Scale;
        column.Collation = wanted.Collation;
        column.Generation = wanted.Generation;
        column.Comment = wanted.Comment;
    }

    private static void SyncKeys(TableDesignerViewModel from, TableDesignerViewModel to)
    {
        static bool Matches(KeyDefinition a, KeyDefinition b) => a.Kind == b.Kind &&
            (a.Kind == KeyKind.Primary || (a.Name != null && Same(a.Name, b.Name)) || a.Columns.SequenceEqual(b.Columns, StringComparer.OrdinalIgnoreCase));
        foreach (var key in to.Keys.Items.ToList())
        {
            if (from.Keys.Items.FirstOrDefault(k => Matches(k, key)) is not { } wanted)
            {
                key.IsDropped = true;
                continue;
            }
            if (!key.Columns.SequenceEqual(wanted.Columns, StringComparer.OrdinalIgnoreCase))
            {
                key.Columns.Clear();
                foreach (var column in wanted.Columns) key.Columns.Add(column);
            }
            if (!key.NameLocked && wanted.Name != null) key.Name = wanted.Name;
        }
        foreach (var wanted in from.Keys.Items.Where(k => !to.Keys.Items.Any(t => Matches(k, t))))
            to.Keys.Items.Add(new KeyDefinition(wanted.Kind, wanted.Columns) { Name = wanted.Name });
    }

    /// <summary>An index's whole definition, to leave an index alone when nothing about it differs.</summary>
    private static string Signature(EditableIndex index) => string.Join("|", index.IsUnique, index.Filter.Trim(),
        string.Join(",", index.Parts.Select(p => $"{p.Column ?? p.Expression?.Trim()}:{p.Descending}")), string.Join(",", index.IncludedColumns));

    private static void SyncIndexes(TableDesignerViewModel from, TableDesignerViewModel to)
    {
        // Indexes that back keys come and go with their keys.
        foreach (var index in to.Indexes.Items.Where(i => !i.IsReadOnly).ToList())
        {
            var wanted = from.Indexes.Items.FirstOrDefault(i => !i.IsReadOnly && Same(i.Name, index.Name));
            if (wanted == null || Signature(wanted) != Signature(index))
                index.IsDropped = true;
        }
        foreach (var wanted in from.Indexes.Items.Where(i => !i.IsReadOnly))
        {
            if (to.Indexes.Items.Any(i => !i.IsReadOnly && !i.IsDropped && Same(i.Name, wanted.Name))) continue;
            // A method the target's designer does not offer (MySQL reports BTREE, its only one) is left to the engine's default.
            var method = to.Indexes.Methods.FirstOrDefault(m => Same(m, wanted.Method)) ?? to.Indexes.Methods.FirstOrDefault();
            var added = new EditableIndex(method: method) { Name = wanted.Name, IsUnique = wanted.IsUnique, Filter = wanted.Filter };
            foreach (var part in wanted.Parts) added.Parts.Add(new EditableIndexPart(part.Column, part.Expression, part.Descending));
            foreach (var column in wanted.IncludedColumns) added.IncludedColumns.Add(column);
            to.Indexes.Items.Add(added);
        }
    }

    private static void SyncForeignKeys(TableDesignerViewModel from, TableDesignerViewModel to, DatabaseObjectIdentity targetTable)
    {
        // The source's references name its own database; on the target they name the same table there.
        DatabaseObjectIdentity Retarget(DatabaseObjectIdentity? referenced) => referenced == null ? targetTable
            : new(targetTable.Catalog, referenced.Schema, referenced.Name, SchemaNodeType.Table);
        static string Pairs(EditableForeignKey key) => string.Join(",", key.Pairs.Select(p => $"{p.Column}>{p.ReferencedColumn}"));
        static string Signature(EditableForeignKey key) =>
            $"{Pairs(key)}|{key.ReferencedTable?.Schema}.{key.ReferencedTable?.Name}|{key.OnDelete}|{key.OnUpdate}".ToUpperInvariant();
        foreach (var key in to.ForeignKeys.Items.ToList())
        {
            var wanted = from.ForeignKeys.Items.FirstOrDefault(k => k.Name != null && Same(k.Name, key.Name))
                         ?? from.ForeignKeys.Items.FirstOrDefault(k => Pairs(k) == Pairs(key));
            if (wanted == null || Signature(wanted) != Signature(key)) key.IsDropped = true;
        }
        foreach (var wanted in from.ForeignKeys.Items)
        {
            if (to.ForeignKeys.Items.Any(k => !k.IsDropped && Signature(k) == Signature(wanted))) continue;
            to.ForeignKeys.Items.Add(new EditableForeignKey(wanted.Pairs.Select(p => new TableDesignColumnPair(p.Column, p.ReferencedColumn)))
            {
                Name = wanted.Name,
                ReferencedTable = Retarget(wanted.ReferencedTable),
                OnDelete = wanted.OnDelete,
                OnUpdate = wanted.OnUpdate
            });
        }
    }

    private static void SyncChecks(TableDesignerViewModel from, TableDesignerViewModel to)
    {
        static string Normal(string expression) => string.Concat(expression.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        foreach (var check in to.Checks.Items.ToList())
        {
            var wanted = from.Checks.Items.FirstOrDefault(c => c.Name != null && Same(c.Name, check.Name))
                         ?? from.Checks.Items.FirstOrDefault(c => Normal(c.Expression) == Normal(check.Expression));
            if (wanted == null) check.IsDropped = true;
            else if (Normal(wanted.Expression) != Normal(check.Expression)) check.Expression = wanted.Expression;
        }
        foreach (var wanted in from.Checks.Items.Where(w => !to.Checks.Items.Any(c =>
                     (w.Name != null && Same(w.Name, c.Name)) || Normal(c.Expression) == Normal(w.Expression))))
            to.Checks.Items.Add(new EditableCheck { Name = wanted.Name, Expression = wanted.Expression });
    }
}
