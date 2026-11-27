using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// How a dump loads tables in a foreign-key cycle or that reference themselves. Where the foreign keys are separate
/// statements (PostgreSQL, SQL Server), the cycle's keys are held back until every table is created and loaded.
/// Where they are part of CREATE TABLE, the script turns foreign-key checks off for the load when the engine can
/// (<see cref="DatabaseProviderCapabilities.ForeignKeyChecks"/>). An engine that cannot (DuckDB) loads a
/// self-referencing table only when each row comes after the row it references, which the dump checks
/// (<see cref="OrderChecked"/>); a cycle of several tables there fails the dump, naming them.
/// </summary>
internal sealed class DumpCycleLoading
{
    private readonly HashSet<(TableDefinitionSnapshot Table, string Name)> _deferred = [];
    private readonly IReadOnlyList<DumpCycle> _cycles;
    private readonly bool _deferredAfterData;

    private DumpCycleLoading(IReadOnlyList<DumpCycle> cycles, bool deferredAfterData)
    {
        _cycles = cycles;
        _deferredAfterData = deferredAfterData;
    }

    /// <summary>Statements that turn foreign-key checks off before the first table and back on after the last statement.</summary>
    public ForeignKeyCheckSwitch? ChecksOff { get; private set; }

    /// <summary>Self-referencing foreign keys loaded with checks on, so each row must follow the row it references.</summary>
    public IReadOnlyList<(TableDefinitionSnapshot Table, ForeignKeyRelationship Key)> OrderChecked { get; private set; } = [];

    /// <summary>Whether <paramref name="part"/> is held back from its table's section and added once all tables are loaded.</summary>
    public bool IsDeferred(TableDefinitionSnapshot table, TableDefinitionPart part) =>
        part.Kind is TableDefinitionPartKind.Constraint or TableDefinitionPartKind.Comment && _deferred.Contains((table, part.Name));

    public bool HasDeferred(TableDefinitionSnapshot table) => _deferred.Any(d => d.Table == table);

    public static DumpCycleLoading Plan(DumpTableOrder order, DatabaseProviderCapabilities capabilities, string providerName, DumpMode mode)
    {
        var plan = new DumpCycleLoading(order.Cycles, mode == DumpMode.StructureAndData);
        var orderChecked = new List<(TableDefinitionSnapshot, ForeignKeyRelationship)>();
        foreach (var cycle in order.Cycles)
        {
            // A key is a separate statement when its table's snapshot has a constraint part of that name.
            if (cycle.Keys.All(k => k.Table.Parts.Any(p => p.Kind == TableDefinitionPartKind.Constraint && p.Name == k.Key.Name)))
            {
                foreach (var (table, key) in cycle.Keys) plan._deferred.Add((table, key.Name));
                continue;
            }
            if (capabilities.ForeignKeyChecks is { } checks)
            {
                plan.ChecksOff = checks;
                continue;
            }
            if (!cycle.IsSelfReference)
                throw new DatabaseDumpException($"Tables {Names(cycle)} reference each other through foreign keys, and {providerName} " +
                                                "can neither turn off foreign-key checks nor add foreign keys after the data, so the dump would not load.");
            orderChecked.AddRange(cycle.Keys);
        }
        plan.OrderChecked = orderChecked;
        return plan;
    }

    /// <summary>The header lines naming the cycles and how the script loads them; none when there are no cycles.</summary>
    public IEnumerable<string> HeaderLines(string providerName)
    {
        if (_cycles.Count == 0) yield break;
        yield return "Foreign-key cycles:";
        foreach (var cycle in _cycles) yield return "  " + Names(cycle) + (cycle.IsSelfReference ? " (references itself)" : "");
        if (_deferred.Count > 0)
            yield return $"Foreign keys in these cycles: added after {(_deferredAfterData ? "the data" : "all tables are created")}";
        if (ChecksOff is { } checks)
            yield return $"Foreign-key checks: off while this script loads ({checks.Off}), restored at its end ({checks.On})" +
                         (checks.Caveat is { } caveat ? "; " + caveat : "");
        if (OrderChecked.Count > 0 && _deferredAfterData)
            yield return $"Foreign-key checks: {providerName} cannot turn them off; every row of a self-referencing table is written after the row it references";
    }

    private static string Names(DumpCycle cycle) => string.Join(", ", cycle.Tables.Select(t => ObjectNames.SchemaQualified(t.Table)));
}
