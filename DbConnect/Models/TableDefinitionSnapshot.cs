using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DbConnect.Models;

/// <summary>Order in which the parts of a table definition must be executed.</summary>
public enum TableDefinitionPartKind
{
    /// <summary>Objects the table owns that must exist before it, such as serial sequences.</summary>
    Sequence,
    Table,
    Constraint,
    Index,
    /// <summary>ALTER statements for table settings that CREATE TABLE cannot express.</summary>
    Option,
    Comment,
    Trigger
}

/// <summary>One executable statement of a table definition, as the provider's own DDL.</summary>
public sealed record TableDefinitionPart(TableDefinitionPartKind Kind, string Name, string Sql);

/// <summary>
/// A complete, read-only definition of one table and the objects it owns. It is read
/// from authoritative provider metadata and is never partial: a provider that cannot
/// read part of it throws <see cref="TableDefinitionUnavailableException"/> instead.
/// </summary>
public sealed record TableDefinitionSnapshot(
    DatabaseObjectIdentity Table,
    string ProviderName,
    IReadOnlyList<TableDefinitionPart> Parts)
{
    /// <summary>Objects that must already exist; they are not scripted.</summary>
    public IReadOnlyList<TablePrerequisite> Prerequisites { get; init; } = [];

    /// <summary>Behavior the script does not reproduce, stated so it cannot pass as complete.</summary>
    public IReadOnlyList<string> Exclusions { get; init; } = [];

    /// <summary>How the engine's clients split the script: GO after every statement on SQL Server, DELIMITER around MySQL triggers.</summary>
    public SqlScriptRules ScriptRules { get; init; } = SqlScriptRules.Standard;

    public IEnumerable<TableDefinitionPart> Ordered => Parts
        .Select((part, index) => (part, index))
        .OrderBy(p => p.part.Kind).ThenBy(p => p.index)
        .Select(p => p.part);

    public string ToScript()
    {
        var name = string.Join(".", new[] { Table.Catalog, Table.Schema, Table.Name }.Where(s => s is not null));
        var builder = new StringBuilder();
        builder.Append("-- ").Append(ProviderName).Append(" table ").AppendLine(name);
        AppendList(builder, "Prerequisites (must already exist; not scripted)", Prerequisites.Select(p => p.Text).ToList());
        AppendList(builder, "Not reproduced by this script", Exclusions);
        builder.AppendLine();
        builder.Append(OrderedStatements());
        builder.AppendLine();
        return builder.ToString();
    }

    /// <summary>
    /// The parts in execution order as statements, terminated and separated as <see cref="ToScript"/> writes them,
    /// without its header; only the parts <paramref name="include"/> accepts, when given.
    /// </summary>
    public string OrderedStatements(Func<TableDefinitionPart, bool>? include = null) =>
        string.Join(Environment.NewLine + Environment.NewLine, Ordered.Where(include ?? (_ => true)).Select(Terminate));

    private string Terminate(TableDefinitionPart part) =>
        ScriptRules.Terminate(part.Sql, compound: part.Kind == TableDefinitionPartKind.Trigger, newLine: Environment.NewLine);

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        builder.Append("-- ").Append(title).AppendLine(":");
        foreach (var item in items) builder.Append("--   ").AppendLine(item.Replace("\n", " ").Replace("\r", ""));
    }
}

/// <summary>The definition could not be read completely; no partial script is produced.</summary>
public sealed class TableDefinitionUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
