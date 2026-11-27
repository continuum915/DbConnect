using System.Text.Json;
using DbConnect.PerformanceTests;

namespace DbConnect.Tests;

public class DirectBatchScenarioTests
{
    [Fact]
    public async Task MinimumImportCompletesBothInsertsAndReportsCommentOnlySubmission()
    {
        var scenario = new DirectBatchScenario(RepresentativeImport.Generate(1024));
        await scenario.ExecuteAsync();
        var observed = scenario.Validate();

        Assert.Equal(2, observed.VerifiedInserts);
        Assert.Equal(2, observed.CommandCount);
        Assert.Equal(2, observed.ResultCount);
        // Only the standalone GO + CRLF and the CRLF on either side of it and at the end are excluded.
        Assert.Equal(1024 - 8, observed.SubmittedCharacters);
        Assert.NotEqual(0UL, observed.SubmissionHash);
        Assert.Contains("trailing comment padding", Assert.Single(observed.ExecutionUnitDiscrepancies));

        var retained = scenario.MeasureRetention(observed);
        Assert.Equal(retained.HeapWithResultsBytes - retained.HeapWithoutResultsBytes, retained.RetainedResultBytes);
        Assert.Equal(observed.SubmissionHash, retained.SubmissionHash);
        Assert.Throws<InvalidOperationException>(() => scenario.Validate());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidationRejectsMissingOrReorderedGeneratorExpectations(bool reorder)
    {
        var fixture = RepresentativeImport.Generate(1024);
        var expected = reorder ? fixture.Expected.Reverse().ToArray() : fixture.Expected.Take(1).ToArray();
        var scenario = new DirectBatchScenario(fixture with { Expected = expected });
        await scenario.ExecuteAsync();

        Assert.Throws<InvalidDataException>(() => scenario.Validate());
    }

    [Fact]
    public async Task CompactEvidenceIsRepeatableAndSensitiveToSubmittedContent()
    {
        var fixture = RepresentativeImport.Generate(1024);
        async Task<ExecutionObservations> Observe(ImportFixture input)
        {
            var scenario = new DirectBatchScenario(input);
            await scenario.ExecuteAsync();
            return scenario.Validate();
        }

        var first = await Observe(fixture);
        var repeated = await Observe(fixture);
        var changed = await Observe(fixture with { Sql = fixture.Sql.Replace("O''Brien", "O''Briex") });
        Assert.Equal(first.SubmissionHash, repeated.SubmissionHash);
        Assert.NotEqual(first.SubmissionHash, changed.SubmissionHash);
        Assert.Equal(first.CommandCount, changed.CommandCount);
    }

    [Fact]
    public async Task SupervisorAndReportsPreserveExecutionEvidenceAndRetentionReadings()
    {
        var settings = new RunSettings(DirectBatchScenario.Id, "representative", 1024);
        var run = await new ProcessSupervisor().RunAsync(settings, 1, false, TimeSpan.FromSeconds(20));
        Assert.True(run.Outcome == "completed", run.Failure);
        var observed = Assert.IsType<ExecutionObservations>(run.Execution);
        Assert.Equal(2, observed.CommandCount);
        Assert.Equal(2, observed.VerifiedInserts);
        Assert.Equal(1016, observed.SubmittedCharacters);
        Assert.NotNull(run.Metrics);
        Assert.InRange(observed.FirstSubmissionMilliseconds, 0, run.Metrics.DurationMilliseconds);

        var directory = Path.Combine(Path.GetTempPath(), "rain-direct-" + Guid.NewGuid());
        try
        {
            await ReportWriter.WriteAsync(directory, [run, run with { Repetition = 2 }],
                new(directory, [DirectBatchScenario.Id], 2, 20, 20));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "report.json")));
            var execution = json.RootElement.GetProperty("Runs")[0].GetProperty("Execution");
            Assert.Equal(observed.SubmissionHash, execution.GetProperty("SubmissionHash").GetUInt64());
            Assert.Equal(observed.RetainedResultBytes, execution.GetProperty("RetainedResultBytes").GetInt64());
            var markdown = await File.ReadAllTextAsync(Path.Combine(directory, "report.md"));
            var table = markdown[markdown.IndexOf("| Bytes / repetition / warmup", StringComparison.Ordinal)..];
            Assert.StartsWith("| 1024 / 1 / False |", table.Split('\n')[2]);
            Assert.StartsWith("| 1024 / 2 / False |", table.Split('\n')[3]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
        Assert.Equal(new long[] { 1_048_576, 16_777_216, 67_108_864 },
            ScenarioSettings.Expand(DirectBatchScenario.Id).Select(item => item.SizeBytes));
    }
}


