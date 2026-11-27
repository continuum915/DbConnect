using Moq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class CsvBoundaryOutcomeTests
{
    [Fact]
    public async Task AcknowledgedPostCreateUncertaintyRetainsTransactionsAndDestination()
    {
        var provider = new Mock<IDatabaseProvider>();
        provider.SetupGet(p => p.Capabilities).Returns(new MySqlProvider().Capabilities);
        provider.SetupGet(p => p.ProviderName).Returns("MySQL");
        provider.Setup(p => p.ExecuteQueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryExecutionOptions?>()))
            .ReturnsAsync(new QueryResult { ErrorMessage = "metadata unavailable" });
        var creations = 0;
        provider.Setup(p => p.ExecuteNonQueryAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(() => { creations++; return 0; });
        provider.Setup(p => p.ExecuteTransactionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<SqlCommandSpec>>()))
            .ReturnsAsync(new TransactionResult { Outcome = TransactionOutcome.RolledBack, ErrorMessage = "insert failed" });
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "id\n1");
            var options = new CsvImportOptions { FilePath = path, TableName = "destination" };
            var warning = await CsvImportService.ExecuteImportAsync(session, options);
            options.RiskAcknowledgement = Assert.IsType<CsvImportAssessment>(warning.RequiredAcknowledgement).Token;
            var result = await CsvImportService.ExecuteImportAsync(session, options);
            Assert.Equal(TransactionOutcome.Unknown, result.Outcome);
            Assert.Contains("table remains", result.ErrorMessage);
            Assert.Equal(1, creations);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task InputChangesDuringAssessmentCannotTurnAppendIntoReplacement()
    {
        await using var fixture = await ProviderConformance.ProviderConformanceFixture.CreateLocalAsync("SQLite");
        await fixture.Provider.ExecuteNonQueryAsync(fixture.ConnectionString, "CREATE TABLE destination(id INTEGER); INSERT INTO destination VALUES(9)");
        var response = new TaskCompletionSource<QueryResult>();
        var provider = new Mock<IDatabaseProvider>();
        provider.SetupGet(p => p.Capabilities).Returns(fixture.Provider.Capabilities);
        provider.SetupGet(p => p.ProviderName).Returns("SQLite");
        provider.Setup(p => p.ExecuteQueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryExecutionOptions?>())).Returns(response.Task);
        provider.Setup(p => p.ExecuteTransactionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<SqlCommandSpec>>()))
            .Returns((string connection, IReadOnlyList<SqlCommandSpec> commands) => fixture.Provider.ExecuteTransactionAsync(connection, commands));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "id\n1");
            var options = new CsvImportOptions { FilePath = path, TableName = "destination", Mode = ImportMode.ExistingTable };
            var pending = CsvImportService.ExecuteImportAsync(new DatabaseSession(new ConnectionInfo { ConnectionString = fixture.ConnectionString }, provider.Object), options);
            options.TruncateBeforeImport = true;
            response.SetResult(new QueryResult { ObjectRows = [new object?[] { 1L, 0L }] });
            var result = await pending;
            Assert.True(result.Succeeded, result.ErrorMessage);
            var stored = await fixture.Provider.ExecuteQueryAsync(fixture.ConnectionString, "SELECT id FROM destination ORDER BY id");
            Assert.Equal(new[] { "1", "9" }, stored.ObjectRows.Select(r => r[0]!.ToString()));
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData(TransactionOutcome.NoChanges)]
    [InlineData(TransactionOutcome.RolledBack)]
    [InlineData(TransactionOutcome.Unknown)]
    public async Task BoundaryFailureDoesNotInventCommittedRowsOrDataRecord(TransactionOutcome outcome)
    {
        var provider = new Mock<IDatabaseProvider>();
        provider.SetupGet(p => p.Capabilities).Returns(new SqliteProvider().Capabilities);
        provider.SetupGet(p => p.ProviderName).Returns("SQLite");
        provider.Setup(p => p.ExecuteTransactionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<SqlCommandSpec>>()))
            .ReturnsAsync(new TransactionResult { Outcome = outcome, AttemptedRecordsAffected = 17, ErrorMessage = "boundary failure" });
        var session = new DatabaseSession(new ConnectionInfo(), provider.Object);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "id\n1");
            var result = await CsvImportService.ExecuteImportAsync(session, new CsvImportOptions { FilePath = path, TableName = "destination" });
            Assert.Equal(outcome, result.Outcome);
            Assert.False(result.Succeeded);
            Assert.Equal(0, result.RowsImported);
            Assert.Null(result.FailedDataRecord);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UnexpectedExecutionExceptionCannotBeReportedAsNoChanges()
    {
        var provider = new Mock<IDatabaseProvider>();
        provider.SetupGet(p => p.Capabilities).Returns(new SqliteProvider().Capabilities);
        provider.SetupGet(p => p.ProviderName).Returns("SQLite");
        provider.Setup(p => p.ExecuteTransactionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<SqlCommandSpec>>()))
            .ThrowsAsync(new IOException("Lost acknowledgement"));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "id\n1");
            var result = await CsvImportService.ExecuteImportAsync(new DatabaseSession(new ConnectionInfo(), provider.Object),
                new CsvImportOptions { FilePath = path, TableName = "destination" });
            Assert.Equal(TransactionOutcome.Unknown, result.Outcome);
            Assert.Equal(0, result.RowsImported);
            Assert.Null(result.FailedDataRecord);
        }
        finally { File.Delete(path); }
    }
}
