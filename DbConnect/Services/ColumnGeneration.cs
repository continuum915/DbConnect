using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

/// <summary>
/// Identity and generated-column settings as the designer edits them: loading them from the table
/// design, normalising them for the builders, checking them against the engine's
/// <see cref="ColumnGenerationRules"/>, and deciding which changes are blocked or need review.
/// </summary>
public static partial class ColumnGeneration
{
    [GeneratedRegex(@"^[+-]?\d{1,38}$")]
    private static partial Regex Integer();

    /// <summary>
    /// The settings as the builders take them: blank text is none, a generated column's storage is the
    /// engine's default when unset, and a SQLite rowid alias is no identity setting (the type and key make it).
    /// </summary>
    public static ColumnGenerationSettings Normalize(ColumnGenerationSettings settings, ColumnGenerationRules rules)
    {
        var expression = Blank(settings.Expression)?.Trim();
        return new(settings.IdentityKind == TableDesignIdentityKind.RowIdAlias ? null : settings.IdentityKind,
            Blank(settings.Seed)?.Trim(), Blank(settings.Increment)?.Trim(), expression,
            expression == null ? null : settings.Storage ?? rules.DefaultStorage);
    }

    /// <summary>True when the column's identity or generation, as the engine would apply it, was edited.</summary>
    public static bool Edited(ColumnDefinition column, ColumnDefinition original, ColumnGenerationRules rules) =>
        Normalize(column.Generation, rules) != Normalize(original.Generation, rules);

    public static ColumnGenerationChange Changes(ColumnDefinition original, ColumnDefinition column) =>
        Changes(original.Generation, column.Generation);

    /// <summary>The identity and generation changes from <paramref name="original"/>; both normalized.</summary>
    public static ColumnGenerationChange Changes(ColumnGenerationSettings original, ColumnGenerationSettings column)
    {
        var changes = ColumnGenerationChange.None;
        if (original.IdentityKind == null && column.IdentityKind != null) changes |= ColumnGenerationChange.AddIdentity;
        else if (original.IdentityKind != null && column.IdentityKind == null) changes |= ColumnGenerationChange.DropIdentity;
        else if (column.IdentityKind != null)
        {
            if (original.IdentityKind != column.IdentityKind) changes |= ColumnGenerationChange.IdentityKind;
            if (original.Seed != column.Seed) changes |= ColumnGenerationChange.IdentitySeed;
            if (original.Increment != column.Increment) changes |= ColumnGenerationChange.IdentityIncrement;
        }
        if (original.Expression == null && column.Expression != null) changes |= ColumnGenerationChange.AddGeneration;
        else if (original.Expression != null && column.Expression == null) changes |= ColumnGenerationChange.DropGeneration;
        else if (column.Expression != null)
        {
            if (original.Expression != column.Expression) changes |= ColumnGenerationChange.Expression;
            if (original.Storage != column.Storage) changes |= ColumnGenerationChange.Storage;
        }
        return changes;
    }

    /// <summary>Why the engine cannot make the column's identity or generation changes; empty when it can.</summary>
    public static IEnumerable<string> Blocked(ColumnDefinition original, ColumnDefinition column, ColumnGenerationRules rules, TableDesign baseline)
    {
        var changes = Changes(original, column);
        foreach (var change in Enum.GetValues<ColumnGenerationChange>().Where(c => c != 0 && changes.HasFlag(c)))
        {
            if (!rules.Allows(change)) yield return rules.Explain(change);
            else if (change == ColumnGenerationChange.Expression
                     && baseline.UnsupportedReason(TableDesignFeature.GeneratedExpressionChanges) is { } reason)
                yield return $"changing a generated column's expression is {reason}.";
            else if (rules.OnlyStoredConvertible && ConvertedColumn(change, original, column) is { GeneratedStorage: not TableDesignGeneratedStorage.Stored })
                yield return rules.Explain(change);
        }
        if (rules.TypeLockedByGeneratedColumns && (original.DataType != column.DataType || original.Collation != column.Collation)
            && ReadBy(original.Name, baseline).FirstOrDefault() is { } reader)
            yield return $"{rules.Provider} cannot change the type or collation of a column that generated column {reader} reads.";
    }

    /// <summary>
    /// The other generated columns whose expression names the column. This is a token match on the loaded
    /// expression, not a parse: a name inside a string literal counts, which can only block, never allow.
    /// </summary>
    private static IEnumerable<string> ReadBy(string column, TableDesign baseline) =>
        baseline.Columns.Where(c => c.Generated is { } g && !c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)
                                    && SqliteDefinitionSql.Tokens(g.Expression).Any(t => t.Identifier.Equals(column, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Name);

    /// <summary>The generated side of a conversion: the new column when it becomes generated, the original when it stops.</summary>
    private static ColumnDefinition? ConvertedColumn(ColumnGenerationChange change, ColumnDefinition original, ColumnDefinition column) =>
        change switch
        {
            ColumnGenerationChange.AddGeneration => column,
            ColumnGenerationChange.DropGeneration => original,
            _ => null
        };

    /// <summary>Why a new column's identity or generation cannot be added to the existing table, or null.</summary>
    public static string? NewColumnBlocked(ColumnDefinition column, ColumnGenerationRules rules)
    {
        if (column.IdentityKind != null && !rules.Allows(ColumnGenerationChange.NewIdentityColumn))
            return rules.Explain(ColumnGenerationChange.NewIdentityColumn);
        if (column.GeneratedExpression != null && !rules.Allows(ColumnGenerationChange.NewGeneratedColumn))
            return rules.Explain(ColumnGenerationChange.NewGeneratedColumn);
        return null;
    }

    /// <summary>Identity and generation changes that may change stored values or later ones; Save needs them confirmed.</summary>
    public static IEnumerable<string> Destructive(ColumnDefinition original, ColumnDefinition column, ColumnGenerationRules rules)
    {
        var changes = Changes(original, column);
        var name = original.Name;
        if (changes.HasFlag(ColumnGenerationChange.AddIdentity) && rules.SeedAndIncrement)
            yield return $"Makes {name} an identity column whose sequence starts at {column.IdentitySeed ?? "1"} without regard to " +
                         "the values already stored, so new rows may collide with existing ones until the sequence passes them.";
        else if (changes.HasFlag(ColumnGenerationChange.AddIdentity) && rules.IdentityNeedsKey)
            yield return $"Makes {name} AUTO_INCREMENT; existing zero or NULL values are replaced with generated numbers.";
        if (changes.HasFlag(ColumnGenerationChange.AddGeneration))
            yield return $"Makes {name} generated from ({column.GeneratedExpression}); its stored values are replaced with computed ones.";
        else if (changes.HasFlag(ColumnGenerationChange.Expression) && column.GeneratedStorage == TableDesignGeneratedStorage.Stored)
            yield return $"Changes the expression of {name} to ({column.GeneratedExpression}); its stored values are recomputed.";
    }

    /// <summary>Mistakes in the identity and generation settings of a new or edited column.</summary>
    public static IEnumerable<string> Problems(ColumnDefinition column, TableDesignerEdits edits, ColumnGenerationRules rules)
    {
        var name = $"Column \"{column.Name}\"";
        return IdentityProblems(column, name, edits, rules)
            .Concat(SeedAndIncrementProblems(column, name, rules))
            .Concat(GenerationProblems(column, name, rules));
    }

    private static IEnumerable<string> IdentityProblems(ColumnDefinition column, string name, TableDesignerEdits edits, ColumnGenerationRules rules)
    {
        var kind = column.IdentityKind;
        var type = column.DataType?.Trim() ?? "";
        // A rowid alias is the INTEGER PRIMARY KEY itself, not a generator: it may have a default, like any rowid.
        if (kind == TableDesignIdentityKind.RowIdAlias && rules.IdentityKinds.Contains(kind.Value))
        {
            if (!IntegerPrimaryKey(column, edits))
                yield return $"{name}: only a column typed INTEGER that is the whole primary key is a rowid alias.";
        }
        else if (kind != null)
        {
            if (!rules.IdentityKinds.Contains(kind.Value))
                yield return rules.IdentityKinds.Count == 0
                    ? $"{name}: {rules.Explain(ColumnGenerationChange.AddIdentity)}"
                    : $"{name}: {rules.Provider} has no {ColumnGenerationLabels.Of(kind)} identity.";
            if (Blank(column.GeneratedExpression) != null) yield return $"{name} cannot be both an identity and a generated column.";
            if (Blank(column.DefaultValue) != null) yield return $"{name} cannot have both an identity and a default.";
            if (rules.IdentityTypes.Count > 0 && (!rules.IdentityTypes.Contains(type) || column.Scale is > 0))
                yield return $"{name}: a {rules.Provider} identity must have an integer type ({string.Join(", ", rules.IdentityTypes.Order())}).";
            if (rules.IdentityNeedsNotNull && column.IsNullable) yield return $"{name}: an identity column must be Not Null.";
            if (rules.OneIdentityPerTable && edits.Columns.Count(c => !c.IsDropped && c.IdentityKind != null) > 1)
                yield return $"{name}: a {rules.Provider} table can have only one identity column.";
            if (rules.IdentityIsIntegerPrimaryKey && !IntegerPrimaryKey(column, edits))
                yield return $"{name}: {rules.Provider} gives {ColumnGenerationLabels.Of(kind)} only to a column typed INTEGER that is the whole primary key.";
            // The key's first column must be the identity: InnoDB looks its next value up through that index.
            if (rules.IdentityNeedsKey && !LeadsKey(column, edits))
                yield return $"{name}: a {rules.Provider} {ColumnGenerationLabels.Of(kind)} column must lead the primary key or an index.";
        }
    }

    private static IEnumerable<string> SeedAndIncrementProblems(ColumnDefinition column, string name, ColumnGenerationRules rules)
    {
        var type = column.DataType?.Trim() ?? "";
        foreach (var (label, value, isIncrement) in new[] { ("seed", column.IdentitySeed, false), ("increment", column.IdentityIncrement, true) })
        {
            if (Blank(value) is not { } text) continue;
            if (column.IdentityKind == null) yield return $"{name} has an identity {label} but no identity.";
            else if (!rules.SeedAndIncrement) yield return $"{name}: {rules.Provider} has no identity {label} per column.";
            else if (!Integer().IsMatch(text.Trim())) yield return $"{name}: the identity {label} must be a whole number.";
            // Only decimal and numeric identities go past bigint.
            else if (!long.TryParse(text.Trim(), out _) && !type.StartsWith("decimal", StringComparison.OrdinalIgnoreCase)
                     && !type.StartsWith("numeric", StringComparison.OrdinalIgnoreCase))
                yield return $"{name}: the identity {label} is outside the range of {type}.";
            else if (isIncrement && text.Trim().TrimStart('+', '-').All(c => c == '0'))
                yield return $"{name}: the identity increment cannot be zero.";
        }
    }

    private static IEnumerable<string> GenerationProblems(ColumnDefinition column, string name, ColumnGenerationRules rules)
    {
        if (Blank(column.GeneratedExpression) == null) yield break;
        if (rules.Storages.Count == 0) yield return $"{name}: {rules.Explain(ColumnGenerationChange.NewGeneratedColumn)}";
        else if (column.GeneratedStorage is { } storage && !rules.Storages.Contains(storage))
            yield return $"{name}: {rules.Provider} has no {storage.ToString().ToUpperInvariant()} generated columns.";
        if (Blank(column.DefaultValue) != null) yield return $"{name} cannot have both a generation expression and a default.";
        if (rules.OnlyStoredGeneratedNotNull && !column.IsNullable && (column.GeneratedStorage ?? rules.DefaultStorage) != TableDesignGeneratedStorage.Stored)
            yield return $"{name}: only a stored (PERSISTED) computed column can be Not Null.";
    }

    /// <summary>The column is the first member of a primary or unique key, or of an index.</summary>
    private static bool LeadsKey(ColumnDefinition column, TableDesignerEdits edits)
    {
        var name = column.Name.Trim();
        bool Leads(IEnumerable<string> members) => members.FirstOrDefault()?.Trim().Equals(name, StringComparison.OrdinalIgnoreCase) == true;
        var keys = edits.Keys.Count > 0
            ? edits.Keys.Where(k => !k.IsDropped).Select(k => (IEnumerable<string>)k.Columns)
            : [edits.Columns.Where(c => !c.IsDropped && c.IsPrimaryKey).Select(c => c.Name)];
        return keys.Any(Leads) || edits.Indexes.Any(i => !i.IsDropped && Leads(i.Parts.Select(p => p.Column ?? "")));
    }

    private static bool IntegerPrimaryKey(ColumnDefinition column, TableDesignerEdits edits) =>
        (column.DataType?.Trim() ?? "").Equals("INTEGER", StringComparison.OrdinalIgnoreCase) && column.IsPrimaryKey
        && edits.Columns.Count(c => !c.IsDropped && c.IsPrimaryKey) == 1;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
