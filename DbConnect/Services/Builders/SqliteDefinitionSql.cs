using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services.Builders;

/// <summary>Edits column declarations while retaining stored SQLite constraint SQL.</summary>
internal static class SqliteDefinitionSql
{
    internal sealed record Token(string Text, int Start, int End, int Depth, bool Quoted = false)
    {
        public bool Is(string keyword) => !Quoted && Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);
        public string Identifier => Quoted ? Text[1..^1].Replace(Text[^1].ToString() + Text[^1], Text[^1].ToString()) : Text;
    }

    private static readonly string[] ConstraintKeywords =
        ["CONSTRAINT", "PRIMARY", "NOT", "NULL", "UNIQUE", "CHECK", "DEFAULT", "COLLATE", "REFERENCES", "GENERATED", "AS"];

    internal static List<Token> Tokens(string sql)
    {
        var result = new List<Token>();
        var depth = 0;
        for (var i = 0; i < sql.Length;)
        {
            if (char.IsWhiteSpace(sql[i])) { i++; continue; }
            if (i + 1 < sql.Length && sql[i..(i + 2)] == "--")
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (i + 1 < sql.Length && sql[i..(i + 2)] == "/*")
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new InvalidOperationException("Unterminated SQL comment.");
                i = end + 2;
                continue;
            }
            var start = i;
            var quoted = sql[i] is '\'' or '"' or '`' or '[';
            if (quoted)
            {
                var closing = sql[i] == '[' ? ']' : sql[i];
                i++;
                while (i < sql.Length)
                {
                    if (sql[i++] != closing) continue;
                    if (closing != ']' && i < sql.Length && sql[i] == closing) { i++; continue; }
                    break;
                }
            }
            else if (!char.IsLetterOrDigit(sql[i]) && sql[i] is not '_' and not '$') i++;
            else
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$')) i++;
            }
            var text = sql[start..i];
            if (text == ")") depth--;
            result.Add(new Token(text, start, i, depth, quoted));
            if (text == "(") depth++;
        }
        return result;
    }

    private static (Token Open, Token Close) Body(string sql)
    {
        var tokens = Tokens(sql);
        var open = tokens.First(t => t.Is("("));
        var close = tokens.First(t => t.Start > open.Start && t.Is(")") && t.Depth == open.Depth);
        return (open, close);
    }

    public static List<string> BodyTerms(string sql)
    {
        var (open, close) = Body(sql);
        var boundaries = Tokens(sql).Where(t => t.Is(",") && t.Depth == open.Depth + 1
            && t.Start > open.Start && t.Start < close.Start).Select(t => t.Start).Append(close.Start);
        var terms = new List<string>();
        var start = open.End;
        foreach (var end in boundaries)
        {
            terms.Add(sql[start..end].Trim());
            start = end + 1;
        }
        return terms;
    }

    public static string? IndexPredicate(string sql)
    {
        var (_, close) = Body(sql);
        var where = Tokens(sql).FirstOrDefault(t => t.Start >= close.End && t.Is("WHERE"));
        return where == null ? null : sql[where.End..].Trim().TrimEnd(';');
    }

    public static bool HasAutoincrement(string sql) => Tokens(sql).Any(t => t.Is("AUTOINCREMENT"));

    public static string RebuildCreate(string sql, string tempName, IList<ColumnDefinition> original,
        IList<ColumnDefinition> modified, Func<ColumnDefinition, string> newColumn, Func<string, string> quote,
        IReadOnlySet<string> dropped, ICollection<string>? removedObjects = null, IReadOnlyList<TableKeyChange>? keyChanges = null,
        IReadOnlyList<TableForeignKeyChange>? foreignKeyChanges = null, IReadOnlyList<TableCheckChange>? checkChanges = null)
    {
        checkChanges ??= [];
        var pendingChecks = checkChanges.Where(c => c.From != null).ToList();
        keyChanges ??= [];
        foreignKeyChanges ??= [];
        var pending = keyChanges.Where(c => c.From != null).ToList();
        var pendingForeignKeys = foreignKeyChanges.Where(c => c.From != null).ToList();
        var renamedInPlace = new HashSet<TableKeyChange>(ReferenceEqualityComparer.Instance);
        if (Tokens(sql).TakeWhile(t => !t.Is("(")).Any(t => t.Is("VIRTUAL")))
            throw new InvalidOperationException("Virtual tables cannot be rebuilt by the table designer.");
        var tableName = Tokens(sql).TakeWhile(t => !t.Is("(")).Last().Identifier;
        var terms = new List<string>();
        var constraints = new List<string>();
        foreach (var term in BodyTerms(sql))
        {
            var first = Tokens(term)[0];
            if (first.Is("CONSTRAINT") || first.Is("PRIMARY") || first.Is("UNIQUE") || first.Is("CHECK") || first.Is("FOREIGN"))
            {
                // A changed key or foreign key is left out here; its new form is added after the other constraints.
                if (SqliteKeyDefinitionSql.TableKey(term) is { } key && pending.FirstOrDefault(c => SqliteKeyDefinitionSql.Matches(c.From!, key)) is { } change)
                {
                    pending.Remove(change);
                    continue;
                }
                if (SqliteKeyDefinitionSql.Checks(term).FirstOrDefault() is { } check
                    && pendingChecks.FirstOrDefault(c => SqliteKeyDefinitionSql.Matches(c.From!, check)) is { } checkChange)
                {
                    pendingChecks.Remove(checkChange);
                    continue;
                }
                if (SqliteKeyDefinitionSql.ForeignKey(term) is { } reference
                    && pendingForeignKeys.FirstOrDefault(c => SqliteKeyDefinitionSql.Matches(c.From!, reference)) is { } fkChange)
                {
                    pendingForeignKeys.Remove(fkChange);
                    continue;
                }
                if (SqliteConstraintDependencies.KeepTableConstraint(term, tableName, dropped, removedObjects))
                    constraints.Add(term);
                continue;
            }
            var old = original.FirstOrDefault(c => c.Name.Equals(first.Identifier, StringComparison.OrdinalIgnoreCase));
            var column = modified.FirstOrDefault(c => !c.IsNew && c.Name.Equals(first.Identifier, StringComparison.OrdinalIgnoreCase));
            // Key changes come first, so a key on a column that is also being dropped is matched before the column goes.
            var keyed = EditColumnKeys(term, pending, renamedInPlace, quote);
            if (SqliteKeyDefinitionSql.ForeignKey(keyed) is { Column: not null } columnReference
                && pendingForeignKeys.FirstOrDefault(c => SqliteKeyDefinitionSql.Matches(c.From!, columnReference)) is { } columnFkChange)
            {
                pendingForeignKeys.Remove(columnFkChange);
                keyed = SqliteKeyDefinitionSql.RemoveColumnReference(keyed);
            }
            foreach (var columnCheck in SqliteKeyDefinitionSql.Checks(keyed).ToList())
                if (pendingChecks.FirstOrDefault(c => SqliteKeyDefinitionSql.Matches(c.From!, columnCheck)) is { } columnCheckChange)
                {
                    pendingChecks.Remove(columnCheckChange);
                    keyed = SqliteKeyDefinitionSql.RemoveColumnCheck(keyed, columnCheck.Expression!);
                }
            var filtered = SqliteConstraintDependencies.FilterColumn(keyed, tableName, dropped, column == null || column.IsDropped, removedObjects);
            if (column == null || column.IsDropped) continue;
            if (old == null) throw new InvalidOperationException($"Missing original column {column.Name}.");
            terms.Add(EditColumn(filtered, old, column));
        }
        if (pending.Count > 0)
            throw new InvalidOperationException($"Key {pending[0].From!.Name ?? string.Join(", ", pending[0].From!.Columns)} was not found in the stored definition; reload the table.");
        if (pendingChecks.Count > 0)
            throw new InvalidOperationException($"Check {pendingChecks[0].From!.Name ?? pendingChecks[0].From!.Expression} was not found in the stored definition; reload the table.");
        if (pendingForeignKeys.Count > 0)
            throw new InvalidOperationException($"Foreign key {pendingForeignKeys[0].From!.Name ?? string.Join(", ", pendingForeignKeys[0].From!.Columns)} " +
                                                "was not found in the stored definition; reload the table.");
        terms.AddRange(modified.Where(c => c.IsNew && !c.IsDropped).Select(newColumn));
        terms.AddRange(constraints);
        // Added and re-created keys; a renamed column-level key was renamed in place.
        terms.AddRange(keyChanges.Where(c => c.To != null && !renamedInPlace.Contains(c)).Select(c => DdlColumnRules.KeyClause(c.To!, quote)));
        // Foreign keys reference tables of the same database by bare name.
        terms.AddRange(foreignKeyChanges.Where(c => c.To != null).Select(c => DdlColumnRules.ForeignKeyClause(c.To!, quote, t => quote(t.Name))));
        terms.AddRange(checkChanges.Where(c => c.To != null).Select(c => DdlColumnRules.CheckClause(c.To!, quote)));
        var (_, close) = Body(sql);
        var suffix = sql[close.End..].Trim().TrimEnd(';');
        // A newline before the delimiter prevents trailing line comments swallowing it.
        return $"CREATE TABLE {quote(tempName)} (\n    {string.Join("\n,\n    ", terms)}\n) {suffix};";
    }

    /// <summary>
    /// Applies key changes to a key declared on the column itself: a rename keeps the clause (and a rowid
    /// alias or AUTOINCREMENT with it); a drop or re-creation removes it.
    /// </summary>
    private static string EditColumnKeys(string term, List<TableKeyChange> pending, HashSet<TableKeyChange> renamedInPlace, Func<string, string> quote)
    {
        foreach (var kind in new[] { KeyKind.Primary, KeyKind.Unique })
        {
            if (SqliteKeyDefinitionSql.ColumnKey(term, kind) is not { } key) continue;
            if (pending.FirstOrDefault(c => SqliteKeyDefinitionSql.Matches(c.From!, key)) is not { } change) continue;
            pending.Remove(change);
            if (DdlColumnRules.IsRename(change))
            {
                term = SqliteKeyDefinitionSql.EditColumnKey(term, kind, quote, change.To!.Name);
                renamedInPlace.Add(change);
            }
            else term = SqliteKeyDefinitionSql.EditColumnKey(term, kind, quote);
        }
        return term;
    }

    private static string EditColumn(string term, ColumnDefinition original, ColumnDefinition modified)
    {
        var tokens = Tokens(term);
        var constraint = tokens.Skip(1).FirstOrDefault(t => t.Depth == 0 && ConstraintKeywords.Any(t.Is));
        var tail = constraint == null ? "" : term[constraint.Start..];
        // Primary-key membership changes arrive as key changes (EditColumnKeys), not through the column.
        if (original.IsNullable != modified.IsNullable)
            tail = ReplaceConstraint(tail, "NOT", modified.IsNullable ? "" : "NOT NULL");
        if (original.DefaultValue != modified.DefaultValue)
            tail = ReplaceConstraint(tail, "DEFAULT", string.IsNullOrWhiteSpace(modified.DefaultValue) ? "" : $"DEFAULT {modified.DefaultValue}");
        if (original.Collation != modified.Collation)
            tail = ReplaceConstraint(tail, "COLLATE", string.IsNullOrWhiteSpace(modified.Collation) ? "" : $"COLLATE {modified.Collation}");
        if (original.GeneratedExpression != modified.GeneratedExpression || original.GeneratedStorage != modified.GeneratedStorage)
            tail = ReplaceGenerated(tail, DdlColumnRules.GeneratedClause(modified).Trim());
        if ((original.IdentityKind == TableDesignIdentityKind.AutoIncrement) != (modified.IdentityKind == TableDesignIdentityKind.AutoIncrement))
            tail = SetAutoincrement(tail, modified.IdentityKind == TableDesignIdentityKind.AutoIncrement);
        var type = original.DataType == modified.DataType
            ? term[tokens[0].End..(constraint?.Start ?? term.Length)].Trim()
            : modified.DataType;
        return $"{term[..tokens[0].End]} {type} {tail}".TrimEnd();
    }

    /// <summary>Replaces (or adds, or with an empty replacement removes) the column's [GENERATED ALWAYS] AS (...) [STORED|VIRTUAL] clause.</summary>
    private static string ReplaceGenerated(string tail, string replacement)
    {
        var tokens = Tokens(tail);
        var index = tokens.FindIndex(t => t.Depth == 0 && (t.Is("GENERATED") || t.Is("AS")));
        if (index < 0) return $"{tail} {replacement}".Trim();
        var start = index >= 2 && tokens[index - 2].Is("CONSTRAINT") ? tokens[index - 2].Start : tokens[index].Start;
        var open = tokens.FindIndex(index, t => t.Is("("));
        var end = tokens.First(t => t.Start > tokens[open].Start && t.Depth == tokens[open].Depth && t.Is(")")).End;
        if (tokens.FirstOrDefault(t => t.Start >= end) is { } storage && (storage.Is("STORED") || storage.Is("VIRTUAL"))) end = storage.End;
        return $"{tail[..start]}{replacement} {tail[end..]}".Trim();
    }

    /// <summary>Adds AUTOINCREMENT after the column's PRIMARY KEY [ASC|DESC] [ON CONFLICT x] clause, or removes it.</summary>
    private static string SetAutoincrement(string tail, bool on)
    {
        var tokens = Tokens(tail);
        if (!on)
            return tokens.FirstOrDefault(t => t.Depth == 0 && t.Is("AUTOINCREMENT")) is { } existing
                ? $"{tail[..existing.Start].TrimEnd()} {tail[existing.End..].TrimStart()}".Trim()
                : tail;
        var primary = tokens.FindIndex(t => t.Depth == 0 && t.Is("PRIMARY"));
        if (primary < 0 || tokens.ElementAtOrDefault(primary + 1)?.Is("KEY") != true)
            throw new InvalidOperationException("AUTOINCREMENT needs the column's own PRIMARY KEY clause; the table declares its key separately.");
        var after = primary + 2;
        if (tokens.ElementAtOrDefault(after) is { } direction && (direction.Is("ASC") || direction.Is("DESC"))) after++;
        if (tokens.ElementAtOrDefault(after)?.Is("ON") == true && tokens.ElementAtOrDefault(after + 1)?.Is("CONFLICT") == true) after += 3;
        var at = tokens[after - 1].End;
        return $"{tail[..at]} AUTOINCREMENT{tail[at..]}";
    }

    private static string ReplaceConstraint(string tail, string keyword, string replacement)
    {
        var tokens = Tokens(tail);
        var index = tokens.FindIndex(t => t.Depth == 0 && t.Is(keyword)
            && (keyword != "NOT" || tokens.ElementAtOrDefault(tokens.IndexOf(t) + 1)?.Is("NULL") == true)
            && (keyword != "DEFAULT" || tokens.ElementAtOrDefault(tokens.IndexOf(t) - 1)?.Is("SET") != true));
        if (index < 0) return $"{tail} {replacement}".Trim();
        var start = tokens[index].Start;
        var prefix = "";
        if (index >= 2 && tokens[index - 2].Is("CONSTRAINT"))
        {
            start = tokens[index - 2].Start;
            prefix = tail[start..tokens[index].Start];
        }
        // Consume the value (including NULL or a parenthesized expression) before looking for the next constraint.
        var valueIndex = index + 1;
        if (valueIndex < tokens.Count && (tokens[valueIndex].Is("+") || tokens[valueIndex].Is("-"))) valueIndex++;
        var end = valueIndex < tokens.Count ? tokens[valueIndex].End : tail.Length;
        if (valueIndex < tokens.Count && tokens[valueIndex].Is("("))
            end = tokens.First(t => t.Start > tokens[valueIndex].Start && t.Depth == 0 && t.Is(")")).End;
        var next = tokens.FirstOrDefault(t => t.Start >= end && t.Depth == 0 && ConstraintKeywords.Any(t.Is));
        var updated = replacement.Length == 0 ? "" : prefix + replacement;
        return $"{tail[..start]}{updated} {tail[(next?.Start ?? tail.Length)..]}".Trim();
    }
}
