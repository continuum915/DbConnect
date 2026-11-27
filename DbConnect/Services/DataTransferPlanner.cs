using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Plans a transfer's tables. Between connections of one engine a table is created from its own definition
/// (<see cref="ITableScriptSource"/>), keeping everything that engine wrote; the foreign keys it writes as separate
/// statements are held back until every table's rows are in. Between engines a table is created from portable columns
/// (<see cref="PortableTables"/>) in the target's default schema, with its primary and unique keys, its plain indexes and
/// its foreign keys (added after the data where the target can, written into CREATE TABLE on SQLite and DuckDB);
/// checks, expression and filtered indexes, triggers and comments are engine-specific and are listed as not carried over.
/// </summary>
internal static class DataTransferPlanner
{
    public static async Task<DataTransferPlan> PlanAsync(DatabaseSession source, DatabaseSession target, IReadOnlyList<DatabaseObjectIdentity> tables,
        IDbSession reader, CancellationToken cancellationToken)
    {
        var failures = new Dictionary<int, string>();
        var foreignKeys = await ForeignKeysAsync(reader);
        return source.ProviderName == target.ProviderName
            ? await SameEngineAsync(source, target, tables, foreignKeys, failures, cancellationToken)
            : await AcrossEnginesAsync(source, target, tables, foreignKeys, failures, cancellationToken);
    }

    private static async Task<DataTransferPlan> SameEngineAsync(DatabaseSession source, DatabaseSession target,
        IReadOnlyList<DatabaseObjectIdentity> tables, IReadOnlyList<ForeignKeyRelationship> foreignKeys, Dictionary<int, string> failures,
        CancellationToken cancellationToken)
    {
        var snapshots = new Dictionary<TableDefinitionSnapshot, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < tables.Count; i++)
        {
            try { snapshots[await ((ITableScriptSource)source.Provider).GetTableSnapshotAsync(source.ConnectionString, tables[i], cancellationToken)] = i; }
            catch (Exception ex) when (ex is not OperationCanceledException) { failures[i] = Reason(ex); }
        }
        var order = DumpTableOrder.Sort(snapshots.Keys.OrderBy(t => snapshots[t]).ToList(), foreignKeys);
        var names = new DumpTableNames(order.Tables.Select(t => t.Table).ToList());
        var plans = new List<(int, DataTransferTablePlan)>();
        foreach (var snapshot in order.Tables)
        {
            var keys = foreignKeys.Where(k => names.Find(k.Table) is { } child && ReferenceEquals(order.Tables[child], snapshot)).ToList();
            var deferred = new List<DataTransferDeferredKey>();
            var notes = snapshot.Prerequisites.Select(p => "Must already exist: " + p.Text)
                .Concat(snapshot.Exclusions.Select(e => "Not reproduced: " + e)).ToList();
            foreach (var key in keys)
            {
                var outside = names.Find(key.ReferencedTable) == null ? key.ReferencedTable : null;
                if (snapshot.Parts.FirstOrDefault(p => p.Kind == TableDefinitionPartKind.Constraint && p.Name == key.Name) is { } part)
                    deferred.Add(new(key.Name, part.Sql, outside));
                else if (outside != null)
                    notes.Add($"Foreign key {key.Name} references {outside}, which is not part of the transfer; it is part of the table's " +
                              "definition, so that table must exist on the target.");
            }
            var held = deferred.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
            var parts = snapshot.Ordered.Select(p => new DataTransferStatement(p.Kind, p.Name, p.Sql)).ToList();
            plans.Add((snapshots[snapshot], new DataTransferTablePlan(snapshot.Table, snapshot.Table, SqlInsertTarget.ForTable(snapshot.Table, target.Capabilities))
            {
                BeforeData = parts.Where(p => p.Kind is TableDefinitionPartKind.Sequence or TableDefinitionPartKind.Table).ToList(),
                AfterData = parts.Where(p => p.Kind is not (TableDefinitionPartKind.Sequence or TableDefinitionPartKind.Table or TableDefinitionPartKind.Index) &&
                                             !(p.Kind == TableDefinitionPartKind.Constraint && held.Contains(p.Name))).ToList(),
                Indexes = parts.Where(p => p.Kind == TableDefinitionPartKind.Index).ToList(),
                DeferredKeys = deferred,
                Notes = notes
            }));
        }
        return new(plans, failures);
    }

    private static async Task<DataTransferPlan> AcrossEnginesAsync(DatabaseSession source, DatabaseSession target,
        IReadOnlyList<DatabaseObjectIdentity> tables, IReadOnlyList<ForeignKeyRelationship> foreignKeys, Dictionary<int, string> failures,
        CancellationToken cancellationToken)
    {
        var types = PortableTypeDialects.For(target.ProviderName)!;
        var designs = new Dictionary<TableDefinitionSnapshot, (int Index, TableDesign Design)>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < tables.Count; i++)
        {
            try
            {
                var design = await ((ITableDesignSource)source.Provider).GetTableDesignAsync(source.ConnectionString, tables[i], cancellationToken);
                // Ordering needs only each table's name, which an empty definition carries.
                designs[new TableDefinitionSnapshot(design.Table, source.ProviderName, [])] = (i, design);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { failures[i] = Reason(ex); }
        }
        var order = DumpTableOrder.Sort(designs.Keys.OrderBy(t => designs[t].Index).ToList(), foreignKeys);
        var schema = target.Connection.Options?.DefaultSchema is { Length: > 0 } chosen ? chosen : types.DefaultSchema;
        DatabaseObjectIdentity TargetOf(DatabaseObjectIdentity table) => new(null, schema, table.Name, SchemaNodeType.Table);
        var selected = order.Tables.Select(t => designs[t].Design.Table).ToList();
        var names = new DumpTableNames(selected);
        var dialect = target.Capabilities;
        var plans = new List<(int, DataTransferTablePlan)>();
        foreach (var snapshot in order.Tables)
        {
            var (index, design) = designs[snapshot];
            var portable = PortableTables.From(source.ProviderName, source.Capabilities, design);
            var targetTable = TargetOf(design.Table);
            var name = SqlInsertTarget.ForTable(targetTable, dialect);
            var inline = new List<string>();
            var deferred = new List<DataTransferDeferredKey>();
            var notes = new List<string>();
            foreach (var (key, number) in design.ForeignKeys.Select((k, n) => (k, n + 1)))
            {
                var keyName = key.Name ?? key.GeneratedName ?? $"fk_{design.Table.Name}_{number}";
                var outside = names.Find(key.ReferencedTable) == null ? ObjectNames.SchemaQualified(key.ReferencedTable) : null;
                // SQLite refuses a schema in REFERENCES; a key written into CREATE TABLE names a table of the same schema.
                var referenced = types.AddsForeignKeysAfterCreate
                    ? SqlInsertTarget.ForTable(TargetOf(key.ReferencedTable), dialect)
                    : dialect.QuoteIdentifier(key.ReferencedTable.Name);
                var clause = $"FOREIGN KEY ({string.Join(", ", key.Columns.Select(c => dialect.QuoteIdentifier(c.Column)))}) REFERENCES " +
                             $"{referenced} " +
                             $"({string.Join(", ", key.Columns.Select(c => dialect.QuoteIdentifier(c.ReferencedColumn)))})" +
                             types.ForeignKeyActions(key.OnDelete, key.OnUpdate, out var actionNote);
                if (actionNote != null) notes.Add($"Foreign key {keyName}: {actionNote}");
                if (types.AddsForeignKeysAfterCreate)
                    deferred.Add(new(keyName, $"ALTER TABLE {name} ADD CONSTRAINT {dialect.QuoteIdentifier(keyName)} {clause}", outside));
                else if (outside == null)
                    inline.Add(clause);
                else
                    notes.Add($"Foreign key {keyName} is not created: it references {outside}, which is not part of the transfer.");
            }
            var created = PortableTables.CreateTable(target.ProviderName, dialect, targetTable, portable, inline);
            notes.InsertRange(0, created.Notes);
            var indexes = new List<DataTransferStatement>();
            var indexTable = types.IndexesNameBareTable ? dialect.QuoteIdentifier(targetTable.Name) : name;
            foreach (var unique in design.UniqueKeys)
                indexes.Add(Index(dialect, indexTable, unique.Name ?? $"uq_{design.Table.Name}_{indexes.Count + 1}", true,
                    unique.Columns.Select(c => (c, false))));
            foreach (var index2 in design.Indexes.Where(i => i.Origin == TableDesignIndexOrigin.CreateIndex))
            {
                if (index2.Parts.Any(p => p.Column == null) || index2.Filter != null || index2.IncludedColumns.Count > 0)
                    notes.Add($"Index {index2.Name} is not created: its expressions, filter or included columns are specific to {source.ProviderName}.");
                else
                    indexes.Add(Index(dialect, indexTable, index2.Name, index2.IsUnique, index2.Parts.Select(p => (p.Column!, p.Descending))));
            }
            notes.AddRange(design.Checks.Select(c => $"Check {c.Name ?? c.GeneratedName ?? ""} ({c.Expression}) is not carried over.".Replace("Check  (", "Check (")));
            notes.AddRange(design.Triggers.Select(t => $"Trigger {t.Name} is not carried over."));
            if (design.Comment != null || design.Columns.Any(c => c.Comment != null)) notes.Add("Comments are not carried over.");
            plans.Add((index, new DataTransferTablePlan(tables[index], targetTable, name)
            {
                BeforeData = [new(TableDefinitionPartKind.Table, design.Table.Name, created.Sql)],
                Indexes = indexes,
                DeferredKeys = deferred,
                Notes = notes,
                Columns = portable.Columns
            }));
        }
        return new(plans, failures);
    }

    private static DataTransferStatement Index(DatabaseProviderCapabilities dialect, string table, string name, bool unique,
        IEnumerable<(string Column, bool Descending)> parts) =>
        new(TableDefinitionPartKind.Index, name,
            $"CREATE {(unique ? "UNIQUE " : "")}INDEX {dialect.QuoteIdentifier(name)} ON {table} " +
            $"({string.Join(", ", parts.Select(p => dialect.QuoteIdentifier(p.Column) + (p.Descending ? " DESC" : "")))})");

    private static string Reason(Exception ex) => ex is TableDefinitionUnavailableException ? ex.Message : DiagnosticRedactor.Redact(ex.Message);

    /// <summary>The source's foreign keys; without them tables copy in the order chosen.</summary>
    private static async Task<IReadOnlyList<ForeignKeyRelationship>> ForeignKeysAsync(IDbSession reader)
    {
        try { return await ForeignKeyService.LoadAsync(reader); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return []; }
    }
}
