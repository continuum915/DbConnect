using System.Collections.Generic;

namespace DbConnect.Models;

/// <summary>What a Dump SQL File writes, as Navicat names it: Structure Only, or Structure and Data.</summary>
public enum DumpMode { StructureOnly, StructureAndData }

public static class DumpModes
{
    /// <summary>The menu's name for the mode, as in "Dump SQL File ▸ Structure Only".</summary>
    public static string Title(this DumpMode mode) => mode switch
    {
        DumpMode.StructureOnly => "Structure Only",
        DumpMode.StructureAndData => "Structure and Data",
        _ => throw new System.ArgumentOutOfRangeException(nameof(mode))
    };
}

/// <summary>
/// The tables a dump covers: the whole database, every table in some schemas, or listed tables. With no schemas and
/// no tables it is the whole database.
/// </summary>
public sealed record DumpSelection
{
    public IReadOnlyList<string> Schemas { get; init; } = [];
    public IReadOnlyList<DatabaseObjectIdentity> Tables { get; init; } = [];

    public static DumpSelection Database { get; } = new();
    public static DumpSelection ForSchemas(params string[] schemas) => new() { Schemas = schemas };
    public static DumpSelection ForTables(params DatabaseObjectIdentity[] tables) => new() { Tables = tables };
}

/// <summary>
/// Reported after each table's definition is read (<see cref="Rows"/> null), then while each table's data is written:
/// table <see cref="TablesRead"/> of <see cref="TableCount"/> and the rows written from it so far.
/// </summary>
public sealed record DumpProgress(int TablesRead, int TableCount, string Table, long? Rows = null);
