using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services.Builders;

/// <summary>
/// Removes, renames and adds primary, unique and foreign keys in a stored SQLite CREATE TABLE during a rebuild.
/// Each term's key is identified by <see cref="StoredTableText"/>, the parser the design reader uses, so
/// a loaded key is found by its name or, when unnamed, by its kind and ordered columns.
/// </summary>
internal static class SqliteKeyDefinitionSql
{
    private static readonly string[] ColumnConstraintKeywords =
        ["CONSTRAINT", "PRIMARY", "NOT", "NULL", "UNIQUE", "CHECK", "DEFAULT", "COLLATE", "REFERENCES", "GENERATED", "AS"];

    /// <summary>The key a table-level term declares, or null when it declares none.</summary>
    public static StoredConstraint? TableKey(string term) =>
        Constraints(term).FirstOrDefault(c => c.Kind is StoredConstraintKind.PrimaryKey or StoredConstraintKind.Unique);

    /// <summary>The key a column term declares on its own column, or null.</summary>
    public static StoredConstraint? ColumnKey(string term, KeyKind kind) =>
        Constraints(term).FirstOrDefault(c => c.Column != null && c.Kind == Stored(kind));

    // The newline keeps a trailing line comment in the term from swallowing the closing parenthesis.
    private static IReadOnlyList<StoredConstraint> Constraints(string term) =>
        SqliteDefinitionSql.Tokens(term).Any(t => t.Depth == 0 && (t.Is("PRIMARY") || t.Is("UNIQUE") || t.Is("REFERENCES") || t.Is("CHECK")))
            ? StoredTableText.Parse($"CREATE TABLE t ({term}\n)").Constraints
            : [];

    public static bool Matches(TableKeySpec key, StoredConstraint constraint) =>
        constraint.Kind == Stored(key.Kind) && (key.Name != null
            ? key.Name.Equals(constraint.Name, StringComparison.OrdinalIgnoreCase)
            : constraint.Name == null && constraint.Columns.SequenceEqual(key.Columns, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Removes the column's [CONSTRAINT name] PRIMARY KEY ... or UNIQUE ... clause, or with
    /// <paramref name="rename"/> gives it that name in place, so a rowid alias and its AUTOINCREMENT stay.
    /// </summary>
    public static string EditColumnKey(string term, KeyKind kind, Func<string, string> quote, string? rename = null)
    {
        var tokens = SqliteDefinitionSql.Tokens(term);
        var keyword = kind == KeyKind.Primary ? "PRIMARY" : "UNIQUE";
        var index = tokens.FindIndex(1, t => t.Depth == 0 && t.Is(keyword));
        if (index < 0) throw new InvalidOperationException($"The {keyword} clause of the column was not found in its stored definition.");
        var start = index >= 2 && tokens[index - 2].Is("CONSTRAINT") ? tokens[index - 2].Start : tokens[index].Start;
        if (rename != null) return $"{term[..start]}CONSTRAINT {quote(rename)} {term[tokens[index].Start..]}";
        var end = tokens.FindIndex(index + 1, t => t.Depth == 0 && ColumnConstraintKeywords.Any(t.Is) && !t.Is("NULL"));
        // "ON CONFLICT x" and AUTOINCREMENT belong to the key clause; NOT NULL that follows does not.
        var stop = end < 0 ? term.Length : tokens[end].Start;
        return $"{term[..start].TrimEnd()} {term[stop..].TrimStart()}".Trim();
    }

    /// <summary>The foreign key a table-level term, or a column term on its column, declares; null when none.</summary>
    public static StoredConstraint? ForeignKey(string term) =>
        Constraints(term).FirstOrDefault(c => c.Kind == StoredConstraintKind.ForeignKey);

    /// <summary>
    /// The stored foreign key is the loaded one: by name, or when unnamed by its columns and referenced table and
    /// columns (a REFERENCES without columns means the referenced primary key, which the loaded key lists).
    /// </summary>
    public static bool Matches(TableForeignKeySpec key, StoredConstraint constraint) =>
        constraint.Kind == StoredConstraintKind.ForeignKey && (key.Name != null
            ? key.Name.Equals(constraint.Name, StringComparison.OrdinalIgnoreCase)
            : constraint.Name == null && constraint.Columns.SequenceEqual(key.Columns, StringComparer.OrdinalIgnoreCase)
              && key.ReferencedTable.Name.Equals(constraint.ReferencedTable, StringComparison.OrdinalIgnoreCase)
              && (constraint.ReferencedColumns.Count == 0
                  || constraint.ReferencedColumns.SequenceEqual(key.ReferencedColumns, StringComparer.OrdinalIgnoreCase)));

    /// <summary>
    /// Removes a column's [CONSTRAINT name] REFERENCES ... clause with its actions, MATCH and deferral, up to the
    /// column's next constraint. SET NULL, SET DEFAULT and NOT DEFERRABLE belong to the clause; NOT NULL does not.
    /// </summary>
    public static string RemoveColumnReference(string term)
    {
        var tokens = SqliteDefinitionSql.Tokens(term);
        var index = tokens.FindIndex(1, t => t.Depth == 0 && t.Is("REFERENCES"));
        if (index < 0) throw new InvalidOperationException("The REFERENCES clause of the column was not found in its stored definition.");
        var start = index >= 2 && tokens[index - 2].Is("CONSTRAINT") ? tokens[index - 2].Start : tokens[index].Start;
        var end = term.Length;
        for (var i = index + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Depth != 0) continue;
            var previous = tokens[i - 1];
            var boundary = token.Is("NOT") ? tokens.ElementAtOrDefault(i + 1)?.Is("NULL") == true
                : token.Is("NULL") || token.Is("DEFAULT") ? !previous.Is("SET") && !previous.Is("NOT")
                : ColumnConstraintKeywords.Any(token.Is) && !token.Is("NULL");
            if (!boundary) continue;
            end = token.Start;
            break;
        }
        return $"{term[..start].TrimEnd()} {term[end..].TrimStart()}".Trim();
    }

    /// <summary>The checks a table-level term, or a column term on its column, declares.</summary>
    public static IEnumerable<StoredConstraint> Checks(string term) =>
        Constraints(term).Where(c => c.Kind == StoredConstraintKind.Check);

    /// <summary>The stored check is the loaded one: by name, or when unnamed by its column and expression text.</summary>
    public static bool Matches(TableCheckSpec check, StoredConstraint constraint) =>
        constraint.Kind == StoredConstraintKind.Check && (check.Name != null
            ? check.Name.Equals(constraint.Name, StringComparison.OrdinalIgnoreCase)
            : constraint.Name == null && constraint.Expression == check.Expression
              && string.Equals(constraint.Column, check.Column, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes the column's [CONSTRAINT name] CHECK (...) clause whose expression is <paramref name="expression"/>.</summary>
    public static string RemoveColumnCheck(string term, string expression)
    {
        var tokens = SqliteDefinitionSql.Tokens(term);
        for (var i = 1; i < tokens.Count; i++)
        {
            if (tokens[i].Depth != 0 || !tokens[i].Is("CHECK") || tokens.ElementAtOrDefault(i + 1)?.Is("(") != true) continue;
            var open = tokens[i + 1];
            var close = tokens.First(t => t.Start > open.Start && t.Depth == open.Depth && t.Is(")"));
            if (term[open.End..close.Start].Trim() != expression) continue;
            var start = i >= 2 && tokens[i - 2].Is("CONSTRAINT") ? tokens[i - 2].Start : tokens[i].Start;
            return $"{term[..start].TrimEnd()} {term[close.End..].TrimStart()}".Trim();
        }
        throw new InvalidOperationException("The CHECK clause of the column was not found in its stored definition.");
    }

    private static StoredConstraintKind Stored(KeyKind kind) => kind == KeyKind.Primary ? StoredConstraintKind.PrimaryKey : StoredConstraintKind.Unique;
}
