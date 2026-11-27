using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Turns the ticked differences of a same-engine Schema Diff into a <see cref="SyncPlan"/>. Tables only in the source are
/// created from their complete definition (§1.6), referenced tables first, with foreign keys that are separate
/// statements added once every table exists; views, materialized views, functions and procedures only in the source, or
/// whose definition changed, are created (or replaced) from the source's own definition, in dependency order. Objects
/// only in the target are dropped, dependents first; every drop is destructive. Changed tables, triggers on their own
/// and anything DbConnect cannot read faithfully are listed as notes and not applied.
/// </summary>
public static class SchemaSyncPlanner
{
    /// <summary>Why Apply is not offered between these two connections; null when it is.</summary>
    public static string? Refusal(DatabaseSession source, DatabaseSession target) =>
        source.ProviderName != target.ProviderName
            ? $"Apply needs two connections of one engine; for {source.ProviderName} → {target.ProviderName} open the script in a tab to review it."
            : source.Provider is not ITableScriptSource
                ? $"{source.ProviderName} cannot script complete table definitions, so its changes cannot be applied."
                : null;

    /// <param name="planTable">The table designer's change plan that makes a target table match the source table.</param>
    public static async Task<SyncPlan> BuildAsync(DatabaseSession source, DatabaseSession target, IReadOnlyList<SchemaDiffItem> selected,
        Func<DatabaseObjectIdentity, DatabaseObjectIdentity, IReadOnlyList<DatabaseObjectIdentity>, Task<TableChangePlan>>? planTable = null,
        CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var dialect = target.Capabilities;
        var sourceCatalog = await CatalogAsync(source);
        var targetCatalog = await CatalogAsync(target);
        // An item without a schema (SQLite lists none) matches the object of that name in any schema.
        DatabaseObjectIdentity? Find(List<DatabaseObjectIdentity> catalog, SchemaDiffItem item) => catalog.FirstOrDefault(o =>
            KindOf(item.ObjectKind).Contains(o.Kind) && string.Equals(o.Name, item.ObjectName, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(item.SchemaName) || string.IsNullOrEmpty(o.Schema) ||
             string.Equals(o.Schema, item.SchemaName, StringComparison.OrdinalIgnoreCase)));

        var createTables = new List<DatabaseObjectIdentity>();
        var createObjects = new List<DatabaseObjectIdentity>();
        var dropTables = new List<DatabaseObjectIdentity>();
        var dropObjects = new List<DatabaseObjectIdentity>();
        var replaceObjects = new List<DatabaseObjectIdentity>();
        var changedTables = new List<(DatabaseObjectIdentity Source, DatabaseObjectIdentity Target)>();
        var indexes = new List<SchemaDiffItem>();
        foreach (var item in selected)
        {
            // What the comparison saw on the target must still hold: what it lacked still missing, what it had still there.
            if (KindOf(item.ObjectKind).Length > 0)
            {
                var onTarget = Find(targetCatalog, item) != null;
                if (item.ChangeType == SchemaDiffChangeType.Add && onTarget)
                    throw new SchemaDriftException($"{item.ObjectType} {item.QualifiedName} now exists on the target.");
                if (item.ChangeType is SchemaDiffChangeType.Remove or SchemaDiffChangeType.Modify && !onTarget)
                    throw new SchemaDriftException($"{item.ObjectType} {item.QualifiedName} is no longer on the target.");
            }
            if (item.ObjectKind == SchemaDiffObjectType.Index)
            {
                indexes.Add(item);
                continue;
            }
            var supported = item.ObjectKind is SchemaDiffObjectType.Table or SchemaDiffObjectType.View or SchemaDiffObjectType.Function
                or SchemaDiffObjectType.Procedure;
            if (!supported)
            {
                notes.Add($"{item.ObjectType} {item.QualifiedName} is not applied: only tables, views and routines are; open the script to review it.");
                continue;
            }
            switch (item.ChangeType)
            {
                case SchemaDiffChangeType.Add when Find(sourceCatalog, item) is { } created:
                    (item.ObjectKind == SchemaDiffObjectType.Table ? createTables : createObjects).Add(created);
                    break;
                case SchemaDiffChangeType.Remove when Find(targetCatalog, item) is { } dropped:
                    (item.ObjectKind == SchemaDiffObjectType.Table ? dropTables : dropObjects).Add(dropped);
                    break;
                case SchemaDiffChangeType.Modify when item.ObjectKind != SchemaDiffObjectType.Table && Find(sourceCatalog, item) is { } changed:
                    replaceObjects.Add(changed);
                    break;
                case SchemaDiffChangeType.Modify when item.ObjectKind == SchemaDiffObjectType.Table && planTable != null &&
                                                     Find(sourceCatalog, item) is { } from && Find(targetCatalog, item) is { } to:
                    changedTables.Add((from, to));
                    break;
                case SchemaDiffChangeType.None:
                    break;
                default:
                    notes.Add($"{item.ObjectType} {item.QualifiedName} could not be found again; compare again before applying.");
                    break;
            }
        }

        // An index on a table the plan creates or drops comes or goes with it; any other belongs to a changed table.
        var covered = (await IndexNamesAsync(source, [.. createTables, .. changedTables.Select(t => t.Source)], cancellationToken))
            .Concat(await IndexNamesAsync(target, [.. dropTables, .. changedTables.Select(t => t.Target)], cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        notes.AddRange(indexes.Where(i => !covered.Contains(i.ObjectName))
            .Select(i => $"Index {i.QualifiedName} is on a table whose own difference is not ticked; tick the table to apply it."));

        var steps = new List<SyncStep>();
        steps.AddRange(await DropObjectsAsync(target, dropObjects, notes));
        steps.AddRange(await DropTablesAsync(target, dropTables));
        steps.AddRange(await CreateTablesAsync(source, createTables, notes, cancellationToken));
        foreach (var (from, to) in changedTables) steps.AddRange(await ChangeTableAsync(from, to, createTables, planTable!, notes));
        steps.AddRange(await CreateObjectsAsync(source, target, createObjects, replaceObjects, notes, cancellationToken));
        var commitsImplicitly = !dialect.TransactionalDdl;
        var rebuilds = steps.Any(s => s.OwnsTransaction);
        if (rebuilds && dialect.TransactionalDdl)
            notes.Insert(0, "SQLite rebuilds a changed table in a transaction of its own, so this plan runs step by step: a failure keeps the steps before it.");
        // The target objects the plan changes, as they are now, and the ones it creates, which must stay missing.
        var touched = new List<DatabaseObjectIdentity>([.. dropTables, .. dropObjects, .. changedTables.Select(t => t.Target)]);
        touched.AddRange(replaceObjects.Select(o => targetCatalog.First(t => t.Kind == o.Kind && t.Name == o.Name && (o.Schema == null || t.Schema == o.Schema))));
        var baseline = await FingerprintsAsync(target, touched, cancellationToken);
        var absent = createTables.Concat(createObjects).Select(o => (o.Kind, o.Schema, o.Name)).ToList();
        return new SyncPlan(target.Name, target.Server != null ? target.DatabaseLabel : null, target.Connection.EnvironmentTier,
            steps.Select(s => s with { CommitsImplicitly = commitsImplicitly }).ToList(), Transactional: dialect.TransactionalDdl && !rebuilds)
        {
            Notes = notes,
            ChangesSchema = true,
            DriftCheck = async (_, ct) => await DriftAsync(target, touched, baseline, absent, ct)
        };
    }

    /// <summary>
    /// The designer's steps for a changed table. Steps that may lose data carry the designer's reasons; a plan the
    /// designer cannot carry out (blocked or invalid) is left out with its reasons.
    /// </summary>
    private static async Task<List<SyncStep>> ChangeTableAsync(DatabaseObjectIdentity source, DatabaseObjectIdentity target,
        IReadOnlyList<DatabaseObjectIdentity> created,
        Func<DatabaseObjectIdentity, DatabaseObjectIdentity, IReadOnlyList<DatabaseObjectIdentity>, Task<TableChangePlan>> planTable, List<string> notes)
    {
        var name = $"Table {ObjectNames.SchemaQualified(target)}";
        TableChangePlan plan;
        try { plan = await planTable(source, target, created); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notes.Add($"{name} is not changed: {DiagnosticRedactor.Redact(ex.Message)}");
            return [];
        }
        if (!plan.CanExecute)
        {
            notes.Add($"{name} is not changed: {string.Join(" ", plan.Problems)}");
            return [];
        }
        var destructive = plan.Destructive.Count == 0 ? null : string.Join(" ", plan.Destructive);
        return plan.Steps.Select((sql, i) => Statement(name, "Change", sql) with
        {
            Destructive = i == 0 ? destructive : null,
            OwnsTransaction = sql.StartsWith(SqliteRebuildExecution.Marker, StringComparison.Ordinal)
        }).ToList();
    }

    /// <summary>
    /// Each object's definition as the target holds it: a table's design (columns, keys, indexes, foreign keys, checks,
    /// triggers, comments), a view's or routine's own text.
    /// </summary>
    private static async Task<Dictionary<string, string>> FingerprintsAsync(DatabaseSession target, IReadOnlyList<DatabaseObjectIdentity> objects,
        CancellationToken cancellationToken)
    {
        var prints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in objects.Where(o => o.Kind == SchemaNodeType.Table))
        {
            try
            {
                var design = await ((ITableDesignSource)target.Provider).GetTableDesignAsync(target.ConnectionString, table, cancellationToken);
                prints[Key(table)] = string.Join("\n", TableDesignText.Lines(design));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { prints[Key(table)] = "missing"; }
        }
        var others = objects.Where(o => o.Kind != SchemaNodeType.Table).ToList();
        if (others.Count > 0)
        {
            var read = await DumpObjects.ReadAsync(target, new DumpCatalog([], others, []), cancellationToken);
            foreach (var other in others)
                prints[Key(other)] = read.ViewsAndRoutines.FirstOrDefault(o => Key(o.Identity) == Key(other))?.Definition ?? "missing";
        }
        return prints;
    }

    private static string Key(DatabaseObjectIdentity identity) => $"{identity.Kind}:{identity.Schema}.{identity.Name}{identity.RoutineSignature}";

    /// <summary>The first object that is not as the plan found it, named; null when none differs.</summary>
    private static async Task<string?> DriftAsync(DatabaseSession target, IReadOnlyList<DatabaseObjectIdentity> touched,
        IReadOnlyDictionary<string, string> baseline, IReadOnlyList<(SchemaNodeType Kind, string? Schema, string Name)> absent,
        CancellationToken cancellationToken)
    {
        static string Changed(string what) => $"The target changed since the comparison: {what}. Nothing was applied; compare again before applying.";
        var now = await FingerprintsAsync(target, touched, cancellationToken);
        foreach (var identity in touched)
            if (now[Key(identity)] != baseline[Key(identity)])
                return Changed($"{DumpObjects.Title(identity.Kind)} {ObjectNames.SchemaQualified(identity)} is not as it was");
        var catalog = await CatalogAsync(target);
        foreach (var (kind, schema, name) in absent)
            if (catalog.Any(o => o.Kind == kind && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                 (schema == null || o.Schema == null || string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase))))
                return Changed($"{DumpObjects.Title(kind)} {(schema == null ? "" : schema + ".")}{name} now exists");
        return null;
    }

    private static async Task<IEnumerable<string>> IndexNamesAsync(DatabaseSession session, List<DatabaseObjectIdentity> tables,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        foreach (var table in tables)
        {
            try
            {
                var snapshot = await ((ITableScriptSource)session.Provider).GetTableSnapshotAsync(session.ConnectionString, table, cancellationToken);
                names.AddRange(snapshot.Parts.Where(p => p.Kind is TableDefinitionPartKind.Index or TableDefinitionPartKind.Constraint).Select(p => p.Name));
            }
            catch (TableDefinitionUnavailableException) { }
        }
        return names;
    }

    private static SchemaNodeType[] KindOf(SchemaDiffObjectType kind) => kind switch
    {
        SchemaDiffObjectType.Table => [SchemaNodeType.Table],
        SchemaDiffObjectType.View => [SchemaNodeType.View, SchemaNodeType.MaterializedView],
        SchemaDiffObjectType.Function => [SchemaNodeType.Function],
        SchemaDiffObjectType.Procedure => [SchemaNodeType.Procedure],
        _ => []
    };

    private static async Task<List<DatabaseObjectIdentity>> CatalogAsync(DatabaseSession session)
    {
        try
        {
            // The explorer's listing may be older than the target; read it afresh.
            await session.SchemaChanges.InvalidateAsync(MetadataScope.Root);
            var catalog = await DumpCatalog.ResolveAsync(session, DumpSelection.Database);
            return [.. catalog.Tables, .. catalog.Objects];
        }
        catch (DatabaseDumpException) { return []; }
    }

    private static SyncStep Statement(string target, string action, string sql) =>
        new(target, action, new SqlCommandSpec(sql, new Dictionary<string, object?>()), sql);

    private static string Qualified(DatabaseObjectIdentity identity, DatabaseProviderCapabilities dialect) =>
        SqlInsertTarget.ForTable(identity, dialect);

    /// <summary>Tables referenced first; foreign keys that are separate statements once every new table exists.</summary>
    private static async Task<List<SyncStep>> CreateTablesAsync(DatabaseSession source, List<DatabaseObjectIdentity> tables, List<string> notes,
        CancellationToken cancellationToken)
    {
        if (tables.Count == 0) return [];
        var snapshots = new List<TableDefinitionSnapshot>();
        foreach (var table in tables)
        {
            try { snapshots.Add(await ((ITableScriptSource)source.Provider).GetTableSnapshotAsync(source.ConnectionString, table, cancellationToken)); }
            catch (TableDefinitionUnavailableException ex) { notes.Add($"Table {ObjectNames.SchemaQualified(table)} is not created: {ex.Message}"); }
        }
        IReadOnlyList<ForeignKeyRelationship> keys;
        await using (var db = await SessionSetup.OpenAsync(source, cancellationToken))
        {
            try { keys = await ForeignKeyService.LoadAsync(db); }
            catch (Exception ex) when (ex is not OperationCanceledException) { keys = []; }
        }
        var order = DumpTableOrder.Sort(snapshots, keys);
        var keyNames = keys.Select(k => k.Name).ToHashSet(StringComparer.Ordinal);
        var steps = new List<SyncStep>();
        var deferred = new List<SyncStep>();
        foreach (var snapshot in order.Tables)
        {
            var name = ObjectNames.SchemaQualified(snapshot.Table);
            // A prerequisite the plan creates itself (a referenced table among the new ones) needs no note.
            notes.AddRange(snapshot.Prerequisites
                .Where(p => p.Object is not { } needed || !order.Tables.Any(t => string.Equals(t.Table.Name, needed.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(p => $"Table {name} needs {p.Text}, which must already exist."));
            foreach (var part in snapshot.Ordered)
            {
                var step = Statement($"Table {name}", $"Create {part.Kind.ToString().ToLowerInvariant()} {part.Name}", part.Sql);
                if (part.Kind == TableDefinitionPartKind.Constraint && keyNames.Contains(part.Name)) deferred.Add(step);
                else steps.Add(step);
            }
        }
        return [.. steps, .. deferred];
    }

    /// <summary>Views and routines from the source's own definitions, each after the objects it names; a changed one is replaced.</summary>
    private static async Task<List<SyncStep>> CreateObjectsAsync(DatabaseSession source, DatabaseSession target, List<DatabaseObjectIdentity> created,
        List<DatabaseObjectIdentity> replaced, List<string> notes, CancellationToken cancellationToken)
    {
        if (created.Count + replaced.Count == 0) return [];
        var objects = await DumpObjects.ReadAsync(source, new DumpCatalog([], [.. created, .. replaced], []), cancellationToken);
        notes.AddRange(objects.Exclusions);
        var steps = new List<SyncStep>();
        foreach (var dumped in objects.ViewsAndRoutines)
        {
            var name = $"{dumped.Title} {ObjectNames.SchemaQualified(dumped.Identity)}";
            if (replaced.Contains(dumped.Identity) && !DumpObjects.ReplacesItself(dumped.Definition))
                steps.Add(Statement(name, "Drop to replace", Drop(dumped.Identity, target.Capabilities)));
            steps.Add(Statement(name, replaced.Contains(dumped.Identity) ? "Replace" : "Create", dumped.Definition));
        }
        return steps;
    }

    private static Task<List<SyncStep>> DropTablesAsync(DatabaseSession target, List<DatabaseObjectIdentity> tables) =>
        Task.FromResult(tables.Select(t => Statement($"Table {ObjectNames.SchemaQualified(t)}", "Drop", Drop(t, target.Capabilities)) with
        {
            Destructive = $"Drops table {ObjectNames.SchemaQualified(t)} and every row in it."
        }).ToList());

    private static Task<List<SyncStep>> DropObjectsAsync(DatabaseSession target, List<DatabaseObjectIdentity> objects, List<string> notes) =>
        Task.FromResult(objects.Select(o => Statement($"{DumpObjects.Title(o.Kind)} {ObjectNames.SchemaQualified(o)}", "Drop", Drop(o, target.Capabilities)) with
        {
            Destructive = $"Drops {DumpObjects.Title(o.Kind).ToLowerInvariant()} {ObjectNames.SchemaQualified(o)}; objects that use it stop working."
        }).ToList());

    /// <summary>PostgreSQL names an overload by its argument types; other engines by name alone.</summary>
    private static string Signature(DatabaseObjectIdentity identity) => identity.RoutineSignature is { } arguments ? $"({arguments})" : "";

    private static string Drop(DatabaseObjectIdentity identity, DatabaseProviderCapabilities dialect) => identity.Kind switch
    {
        SchemaNodeType.Table => $"DROP TABLE {Qualified(identity, dialect)}",
        SchemaNodeType.View => $"DROP VIEW {Qualified(identity, dialect)}",
        SchemaNodeType.MaterializedView => $"DROP MATERIALIZED VIEW {Qualified(identity, dialect)}",
        SchemaNodeType.Function => $"DROP FUNCTION {Qualified(identity, dialect)}{Signature(identity)}",
        SchemaNodeType.Procedure => $"DROP PROCEDURE {Qualified(identity, dialect)}{Signature(identity)}",
        _ => throw new NotSupportedException($"{identity.Kind} cannot be dropped here.")
    };
}

/// <summary>The target no longer holds what the comparison saw; the message names the object.</summary>
public sealed class SchemaDriftException(string what)
    : Exception($"The target changed since the comparison: {what} Compare again before applying.");
