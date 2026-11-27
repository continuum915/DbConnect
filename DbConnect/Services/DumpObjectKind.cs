using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// What a dump knows about each kind of object it writes besides tables: its title, the explorer folder that lists it,
/// the keyword its CREATE statement names, the existing reader of its definition, and whether its body holds statements
/// of its own or it can carry triggers.
/// </summary>
internal sealed record DumpObjectKind(SchemaNodeType Kind, SchemaNodeType? Folder, string Title, string CreateKeyword,
    Func<DatabaseSession, DatabaseObjectIdentity, Task<string?>> ReadAsync)
{
    /// <summary>Routines and triggers, whose bodies hold statements of their own.</summary>
    public bool IsCompound { get; private init; }

    /// <summary>Views, which can carry triggers that no table snapshot holds.</summary>
    public bool HasTriggers { get; private init; }

    /// <summary>Functions and procedures; a dump reads them before views, since views call functions far more often than routines name views.</summary>
    public bool IsRoutine { get; private init; }

    public static DumpObjectKind View { get; } = new(SchemaNodeType.View, SchemaNodeType.ViewFolder, "View", "VIEW", ReadView)
    { HasTriggers = true };

    public static DumpObjectKind MaterializedView { get; } = new(SchemaNodeType.MaterializedView, SchemaNodeType.MaterializedViewFolder,
        "Materialized view", @"MATERIALIZED\s+VIEW", ReadView)
    { HasTriggers = true };

    public static DumpObjectKind Function { get; } = new(SchemaNodeType.Function, SchemaNodeType.FunctionFolder, "Function",
        "(?:FUNCTION|MACRO)", (session, o) => session.GetFunctionDefinitionAsync(o.Schema ?? string.Empty, o.Name))
    { IsCompound = true, IsRoutine = true };

    public static DumpObjectKind Procedure { get; } = new(SchemaNodeType.Procedure, SchemaNodeType.ProcedureFolder, "Procedure",
        "(?:PROCEDURE|PROC)", (session, o) => session.GetProcedureDefinitionAsync(o.Schema ?? string.Empty, o.Name))
    { IsCompound = true, IsRoutine = true };

    /// <summary>A trigger on a view; the explorer lists them under their view, not in a folder.</summary>
    public static DumpObjectKind Trigger { get; } = new(SchemaNodeType.Trigger, null, "Trigger", "TRIGGER",
        (session, o) => session.GetTriggerDefinitionAsync(o.Schema ?? string.Empty, o.Name))
    { IsCompound = true };

    /// <summary>The kinds a dump collects from the catalog; triggers are found under the views.</summary>
    public static IReadOnlyList<DumpObjectKind> Listed { get; } = [View, MaterializedView, Function, Procedure];

    private static readonly Dictionary<SchemaNodeType, DumpObjectKind> ByKind =
        Listed.Append(Trigger).ToDictionary(k => k.Kind);

    /// <summary>The kind's facts, or null for a table or anything else a dump does not write as an object.</summary>
    public static DumpObjectKind? Of(SchemaNodeType kind) => ByKind.GetValueOrDefault(kind);

    /// <summary>The view reader takes schema.name where the engine has schemas, and the bare name elsewhere.</summary>
    private static Task<string?> ReadView(DatabaseSession session, DatabaseObjectIdentity view) =>
        session.GetViewDefinitionAsync(session.Capabilities.SupportsSchemas && view.Schema != null ? $"{view.Schema}.{view.Name}" : view.Name);
}
