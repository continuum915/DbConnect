using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Generates the text of explorer scripts. Generation only reads metadata; it never runs
/// the script. DML templates use named parameters so the query tab prompts for values,
/// and UPDATE/DELETE without a primary key keep a non-executable search-condition marker.
/// </summary>
public static class ObjectScriptText
{
    public const string SearchConditionMarker = "<search_condition>";

    public static bool IsTableLike(SchemaNodeType type) =>
        type is SchemaNodeType.Table or SchemaNodeType.VirtualTable;

    /// <summary>Which script kinds apply to an explorer node.</summary>
    public static IReadOnlyList<ScriptKind> KindsFor(SchemaNodeType type) => type switch
    {
        SchemaNodeType.Table or SchemaNodeType.VirtualTable =>
            [ScriptKind.Create, ScriptKind.Drop, ScriptKind.Select, ScriptKind.Insert, ScriptKind.Update, ScriptKind.Delete],
        SchemaNodeType.View or SchemaNodeType.MaterializedView => [ScriptKind.Create, ScriptKind.Drop, ScriptKind.Select],
        SchemaNodeType.Function or SchemaNodeType.Procedure or SchemaNodeType.Trigger => [ScriptKind.Create],
        _ => []
    };

    public static async Task<TableScriptResult> GenerateAsync(DatabaseSession session, SchemaNode node,
        DatabaseObjectIdentity identity, ScriptKind kind)
    {
        try
        {
            return kind switch
            {
                ScriptKind.Create when IsTableLike(node.Type) => await TableScripting.ScriptCreateAsync(session, identity),
                ScriptKind.Create => TableScriptResult.Ok(await CreateOtherAsync(session, node, identity)),
                ScriptKind.Drop => TableScriptResult.Ok(Drop(session, node, identity)),
                ScriptKind.Select => TableScriptResult.Ok(await SelectAsync(session, identity)),
                _ => TableScriptResult.Ok(Dml(session, identity, kind, await session.Objects.GetColumnsAsync(identity)))
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TableScriptResult.Fail($"Could not script {identity.Name}: {DiagnosticRedactor.Redact(ex.Message)}");
        }
    }

    private static async Task<string> CreateOtherAsync(DatabaseSession session, SchemaNode node, DatabaseObjectIdentity identity)
    {
        var schema = node.SchemaPath ?? node.SchemaName ?? string.Empty;
        var name = node.RawName ?? node.Name;
        var quoted = ObjectNames.Quote(session.Capabilities, identity);
        switch (node.Type)
        {
            case SchemaNodeType.Function:
                return await session.GetFunctionDefinitionAsync(schema, name)
                       ?? $"-- Could not retrieve definition for function {quoted}.\n-- Connect and retry, or check permissions.";
            case SchemaNodeType.Procedure:
                return await session.GetProcedureDefinitionAsync(schema, name)
                       ?? $"-- Could not retrieve definition for procedure {quoted}.\n-- Connect and retry, or check permissions.";
            case SchemaNodeType.Trigger:
                return await session.GetTriggerDefinitionAsync(schema, name)
                       ?? $"-- Could not retrieve definition for trigger {quoted}.";
            default:
                // Views, materialized views and sequences: providers return verbatim DDL.
                var definition = await session.GetViewDefinitionAsync(name);
                if (definition != null) return definition;
                var quotedNew = DdlGenerator.Quote($"new_{name}", session.ProviderName);
                return $"-- Could not retrieve definition for {node.Type} {DdlGenerator.Quote(name, session.ProviderName)}.\n"
                       + "-- Edit the body below before executing.\n"
                       + $"CREATE VIEW {quotedNew} AS\nSELECT * FROM {DdlGenerator.Quote(name, session.ProviderName)};";
        }
    }

    private static string Drop(DatabaseSession session, SchemaNode node, DatabaseObjectIdentity identity)
    {
        var objectType = node.Type switch
        {
            SchemaNodeType.View => "VIEW",
            SchemaNodeType.MaterializedView => "MATERIALIZED VIEW",
            _ => "TABLE"
        };
        return $"DROP {objectType} {ObjectNames.Quote(session.Capabilities, identity)};";
    }

    private static async Task<string> SelectAsync(DatabaseSession session, DatabaseObjectIdentity identity)
    {
        var columns = await session.Objects.GetColumnsAsync(identity);
        var list = columns.Count == 0 ? "*" : string.Join(",\n       ", columns.Select(c => session.Capabilities.QuoteIdentifier(c.Name)));
        return $"SELECT {list}\nFROM {ObjectNames.Quote(session.Capabilities, identity)};";
    }

    internal static string Dml(DatabaseSession session, DatabaseObjectIdentity identity, ScriptKind kind, IList<ColumnDefinition> columns)
    {
        if (columns.Count == 0) throw new InvalidOperationException($"{identity.Name} has no readable columns.");
        var capabilities = session.Capabilities;
        var table = ObjectNames.Quote(capabilities, identity);
        var parameters = ParameterNames(capabilities, columns);
        string Q(ColumnDefinition c) => capabilities.QuoteIdentifier(c.Name);
        string Assign(ColumnDefinition c) => $"{Q(c)} = {parameters[c]}";
        var keys = columns.Where(c => c.IsPrimaryKey).ToList();
        var where = keys.Count > 0 ? string.Join("\n  AND ", keys.Select(Assign)) : SearchConditionMarker;
        var builder = new StringBuilder();
        switch (kind)
        {
            case ScriptKind.Insert:
                var insertable = columns.Where(c => !c.IsIdentity).ToList();
                builder.Append($"INSERT INTO {table} (\n    {string.Join(",\n    ", insertable.Select(Q))}\n)\n");
                builder.Append($"VALUES (\n    {string.Join(",\n    ", insertable.Select(c => parameters[c]))}\n);");
                break;
            case ScriptKind.Update:
                var settable = columns.Where(c => !c.IsPrimaryKey && !c.IsIdentity).ToList();
                if (settable.Count == 0) settable = columns.ToList();
                builder.Append($"UPDATE {table}\nSET {string.Join(",\n    ", settable.Select(Assign))}\nWHERE {where};");
                break;
            case ScriptKind.Delete:
                builder.Append($"DELETE FROM {table}\nWHERE {where};");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return builder.ToString();
    }

    /// <summary>One named parameter per column; positional-only providers get ? markers.</summary>
    private static Dictionary<ColumnDefinition, string> ParameterNames(DatabaseProviderCapabilities capabilities,
        IList<ColumnDefinition> columns)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<ColumnDefinition, string>();
        foreach (var column in columns)
        {
            if (capabilities.UsesPositionalParameters)
            {
                names[column] = "?";
                continue;
            }
            var stem = new string(column.Name.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
            if (stem.Length == 0 || char.IsDigit(stem[0])) stem = "p_" + stem;
            var name = stem;
            for (var i = 2; !used.Add(name); i++) name = $"{stem}_{i}";
            names[column] = capabilities.ParameterPrefix + name;
        }
        return names;
    }
}
