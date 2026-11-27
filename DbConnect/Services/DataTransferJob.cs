using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Copies tables from one connection to another, like Navicat's Data Transfer, between connections of one engine or
/// of two. The job reads on a session of its own on the source and writes on one of its own on the target, never on a
/// query tab's. It plans every table first (<see cref="DataTransferPlanner"/>), copies them referenced tables first,
/// and adds the foreign keys it held back once every table's rows are in. A missing table is created, the rows stream
/// across page by page in parameterized multi-row batches, and its definition is completed; what a failed or canceled
/// table leaves behind follows its transaction mode (<see cref="DataTransferTableCopy"/>), and the next table goes ahead.
/// </summary>
public static class DataTransferJob
{
    /// <summary>Why these two connections cannot be transferred between; null when they can.</summary>
    public static string? Refusal(DatabaseSession? source, DatabaseSession? target)
    {
        if (source == null || target == null) return "Choose a source and a target connection.";
        if (ReferenceEquals(source, target) ||
            (source.ProviderName == target.ProviderName && source.ConnectionString == target.ConnectionString))
            return "The source and the target are the same database.";
        if (source.ProviderName == target.ProviderName)
            return source.Provider is ITableScriptSource and ITableDesignSource ? null
                : $"{source.ProviderName} cannot read complete table definitions, so its tables cannot be transferred.";
        if (source.Provider is not ITableDesignSource)
            return $"{source.ProviderName} cannot describe its tables, so they cannot be transferred to {target.ProviderName}.";
        if (PortableTypeDialects.For(target.ProviderName) == null)
            return $"{target.ProviderName} cannot create tables from another engine's.";
        return null;
    }

    /// <summary>The script the transfer would run for the tables it creates, with every note; nothing is changed.</summary>
    public static async Task<string> PreviewAsync(DatabaseSession source, DatabaseSession target, IReadOnlyList<DatabaseObjectIdentity> tables,
        DataTransferOptions options, CancellationToken cancellationToken = default)
    {
        if (Refusal(source, target) is { } refusal) throw new InvalidOperationException(refusal);
        await using var reader = await SessionSetup.OpenAsync(source, cancellationToken);
        var plan = await DataTransferPlanner.PlanAsync(source, target, tables, reader, cancellationToken);
        var rules = target.Capabilities.ScriptRules;
        return plan.Preview(tables, options, sql => rules.Terminate(sql, compound: false, newLine: "\n"));
    }

    public static async Task<IReadOnlyList<DataTransferTableOutcome>> RunAsync(DatabaseSession source, DatabaseSession target,
        IReadOnlyList<DatabaseObjectIdentity> tables, DataTransferOptions options, IProgress<DataTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Refusal(source, target) is { } refusal) throw new InvalidOperationException(refusal);
        var outcomes = new DataTransferTableOutcome?[tables.Count];
        IDbSession? writer = null;
        ForeignKeyCheckSwitch? checksOff = null;
        try
        {
            await using var reader = new ExportDbSession(await SessionSetup.OpenAsync(source, cancellationToken));
            writer = await SessionSetup.OpenAsync(target, cancellationToken);
            var plan = await DataTransferPlanner.PlanAsync(source, target, tables, reader, cancellationToken);
            foreach (var (index, reason) in plan.Failures)
                outcomes[index] = new(tables[index], DataTransferTableStatus.Failed, 0, TimeSpan.Zero, reason);
            // Where foreign keys are part of CREATE TABLE, rows load with checks off, so cycles and self-references load
            // in any order; the switch runs outside any transaction (SQLite ignores it inside one).
            if (target.Capabilities.ForeignKeyChecks is { } checks && plan.Tables.Count > 0)
            {
                var off = await writer.ExecuteQueryAsync(checks.Off, new QueryExecutionOptions { CancellationToken = cancellationToken });
                if (!off.HasError) checksOff = checks;
            }
            foreach (var (index, table) in plan.Tables)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    outcomes[index] = new(tables[index], DataTransferTableStatus.Canceled, 0, TimeSpan.Zero, "Not started.");
                    continue;
                }
                outcomes[index] = await new DataTransferTableCopy(source, target, reader, writer, tables[index], table, options,
                    rows => progress?.Report(new(index, tables[index], rows))).RunAsync(cancellationToken);
            }
            // Foreign keys held back from created tables go on once every table's rows are in.
            foreach (var (index, table) in plan.Tables)
            {
                if (outcomes[index] is not { Status: DataTransferTableStatus.Created or DataTransferTableStatus.Recreated } copied) continue;
                var notes = await AddKeysAsync(table, writer, options.IncludeForeignKeys, cancellationToken);
                if (notes.Count > 0) outcomes[index] = copied with { Notes = [.. copied.Notes, .. notes] };
            }
        }
        catch (Exception ex) when (outcomes.Any(o => o == null))
        {
            // Opening a session or planning failed or was canceled: no remaining table was touched.
            var canceled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var reason = canceled ? "Not started." : $"Could not start: {DiagnosticRedactor.Redact(ex.Message)}";
            for (var i = 0; i < tables.Count; i++)
                outcomes[i] ??= new(tables[i], canceled ? DataTransferTableStatus.Canceled : DataTransferTableStatus.Failed, 0, TimeSpan.Zero, reason);
        }
        finally
        {
            if (writer != null)
            {
                if (checksOff != null)
                    try { await writer.ExecuteQueryAsync(checksOff.On); }
                    catch (Exception) { /* The session closes next, and the setting lasts only as long as it does. */ }
                await writer.DisposeAsync();
            }
            try { await target.SchemaChanges.InvalidateAsync(MetadataScope.Root); }
            catch (Exception) { /* The explorer refreshes on demand; the transfer's outcome stands. */ }
        }
        return outcomes.Select(o => o!).ToList();
    }

    /// <summary>
    /// Adds a created table's held-back foreign keys, each in a transaction of its own, and returns what was not added
    /// and why; a key that fails leaves the data as it is.
    /// </summary>
    private static async Task<IReadOnlyList<string>> AddKeysAsync(DataTransferTablePlan table, IDbSession writer, bool include,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        foreach (var key in table.DeferredKeys)
        {
            if (key.OutsideTable != null)
                notes.Add($"Foreign key {key.Name} was not created: it references {key.OutsideTable}, which is not part of the transfer.");
            else if (!include)
                notes.Add($"Foreign key {key.Name} was not created (foreign keys were turned off).");
            else if (cancellationToken.IsCancellationRequested)
                notes.Add($"Foreign key {key.Name} was not created: the transfer was canceled.");
            else if (await writer.ExecuteTransactionAsync([new SqlCommandSpec(key.Sql, new Dictionary<string, object?>())]) is { Succeeded: false } result)
                notes.Add($"Foreign key {key.Name} was not created: {DiagnosticRedactor.Redact(result.ErrorMessage ?? "")}");
        }
        return notes;
    }
}

/// <summary>A table could not be transferred; the message says why, without connection details.</summary>
public sealed class DataTransferException(string message) : Exception(message);
