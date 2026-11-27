using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public enum TableChangeStepStatus
{
    NotRun,
    Applied,
    Failed,
    /// <summary>Ran inside the transaction, which was rolled back.</summary>
    RolledBack,
    /// <summary>The connection or the commit failed, so whether it persisted is unknown.</summary>
    Uncertain
}

public sealed record TableChangeStepResult(string Sql, TableChangeStepStatus Status, string? Error = null);

public enum TableChangeOutcomeKind
{
    /// <summary>Every step was applied.</summary>
    Applied,
    /// <summary>Nothing was applied: the first step failed, or the transaction was rolled back.</summary>
    NotApplied,
    /// <summary>Some steps were applied and committed before a later step failed.</summary>
    Partial,
    /// <summary>It is unknown whether some steps persisted.</summary>
    Uncertain
}

/// <summary>What happened to each step of a saved plan.</summary>
public sealed record TableChangeOutcome(TableChangeOutcomeKind Kind, IReadOnlyList<TableChangeStepResult> Steps, string? Error = null)
{
    public string Summary => Kind switch
    {
        TableChangeOutcomeKind.Applied => "✅ Table saved successfully.",
        TableChangeOutcomeKind.NotApplied => $"❌ Save failed and nothing was applied: {Error}",
        TableChangeOutcomeKind.Partial =>
            $"❌ Save failed part-way: {Steps.Count(s => s.Status == TableChangeStepStatus.Applied)} of {Steps.Count} steps were applied and remain. {Error}",
        _ => $"❌ Save failed and it is unknown whether some steps were applied: {Error}"
    };
}

/// <summary>
/// Runs a designer change plan step by step and reports each step's outcome. Where the engine has
/// transactional DDL every step runs in one transaction on a connection of its own, so a failure
/// rolls them all back; elsewhere (MySQL) each step commits as it runs and a failure stops the
/// rest. Either way the designer never touches a transaction a query tab owns.
/// </summary>
public static class TableChangeExecutor
{
    public static async Task<TableChangeOutcome> ExecuteAsync(DatabaseSession session, IReadOnlyList<string> steps, MetadataScope scope)
    {
        TableChangeOutcome outcome;
        try
        {
            outcome = steps is [var only] && only.StartsWith(SqliteRebuildExecution.Marker, StringComparison.Ordinal)
                ? await RebuildAsync(session, only)
                : session.Capabilities.TransactionalDdl
                    ? await InOneTransactionAsync(session, steps)
                    : await StepByStepAsync(session, steps);
        }
        catch (Exception ex)
        {
            outcome = new(TableChangeOutcomeKind.Uncertain, steps.Select(s => new TableChangeStepResult(s, TableChangeStepStatus.Uncertain)).ToList(),
                DiagnosticRedactor.Redact(ex.Message));
        }
        // Anything but a clean result may have touched more than the table's details.
        await session.SchemaChanges.InvalidateAsync(outcome.Kind is TableChangeOutcomeKind.Partial or TableChangeOutcomeKind.Uncertain
            ? MetadataScope.Root
            : scope);
        return outcome;
    }

    /// <summary>A SQLite rebuild is one script that owns its transaction and rolls it back on failure.</summary>
    private static async Task<TableChangeOutcome> RebuildAsync(DatabaseSession session, string script)
    {
        try
        {
            await session.Provider.ExecuteNonQueryAsync(session.ConnectionString, script);
            return new(TableChangeOutcomeKind.Applied, [new(script, TableChangeStepStatus.Applied)]);
        }
        catch (Exception ex)
        {
            var error = DiagnosticRedactor.Redact(ex.Message);
            return new(TableChangeOutcomeKind.NotApplied, [new(script, TableChangeStepStatus.Failed, error)], error);
        }
    }

    private static async Task<TableChangeOutcome> InOneTransactionAsync(DatabaseSession session, IReadOnlyList<string> steps)
    {
        var result = await session.ExecuteTransactionAsync(steps);
        var error = result.DiagnosticMessage is { } message ? DiagnosticRedactor.Redact(message) : null;
        if (result.Outcome == TransactionOutcome.Committed)
            return new(TableChangeOutcomeKind.Applied, steps.Select(s => new TableChangeStepResult(s, TableChangeStepStatus.Applied)).ToList());

        var failed = result.FailedStatement is { } statement ? IndexOf(steps, statement) : -1;
        var rows = new List<TableChangeStepResult>();
        for (var i = 0; i < steps.Count; i++)
        {
            var status = result.Outcome switch
            {
                TransactionOutcome.RolledBack => i < failed ? TableChangeStepStatus.RolledBack
                    : i == failed ? TableChangeStepStatus.Failed : TableChangeStepStatus.NotRun,
                TransactionOutcome.NoChanges => TableChangeStepStatus.NotRun,
                // The rollback or commit failed: a failed step is known to have failed, the others are unknown.
                _ => i == failed ? TableChangeStepStatus.Failed : failed >= 0 && i > failed ? TableChangeStepStatus.NotRun : TableChangeStepStatus.Uncertain
            };
            rows.Add(new(steps[i], status, i == failed ? error : null));
        }
        var kind = result.Outcome is TransactionOutcome.RolledBack or TransactionOutcome.NoChanges
            ? TableChangeOutcomeKind.NotApplied
            : TableChangeOutcomeKind.Uncertain;
        return new(kind, rows, error);
    }

    /// <summary>Each step commits on its own; the first failure stops the rest.</summary>
    private static async Task<TableChangeOutcome> StepByStepAsync(DatabaseSession session, IReadOnlyList<string> steps)
    {
        var rows = new List<TableChangeStepResult>();
        for (var i = 0; i < steps.Count; i++)
        {
            var result = await session.ExecuteTransactionAsync([steps[i]]);
            if (result.Outcome == TransactionOutcome.Committed)
            {
                rows.Add(new(steps[i], TableChangeStepStatus.Applied));
                continue;
            }
            var error = DiagnosticRedactor.Redact(result.DiagnosticMessage ?? "The step failed.");
            // A statement that failed was not applied (MySQL 8 DDL is atomic); a failed boundary leaves it unknown.
            var known = result.Outcome is TransactionOutcome.RolledBack or TransactionOutcome.NoChanges;
            rows.Add(new(steps[i], known ? TableChangeStepStatus.Failed : TableChangeStepStatus.Uncertain, error));
            rows.AddRange(steps.Skip(i + 1).Select(s => new TableChangeStepResult(s, TableChangeStepStatus.NotRun)));
            var kind = !known ? TableChangeOutcomeKind.Uncertain : i == 0 ? TableChangeOutcomeKind.NotApplied : TableChangeOutcomeKind.Partial;
            return new(kind, rows, error);
        }
        return new(TableChangeOutcomeKind.Applied, rows);
    }

    private static int IndexOf(IReadOnlyList<string> steps, string statement)
    {
        for (var i = 0; i < steps.Count; i++)
            if (steps[i] == statement) return i;
        return -1;
    }

    /// <summary>
    /// Re-reads the table and compares it with the design the edits were made against. Returns why
    /// saving must wait for a reload, or null when the table is unchanged. This narrows, but does
    /// not close, the window in which another session can change the table.
    /// </summary>
    public static async Task<string?> CheckBaselineAsync(DatabaseSession session, TableDesign baseline)
    {
        if (session.Provider is not ITableDesignSource source) return "This provider cannot re-read the table design.";
        TableDesign current;
        try
        {
            current = await Task.Run(() => source.GetTableDesignAsync(session.ConnectionString, baseline.Table));
        }
        catch (Exception ex)
        {
            return $"The table could not be re-read before saving: {DiagnosticRedactor.Redact(ex.Message)}";
        }
        var before = TableDesignText.Lines(baseline);
        var now = TableDesignText.Lines(current);
        var changes = before.Except(now).Select(l => "was: " + l).Concat(now.Except(before).Select(l => "now: " + l)).Take(4).ToList();
        return changes.Count == 0 ? null
            : $"The table changed since it was loaded ({string.Join("; ", changes)}). Reload it to see the current design before saving.";
    }
}
