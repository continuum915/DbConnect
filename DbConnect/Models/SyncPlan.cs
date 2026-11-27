using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DbConnect.Models;

/// <summary>
/// One statement of a synchronization: the object it changes, the command it runs, and the same statement with its
/// values written out for the preview. <see cref="Destructive"/> says what data it may lose, when it may.
/// </summary>
public sealed record SyncStep(string Object, string Action, SqlCommandSpec Command, string Preview)
{
    public string? Destructive { get; init; }

    /// <summary>A step on an engine whose DDL commits as it runs (MySQL) cannot be rolled back.</summary>
    public bool CommitsImplicitly { get; init; }

    /// <summary>The step is a script that runs in a transaction of its own (a SQLite table rebuild), so its plan runs step by step.</summary>
    public bool OwnsTransaction { get; init; }
}

/// <summary>
/// What Apply runs on the target, in order. With <see cref="Transactional"/> every step runs in one transaction and a
/// failure rolls all of them back; otherwise each step commits as it runs and a failure stops the rest.
/// </summary>
public sealed record SyncPlan(string TargetConnection, string? TargetDatabase, string EnvironmentTier, IReadOnlyList<SyncStep> Steps,
    bool Transactional)
{
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The plan changes definitions, so the target's catalog is refreshed after it runs.</summary>
    public bool ChangesSchema { get; init; }

    /// <summary>
    /// Run on the apply session before the first step (inside the transaction when there is one): what in the target
    /// differs from what the comparison saw, or null when nothing does. Apply stops on any difference.
    /// </summary>
    public Func<DbConnect.Services.IDbSession, System.Threading.CancellationToken, System.Threading.Tasks.Task<string?>>? DriftCheck { get; init; }

    public IEnumerable<SyncStep> DestructiveSteps => Steps.Where(s => s.Destructive != null);

    /// <summary>The script the preview shows: each step's statement, destructive ones marked.</summary>
    public string Script(Func<string, string> terminate)
    {
        var text = new StringBuilder();
        text.Append("-- Target: ").Append(TargetConnection).Append(TargetDatabase is { } database ? " / " + database : "")
            .Append(" [").Append(EnvironmentTier).Append("]\n");
        text.Append(Transactional ? "-- All steps run in one transaction; a failure rolls every step back.\n"
            : "-- Each step commits as it runs; a failure stops the steps after it, unless Continue on error is ticked.\n");
        var implicitCommits = Steps.Count(s => s.CommitsImplicitly);
        if (implicitCommits > 0)
            text.Append("-- ").Append(implicitCommits).Append(" step").Append(implicitCommits == 1 ? "" : "s")
                .Append(" commit at once and cannot be rolled back (marked below).\n");
        foreach (var note in Notes) text.Append("-- ").Append(note).Append('\n');
        text.Append('\n');
        foreach (var step in Steps)
        {
            if (step.Destructive != null) text.Append("-- DESTRUCTIVE: ").Append(step.Destructive).Append('\n');
            if (step.CommitsImplicitly) text.Append("-- COMMITS AT ONCE: cannot be rolled back.\n");
            text.Append(terminate(step.Preview)).Append('\n');
        }
        return text.ToString();
    }
}

public enum SyncStepStatus { NotRun, Applied, Failed, RolledBack, Uncertain }

public sealed record SyncStepOutcome(SyncStep Step, SyncStepStatus Status, int RowsAffected, TimeSpan Elapsed, string? Error = null);

public enum SyncOutcomeKind
{
    /// <summary>Every step was applied.</summary>
    Applied,
    /// <summary>Nothing changed: Apply was refused, the first step failed, or the transaction rolled back.</summary>
    NotApplied,
    /// <summary>Some steps were applied and stay; a later one failed.</summary>
    Partial,
    /// <summary>The connection or the commit failed, so whether steps persisted is unknown.</summary>
    Uncertain
}

/// <summary>What Apply did: each step's outcome, and a summary.</summary>
public sealed record SyncOutcome(SyncOutcomeKind Kind, IReadOnlyList<SyncStepOutcome> Steps, string? Error = null)
{
    public string Summary
    {
        get
        {
            var applied = Steps.Count(s => s.Status == SyncStepStatus.Applied);
            return Kind switch
            {
                SyncOutcomeKind.Applied => $"Applied {applied} step{(applied == 1 ? "" : "s")} · {Steps.Sum(s => s.RowsAffected)} rows affected.",
                SyncOutcomeKind.NotApplied => $"Nothing was applied: {Error}",
                SyncOutcomeKind.Partial => $"{applied} of {Steps.Count} steps were applied and remain; {Error}",
                _ => $"It is unknown whether some steps were applied: {Error}"
            };
        }
    }

    /// <summary>The log as SQL with each step's result as a comment, or as plain text.</summary>
    public string Log(bool asSql)
    {
        var text = new StringBuilder();
        var prefix = asSql ? "-- " : "";
        text.Append(prefix).Append(Summary).Append('\n').Append('\n');
        foreach (var step in Steps)
        {
            var result = $"{step.Status} · {step.RowsAffected} rows · {step.Elapsed.TotalMilliseconds:0} ms" +
                         (step.Error == null ? "" : " · " + step.Error.ReplaceLineEndings(" "));
            if (asSql)
                text.Append("-- ").Append(step.Step.Object).Append(": ").Append(result).Append('\n').Append(step.Step.Preview).Append(";\n\n");
            else
                text.Append(step.Step.Object).Append(" · ").Append(step.Step.Action).Append(": ").Append(result).Append('\n');
        }
        return text.ToString();
    }
}
