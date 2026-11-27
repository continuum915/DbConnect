using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>One statement of a table's definition as the transfer runs it.</summary>
public sealed record DataTransferStatement(TableDefinitionPartKind Kind, string Name, string Sql);

/// <summary>A foreign key added once every table's rows are in; <see cref="OutsideTable"/> names a referenced table the transfer leaves out.</summary>
public sealed record DataTransferDeferredKey(string Name, string Sql, string? OutsideTable);

/// <summary>
/// How one table is copied: the table it becomes on the target, the statements that create it before its rows
/// (<see cref="BeforeData"/>) and complete it after them in the same transaction (<see cref="AfterData"/>), the indexes
/// added once it is committed (a failed index is reported and the data stays), the foreign keys added once every
/// table's rows are in, and what does not carry over. <see cref="Columns"/> is set between different engines, whose
/// values are converted column by column.
/// </summary>
public sealed record DataTransferTablePlan(DatabaseObjectIdentity Table, DatabaseObjectIdentity Target, string TargetName)
{
    public IReadOnlyList<DataTransferStatement> BeforeData { get; init; } = [];
    public IReadOnlyList<DataTransferStatement> AfterData { get; init; } = [];
    public IReadOnlyList<DataTransferStatement> Indexes { get; init; } = [];
    public IReadOnlyList<DataTransferDeferredKey> DeferredKeys { get; init; } = [];
    /// <summary>What the created table does not reproduce; reported when the transfer creates it.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    public IReadOnlyList<PortableColumn>? Columns { get; init; }
}

/// <summary>A transfer's tables in copy order (referenced tables first), and the tables that cannot be planned, with why.</summary>
public sealed record DataTransferPlan(IReadOnlyList<(int Index, DataTransferTablePlan Table)> Tables, IReadOnlyDictionary<int, string> Failures)
{
    /// <summary>The script the plan runs for tables it creates, with every note, for review before anything runs.</summary>
    public string Preview(IReadOnlyList<DatabaseObjectIdentity> tables, DataTransferOptions options, Func<string, string> terminate)
    {
        var text = new StringBuilder();
        foreach (var (index, plan) in Tables)
        {
            var mode = options.ModeFor(plan.Table) switch
            {
                DataTransferTargetMode.Create => "created; the transfer fails it if it already exists",
                DataTransferTargetMode.DropAndRecreate => "dropped if it exists, then created",
                DataTransferTargetMode.Empty => "created if missing; an existing table is emptied and keeps its definition",
                _ => "created if missing; an existing table is appended to and keeps its definition"
            };
            text.Append("-- ").Append(ObjectNames.SchemaQualified(tables[index])).Append(" → ").Append(plan.TargetName).Append('\n');
            text.Append("-- The table is ").Append(mode).Append(".\n");
            foreach (var statement in plan.BeforeData) text.Append(terminate(statement.Sql)).Append('\n');
            if (plan.AfterData.Count > 0) text.Append("-- After its rows:\n");
            foreach (var statement in plan.AfterData) text.Append(terminate(statement.Sql)).Append('\n');
            if (options.IncludeIndexes && plan.Indexes.Count > 0) text.Append("-- Indexes, once the table is committed:\n");
            if (options.IncludeIndexes) foreach (var statement in plan.Indexes) text.Append(terminate(statement.Sql)).Append('\n');
            var keys = plan.DeferredKeys.Where(k => k.OutsideTable == null).ToList();
            if (options.IncludeForeignKeys && keys.Count > 0) text.Append("-- Foreign keys, once every table's rows are in:\n");
            if (options.IncludeForeignKeys) foreach (var key in keys) text.Append(terminate(key.Sql)).Append('\n');
            var notes = plan.Notes.Concat(plan.DeferredKeys.Where(k => k.OutsideTable != null)
                .Select(k => $"Foreign key {k.Name} is not created: it references {k.OutsideTable}, which is not part of the transfer.")).ToList();
            if (notes.Count > 0) text.Append("-- Not carried over:\n");
            foreach (var note in notes) text.Append("--   ").Append(note.Replace("\n", " ")).Append('\n');
            text.Append('\n');
        }
        foreach (var (index, reason) in Failures.OrderBy(f => f.Key))
            text.Append("-- ").Append(ObjectNames.SchemaQualified(tables[index])).Append(" cannot be transferred: ").Append(reason).Append("\n\n");
        return text.ToString().TrimEnd() + "\n";
    }
}
