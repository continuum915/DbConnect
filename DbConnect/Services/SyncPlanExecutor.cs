using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Runs a <see cref="SyncPlan"/> on a session of its own on the target, never a query tab's. A transactional plan runs
/// every step in one transaction and rolls all of them back on the first failure; otherwise each step commits as it runs
/// and the first failure stops the rest. A step that must affect a number of rows (an UPDATE or DELETE matched on what
/// the comparison saw) and does not fails, which is how a target that changed since the comparison is caught.
/// </summary>
public static class SyncPlanExecutor
{
    /// <param name="continueOnError">For a plan that runs step by step: go on after a failed step instead of stopping.</param>
    public static async Task<SyncOutcome> ExecuteAsync(DatabaseSession target, SyncPlan plan, CancellationToken cancellationToken = default,
        bool continueOnError = false)
    {
        var results = plan.Steps.Select(s => new SyncStepOutcome(s, SyncStepStatus.NotRun, 0, TimeSpan.Zero)).ToList();
        IDbSession? session = null;
        try
        {
            session = await SessionSetup.OpenAsync(target, cancellationToken);
            return plan.Transactional
                ? await InOneTransactionAsync(session, plan, results, cancellationToken)
                : await StepByStepAsync(target, session, plan, results, continueOnError, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || session == null)
        {
            var applied = results.Any(r => r.Status == SyncStepStatus.Applied);
            return new(applied ? SyncOutcomeKind.Uncertain : SyncOutcomeKind.NotApplied, results, DiagnosticRedactor.Redact(ex.Message));
        }
        finally
        {
            if (session != null) await session.DisposeAsync();
            if (plan.ChangesSchema)
                try { await target.SchemaChanges.InvalidateAsync(MetadataScope.Root); }
                catch (Exception) { /* The explorer refreshes on demand; the outcome stands. */ }
        }
    }

    private static async Task<SyncOutcome> InOneTransactionAsync(IDbSession session, SyncPlan plan, List<SyncStepOutcome> results,
        CancellationToken cancellationToken)
    {
        await session.BeginTransactionAsync(cancellationToken);
        if (plan.DriftCheck != null && await plan.DriftCheck(session, cancellationToken) is { } drift)
        {
            await session.RollbackAsync();
            return new(SyncOutcomeKind.NotApplied, results, drift);
        }
        for (var i = 0; i < results.Count; i++)
        {
            var (status, rows, elapsed, error) = await RunAsync(session, results[i].Step);
            results[i] = results[i] with { Status = status, RowsAffected = rows, Elapsed = elapsed, Error = error };
            if (status == SyncStepStatus.Applied && !cancellationToken.IsCancellationRequested) continue;
            error ??= "Canceled.";
            try { await session.RollbackAsync(); }
            catch (Exception ex)
            {
                return new(SyncOutcomeKind.Uncertain, results, $"{results[i].Step.Object}: {error} The rollback failed: {DiagnosticRedactor.Redact(ex.Message)}");
            }
            for (var j = 0; j < i; j++) results[j] = results[j] with { Status = SyncStepStatus.RolledBack };
            return new(SyncOutcomeKind.NotApplied, results, $"{results[i].Step.Object}: {error} Every step was rolled back.");
        }
        try { await session.CommitAsync(cancellationToken); }
        catch (Exception ex)
        {
            for (var j = 0; j < results.Count; j++) results[j] = results[j] with { Status = SyncStepStatus.Uncertain };
            return new(SyncOutcomeKind.Uncertain, results, $"The commit failed: {DiagnosticRedactor.Redact(ex.Message)}");
        }
        return new(SyncOutcomeKind.Applied, results);
    }

    private static async Task<SyncOutcome> StepByStepAsync(DatabaseSession target, IDbSession session, SyncPlan plan, List<SyncStepOutcome> results,
        bool continueOnError, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        if (plan.DriftCheck != null && await plan.DriftCheck(session, cancellationToken) is { } drift)
            return new(SyncOutcomeKind.NotApplied, results, drift);
        for (var i = 0; i < results.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                return new(i == 0 ? SyncOutcomeKind.NotApplied : SyncOutcomeKind.Partial, results, "Canceled.");
            var (status, rows, elapsed, error) = results[i].Step.OwnsTransaction
                ? await RunOwnTransactionAsync(target, results[i].Step)
                : await RunAsync(session, results[i].Step);
            results[i] = results[i] with { Status = status, RowsAffected = rows, Elapsed = elapsed, Error = error };
            if (status == SyncStepStatus.Applied) continue;
            failures.Add($"{results[i].Step.Object}: {error}");
            if (!continueOnError || status == SyncStepStatus.Uncertain)
                return new(results.Any(r => r.Status == SyncStepStatus.Applied) ? SyncOutcomeKind.Partial : SyncOutcomeKind.NotApplied,
                    results, $"then {results[i].Step.Object} failed: {error}");
        }
        if (failures.Count == 0) return new(SyncOutcomeKind.Applied, results);
        return new(results.Any(r => r.Status == SyncStepStatus.Applied) ? SyncOutcomeKind.Partial : SyncOutcomeKind.NotApplied, results,
            $"{failures.Count} failed and were skipped: {string.Join("; ", failures)}");
    }

    /// <summary>A script that owns its transaction runs as the table designer runs it, which also undoes it on failure.</summary>
    private static async Task<(SyncStepStatus, int, TimeSpan, string?)> RunOwnTransactionAsync(DatabaseSession target, SyncStep step)
    {
        var clock = Stopwatch.StartNew();
        var outcome = await TableChangeExecutor.ExecuteAsync(target, [step.Command.CommandText], MetadataScope.Root);
        return outcome.Kind == TableChangeOutcomeKind.Applied
            ? (SyncStepStatus.Applied, 0, clock.Elapsed, null)
            : (outcome.Kind == TableChangeOutcomeKind.NotApplied ? SyncStepStatus.Failed : SyncStepStatus.Uncertain, 0, clock.Elapsed, outcome.Error);
    }

    private static async Task<(SyncStepStatus, int, TimeSpan, string?)> RunAsync(IDbSession session, SyncStep step)
    {
        var clock = Stopwatch.StartNew();
        var result = await session.ExecuteTransactionAsync([step.Command]);
        var rows = Math.Max(0, result.AttemptedRecordsAffected);
        if (result.Succeeded) return (SyncStepStatus.Applied, rows, clock.Elapsed, null);
        var error = result.IsConcurrencyConflict
            ? $"expected {result.ExpectedRecordsAffected} row{(result.ExpectedRecordsAffected == 1 ? "" : "s")} but {result.ActualRecordsAffected} matched: the target changed since the comparison."
            : DiagnosticRedactor.Redact(result.ErrorMessage ?? "Failed.");
        return (SyncStepStatus.Failed, rows, clock.Elapsed, error);
    }
}
