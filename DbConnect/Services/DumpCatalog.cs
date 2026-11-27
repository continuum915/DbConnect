using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// The objects a dump covers, as the explorer lists them: tables, and for a whole database or some schemas also views,
/// materialized views, functions and procedures. Folders the explorer has not opened are loaded.
/// </summary>
internal sealed record DumpCatalog(IReadOnlyList<DatabaseObjectIdentity> Tables, IReadOnlyList<DatabaseObjectIdentity> Objects,
    IReadOnlyList<string> ListingFailures)
{
    /// <summary>Folder kinds walked, with the kind of object each lists.</summary>
    private static readonly Dictionary<SchemaNodeType, SchemaNodeType> Folders = DumpObjectKind.Listed
        .ToDictionary(k => k.Folder!.Value, k => k.Kind)
        .Append(new(SchemaNodeType.TableFolder, SchemaNodeType.Table)).ToDictionary();

    /// <summary>Whether the dump writes this kind of object besides tables.</summary>
    public static bool IsProgrammable(SchemaNodeType kind) => DumpObjectKind.Listed.Any(k => k.Kind == kind);

    /// <summary>Listed tables are dumped alone; otherwise every table and object in the database or schemas.</summary>
    public static async Task<DumpCatalog> ResolveAsync(DatabaseSession session, DumpSelection selection)
    {
        if (selection.Tables.Count > 0) return new(selection.Tables, [], []);
        var found = new List<DatabaseObjectIdentity>();
        var listingFailures = new List<string>();
        foreach (var root in await session.Objects.GetSchemaAsync())
            await CollectAsync(session, root, selection.Schemas, found, listingFailures);
        if (selection.Schemas.Count > 0)
            found.RemoveAll(o => !selection.Schemas.Contains(o.Schema ?? string.Empty, StringComparer.Ordinal));
        var tables = found.Where(o => ObjectScriptText.IsTableLike(o.Kind)).ToList();
        if (tables.Count == 0) throw new DatabaseDumpException("There are no tables to dump.");
        return new(tables, found.Where(o => IsProgrammable(o.Kind)).ToList(), listingFailures);
    }

    private static async Task CollectAsync(DatabaseSession session, SchemaNode node, IReadOnlyList<string> schemas,
        List<DatabaseObjectIdentity> found, List<string> listingFailures)
    {
        if (ObjectScriptText.IsTableLike(node.Type) || IsProgrammable(node.Type))
        {
            found.Add(node.Identity!);
            return;
        }
        if (node.Type == SchemaNodeType.Schema && schemas.Count > 0 && !schemas.Contains(node.RawName ?? node.Name, StringComparer.Ordinal))
            return;
        var isFolder = Folders.TryGetValue(node.Type, out var kind);
        if (!isFolder && node.Type is not (SchemaNodeType.Database or SchemaNodeType.Schema)) return;
        var children = node.Children.ToList();
        if (isFolder && !node.IsLoaded)
        {
            try
            {
                children = await session.Objects.GetChildrenAsync(node.Identity!.Parent!, kind);
            }
            // A table that cannot be listed fails the dump, as one that cannot be scripted does; other objects are stated.
            catch (Exception ex) when (ex is not OperationCanceledException && IsProgrammable(kind))
            {
                listingFailures.Add($"{node.Name} in {ObjectNames.SchemaQualified(node.Identity!.Parent!)} could not be listed: " +
                             DiagnosticRedactor.Redact(ex.Message));
                return;
            }
        }
        foreach (var child in children) await CollectAsync(session, child, schemas, found, listingFailures);
    }
}
