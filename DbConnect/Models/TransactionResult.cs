using System.Collections.Generic;

namespace DbConnect.Models;

public enum TransactionOutcome
{
    /// <summary>The boundary did not establish whether effects persisted.</summary>
    Unknown,
    /// <summary>No commands were submitted (including open/begin failure).</summary>
    NoChanges,
    Committed,
    /// <summary>The driver acknowledged rollback of supported transactional effects.</summary>
    RolledBack,
    /// <summary>Work enlisted in a caller-owned transaction; no final boundary was established here.</summary>
    Pending,
    Partial
}

public sealed class TransactionResult
{
    /// <summary>Driver acknowledgements for individual completed commands; these are persisted only when the outcome establishes that.</summary>
    public IReadOnlyDictionary<SqlCommandSpec, int> CommandRecordsAffected { get; init; } = new Dictionary<SqlCommandSpec, int>();
    public TransactionOutcome Outcome { get; init; }
    /// <summary>Rows acknowledged by commands, regardless of the final transaction outcome.</summary>
    public int AttemptedRecordsAffected { get; init; }
    /// <summary>Boundary and cleanup diagnostics, separate from the primary command error.</summary>
    public string? BoundaryErrorMessage { get; set; }
    /// <summary>Commands succeeded; consult Outcome to distinguish a commit from caller-owned pending work.</summary>
    public bool Succeeded { get; init; }
    /// <summary>Confirmed committed rows only; excludes pending, rolled-back and uncertain effects.</summary>
    public int RecordsAffected { get; init; }
    public string? FailedStatement { get; init; }
    public string? ErrorMessage { get; init; }
    public string? DiagnosticMessage => string.IsNullOrEmpty(BoundaryErrorMessage) || BoundaryErrorMessage == ErrorMessage
        ? ErrorMessage
        : $"{ErrorMessage} {BoundaryErrorMessage}".Trim();
    public bool IsConcurrencyConflict { get; init; }
    public int? ExpectedRecordsAffected { get; init; }
    public int? ActualRecordsAffected { get; init; }

    /// <summary>RAIN-P2-12 — The command whose affected-row count broke its expectation, so the caller can tell which of its rows conflicted.</summary>
    public SqlCommandSpec? FailedCommand { get; init; }

    public static TransactionResult Success(int recordsAffected) =>
        new() { Succeeded = true, Outcome = TransactionOutcome.Committed, RecordsAffected = recordsAffected, AttemptedRecordsAffected = recordsAffected };

    /// <summary>For execution boundaries that establish a committed prefix before a later failure.</summary>
    public static TransactionResult PartialFailure(string errorMessage, int committedRecordsAffected, int attemptedRecordsAffected) =>
        new()
        {
            Outcome = TransactionOutcome.Partial,
            ErrorMessage = errorMessage,
            RecordsAffected = committedRecordsAffected,
            AttemptedRecordsAffected = attemptedRecordsAffected
        };

    public static TransactionResult Failure(string failedStatement, string errorMessage, int recordsAffected = 0) =>
        new()
        {
            Succeeded = false,
            FailedStatement = failedStatement,
            ErrorMessage = errorMessage,
            AttemptedRecordsAffected = recordsAffected
        };

    public static TransactionResult ConcurrencyConflict(
        string failedStatement,
        int expectedRecordsAffected,
        int actualRecordsAffected,
        int recordsAffected = 0,
        SqlCommandSpec? failedCommand = null) =>
        new()
        {
            Succeeded = false,
            AttemptedRecordsAffected = recordsAffected,
            FailedStatement = failedStatement,
            ErrorMessage = $"Concurrency conflict: expected exactly {expectedRecordsAffected} row(s), but the command affected {actualRecordsAffected}.",
            IsConcurrencyConflict = true,
            ExpectedRecordsAffected = expectedRecordsAffected,
            ActualRecordsAffected = actualRecordsAffected,
            FailedCommand = failedCommand
        };
}
