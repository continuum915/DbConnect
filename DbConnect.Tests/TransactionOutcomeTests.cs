using System.Data;
using System.Data.Common;
using Moq;
using Moq.Protected;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class TransactionOutcomeTests
{
    private static readonly SqlCommandSpec Write = new("UPDATE items SET value = 1", new Dictionary<string, object?>());

    [Theory]
    [InlineData("commit", TransactionOutcome.Committed)]
    [InlineData("rollback", TransactionOutcome.RolledBack)]
    [InlineData("dispose", TransactionOutcome.RolledBack)]
    public async Task ExplicitSessionEndExposesCleanupFailureWithoutLosingConfirmedOutcome(string operation, TransactionOutcome outcome)
    {
        var boundary = new Boundary();
        boundary.Transaction.Setup(t => t.DisposeAsync())
            .Returns(() => ValueTask.FromException(new InvalidOperationException("cleanup failed")));
        await using var session = await boundary.OpenSessionAsync();
        await session.BeginTransactionAsync();

        var error = await Assert.ThrowsAsync<TransactionBoundaryException>(() => operation switch
        {
            "commit" => session.CommitAsync(),
            "rollback" => session.RollbackAsync(),
            _ => session.DisposeAsync().AsTask()
        });

        Assert.Equal(outcome, error.Result.Outcome);
        Assert.True(error.Result.Succeeded);
        Assert.Contains("cleanup failed", error.Result.BoundaryErrorMessage);
        Assert.False(session.IsInTransaction);
    }

    [Fact]
    public async Task BeginFailureReportsNoChanges()
    {
        var boundary = new Boundary();
        boundary.Connection.Protected().Setup<ValueTask<DbTransaction>>("BeginDbTransactionAsync",
                ItExpr.IsAny<IsolationLevel>(), ItExpr.IsAny<CancellationToken>())
            .Returns(() => ValueTask.FromException<DbTransaction>(new InvalidOperationException("begin failed")));
        await using var session = await boundary.OpenSessionAsync();

        var result = await session.ExecuteTransactionAsync([Write]);

        Assert.False(result.Succeeded);
        Assert.Equal(TransactionOutcome.NoChanges, result.Outcome);
        Assert.Equal(0, result.RecordsAffected);
        Assert.Equal("begin failed", result.ErrorMessage);
    }

    [Fact]
    public async Task OwnedTransactionReportsConfirmedCommit()
    {
        var boundary = new Boundary();
        await using var session = await boundary.OpenSessionAsync();

        var result = await session.ExecuteTransactionAsync([Write]);

        Assert.True(result.Succeeded);
        Assert.Equal(TransactionOutcome.Committed, result.Outcome);
        Assert.Equal(3, result.RecordsAffected);
        Assert.Equal(3, result.AttemptedRecordsAffected);
    }

    [Theory]
    [InlineData(false, TransactionOutcome.RolledBack)]
    [InlineData(true, TransactionOutcome.Unknown)]
    public async Task CommandFailureReportsRollbackEvidenceWithoutCountingAttemptsAsCommits(bool rollbackFails, TransactionOutcome outcome)
    {
        var boundary = new Boundary();
        boundary.Command.SetupSequence(c => c.ExecuteNonQueryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(3).ThrowsAsync(new InvalidOperationException("command failed"));
        if (rollbackFails)
            boundary.Transaction.Setup(t => t.RollbackAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("rollback failed"));
        await using var session = await boundary.OpenSessionAsync();

        var result = await session.ExecuteTransactionAsync([Write, Write]);

        Assert.False(result.Succeeded);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(0, result.RecordsAffected);
        Assert.Equal(3, result.AttemptedRecordsAffected);
        Assert.Equal("command failed", result.ErrorMessage);
        Assert.Equal(Write.CommandText, result.FailedStatement);
        if (rollbackFails)
            Assert.Contains("rollback failed", result.BoundaryErrorMessage);
    }

    [Fact]
    public async Task LostCommitAcknowledgementRemainsUnknownEvenWhenDisposalSucceeds()
    {
        var boundary = new Boundary();
        boundary.Transaction.Setup(t => t.CommitAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("acknowledgement lost"));
        await using var session = await boundary.OpenSessionAsync();

        var result = await session.ExecuteTransactionAsync([Write]);

        Assert.False(result.Succeeded);
        Assert.Equal(TransactionOutcome.Unknown, result.Outcome);
        Assert.Equal(0, result.RecordsAffected);
        Assert.Equal(3, result.AttemptedRecordsAffected);
        Assert.Contains("acknowledgement lost", result.ErrorMessage);
        Assert.Null(result.FailedStatement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerOwnedTransactionDoesNotClaimCommitOrRollback(bool fails)
    {
        var boundary = new Boundary();
        if (fails)
            boundary.Command.SetupSequence(c => c.ExecuteNonQueryAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(3).ThrowsAsync(new InvalidOperationException("command failed"));
        await using var session = await boundary.OpenSessionAsync();
        await session.BeginTransactionAsync();

        var result = await session.ExecuteTransactionAsync([Write, Write]);

        Assert.Equal(!fails, result.Succeeded);
        Assert.Equal(TransactionOutcome.Pending, result.Outcome);
        Assert.Equal(0, result.RecordsAffected);
        Assert.Equal(fails ? 3 : 6, result.AttemptedRecordsAffected);
        Assert.True(session.IsInTransaction);
    }

    [Fact]
    public async Task CleanupFailuresDoNotEraseConfirmedCommit()
    {
        var boundary = new Boundary();
        boundary.Command.Setup(c => c.DisposeAsync())
            .Returns(() => ValueTask.FromException(new InvalidOperationException("command cleanup failed")));
        boundary.Transaction.Setup(t => t.DisposeAsync())
            .Returns(() => ValueTask.FromException(new InvalidOperationException("transaction cleanup failed")));
        boundary.Connection.Setup(c => c.DisposeAsync())
            .Returns(() => ValueTask.FromException(new InvalidOperationException("connection cleanup failed")));

        var result = await TransactionExecution.OpenAndExecuteAsync(() => boundary.Connection.Object, [Write]);

        Assert.True(result.Succeeded);
        Assert.Equal(TransactionOutcome.Committed, result.Outcome);
        Assert.Equal(3, result.RecordsAffected);
        Assert.Contains("command cleanup failed", result.BoundaryErrorMessage);
        Assert.Contains("transaction cleanup failed", result.BoundaryErrorMessage);
        Assert.Contains("connection cleanup failed", result.BoundaryErrorMessage);
    }

    [Fact]
    public async Task ConflictWithFailedRollbackPreservesBothDiagnostics()
    {
        var boundary = new Boundary();
        boundary.Transaction.Setup(t => t.RollbackAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rollback failed"));
        await using var session = await boundary.OpenSessionAsync();
        var command = new SqlCommandSpec(Write.CommandText, new Dictionary<string, object?>(), expectedRecordsAffected: 1);

        var result = await session.ExecuteTransactionAsync([command]);

        Assert.True(result.IsConcurrencyConflict);
        Assert.Same(command, result.FailedCommand);
        Assert.Equal(TransactionOutcome.Unknown, result.Outcome);
        Assert.Equal(0, result.RecordsAffected);
        Assert.Equal(3, result.AttemptedRecordsAffected);
        Assert.Contains("expected exactly 1", result.ErrorMessage);
        Assert.Contains("rollback failed", result.BoundaryErrorMessage);
    }

    [Fact]
    public async Task TypedCommitExposesUnknownOutcomeAndReleasesTransactionHandle()
    {
        var boundary = new Boundary();
        boundary.Transaction.Setup(t => t.CommitAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("commit failed"));
        boundary.Transaction.Setup(t => t.DisposeAsync())
            .Returns(() => ValueTask.FromException(new InvalidOperationException("cleanup failed")));
        await using var session = await boundary.OpenSessionAsync();
        await session.BeginTransactionAsync();

        var result = await session.ExecuteQueryAsync("COMMIT");

        Assert.True(result.HasError);
        Assert.Equal(TransactionOutcome.Unknown, result.TransactionResult?.Outcome);
        Assert.Equal("commit failed", result.TransactionResult?.ErrorMessage);
        Assert.Contains("cleanup failed", result.TransactionResult?.BoundaryErrorMessage);
        Assert.False(session.IsInTransaction);
    }

    [Fact]
    public void KnownPartialChangesRetainOnlyConfirmedCommittedRows()
    {
        var result = TransactionResult.PartialFailure("later operation failed", 3, 8);

        Assert.False(result.Succeeded);
        Assert.Equal(TransactionOutcome.Partial, result.Outcome);
        Assert.Equal(3, result.RecordsAffected);
        Assert.Equal(8, result.AttemptedRecordsAffected);
    }

    [Fact]
    public async Task SessionDisposalPreservesRollbackEvidenceWhenConnectionDisposalAlsoFails()
    {
        var boundary = new Boundary();
        boundary.Transaction.Setup(t => t.RollbackAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rollback lost"));
        boundary.Connection.Setup(c => c.DisposeAsync())
            .Returns(() => ValueTask.FromException(new InvalidOperationException("close failed")));
        var session = await boundary.OpenSessionAsync();
        await session.BeginTransactionAsync();

        var error = await Assert.ThrowsAsync<TransactionBoundaryException>(() => session.DisposeAsync().AsTask());

        Assert.Equal(TransactionOutcome.Unknown, error.Result.Outcome);
        Assert.Equal("rollback lost", error.Result.ErrorMessage);
        Assert.Contains("close failed", error.Result.BoundaryErrorMessage);
        Assert.False(session.IsInTransaction);
    }

    private sealed class Boundary
    {
        public Mock<DbConnection> Connection { get; } = new();
        public Mock<DbTransaction> Transaction { get; } = new();
        public Mock<DbCommand> Command { get; } = new();

        public Boundary()
        {
            Connection.Setup(c => c.OpenAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Connection.Protected().Setup<DbTransaction>("BeginDbTransaction", ItExpr.IsAny<IsolationLevel>())
                .Returns(Transaction.Object);
            Connection.Protected().Setup<ValueTask<DbTransaction>>("BeginDbTransactionAsync",
                    ItExpr.IsAny<IsolationLevel>(), ItExpr.IsAny<CancellationToken>())
                .Returns(() => ValueTask.FromResult(Transaction.Object));
            Connection.Protected().Setup<DbCommand>("CreateDbCommand").Returns(Command.Object);
            Command.Setup(c => c.ExecuteNonQueryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
            Transaction.Setup(t => t.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Transaction.Setup(t => t.RollbackAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        }

        public Task<IDbSession> OpenSessionAsync() => ConnectionDbSession.OpenAsync(
            new SqliteProvider(), Connection.Object,
            (_, _, _, _) => Task.FromResult(new QueryResult()),
            (_, _, _, _) => Task.FromResult(new List<QueryResult>()), default);
    }
}
