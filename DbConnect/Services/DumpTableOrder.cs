using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Tables whose foreign keys reference each other in a loop, or one table that references itself, in dump order;
/// <see cref="Keys"/> are the foreign keys between them, each with the table that holds it.
/// </summary>
internal sealed record DumpCycle(IReadOnlyList<TableDefinitionSnapshot> Tables,
    IReadOnlyList<(TableDefinitionSnapshot Table, ForeignKeyRelationship Key)> Keys)
{
    public bool IsSelfReference => Tables.Count == 1;
}

/// <summary>
/// A dump's tables ordered so every table comes after the tables its foreign keys reference, keeping the catalog
/// order otherwise. Tables in a cycle (<see cref="Cycles"/>) are kept together in catalog order, after the tables
/// they reference and before the tables that reference them; a self-reference does not constrain the order.
/// </summary>
internal sealed record DumpTableOrder(IReadOnlyList<TableDefinitionSnapshot> Tables, IReadOnlyList<DumpCycle> Cycles)
{
    public static DumpTableOrder Sort(IReadOnlyList<TableDefinitionSnapshot> tables, IReadOnlyList<ForeignKeyRelationship> foreignKeys)
    {
        var names = new DumpTableNames(tables.Select(t => t.Table).ToList());
        var keys = new List<(int Child, int Parent, ForeignKeyRelationship Key)>();
        foreach (var key in foreignKeys)
            if (names.Find(key.Table) is { } child && names.Find(key.ReferencedTable) is { } parent)
                keys.Add((child, parent, key));

        var parents = tables.Select(_ => new HashSet<int>()).ToList();
        foreach (var (child, parent, _) in keys) parents[child].Add(parent);
        var components = StronglyConnected(parents);
        var componentOf = new int[tables.Count];
        for (var c = 0; c < components.Count; c++)
            foreach (var i in components[c]) componentOf[i] = c;

        // Components whose parent components are all placed, earliest catalog table first.
        var waiting = new int[components.Count];
        var children = components.Select(_ => new HashSet<int>()).ToList();
        for (var i = 0; i < tables.Count; i++)
            foreach (var p in parents[i])
                if (componentOf[p] != componentOf[i] && children[componentOf[p]].Add(componentOf[i])) waiting[componentOf[i]]++;
        // Keys between tables of one component make it a cycle.
        var inside = keys.Where(k => componentOf[k.Child] == componentOf[k.Parent]).ToLookup(k => componentOf[k.Child]);
        var ready = new PriorityQueue<int, int>();
        for (var c = 0; c < components.Count; c++)
            if (waiting[c] == 0) ready.Enqueue(c, components[c][0]);
        var ordered = new List<int>();
        var cycles = new List<DumpCycle>();
        while (ready.TryDequeue(out var next, out _))
        {
            foreach (var child in children[next])
                if (--waiting[child] == 0) ready.Enqueue(child, components[child][0]);
            ordered.AddRange(components[next]);
            if (inside.Contains(next))
                cycles.Add(new(components[next].Select(i => tables[i]).ToList(), inside[next].Select(k => (tables[k.Child], k.Key)).ToList()));
        }
        return new(ordered.Select(i => tables[i]).ToList(), cycles);
    }

    /// <summary>Tarjan's strongly connected components, each in catalog order; iterative so long chains cannot overflow the stack.</summary>
    private static List<List<int>> StronglyConnected(IReadOnlyList<HashSet<int>> edges)
    {
        var count = edges.Count;
        var index = new int[count];
        var low = new int[count];
        var onStack = new bool[count];
        Array.Fill(index, -1);
        var stack = new Stack<int>();
        var components = new List<List<int>>();
        var next = 0;
        for (var root = 0; root < count; root++)
        {
            if (index[root] >= 0) continue;
            var work = new Stack<(int Node, IEnumerator<int> Edges)>();
            void Visit(int node)
            {
                index[node] = low[node] = next++;
                stack.Push(node);
                onStack[node] = true;
                work.Push((node, edges[node].GetEnumerator()));
            }
            Visit(root);
            while (work.Count > 0)
            {
                var (node, children) = work.Peek();
                if (children.MoveNext())
                {
                    var child = children.Current;
                    if (index[child] < 0) Visit(child);
                    else if (onStack[child]) low[node] = Math.Min(low[node], index[child]);
                    continue;
                }
                work.Pop();
                if (work.Count > 0) low[work.Peek().Node] = Math.Min(low[work.Peek().Node], low[node]);
                if (low[node] != index[node]) continue;
                var component = new List<int>();
                int member;
                do
                {
                    member = stack.Pop();
                    onStack[member] = false;
                    component.Add(member);
                } while (member != node);
                component.Sort();
                components.Add(component);
            }
        }
        return components;
    }
}
