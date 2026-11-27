using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Services.Builders;
using Token = DbConnect.Services.Builders.SqliteDefinitionSql.Token;

namespace DbConnect.Services;

internal enum StoredConstraintKind { PrimaryKey, Unique, Check, ForeignKey }

/// <summary>A key, check or foreign key as declared in stored CREATE TABLE text. <see cref="Column"/> is set when declared on a column.</summary>
internal sealed record StoredConstraint(StoredConstraintKind Kind, string? Name, string? Column)
{
    public IReadOnlyList<string> Columns { get; init; } = [];
    public string? Expression { get; init; }
    public string? ReferencedTable { get; init; }
    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];
    /// <summary>A foreign key's DEFERRABLE clause as declared.</summary>
    public string? Deferrable { get; init; }
}

internal sealed record StoredColumn(string Name, string Type)
{
    public string? Collation { get; init; }
    public string? GeneratedExpression { get; init; }
    /// <summary>True for STORED, false for VIRTUAL or no storage keyword.</summary>
    public bool GeneratedStored { get; init; }
    public bool Autoincrement { get; init; }
}

/// <summary>
/// A narrow reading of the stored CREATE TABLE text that SQLite and DuckDB keep. It splits the
/// top-level items inside the table's parentheses and recognises only column definitions and
/// CONSTRAINT / PRIMARY KEY / UNIQUE / CHECK / FOREIGN KEY clauses. Anything else is returned in
/// <see cref="Unrecognised"/> and never guessed at. This is not a general SQL parser.
/// </summary>
internal sealed class StoredTableText
{
    public List<StoredColumn> Columns { get; } = [];
    public List<StoredConstraint> Constraints { get; } = [];
    public List<string> Unrecognised { get; } = [];

    private static readonly string[] ColumnKeywords =
        ["CONSTRAINT", "PRIMARY", "NOT", "NULL", "UNIQUE", "CHECK", "DEFAULT", "COLLATE", "REFERENCES", "GENERATED", "AS"];

    public static StoredTableText Parse(string createTable)
    {
        var text = new StoredTableText();
        foreach (var term in SqliteDefinitionSql.BodyTerms(createTable).Where(t => t.Length > 0))
        {
            var tokens = SqliteDefinitionSql.Tokens(term);
            var first = tokens[0];
            var parsed = !first.Quoted && (first.Is("CONSTRAINT") || first.Is("PRIMARY") || first.Is("UNIQUE") || first.Is("CHECK") || first.Is("FOREIGN"))
                ? text.TableConstraint(term, tokens)
                : text.Column(term, tokens);
            if (!parsed) text.Unrecognised.Add(term);
        }
        return text;
    }

    private static int Close(List<Token> tokens, int open)
    {
        if (open >= tokens.Count || !tokens[open].Is("(")) return -1;
        for (var i = open + 1; i < tokens.Count; i++)
            if (tokens[i].Is(")") && tokens[i].Depth == tokens[open].Depth) return i;
        return -1;
    }

    private static string Inner(string term, List<Token> tokens, int open, int close) => term[tokens[open].End..tokens[close].Start].Trim();

    /// <summary>A parenthesised list of plain column names; null when any member is more than a name.</summary>
    private static List<string>? Names(List<Token> tokens, int open, int close)
    {
        var names = new List<string>();
        var item = new List<Token>();
        for (var i = open + 1; i <= close; i++)
        {
            if (i < close && !(tokens[i].Is(",") && tokens[i].Depth == tokens[open].Depth + 1))
            {
                item.Add(tokens[i]);
                continue;
            }
            if (item.Count == 1 || item.Count == 2 && item[1].Is("ASC")) names.Add(item[0].Identifier);
            else return null;
            item.Clear();
        }
        return names;
    }

    private static bool Keyword(List<Token> tokens, int at, params string[] words) =>
        at + words.Length <= tokens.Count && words.Select((w, i) => tokens[at + i].Is(w)).All(b => b);

    private bool TableConstraint(string term, List<Token> tokens)
    {
        var i = 0;
        string? name = null;
        if (tokens[0].Is("CONSTRAINT"))
        {
            if (tokens.Count < 3) return false;
            name = tokens[1].Identifier;
            i = 2;
        }
        StoredConstraint constraint;
        int end;
        if (Keyword(tokens, i, "PRIMARY", "KEY") || tokens[i].Is("UNIQUE"))
        {
            var open = i + (tokens[i].Is("UNIQUE") ? 1 : 2);
            end = Close(tokens, open);
            var columns = end < 0 ? null : Names(tokens, open, end);
            if (columns == null) return false;
            constraint = new(tokens[i].Is("UNIQUE") ? StoredConstraintKind.Unique : StoredConstraintKind.PrimaryKey, name, null) { Columns = columns };
            end++;
        }
        else if (tokens[i].Is("CHECK"))
        {
            end = Close(tokens, i + 1);
            if (end < 0) return false;
            constraint = new(StoredConstraintKind.Check, name, null) { Expression = Inner(term, tokens, i + 1, end) };
            end++;
        }
        else if (Keyword(tokens, i, "FOREIGN", "KEY"))
        {
            var close = Close(tokens, i + 2);
            var columns = close < 0 ? null : Names(tokens, i + 2, close);
            if (columns == null || !Keyword(tokens, close + 1, "REFERENCES")) return false;
            var reference = References(tokens, close + 1, name, null);
            if (reference == null) return false;
            (constraint, end) = (reference.Value.Constraint with { Columns = columns }, reference.Value.End);
        }
        else return false;
        if (end != tokens.Count) return false;
        Constraints.Add(constraint);
        return true;
    }

    /// <summary>
    /// REFERENCES [schema.]table [(columns)] followed by actions, MATCH and DEFERRABLE clauses. DuckDB stores a
    /// target in another named schema qualified; the readers match targets by name in the table's own schema,
    /// so the qualifier is read past and the last segment kept.
    /// </summary>
    private static (StoredConstraint Constraint, int End)? References(List<Token> tokens, int at, string? name, string? column)
    {
        if (at + 1 >= tokens.Count) return null;
        var table = tokens[at + 1].Identifier;
        var i = at + 2;
        while (i + 1 < tokens.Count && tokens[i].Text == "." && !tokens[i].Quoted && !tokens[i + 1].Is("("))
        {
            table = tokens[i + 1].Identifier;
            i += 2;
        }
        IReadOnlyList<string> referenced = [];
        string? deferrable = null;
        if (i < tokens.Count && tokens[i].Is("("))
        {
            var close = Close(tokens, i);
            var names = close < 0 ? null : Names(tokens, i, close);
            if (names == null) return null;
            referenced = names;
            i = close + 1;
        }
        while (i < tokens.Count)
        {
            if (Keyword(tokens, i, "ON") && i + 2 < tokens.Count && (tokens[i + 1].Is("DELETE") || tokens[i + 1].Is("UPDATE")))
            {
                i += 2;
                if (Keyword(tokens, i, "SET", "NULL") || Keyword(tokens, i, "SET", "DEFAULT") || Keyword(tokens, i, "NO", "ACTION")) i += 2;
                else if (tokens[i].Is("CASCADE") || tokens[i].Is("RESTRICT")) i++;
                else return null;
            }
            else if (tokens[i].Is("MATCH") && i + 1 < tokens.Count) i += 2;
            else if (Keyword(tokens, i, "NOT", "DEFERRABLE") || tokens[i].Is("DEFERRABLE"))
            {
                var start = i;
                i += tokens[i].Is("NOT") ? 2 : 1;
                if (Keyword(tokens, i, "INITIALLY", "DEFERRED") || Keyword(tokens, i, "INITIALLY", "IMMEDIATE")) i += 2;
                deferrable = string.Join(" ", tokens.Skip(start).Take(i - start).Select(t => t.Text.ToUpperInvariant()));
            }
            else break;
        }
        return (new StoredConstraint(StoredConstraintKind.ForeignKey, name, column)
        {
            Columns = column == null ? [] : [column],
            ReferencedTable = table,
            ReferencedColumns = referenced,
            Deferrable = deferrable
        }, i);
    }

    private bool Column(string term, List<Token> tokens)
    {
        var name = tokens[0].Identifier;
        var i = 1;
        while (i < tokens.Count && !(tokens[i].Depth == 0 && ColumnKeywords.Any(tokens[i].Is))) i++;
        var type = i > 1 ? term[tokens[1].Start..tokens[i - 1].End] : "";
        var column = new StoredColumn(name, type);
        var constraints = new List<StoredConstraint>();
        string? constraintName = null;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Is("CONSTRAINT") && i + 1 < tokens.Count)
            {
                constraintName = tokens[i + 1].Identifier;
                i += 2;
                continue;
            }
            if (Keyword(tokens, i, "PRIMARY", "KEY"))
            {
                i += 2;
                if (i < tokens.Count && (tokens[i].Is("ASC") || tokens[i].Is("DESC"))) return false;
                if (i < tokens.Count && tokens[i].Is("AUTOINCREMENT"))
                {
                    column = column with { Autoincrement = true };
                    i++;
                }
                constraints.Add(new(StoredConstraintKind.PrimaryKey, constraintName, name) { Columns = [name] });
            }
            else if (Keyword(tokens, i, "NOT", "NULL")) i += 2;
            else if (t.Is("NULL")) i++;
            else if (t.Is("UNIQUE"))
            {
                constraints.Add(new(StoredConstraintKind.Unique, constraintName, name) { Columns = [name] });
                i++;
            }
            else if (t.Is("CHECK"))
            {
                var close = Close(tokens, i + 1);
                if (close < 0) return false;
                constraints.Add(new(StoredConstraintKind.Check, constraintName, name) { Expression = Inner(term, tokens, i + 1, close) });
                i = close + 1;
            }
            else if (t.Is("DEFAULT"))
            {
                i++;
                if (i < tokens.Count && tokens[i].Is("(")) i = Close(tokens, i);
                else if (i < tokens.Count && (tokens[i].Is("-") || tokens[i].Is("+"))) i++;
                if (i < 0 || i >= tokens.Count) return false;
                i++;
            }
            else if (t.Is("COLLATE") && i + 1 < tokens.Count)
            {
                column = column with { Collation = tokens[i + 1].Identifier };
                i += 2;
            }
            else if (t.Is("REFERENCES"))
            {
                var reference = References(tokens, i, constraintName, name);
                if (reference == null) return false;
                constraints.Add(reference.Value.Constraint);
                i = reference.Value.End;
            }
            else if (Keyword(tokens, i, "GENERATED", "ALWAYS", "AS") || t.Is("AS"))
            {
                i += t.Is("AS") ? 1 : 3;
                var close = Close(tokens, i);
                if (close < 0) return false;
                var stored = close + 1 < tokens.Count && tokens[close + 1].Is("STORED");
                var storage = close + 1 < tokens.Count && (stored || tokens[close + 1].Is("VIRTUAL"));
                column = column with { GeneratedExpression = Inner(term, tokens, i, close), GeneratedStored = stored };
                i = close + (storage ? 2 : 1);
            }
            else return false;
            if (!t.Is("CONSTRAINT")) constraintName = null;
        }
        Columns.Add(column);
        Constraints.AddRange(constraints);
        return true;
    }
}
