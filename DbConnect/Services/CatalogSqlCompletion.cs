using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Builds a disposable completion projection from the same scopes used by explorer.</summary>
public static class CatalogSqlCompletion
{
    public static async Task<IReadOnlyList<SqlCompletionCandidate>> GetCandidatesAsync(string text, int caret, DatabaseSession session)
    {
        var qualifier = SqlCompletionNames.Qualifier(text, caret);
        var discoveryComplete = true;
        var records = new List<MetadataRecord>();
        var scopes = new Dictionary<MetadataScope, MetadataSnapshot>();
        var roots = await Load(MetadataScope.Root);
        if (qualifier is { Length: 1 })
        {
            var targets = SqlAliasScope.AliasesAt(text, caret, preserveQuotes: true)
                .Where(alias => session.Objects.MatchesSqlName(session.Objects.SqlName(alias.Key), qualifier[0]))
                .Select(alias => alias.Value).ToList();
            if (targets.Count == 1) qualifier = SqlCompletionNames.Parse(targets[0]);
            else if (targets.Count > 1) qualifier = [];
        }
        async Task Visit(MetadataRecord record)
        {
            records.Add(record);
            var kind = record.Identity.Kind;
            var childKind = kind switch
            {
                SchemaNodeType.TableFolder => SchemaNodeType.Table,
                SchemaNodeType.ViewFolder => SchemaNodeType.View,
                SchemaNodeType.MaterializedViewFolder => SchemaNodeType.MaterializedView,
                SchemaNodeType.FunctionFolder => SchemaNodeType.Function,
                _ => (SchemaNodeType?)null
            };
            if (childKind != null)
            {
                foreach (var child in await Load(new(record.Identity.Parent!, childKind.Value))) await Visit(child);
            }
            else if (IsRelation(kind) && !SqlCompletionEngine.ExpectsRelation(text[..caret]) &&
                (qualifier == null || MatchesRelation(record.Identity, qualifier, session)))
            {
                foreach (var child in await Load(new(record.Identity, SchemaNodeType.Column))) records.Add(child);
            }
            else if (kind is SchemaNodeType.Database or SchemaNodeType.Schema)
            {
                var scope = new MetadataScope(record.Identity, kind);
                var snapshot = session.Catalog.GetSnapshot(scope);
                scopes[scope] = snapshot;
                var children = snapshot.State != MetadataLoadState.NotLoaded || snapshot.Revision > 0
                    ? await Load(scope) : record.Children;
                if (!scopes[scope].HasSnapshot) children = record.Children;
                foreach (var child in children) await Visit(child);
            }
        }
        async Task<IEnumerable<MetadataRecord>> Load(MetadataScope scope)
        {
            await session.Catalog.LoadAsync(scope);
            var snapshot = session.Catalog.GetSnapshot(scope);
            scopes[scope] = snapshot;
            if ((scope == MetadataScope.Root || scope.Kind == SchemaNodeType.Schema || IsRelation(scope.Kind)) &&
                snapshot.State is not (MetadataLoadState.Loaded or MetadataLoadState.LoadedEmpty)) discoveryComplete = false;
            return snapshot.HasSnapshot ? snapshot.Records : [];
        }
        foreach (var root in roots) await Visit(root);
        if (session.Catalog.IsDisposed || scopes.Any(pair => session.Catalog.GetSnapshot(pair.Key) != pair.Value)) return [];
        if (qualifier != null) return Qualified(qualifier, records, session, discoveryComplete);
        var index = new SchemaIndex(caseSensitive: true) { Provider = session.ProviderName };
        index.Functions.UnionWith(session.Capabilities.BuiltInFunctions);
        var insertions = new Dictionary<(string Text, CompletionKind Kind), string>();
        foreach (var record in records)
        {
            var identity = record.Identity;
            if (IsRelation(identity.Kind))
            {
                var duplicate = !discoveryComplete || records.Count(r => IsRelation(r.Identity.Kind) && session.Objects.MatchesSqlName(r.Identity.Name,
                    session.Capabilities.QuoteIdentifier(identity.Name))) > 1;
                var name = Render(identity.Name, session);
                var owner = identity.Schema ?? identity.Catalog;
                var display = duplicate && owner != null ? Render(owner, session) + "." + name : name;
                index.Tables.Add(display);
                insertions[(display, CompletionKind.Table)] = duplicate && owner != null
                    ? session.Capabilities.QuoteIdentifier(owner) + "." + session.Capabilities.QuoteIdentifier(identity.Name)
                    : session.Capabilities.QuoteIdentifier(identity.Name);
            }
            if (identity.Kind == SchemaNodeType.Column)
            {
                index.AllColumns.Add(Render(identity.Name, session));
                insertions[(Render(identity.Name, session), CompletionKind.Column)] = session.Capabilities.QuoteIdentifier(identity.Name);
            }
            if (identity.Kind == SchemaNodeType.Function)
            {
                index.Functions.Add(Render(identity.Name, session));
                insertions[(Render(identity.Name, session), CompletionKind.Function)] = session.Capabilities.QuoteIdentifier(identity.Name);
            }
            if (identity.Kind == SchemaNodeType.Schema)
            {
                index.SchemaObjects.TryAdd(Render(identity.Name, session), new());
                insertions[(Render(identity.Name, session), CompletionKind.Schema)] = session.Capabilities.QuoteIdentifier(identity.Name);
            }
        }
        return SqlCompletionEngine.GetCandidates(text, caret, index).Select(candidate =>
            insertions.TryGetValue((candidate.Text, candidate.Kind), out var insertion)
                ? candidate with { InsertionText = insertion } : candidate).ToList();
    }

    private static IReadOnlyList<SqlCompletionCandidate> Qualified(string[] qualifier,
        List<MetadataRecord> records, DatabaseSession session, bool discoveryComplete)
    {
        bool Match(string? stored, string token) => stored != null && session.Objects.MatchesSqlName(stored, token);
        var relations = records.Where(r => IsRelation(r.Identity.Kind) && MatchesRelation(r.Identity, qualifier, session)).ToList();
        var result = new List<SqlCompletionCandidate>();
        // Without search-path context a bare duplicate is unresolved, never a union of its columns.
        if (relations.Count == 1 && (qualifier.Length > 1 || discoveryComplete))
            result.AddRange(records.Where(r => r.Identity.Kind == SchemaNodeType.Column && r.Identity.Parent == relations[0].Identity)
                .Select(r => new SqlCompletionCandidate(Render(r.Identity.Name, session), CompletionKind.Column, session.Capabilities.QuoteIdentifier(r.Identity.Name))));
        if (qualifier.Length == 1)
            result.AddRange(records.Where(r => (IsRelation(r.Identity.Kind) || r.Identity.Kind == SchemaNodeType.Function) &&
                Match(r.Identity.Schema ?? r.Identity.Catalog, qualifier[0])).Select(r => new SqlCompletionCandidate(
                    Render(r.Identity.Name, session), r.Identity.Kind == SchemaNodeType.Function ? CompletionKind.Function :
                    r.Identity.Kind is SchemaNodeType.View or SchemaNodeType.MaterializedView ? CompletionKind.View : CompletionKind.Table,
                    session.Capabilities.QuoteIdentifier(r.Identity.Name))));
        return result.Distinct().ToList();
    }

    private static bool MatchesRelation(DatabaseObjectIdentity identity, string[] qualifier, DatabaseSession session)
    {
        bool Match(string? stored, string token) => stored != null && session.Objects.MatchesSqlName(stored, token);
        return qualifier.Length switch
        {
            1 => Match(identity.Name, qualifier[0]),
            2 => Match(identity.Schema ?? identity.Catalog, qualifier[0]) && Match(identity.Name, qualifier[1]),
            3 => Match(identity.Catalog, qualifier[0]) && Match(identity.Schema, qualifier[1]) && Match(identity.Name, qualifier[2]),
            _ => false
        };
    }
    private static string Render(string name, DatabaseSession session) => name.All(c => char.IsLetterOrDigit(c) || c == '_') &&
        (session.Capabilities.BatchDialect != SqlBatchDialect.PostgreSql || name == name.ToLowerInvariant())
        ? name : session.Capabilities.QuoteIdentifier(name);

    internal static bool IsRelation(SchemaNodeType kind) => kind is SchemaNodeType.Table or SchemaNodeType.View
        or SchemaNodeType.VirtualTable or SchemaNodeType.MaterializedView;
}
