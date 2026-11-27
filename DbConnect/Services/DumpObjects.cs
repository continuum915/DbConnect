using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>A view, routine or view trigger as the provider's definition reader returns it.</summary>
internal sealed record DumpObject(DatabaseObjectIdentity Identity, string Definition)
{
    public string Title => DumpObjects.Title(Identity.Kind);

    private DumpObjectKind Kind => DumpObjectKind.Of(Identity.Kind)!;

    /// <summary>
    /// The definition terminated by the engine's script rules, as table parts are. Every object here is a module, whose
    /// batch an engine with batch separators keeps as its text, so the definition stays as it was written.
    /// </summary>
    public string Statement(SqlScriptRules rules) =>
        rules.Terminate(Definition, compound: Kind.IsCompound, asWritten: true);
}

/// <summary>
/// A dump's views, materialized views, functions, procedures and the triggers on its views, read with the providers'
/// existing definition readers and ordered so an object follows the objects its definition names. Table triggers are
/// part of the table snapshots and are not read again. An object whose definition cannot be read is listed in
/// <see cref="Exclusions"/> instead.
/// </summary>
internal sealed record DumpObjects(IReadOnlyList<DumpObject> ViewsAndRoutines, IReadOnlyList<DumpObject> Triggers,
    IReadOnlyList<string> Exclusions)
{
    public static DumpObjects None { get; } = new([], [], []);

    /// <summary>The schemas other than the default one that hold these objects; the dump creates no schemas.</summary>
    public IReadOnlyList<string> Prerequisites { get; init; } = [];

    /// <summary>Functions of which the readers could read only one of several overloads, as (schema, name).</summary>
    private IReadOnlySet<(string?, string)> Overloaded { get; init; } = new HashSet<(string?, string)>();

    public static string Title(SchemaNodeType kind) => DumpObjectKind.Of(kind)?.Title ?? kind.ToString();

    public static async Task<DumpObjects> ReadAsync(DatabaseSession session, DumpCatalog catalog, CancellationToken cancellationToken)
    {
        var read = new List<DumpObject>();
        var exclusions = new List<string>(catalog.ListingFailures);
        // The readers find a routine by name and return one of its overloads, which one is up to the engine.
        var groups = catalog.Objects.GroupBy(o => (o.Kind, o.Schema, o.Name)).ToList();
        var overloaded = new HashSet<(string?, string)>();
        foreach (var group in groups.OrderBy(g => DumpObjectKind.Of(g.Key.Kind)!.IsRoutine ? 0 : 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = group.First();
            await ReadOneAsync(session, identity, read, exclusions);
            if (group.Count() == 1) continue;
            overloaded.Add((identity.Schema, identity.Name));
            exclusions.Add($"{Title(identity.Kind)} {ObjectNames.SchemaQualified(identity)}: it has {group.Count()} overloads and only " +
                           "one of them can be read, so the others are not scripted.");
        }

        var triggers = new List<DumpObject>();
        if (session.Capabilities.SupportsTriggers)
            foreach (var view in read.Where(o => DumpObjectKind.Of(o.Identity.Kind)!.HasTriggers).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<SchemaNode> nodes;
                try
                {
                    nodes = await session.Objects.GetChildrenAsync(view.Identity, SchemaNodeType.Trigger);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    exclusions.Add($"Triggers on {Describe(view.Identity)} could not be listed: {DiagnosticRedactor.Redact(ex.Message)}");
                    continue;
                }
                foreach (var node in nodes) await ReadOneAsync(session, node.Identity!, triggers, exclusions);
            }
        var capabilities = session.Capabilities;
        return new(DumpObjectOrder.Sort(read), triggers, exclusions)
        {
            Overloaded = overloaded,
            Prerequisites = capabilities.SupportsSchemas
                ? read.Select(o => o.Identity.Schema).OfType<string>().Where(s => s != capabilities.DefaultSchema).Distinct()
                    .Select(s => $"Schema {capabilities.QuoteIdentifier(s)}").ToList()
                : []
        };
    }

    /// <summary>
    /// Whether the dump creates this function, such as a PostgreSQL trigger's; an overloaded name is never taken as
    /// created, since the overload the dump holds may be another.
    /// </summary>
    public bool CreatesFunction(DatabaseObjectIdentity function) =>
        !Overloaded.Contains((function.Schema, function.Name)) &&
        ViewsAndRoutines.Any(o => o.Identity.Kind == SchemaNodeType.Function && o.Identity.Name == function.Name &&
                                  o.Identity.Schema == function.Schema);

    private static async Task ReadOneAsync(DatabaseSession session, DatabaseObjectIdentity identity, List<DumpObject> read, List<string> exclusions)
    {
        string? definition;
        try
        {
            definition = await DumpObjectKind.Of(identity.Kind)!.ReadAsync(session, identity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            exclusions.Add($"{Describe(identity)}: its definition could not be read ({DiagnosticRedactor.Redact(ex.Message)}).");
            return;
        }
        if (definition != null && IsCreateStatement(definition, identity.Kind)) read.Add(new(identity, definition));
        else exclusions.Add($"{Describe(identity)}: its definition could not be read.");
    }

    private static readonly Regex LeadingComments = new(@"\A(?:\s+|--[^\n]*(?:\n|\z)|/\*.*?\*/)*", RegexOptions.Singleline);

    /// <summary>
    /// Whether a reader returned a statement that creates this kind of object. Readers return null when they cannot
    /// read a definition, but some return a related one, such as PostgreSQL's trigger function for a trigger.
    /// </summary>
    /// <summary>Whether the definition replaces an existing object of its name itself (CREATE OR REPLACE / CREATE OR ALTER).</summary>
    internal static bool ReplacesItself(string definition) =>
        Regex.IsMatch(definition[LeadingComments.Match(definition).Length..], @"\ACREATE\s+OR\s+(REPLACE|ALTER)", RegexOptions.IgnoreCase);

    internal static bool IsCreateStatement(string definition, SchemaNodeType kind)
    {
        var keyword = DumpObjectKind.Of(kind)!.CreateKeyword;
        var text = definition[LeadingComments.Match(definition).Length..];
        // Between CREATE and the keyword only modifiers may stand: OR REPLACE, ALGORITHM=…, DEFINER=…, SQL SECURITY …
        return Regex.IsMatch(text, $@"\ACREATE\b[^;(]*?\b{keyword}\b", RegexOptions.IgnoreCase);
    }

    private static string Describe(DatabaseObjectIdentity identity)
    {
        var name = ObjectNames.SchemaQualified(identity);
        if (identity.RoutineSignature != null) name += $"({identity.RoutineSignature})";
        return identity.Kind == SchemaNodeType.Trigger && identity.Parent != null
            ? $"Trigger {name} on {Title(identity.Parent.Kind).ToLowerInvariant()} {ObjectNames.SchemaQualified(identity.Parent)}"
            : $"{Title(identity.Kind)} {name}";
    }
}
