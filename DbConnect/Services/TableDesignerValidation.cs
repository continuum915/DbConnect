using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

/// <summary>
/// Checks the designer's edits before any SQL runs: names, duplicates, references to columns,
/// and that expressions are single fragments. The fragment check only catches obvious mistakes
/// (a statement separator, a comment, unbalanced quotes or parentheses); the server remains the
/// judge of whether an expression is valid.
/// </summary>
internal static class TableDesignerValidation
{
    public static List<string> Validate(TableDesignerEdits e)
    {
        var errors = new List<string>();
        var activeCols = e.Columns.Where(c => !c.IsDropped).ToList();

        if (activeCols.Count == 0)
            errors.Add("At least one column is required.");

        if (string.IsNullOrWhiteSpace(e.TableName))
            errors.Add("Table name cannot be empty.");

        foreach (var d in Duplicates(activeCols.Select(c => c.Name)))
            errors.Add($"Duplicate column name: \"{d}\".");

        if (activeCols.Any(c => string.IsNullOrWhiteSpace(c.Name)))
            errors.Add("All columns must have a name.");

        var builder = DdlBuilderFactory.For(e.ProviderName);
        var typeRules = builder.TypeRules;
        foreach (var column in activeCols)
        {
            // Loaded types are checked only once edited: an existing column the rules would reject still saves unchanged.
            if (!string.IsNullOrWhiteSpace(column.DataType)
                && (column.IsNew || column.Original == null || ColumnTypeText.TypeEdited(column, column.Original)))
                errors.AddRange(ColumnTypeText.Problems(column, typeRules, e.Capabilities.BatchDialect));
            if (string.IsNullOrWhiteSpace(column.DataType))
                errors.Add($"Column \"{column.Name}\" must have a data type.");
            else if (FragmentProblem(column.DataType, e.Capabilities) is { } typeProblem)
                errors.Add($"The type of column \"{column.Name}\" {typeProblem}.");
            // Like types, a loaded default is checked only once edited: MySQL's catalog gives defaults unquoted (it's).
            if (!string.IsNullOrWhiteSpace(column.DefaultValue) && (column.IsNew || column.Original == null || column.DefaultValue != column.Original.DefaultValue)
                && FragmentProblem(column.DefaultValue, e.Capabilities) is { } problem)
                errors.Add($"The default of column \"{column.Name}\" {problem}.");
            // Like types, loaded identity and generation are checked only once edited (or when the type or key moves under them).
            if (column.IsNew || column.Original == null || ColumnGeneration.Edited(column, column.Original, builder.GenerationRules)
                || ColumnTypeText.TypeEdited(column, column.Original) || column.IsNullable != column.Original.IsNullable
                || column.DefaultValue != column.Original.DefaultValue || column.IsPrimaryKey != column.Original.IsPrimaryKey)
                errors.AddRange(ColumnGeneration.Problems(column, e, builder.GenerationRules));
            if (!string.IsNullOrWhiteSpace(column.GeneratedExpression)
                && FragmentProblem(column.GeneratedExpression, e.Capabilities) is { } generatedProblem)
                errors.Add($"The generation expression of column \"{column.Name}\" {generatedProblem}.");
        }

        errors.AddRange(TableKeyPlanning.Problems(e, builder.KeyRules));

        errors.AddRange(TableIndexValidation.Problems(e, builder.IndexRules));

        errors.AddRange(TableForeignKeyPlanning.Problems(e, builder.ForeignKeyRules));

        errors.AddRange(TableCheckPlanning.Problems(e));

        return errors;
    }

    // Names are compared case-insensitively: on case-sensitive engines this rejects names that differ only by case.
    private static IEnumerable<string> Duplicates(IEnumerable<string> names) =>
        names.Where(n => !string.IsNullOrWhiteSpace(n)).GroupBy(n => n.Trim().ToLowerInvariant())
            .Where(g => g.Count() > 1).Select(g => g.Key);

    /// <summary>Why <paramref name="fragment"/> is not one expression, or null when nothing obvious is wrong.</summary>
    internal static string? FragmentProblem(string fragment, DatabaseProviderCapabilities capabilities)
    {
        var depth = 0;
        foreach (var token in SqlTokenizer.Tokenize(fragment, capabilities.BatchDialect))
        {
            if (!token.IsTerminated) return "has an unterminated quote or comment";
            if (token.Kind == SqlTokenKind.Comment) return "contains a comment";
            if (token.Kind == SqlTokenKind.Directive) return "contains a batch separator";
            if (token.Kind != SqlTokenKind.Symbol) continue;
            if (token.Text == ";") return "contains a statement separator (;)";
            if (token.Text == "(") depth++;
            if (token.Text == ")" && --depth < 0) return "has an unmatched closing parenthesis";
        }
        return depth == 0 ? null : "has an unclosed parenthesis";
    }
}
