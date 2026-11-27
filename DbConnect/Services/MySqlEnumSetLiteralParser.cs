using System.Collections.Generic;
using System.Text;

namespace DbConnect.Services;

/// <summary>
/// RAIN-P2-09 — Parses MySQL's <c>COLUMN_TYPE</c> literal for an ENUM/SET column, e.g.
/// <c>enum('a','b','c')</c> or <c>set('x','y')</c>, into its member values. Members are
/// single-quoted and a literal quote inside a member is escaped by doubling it (<c>''</c>),
/// matching how MySQL itself renders <c>SHOW COLUMNS</c>/<c>information_schema</c> output.
/// A standalone class so the quoting/escaping logic can be unit-tested without a live server.
/// </summary>
public static class MySqlEnumSetLiteralParser
{
    public static List<string> Parse(string columnType)
    {
        var values = new List<string>();
        var openParen = columnType.IndexOf('(');
        var closeParen = columnType.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen)
            return values;

        var inner = columnType[(openParen + 1)..closeParen];
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (!inQuotes)
            {
                if (c == '\'')
                    inQuotes = true;
                continue;
            }

            if (c == '\'')
            {
                if (i + 1 < inner.Length && inner[i + 1] == '\'')
                {
                    current.Append('\'');
                    i++;
                    continue;
                }

                inQuotes = false;
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        return values;
    }
}
