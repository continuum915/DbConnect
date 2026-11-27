using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

public class SchemaIndexer
{
    /// <summary>
    /// Indexes the relations, columns, schemas and routines under <paramref name="nodes"/>, plus the
    /// provider's built-in functions when <paramref name="capabilities"/> is given.
    /// </summary>
    public SchemaIndex BuildIndex(IEnumerable<SchemaNode> nodes, DatabaseProviderCapabilities? capabilities = null)
    {
        var index = new SchemaIndex();
        if (capabilities != null)
            index.Functions.UnionWith(capabilities.BuiltInFunctions);

        AddNodes(index, nodes, schema: null);
        return index;
    }

    private static void AddNodes(SchemaIndex index, IEnumerable<SchemaNode> nodes, string? schema)
    {
        foreach (var node in nodes)
        {
            var name = node.RawName ?? node.Name;
            var nodeSchema = schema ?? node.SchemaPath ?? node.SchemaName;
            switch (node.Type)
            {
                case SchemaNodeType.Schema:
                    SchemaObjectsOf(index, name);
                    AddNodes(index, node.Children, name);
                    break;
                case SchemaNodeType.Table or SchemaNodeType.View or SchemaNodeType.VirtualTable or SchemaNodeType.MaterializedView:
                    AddRelation(index, node, name, nodeSchema);
                    break;
                case SchemaNodeType.Function:
                    // Procedures are left out: they can't be called in an expression, only by EXEC/CALL.
                    index.Functions.Add(name);
                    if (nodeSchema != null)
                        SchemaObjectsOf(index, nodeSchema)[name] = node.Type;
                    break;
                case SchemaNodeType.TableFolder or SchemaNodeType.ViewFolder or SchemaNodeType.MaterializedViewFolder
                    or SchemaNodeType.FunctionFolder:
                    AddNodes(index, node.Children, schema);
                    break;
            }
        }
    }

    private static void AddRelation(SchemaIndex index, SchemaNode node, string name, string? schema)
    {
        index.Tables.Add(name);

        var colSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var colNode in node.Children.Where(n => n.Type == SchemaNodeType.Column))
        {
            var colName = colNode.RawName ?? colNode.Name;
            colSet.Add(colName);
            index.AllColumns.Add(colName);
        }

        index.TableColumns[name] = colSet;
        if (schema != null)
        {
            index.TableColumns[$"{schema}.{name}"] = colSet;
            SchemaObjectsOf(index, schema)[name] = node.Type;
        }
    }

    private static Dictionary<string, SchemaNodeType> SchemaObjectsOf(SchemaIndex index, string schema)
    {
        if (!index.SchemaObjects.TryGetValue(schema, out var objects))
            index.SchemaObjects[schema] = objects = new Dictionary<string, SchemaNodeType>(StringComparer.OrdinalIgnoreCase);
        return objects;
    }

    /// <summary>
    /// Searches all nodes recursively across multiple connections.
    /// Returns up to 50 results ranked by: exact match > starts with > contains.
    /// </summary>
    public IReadOnlyList<GlobalSearchResult> SearchAll(
        string query,
        IEnumerable<(string connectionName, IEnumerable<SchemaNode> rootNodes)> connections)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<GlobalSearchResult>();

        var all = new List<(int rank, GlobalSearchResult result)>();
        var q = query.Trim();

        foreach (var (connectionName, rootNodes) in connections)
        {
            foreach (var node in rootNodes)
                CollectResults(node, connectionName, string.Empty, q, all);
        }

        return all
            .OrderBy(x => x.rank)
            .ThenBy(x => x.result.ObjectName, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.result)
            .Take(50)
            .ToList()
            .AsReadOnly();
    }

    private static void CollectResults(
        SchemaNode node,
        string connectionName,
        string schemaName,
        string query,
        List<(int, GlobalSearchResult)> results)
    {
        var name = node.RawName ?? node.Name;

        // Determine rank: 1 = exact, 2 = starts with, 3 = contains
        int rank;
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
            rank = 1;
        else if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            rank = 2;
        else if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            rank = 3;
        else
            rank = 0;

        if (rank > 0)
        {
            results.Add((rank, new GlobalSearchResult
            {
                ObjectName = name,
                ObjectType = node.Type.ToString(),
                SchemaName = schemaName,
                ConnectionName = connectionName,
                DatabaseName = connectionName,
                Node = node
            }));
        }

        var childSchema = node.Type == SchemaNodeType.Schema ? (node.RawName ?? node.Name) : schemaName;
        foreach (var child in node.Children)
            CollectResults(child, connectionName, childSchema, query, results);
    }
}
