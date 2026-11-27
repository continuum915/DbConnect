using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

/// <summary>A declared type as the designer edits it: the type name and its modifiers, kept apart.</summary>
public sealed record ColumnTypeParts(string Label, string? Length = null, int? Precision = null, int? Scale = null);

/// <summary>
/// Splits a declared type into its name and modifiers, composes them back, and checks edits
/// against the engine's <see cref="ColumnTypeRules"/>. A split is only accepted when composing
/// the parts gives back the declared text; anything else stays whole, so nothing is lost.
/// </summary>
public static partial class ColumnTypeText
{
    [GeneratedRegex(@"^\s*(?<head>[^()]+?)\s*\((?<args>[^()]*)\)(?<tail>[^()]*)$")]
    private static partial Regex TypeWithModifier();

    /// <summary>The parts of <paramref name="declared"/>, or the whole text as the name when the rules do not cover it.</summary>
    public static ColumnTypeParts Split(string declared, ColumnTypeRules rules)
    {
        var whole = new ColumnTypeParts(declared.Trim());
        var match = TypeWithModifier().Match(declared);
        if (!match.Success) return whole;
        var tail = match.Groups["tail"].Value.Trim();
        var label = match.Groups["head"].Value.Trim() + (tail.Length == 0 ? "" : " " + tail);
        var args = match.Groups["args"].Value.Split(',').Select(a => a.Trim()).ToArray();
        ColumnTypeParts? parts = rules.For(label)?.Modifier switch
        {
            ColumnTypeModifier.Length when args.Length == 1 && (Number(args[0]) != null || args[0].Equals("max", StringComparison.OrdinalIgnoreCase))
                => new(label, Length: args[0]),
            ColumnTypeModifier.PrecisionScale when args.Length is 1 or 2 && args.All(a => Number(a) != null)
                => new(label, Precision: Number(args[0]), Scale: args.Length == 2 ? Number(args[1]) : null),
            ColumnTypeModifier.Precision when args.Length == 1 && Number(args[0]) != null
                => new(label, Precision: Number(args[0])),
            _ => null
        };
        return parts != null && WithoutSpaces(Compose(parts)) == WithoutSpaces(declared) ? parts : whole;
    }

    public static ColumnTypeParts PartsOf(ColumnDefinition column) =>
        new(column.DataType ?? "", Blank(column.Length), column.Precision, column.Scale);

    /// <summary>The declared type of a designer column: its type name with its modifiers.</summary>
    public static string Compose(ColumnDefinition column) => Compose(PartsOf(column));

    public static string Compose(ColumnTypeParts parts)
    {
        var args = parts.Length ?? (parts.Precision is { } p ? parts.Scale is { } s ? $"{p},{s}" : $"{p}" : null);
        var label = parts.Label.Trim();
        if (args == null) return label;
        var at = ColumnTypeRules.TrailingWords
            .Select(w => label.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i > 0).DefaultIfEmpty(label.Length).Min();
        return $"{label[..at]}({args}){label[at..]}";
    }

    /// <summary>
    /// Applies a loaded column's declared type and collation to a designer column. The collation is
    /// kept as SQL text; readers that give a bare name (SQLite, DuckDB) are quoted with
    /// <paramref name="quote"/> when the name is not a plain identifier.
    /// </summary>
    public static void Apply(ColumnDefinition column, TableDesignColumn loaded, ColumnTypeRules rules, Func<string, string> quote, SqlBatchDialect dialect)
    {
        var parts = Split(loaded.DataType, rules);
        column.DataType = parts.Label;
        column.Length = parts.Length;
        column.Precision = parts.Precision;
        column.Scale = parts.Scale;
        column.Collation = loaded.Collation is { } collation && !IsCollationName(collation, dialect) ? quote(collation) : loaded.Collation;
    }

    /// <summary>True when the column's type or collation differs from <paramref name="original"/>.</summary>
    public static bool TypeEdited(ColumnDefinition column, ColumnDefinition original) =>
        Compose(column) != Compose(original) || Blank(column.Collation) != Blank(original.Collation);

    /// <summary>Why the column's modifiers or collation do not fit its type on this engine; empty when they fit.</summary>
    public static IEnumerable<string> Problems(ColumnDefinition column, ColumnTypeRules rules, SqlBatchDialect dialect)
    {
        var name = column.Name;
        var label = (column.DataType ?? "").Trim();
        var parts = PartsOf(column);
        var provider = rules.ProviderName;
        var hasModifier = parts.Length != null || parts.Precision != null || parts.Scale != null;
        var rule = label.Contains('(') ? null : rules.For(label);
        if (hasModifier)
        {
            if (label.Contains('('))
                yield return $"Column \"{name}\" gives modifiers both in its type and in the Length, Precision or Scale fields; use one or the other.";
            else if (rule == null)
                yield return $"Column \"{name}\": DbConnect does not know which modifiers {provider} type {label} takes; write the full type in the Type field instead.";
            else foreach (var problem in ModifierProblems(name, label, parts, rule, provider)) yield return problem;
        }
        else if (rule is { RequiresModifier: true })
            yield return $"Column \"{name}\": {provider} type {label} needs a length.";

        if (Blank(column.Collation) is not { } collation) yield break;
        if (!IsCollationName(collation, dialect))
            yield return $"The collation of column \"{name}\" must be a collation name.";
        else if (!rules.CollationOnAnyType && (rule ?? (label.Contains('(') ? rules.For(Split(label, rules).Label) : null)) is { TakesCollation: false })
            yield return $"Column \"{name}\": {provider} type {label} has no collation; only character types do.";
    }

    private static IEnumerable<string> ModifierProblems(string name, string label, ColumnTypeParts parts, ColumnTypeRule rule, string provider)
    {
        var range = $"between {rule.Min} and {rule.Max}";
        switch (rule.Modifier)
        {
            case ColumnTypeModifier.None:
                yield return $"Column \"{name}\": {provider} type {label} takes no length, precision or scale.";
                break;
            case ColumnTypeModifier.Length:
                if (parts.Precision != null || parts.Scale != null)
                    yield return $"Column \"{name}\": {provider} type {label} takes a length, not a precision or scale.";
                else if (parts.Length!.Equals("max", StringComparison.OrdinalIgnoreCase))
                {
                    if (!rule.AllowsMaxLength) yield return $"Column \"{name}\": {provider} type {label} cannot have length max.";
                }
                else if (Number(parts.Length) is not { } length || length < rule.Min || length > rule.Max)
                    yield return $"The length of column \"{name}\" must be a whole number {range}{(rule.AllowsMaxLength ? ", or max" : "")} for {provider} type {label}.";
                break;
            case ColumnTypeModifier.PrecisionScale:
                if (parts.Length != null)
                    yield return $"Column \"{name}\": {provider} type {label} takes a precision and scale, not a length.";
                else if (parts.Precision is not { } precision)
                    yield return $"Column \"{name}\" has a scale but no precision.";
                else if (precision < rule.Min || precision > rule.Max)
                    yield return $"The precision of column \"{name}\" must be {range} for {provider} type {label}.";
                else if (parts.Scale is { } scale && (scale < 0 || scale > Math.Min(precision, rule.MaxScale ?? precision)))
                    yield return $"The scale of column \"{name}\" must be between 0 and {Math.Min(precision, rule.MaxScale ?? precision)} " +
                                 $"(no more than the precision{(rule.MaxScale is { } max ? $" or {max}" : "")}).";
                break;
            case ColumnTypeModifier.Precision:
                if (parts.Length != null || parts.Scale != null)
                    yield return $"Column \"{name}\": {provider} type {label} takes a precision only.";
                else if (parts.Precision < rule.Min || parts.Precision > rule.Max)
                    yield return $"The precision of column \"{name}\" must be {range} for {provider} type {label}.";
                break;
        }
    }

    /// <summary>
    /// True when the declared type <paramref name="modified"/> keeps every value <paramref name="original"/> can hold:
    /// the same type with a longer length (not a padded fixed length), more digits on both sides of the point, or finer
    /// fractional seconds. Anything else may convert or reject existing values.
    /// </summary>
    public static bool Widens(string original, string modified, ColumnTypeRules rules)
    {
        var before = Split(original, rules);
        var after = Split(modified, rules);
        if (!before.Label.Equals(after.Label, StringComparison.OrdinalIgnoreCase) || rules.For(after.Label) is not { } rule) return false;
        return rule.Modifier switch
        {
            ColumnTypeModifier.Length => !rule.FixedLength && (after.Length?.Equals("max", StringComparison.OrdinalIgnoreCase) == true
                || (Number(before.Length) is { } from && Number(after.Length) is { } to && to >= from)),
            ColumnTypeModifier.PrecisionScale => before.Precision is { } p0 && after.Precision is { } p1
                && (after.Scale ?? 0) >= (before.Scale ?? 0) && p1 - (after.Scale ?? 0) >= p0 - (before.Scale ?? 0),
            ColumnTypeModifier.Precision => before.Precision is { } t0 && after.Precision is { } t1 && t1 >= t0,
            _ => false
        };
    }

    /// <summary>A collation name, optionally qualified, with no other syntax.</summary>
    private static bool IsCollationName(string text, SqlBatchDialect dialect)
    {
        var tokens = SqlTokenizer.Tokenize(text, dialect);
        if (tokens.Count == 0) return false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var ok = i % 2 == 0
                ? token.IsTerminated && token.Kind is SqlTokenKind.Word or SqlTokenKind.Quoted && !token.Text.StartsWith('\'')
                : token is { Kind: SqlTokenKind.Symbol, Text: "." };
            if (!ok) return false;
        }
        return tokens.Count % 2 == 1;
    }

    private static int? Number(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string WithoutSpaces(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
