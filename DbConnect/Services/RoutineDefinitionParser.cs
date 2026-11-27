using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DbConnect.Models;

namespace DbConnect.Services;

internal static class RoutineDefinitionParser
{
    public static List<ProcedureParameter> ParseParameters(string routineDefinition)
    {
        if (string.IsNullOrWhiteSpace(routineDefinition))
            return [];

        var signature = ExtractParameterSignature(routineDefinition);
        if (string.IsNullOrWhiteSpace(signature))
            return [];

        var parameters = new List<ProcedureParameter>();
        var parts = SplitTopLevel(signature!, ',');
        for (var i = 0; i < parts.Count; i++)
        {
            var parameter = ParseParameter(parts[i], i);
            if (parameter != null)
                parameters.Add(parameter);
        }

        return parameters;
    }

    public static string GetDefaultTemplate(string name) =>
        $"-- Procedure template\nCREATE PROCEDURE {name}()\nBEGIN\n  -- TODO\nEND;";

    public static (string Schema, string Name) SplitQualifiedName(string routineName)
    {
        if (string.IsNullOrWhiteSpace(routineName))
            return (string.Empty, string.Empty);

        var trimmed = routineName.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot <= 0 || lastDot >= trimmed.Length - 1)
            return (string.Empty, trimmed);

        return (trimmed[..lastDot].Trim(), trimmed[(lastDot + 1)..].Trim());
    }

    public static SchemaNodeType? ExtractRoutineType(string routineDefinition)
    {
        if (string.IsNullOrWhiteSpace(routineDefinition))
            return null;

        var definition = routineDefinition.Trim();
        var createIndex = definition.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase);
        if (createIndex < 0)
            return null;

        var functionIndex = definition.IndexOf("FUNCTION", createIndex, StringComparison.OrdinalIgnoreCase);
        var procedureIndex = definition.IndexOf("PROCEDURE", createIndex, StringComparison.OrdinalIgnoreCase);
        if (functionIndex >= 0 && (procedureIndex < 0 || functionIndex < procedureIndex))
            return SchemaNodeType.Function;
        if (procedureIndex >= 0)
            return SchemaNodeType.Procedure;
        return null;
    }

    public static string? ExtractQualifiedRoutineName(string routineDefinition)
    {
        if (string.IsNullOrWhiteSpace(routineDefinition))
            return null;

        var definition = routineDefinition.Trim();
        var createIndex = definition.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase);
        if (createIndex < 0)
            return null;

        var functionIndex = definition.IndexOf("FUNCTION", createIndex, StringComparison.OrdinalIgnoreCase);
        var procedureIndex = definition.IndexOf("PROCEDURE", createIndex, StringComparison.OrdinalIgnoreCase);
        var keywordIndex = functionIndex >= 0 && (procedureIndex < 0 || functionIndex < procedureIndex)
            ? functionIndex
            : procedureIndex;
        if (keywordIndex < 0)
            return null;

        var keywordLength = definition.AsSpan(keywordIndex).StartsWith("FUNCTION", StringComparison.OrdinalIgnoreCase) ? 8 : 9;
        var afterKeyword = definition[(keywordIndex + keywordLength)..].TrimStart();
        var openParenIndex = afterKeyword.IndexOf('(');
        if (openParenIndex <= 0)
            return null;

        return afterKeyword[..openParenIndex].Trim();
    }

    private static string? ExtractParameterSignature(string routineDefinition)
    {
        var qualifiedName = ExtractQualifiedRoutineName(routineDefinition);
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return null;

        var nameIndex = routineDefinition.IndexOf(qualifiedName, StringComparison.Ordinal);
        if (nameIndex < 0)
            return null;

        var openParenIndex = routineDefinition.IndexOf('(', nameIndex + qualifiedName.Length);
        if (openParenIndex < 0)
            return null;

        var closeParenIndex = FindMatchingParen(routineDefinition, openParenIndex);
        if (closeParenIndex <= openParenIndex)
            return null;

        return routineDefinition[(openParenIndex + 1)..closeParenIndex];
    }

    private static int FindMatchingParen(string text, int openParenIndex)
    {
        var depth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inBacktick = false;

        for (var i = openParenIndex; i < text.Length; i++)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (!inDoubleQuote && !inBacktick && ch == '\'' && next == '\'')
            {
                i++;
                continue;
            }

            if (!inSingleQuote && !inBacktick && ch == '"')
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && ch == '`')
            {
                inBacktick = !inBacktick;
                continue;
            }

            if (!inDoubleQuote && !inBacktick && ch == '\'')
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (inSingleQuote || inDoubleQuote || inBacktick)
                continue;

            if (ch == '(')
                depth++;
            else if (ch == ')')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static ProcedureParameter? ParseParameter(string rawParameter, int ordinal)
    {
        var text = rawParameter.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var tokens = SplitTokens(text);
        if (tokens.Count == 0)
            return null;

        var direction = ParameterDirection.In;
        if (tokens[0].Equals("IN", StringComparison.OrdinalIgnoreCase))
        {
            direction = ParameterDirection.In;
            tokens.RemoveAt(0);
        }
        else if (tokens[0].Equals("OUT", StringComparison.OrdinalIgnoreCase))
        {
            direction = ParameterDirection.Out;
            tokens.RemoveAt(0);
        }
        else if (tokens[0].Equals("INOUT", StringComparison.OrdinalIgnoreCase))
        {
            direction = ParameterDirection.InOut;
            tokens.RemoveAt(0);
        }

        if (tokens.Count == 0)
            return null;

        var defaultIndex = tokens.FindIndex(token => token.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase) || token == "=");
        string? defaultValue = null;
        if (defaultIndex >= 0)
        {
            defaultValue = string.Join(" ", tokens.Skip(defaultIndex + 1)).Trim();
            tokens = tokens.Take(defaultIndex).ToList();
        }

        string name;
        string dataType;
        if (tokens.Count == 1)
        {
            name = $"arg{ordinal + 1}";
            dataType = tokens[0];
        }
        else
        {
            name = tokens[0];
            dataType = string.Join(" ", tokens.Skip(1)).Trim();
        }

        return new ProcedureParameter
        {
            Name = CleanIdentifier(name),
            DataType = dataType,
            Direction = direction,
            DefaultValue = string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue
        };
    }

    private static List<string> SplitTokens(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inBacktick = false;

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            var next = i + 1 < value.Length ? value[i + 1] : '\0';

            if (!inDoubleQuote && !inBacktick && ch == '\'' && next == '\'')
            {
                current.Append(ch).Append(next);
                i++;
                continue;
            }

            if (!inSingleQuote && !inBacktick && ch == '"')
            {
                inDoubleQuote = !inDoubleQuote;
                current.Append(ch);
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && ch == '`')
            {
                inBacktick = !inBacktick;
                current.Append(ch);
                continue;
            }

            if (!inDoubleQuote && !inBacktick && ch == '\'')
            {
                inSingleQuote = !inSingleQuote;
                current.Append(ch);
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && !inBacktick)
            {
                if (ch == '(')
                    depth++;
                else if (ch == ')' && depth > 0)
                    depth--;

                if (depth == 0 && char.IsWhiteSpace(ch))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inBacktick = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (!inDoubleQuote && !inBacktick && ch == '\'' && next == '\'')
            {
                current.Append(ch).Append(next);
                i++;
                continue;
            }

            if (!inSingleQuote && !inBacktick && ch == '"')
            {
                inDoubleQuote = !inDoubleQuote;
                current.Append(ch);
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && ch == '`')
            {
                inBacktick = !inBacktick;
                current.Append(ch);
                continue;
            }

            if (!inDoubleQuote && !inBacktick && ch == '\'')
            {
                inSingleQuote = !inSingleQuote;
                current.Append(ch);
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && !inBacktick)
            {
                if (ch == '(')
                    depth++;
                else if (ch == ')' && depth > 0)
                    depth--;
                else if (ch == separator && depth == 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    continue;
                }
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            parts.Add(current.ToString());

        return parts;
    }

    private static string CleanIdentifier(string value)
    {
        var trimmed = value.Trim();
        if ((trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            || (trimmed.StartsWith('`') && trimmed.EndsWith('`')))
            return trimmed[1..^1];

        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            return trimmed[1..^1];

        return trimmed;
    }
}
