using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// The table data filter bar's SQL mode: renders the Builder's rules as WHERE text with literal values (the same
/// predicates <see cref="TableQuerySql"/> sends with parameters), and maps ORDER BY text to the table's sort keys so
/// it is sent with the provider's own identifier quoting.
/// </summary>
public static class TableFilterSql
{
    /// <summary>The rules and raw WHERE fragment as one WHERE condition (without the WHERE keyword).</summary>
    public static string ToWhere(DatabaseProviderCapabilities capabilities, IReadOnlyList<TableFilter> filters, string? rawWhere)
    {
        var predicates = new List<string>();
        if (!string.IsNullOrWhiteSpace(rawWhere))
            predicates.Add(filters.Count == 0 ? rawWhere.Trim() : $"({rawWhere.Trim()})");

        foreach (var filter in filters)
        {
            var column = capabilities.QuoteIdentifier(filter.Column);
            if (!filter.NeedsValue)
            {
                predicates.Add($"{column} {(filter.Operator == TableFilterOperator.IsNull ? "IS NULL" : "IS NOT NULL")}");
                continue;
            }

            var value = filter.Value ?? string.Empty;
            if (filter.Operator is TableFilterOperator.Contains or TableFilterOperator.StartsWith or TableFilterOperator.EndsWith)
            {
                var escaped = value.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
                var pattern = filter.Operator switch
                {
                    TableFilterOperator.StartsWith => escaped + "%",
                    TableFilterOperator.EndsWith => "%" + escaped,
                    _ => "%" + escaped + "%",
                };
                predicates.Add($"CAST({column} AS {capabilities.TextCastType}) {capabilities.LikeOperator} {SqlLiteral.Format(pattern, capabilities)} ESCAPE '!'");
            }
            else
            {
                predicates.Add($"{column} {Comparison(filter.Operator)} {SqlLiteral.Format(filter.ParameterValue ?? value, capabilities)}");
            }
        }

        return string.Join(" AND ", predicates);
    }

    /// <summary>The sort keys as ORDER BY text (without the keyword), using the provider's quoting.</summary>
    public static string ToOrderBy(DatabaseProviderCapabilities capabilities, IReadOnlyList<TableSortKey> keys) =>
        string.Join(", ", keys.Select(k => capabilities.QuoteIdentifier(k.Column) + (k.Descending ? " DESC" : string.Empty)));

    /// <summary>
    /// Parses ORDER BY text into sort keys: a comma-separated list of the table's columns (quoted or not), each
    /// optionally followed by ASC or DESC. Returns the reason when a part is not one of <paramref name="columns"/>.
    /// </summary>
    public static (IReadOnlyList<TableSortKey> Keys, string? Error) ParseOrderBy(string? text, IReadOnlyList<string> columns)
    {
        var keys = new List<TableSortKey>();
        if (string.IsNullOrWhiteSpace(text)) return (keys, null);

        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0) return (keys, "ORDER BY has an empty column between commas.");
            var words = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var descending = false;
            var nameWords = words;
            if (words.Length > 1 && words[^1].Equals("DESC", StringComparison.OrdinalIgnoreCase))
            {
                descending = true;
                nameWords = words[..^1];
            }
            else if (words.Length > 1 && words[^1].Equals("ASC", StringComparison.OrdinalIgnoreCase))
            {
                nameWords = words[..^1];
            }

            var name = Unquote(string.Join(' ', nameWords));
            var column = columns.FirstOrDefault(c => c.Equals(name, StringComparison.Ordinal))
                ?? columns.FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (column == null)
                return (keys, $"ORDER BY can only list this table's columns; \"{name}\" is not one of them.");
            keys.Add(new TableSortKey(column, descending));
        }

        return (keys, null);
    }

    private static string Unquote(string name) =>
        name.Length >= 2 && (name[0], name[^1]) is ('"', '"') or ('`', '`') or ('[', ']') ? name[1..^1] : name;

    private static string Comparison(TableFilterOperator op) => op switch
    {
        TableFilterOperator.Equals => "=",
        TableFilterOperator.NotEquals => "<>",
        TableFilterOperator.GreaterThan => ">",
        TableFilterOperator.GreaterOrEqual => ">=",
        TableFilterOperator.LessThan => "<",
        TableFilterOperator.LessOrEqual => "<=",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };
}
