using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

/// <summary>Everything the table designer holds when a change plan is derived.</summary>
public sealed record TableDesignerEdits(
    string ProviderName,
    DatabaseProviderCapabilities Capabilities,
    string TableName,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<EditableIndex> Indexes,
    IReadOnlyList<EditableForeignKey> ForeignKeys,
    IReadOnlyList<EditableCheck> Checks,
    string? TableComment)
{
    /// <summary>True when the designer edits an existing table; false when it creates one.</summary>
    public bool IsExisting { get; init; }

    /// <summary>The design the existing table was loaded with; null while it is not loaded.</summary>
    public TableDesign? Baseline { get; init; }

    /// <summary>The table name the existing table was opened with; editing the name is a rename.</summary>
    public string? OpenedName { get; init; }

    /// <summary>What is known about a referenced table, for checking foreign keys; null while nothing is.</summary>
    public Func<DatabaseObjectIdentity, ForeignKeyTarget?> ForeignKeyTarget { get; init; } = _ => null;

    /// <summary>The primary and unique keys: loaded ones with their edits, and added ones.</summary>
    public IReadOnlyList<KeyDefinition> Keys { get; init; } = [];

    /// <summary>The engine changes columns by rebuilding the table from its stored definition (SQLite).</summary>
    public bool RebuildsColumns { get; init; }
    public TableRebuildDefinition? RebuildDefinition { get; init; }
}

/// <summary>
/// The single reviewed change: the SQL the preview shows is exactly the SQL Save runs, one
/// statement per step. Errors are mistakes in the edits; Blocked lists changes the designer cannot
/// yet make without losing something; Destructive lists what Save may only run once confirmed.
/// </summary>
public sealed record TableChangePlan(IReadOnlyList<string> Steps, IReadOnlyList<string> Errors, IReadOnlyList<string> Blocked)
{
    public static TableChangePlan None { get; } = new([], [], []);

    /// <summary>The steps as the preview shows them and Save runs them.</summary>
    public string Sql { get; } = string.Join(Environment.NewLine, Steps);

    /// <summary>Changes that delete data or may change stored values; Save needs them confirmed.</summary>
    public IReadOnlyList<string> Destructive { get; init; } = [];

    public bool HasChanges => Steps.Count > 0;
    public bool CanExecute => Errors.Count == 0 && Blocked.Count == 0;
    public IEnumerable<string> Problems => Errors.Concat(Blocked);
}

/// <summary>Derives the designer's change plan from the loaded design and the current edits.</summary>
public static class TableChangePlanner
{
    public const string DropColumnUnavailable = "Dropping columns is not supported in the current table designer workflow for this provider.";
    public const string RenameColumnUnavailable = "Renaming columns is not available in the table designer for this provider yet.";

    public static TableChangePlan Plan(TableDesignerEdits edits)
    {
        var errors = TableDesignerValidation.Validate(edits);
        var blocked = new List<string>();
        var statements = new List<string>();
        var destructive = new List<string>();
        try
        {
            if (edits.IsExisting) Alter(edits, statements, blocked, destructive);
            else Create(edits, statements, blocked);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            statements.Clear();
            destructive.Clear();
            blocked.Add(ex.Message);
        }
        // Builders end lines with Environment.NewLine. The statements are not normalised further:
        // stored SQL copied into a rebuild may hold line breaks inside string literals.
        return new(statements.Where(s => s.Length > 0).ToList(), errors, blocked) { Destructive = destructive };
    }

    private static void Create(TableDesignerEdits e, List<string> statements, List<string> blocked)
    {
        var builder = DdlBuilderFactory.For(e.ProviderName);
        var columns = e.Columns.Where(c => !c.IsDropped).Select(c => Normalized(c, builder.GenerationRules)).ToList();
        statements.Add(builder.GenerateCreateTable(e.TableName, columns, new TableConstraints(TableKeyPlanning.CreateKeys(e, columns),
            TableForeignKeyPlanning.CreateForeignKeys(e), TableCheckPlanning.CreateChecks(e))));
        foreach (var index in TableIndexPlanning.CreateIndexes(e))
            foreach (var sql in builder.GenerateIndexChange(e.TableName, index)) AddOrBlock(sql, statements, blocked);
        if (!e.Capabilities.SupportsObjectComments) return;
        var comments = new List<CommentChange>();
        if (Blank(e.TableComment) is { } tableComment) comments.Add(new(null, tableComment, false));
        if (!builder.ColumnCommentsInDefinition)
            comments.AddRange(columns.Where(c => c.Comment != null).Select(c => new CommentChange(c.Name, c.Comment, false)));
        statements.AddRange(builder.GenerateCommentChanges(e.TableName, comments));
    }

    private static void Alter(TableDesignerEdits e, List<string> statements, List<string> blocked, List<string> destructive)
    {
        if (e.Baseline is not { } baseline)
        {
            blocked.Add("The table design has not loaded, so no change can be planned.");
            return;
        }
        var name = baseline.Table.Name;
        if (e.OpenedName != null && e.TableName != e.OpenedName)
            blocked.Add("Renaming the table is not available in the designer; use Rename… in the explorer.");
        var builder = DdlBuilderFactory.For(e.ProviderName).InSchema(baseline.Table.Schema ?? baseline.Table.Catalog);

        var columns = e.Columns.Select(c => Normalized(c, builder.GenerationRules)).ToList();
        var commentsInDefinition = e.Capabilities.SupportsObjectComments && builder.ColumnCommentsInDefinition;
        foreach (var column in columns.Where(c => !c.IsNew))
            CheckColumn(e, builder, column, commentsInDefinition, blocked);
        foreach (var column in columns.Where(c => c.IsNew && !c.IsDropped))
            if (ColumnGeneration.NewColumnBlocked(column, builder.GenerationRules) is { } reason)
                blocked.Add($"Column {column.Name}: {reason}");
        var columnsChanged = columns.Any(c => c.IsNew ? !c.IsDropped : c.IsDropped || Changed(c.Original!, c, commentsInDefinition));

        var keyRules = builder.KeyRules;
        var fkRules = builder.ForeignKeyRules;
        var keyChanges = TableKeyPlanning.Changes(e, keyRules);
        var fkChanges = TableForeignKeyPlanning.Changes(e, fkRules);
        var checkRules = builder.CheckRules;
        var checkChanges = TableCheckPlanning.Changes(e, checkRules);
        var rebuild = e.RebuildsColumns && (columnsChanged || keyChanges.Count > 0 || fkChanges.Count > 0 || checkChanges.Count > 0);
        // This table's own foreign keys that the plan drops or re-creates no longer hold its keys in place.
        var released = fkChanges.Where(c => c.Change.From != null && !DdlColumnRules.IsRename(c.Change)).Select(c => c.Change.From!).ToList();
        foreach (var (key, change) in keyChanges)
        {
            blocked.AddRange(TableKeyPlanning.Blocked(key, change, baseline, keyRules, released));
            destructive.AddRange(TableKeyPlanning.Destructive(key, change));
        }
        foreach (var (fk, change) in fkChanges)
        {
            blocked.AddRange(TableForeignKeyPlanning.Blocked(fk, change, baseline, InPlaceRename(change, fkRules.Renames, rebuild)));
            destructive.AddRange(TableForeignKeyPlanning.Destructive(fk, change));
        }
        foreach (var (check, change) in checkChanges)
        {
            blocked.AddRange(TableCheckPlanning.Blocked(check, change, baseline, InPlaceRename(change, checkRules.Renames, rebuild)));
            destructive.AddRange(TableCheckPlanning.Destructive(check, change));
        }
        var indexRules = builder.IndexRules;
        var indexChanges = TableIndexPlanning.Changes(e, indexRules);
        var plannedIndexes = indexChanges.Where(c => c.Change.From != null).ToDictionary(c => c.Change.From!.Name, c => c.Change.To);
        TableDesignIndex? Planned(TableDesignIndex loaded) => plannedIndexes.TryGetValue(loaded.Name, out var to) ? to : loaded;
        foreach (var (index, change) in indexChanges)
        {
            blocked.AddRange(TableIndexPlanning.Blocked(index, change, baseline, indexRules,
                DdlColumnRules.IsRename(change) && indexRules.Renames && !rebuild, released, Planned));
            destructive.AddRange(TableIndexPlanning.Destructive(index, change));
        }
        blocked.AddRange(TableIndexPlanning.NameReuseBlocked(indexChanges, indexRules));
        if (rebuild && e.RebuildDefinition == null)
            throw new InvalidOperationException("The table definition must be loaded before rebuilding.");

        // Foreign keys are dropped first (a key they use can then go) and added last (after the keys they reference).
        // Keys are dropped before the columns change (a column can then leave the key) and added after (it may first become NOT NULL).
        var lateKeys = new List<string>();
        var lateForeignKeys = new List<string>();
        var lateChecks = new List<string>();
        var lateIndexes = new List<string>();
        if (!rebuild)
        {
            // Checks are dropped before the columns change: one may name a column that goes, and so may a re-created one's old form.
            foreach (var (_, change) in checkChanges)
                Schedule(change.From != null, change.To != null, DdlColumnRules.IsRename(change),
                    checkRules.ReplaceInOneStatement && !TableCheckPlanning.UsesDroppedColumn(change, e),
                    whole => builder.GenerateCheckChange(name, whole ? change : change with { To = null }),
                    () => builder.GenerateCheckChange(name, change with { From = null }), statements, lateChecks, blocked);
            // MySQL cannot drop and add a same-named foreign key in one statement, so a re-created one is always split.
            foreach (var (_, change) in fkChanges)
                Schedule(change.From != null, change.To != null, DdlColumnRules.IsRename(change), false,
                    whole => builder.GenerateForeignKeyChange(name, whole ? change : change with { To = null }),
                    () => builder.GenerateForeignKeyChange(name, change with { From = null }), statements, lateForeignKeys, blocked);
            foreach (var (_, change) in keyChanges)
                Schedule(change.From != null, change.To != null, DdlColumnRules.IsRename(change), keyRules.ReplaceInOneStatement,
                    whole => builder.GenerateKeyChange(name, whole ? change : change with { To = null }),
                    () => builder.GenerateKeyChange(name, change with { From = null }), statements, lateKeys, blocked);
        }
        // Indexes are dropped before the columns change (one may cover a column that goes) and created after (it may cover a new one).
        if (!rebuild)
            foreach (var (_, change) in indexChanges)
                Schedule(change.From != null, change.To != null, DdlColumnRules.IsRename(change), indexRules.ReplaceInOneStatement,
                    whole => builder.GenerateIndexChange(name, whole ? change : change with { To = null }),
                    () => builder.GenerateIndexChange(name, change with { From = null }), statements, lateIndexes, blocked);
        if (columnsChanged || rebuild)
        {
            var original = columns.Where(c => c.Original != null).Select(c => c.Original!).ToList();
            var rebuildIndexes = rebuild ? RebuildIndexes(e, indexChanges, builder, name) : null;
            var definition = rebuild
                ? e.RebuildDefinition! with
                {
                    KeyChanges = keyChanges.Select(k => k.Change).ToList(),
                    ForeignKeyChanges = fkChanges.Select(k => k.Change).ToList(),
                    CheckChanges = checkChanges.Select(c => c.Change).ToList()
                }
                : e.RebuildDefinition;
            var alter = builder.GenerateAlterTable(name, original, columns, rebuildIndexes, definition);
            // A rebuild is one script with its own transaction; any other alter is one step per statement.
            if (rebuild) statements.Add(alter);
            else statements.AddRange(Statements(alter, e.Capabilities));
            Describe(columns, rebuild ? name : null, builder, destructive);
        }
        statements.AddRange(lateKeys);
        statements.AddRange(lateIndexes);
        statements.AddRange(lateForeignKeys);
        statements.AddRange(lateChecks);

        if (e.Capabilities.SupportsObjectComments) statements.AddRange(CommentChanges(e, builder, baseline, columns));
        if (rebuild && statements.Count > 1)
            throw new InvalidOperationException("A table rebuild cannot be combined with other statements in one save; save the other changes separately.");
    }

    /// <summary>
    /// Places one constraint change: a drop runs early, before the columns change; an addition, rename or
    /// one-statement replacement runs late, after them; any other replacement is split into an early drop and
    /// a late addition. <paramref name="change"/> takes true for the whole change and false for its drop alone.
    /// </summary>
    private static void Schedule(bool drops, bool adds, bool rename, bool replaceInOneStatement,
        Func<bool, IReadOnlyList<string>> change, Func<IReadOnlyList<string>> addition, List<string> early, List<string> late, List<string> blocked)
    {
        var split = drops && adds && !rename && !replaceInOneStatement;
        var dropOnly = drops && !adds;
        foreach (var sql in change(!(split || dropOnly))) AddOrBlock(sql, split || dropOnly ? early : late, blocked);
        if (split) foreach (var sql in addition()) AddOrBlock(sql, late, blocked);
    }

    /// <summary>
    /// The change only renames, and the engine renames in place, keeping everything the designer does not edit.
    /// Engines without a rename re-create the constraint, and a rebuild restates it.
    /// </summary>
    private static bool InPlaceRename(TableForeignKeyChange change, bool renames, bool rebuild) => DdlColumnRules.IsRename(change) && renames && !rebuild;

    private static bool InPlaceRename(TableCheckChange change, bool renames, bool rebuild) => DdlColumnRules.IsRename(change) && renames && !rebuild;

    private static IEnumerable<string> Statements(string sql, DatabaseProviderCapabilities capabilities) =>
        SqlBatchParser.SplitStatements(sql, capabilities.BatchDialect)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => s.EndsWith(';') ? s : s + ";");

    /// <summary>Lists the column changes that delete data or may change stored values.</summary>
    private static void Describe(List<ColumnDefinition> columns, string? rebuiltTable, IDdlBuilder builder, List<string> destructive)
    {
        foreach (var column in columns.Where(c => !c.IsNew && c.Original != null))
        {
            if (column.IsDropped)
                destructive.Add($"Drops column {column.Original!.Name} and deletes its data." +
                                (column.Loaded?.Sequence is { Owned: true } owned ? $" Its owned sequence {owned.Name} is dropped with it." : "") +
                                (rebuiltTable == null ? "" : " Objects that depend on it are removed; the preview lists them."));
            else
            {
                // Columns here are normalized: DataType is the declared type with its modifiers.
                var original = column.Original!;
                if (original.DataType != column.DataType && !ColumnTypeText.Widens(original.DataType, column.DataType, builder.TypeRules))
                    destructive.Add($"Changes the type of {original.Name} from {original.DataType} to {column.DataType}; " +
                                    "existing values are converted and may be truncated or rejected.");
                if (original.Collation != column.Collation)
                    destructive.Add($"Changes the collation of {original.Name} from {original.Collation ?? "the default"} to " +
                                    $"{column.Collation ?? "the default"}; comparisons, sorting and unique indexes follow the new collation, " +
                                    "and stored text may be converted or rejected.");
                destructive.AddRange(ColumnGeneration.Destructive(original, column, builder.GenerationRules));
            }
        }
        // A rebuild that drops nothing copies every row into the new table in one transaction, so it needs no confirmation.
    }

    /// <summary>Blocks column edits the designer cannot apply without losing something.</summary>
    private static void CheckColumn(TableDesignerEdits e, IDdlBuilder builder, ColumnDefinition column, bool commentsInDefinition, List<string> blocked)
    {
        if (column.Original is not { } original)
        {
            blocked.Add($"Column {column.Name} has no loaded original; reload the table.");
            return;
        }
        if (column.IsDropped)
        {
            if (!e.Capabilities.SupportsTableDesignerDropColumn) blocked.Add(DropColumnUnavailable);
            return;
        }
        if (column.Name != original.Name && !e.Capabilities.SupportsTableDesignerRenameColumn)
            blocked.Add($"Column {original.Name}: {RenameColumnUnavailable}");
        if (!Changed(original, column, commentsInDefinition)) return;
        if (e.Baseline is { } baseline)
            blocked.AddRange(ColumnGeneration.Blocked(original, column, builder.GenerationRules, baseline).Select(r => $"Column {original.Name}: {r}"));
        if (column.Loaded is not { } loaded)
            blocked.Add($"Column {original.Name} is not in the loaded design; reload the table before changing it.");
        else if (builder.AlterColumnLoss(original, column, loaded) is { } loss)
            blocked.Add($"Column {original.Name}: {loss}");
    }

    private static IEnumerable<string> CommentChanges(TableDesignerEdits e, IDdlBuilder builder, TableDesign baseline, List<ColumnDefinition> columns)
    {
        var changes = new List<CommentChange>();
        var tableComment = Blank(e.TableComment);
        if (tableComment != Blank(baseline.Comment)) changes.Add(new(null, tableComment, baseline.Comment != null));
        if (!builder.ColumnCommentsInDefinition)
        {
            foreach (var column in columns.Where(c => !c.IsDropped))
            {
                var before = column.IsNew ? null : column.Original?.Comment;
                if (column.Comment != before) changes.Add(new(column.Name, column.Comment, before != null));
            }
        }
        return builder.GenerateCommentChanges(baseline.Table.Name, changes);
    }

    /// <summary>A builder answers with a comment line when the engine cannot make the change; that is never run as if it succeeded.</summary>
    private static void AddOrBlock(string sql, List<string> statements, List<string> blocked)
    {
        if (sql.TrimStart().StartsWith("--", StringComparison.Ordinal))
            blocked.Add(sql.TrimStart('-', ' ').Replace("⚠️", "").Trim());
        else statements.Add(sql);
    }

    private static bool Changed(ColumnDefinition original, ColumnDefinition column, bool commentsInDefinition) =>
        original.Name != column.Name || original.DataType != column.DataType || original.IsNullable != column.IsNullable
        || original.DefaultValue != column.DefaultValue || original.Collation != column.Collation
        || (commentsInDefinition && original.Comment != column.Comment)
        || ColumnGeneration.Changes(original, column) != ColumnGenerationChange.None;

    /// <summary>
    /// A copy as the builders take it: the type name and its modifiers composed into one declared
    /// type, and empty defaults, comments and collations read as none, so clearing a field restores
    /// the loaded state.
    /// </summary>
    private static ColumnDefinition Normalized(ColumnDefinition column, ColumnGenerationRules rules)
    {
        var copy = Composed(column, rules);
        if (column.Original is { } original) copy.Original = Composed(original, rules);
        return copy;
    }

    private static ColumnDefinition Composed(ColumnDefinition column, ColumnGenerationRules rules)
    {
        var copy = column.Clone();
        copy.DataType = ColumnTypeText.Compose(column);
        copy.Length = null;
        copy.Precision = null;
        copy.Scale = null;
        copy.DefaultValue = Blank(column.DefaultValue);
        copy.Comment = Blank(column.Comment);
        copy.Collation = Blank(column.Collation)?.Trim();
        copy.Generation = ColumnGeneration.Normalize(column.Generation, rules);
        return copy;
    }

    /// <summary>
    /// The independent indexes a rebuild creates on the new table: an unchanged one from the engine's own text,
    /// a changed or added one from its edited definition; dropped ones are left out.
    /// </summary>
    private static List<IndexDefinition> RebuildIndexes(TableDesignerEdits e, List<(EditableIndex Index, TableIndexChange Change)> changes,
        IDdlBuilder builder, string table)
    {
        var indexes = new List<IndexDefinition>();
        foreach (var index in e.Indexes.Where(i => !i.IsReadOnly && !i.IsDropped))
        {
            var change = changes.FirstOrDefault(c => c.Index == index).Change;
            if (change is { To: null }) continue;
            var definition = change?.To ?? index.Loaded!;
            // An unchanged index is replayed from the engine's own text; without it, restating it could lose a setting.
            if (change == null && index.StoredSql == null)
                throw new InvalidOperationException($"The stored definition of index {definition.Name} is not known, so the rebuild cannot keep it; reload the table.");
            var sql = change == null ? index.StoredSql! : builder.GenerateIndexChange(table, new TableIndexChange(null, definition)).Single();
            indexes.Add(new IndexDefinition
            {
                Name = definition.Name,
                TableName = table,
                Columns = definition.Parts.Select(p => p.Column ?? "").ToList(),
                IsUnique = definition.IsUnique,
                WherePredicate = definition.Filter,
                CreateSql = sql
            });
        }
        return indexes;
    }

    internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
