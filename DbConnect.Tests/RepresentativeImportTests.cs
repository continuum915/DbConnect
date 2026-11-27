using System.Text;
using DbConnect.PerformanceTests;

namespace DbConnect.Tests;

public class RepresentativeImportTests
{
    [Fact]
    public void GeneratorIsRepeatableAndAccountsForExactAsciiCrLfBytes()
    {
        var first = RepresentativeImport.Generate(4096);
        var second = RepresentativeImport.Generate(4096);
        Assert.Equal(first.Sql, second.Sql);
        Assert.Equal(first.Metadata.Sha256, second.Metadata.Sha256);
        Assert.StartsWith("-- GO ; @comment :comment $2 ?\r\n/* misleading\r\nGO\r\n; @comment */\r\nINSERT INTO Parents (Id, Name) VALUES (1, 'O''Brien 3891867948; GO @fake :fake $1 ?');\r\n", first.Sql);
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(first.Sql));
        Assert.Equal(4096, first.Metadata.EncodedBytes);
        Assert.Equal(4096, first.Metadata.Utf16Characters);
        Assert.All(first.Sql, character => Assert.InRange((int)character, 0, 127));
        Assert.DoesNotContain("\n", first.Sql.Replace("\r\n", ""));
        Assert.Contains("O''Brien", first.Sql);
        Assert.Contains("\r\nGO\r\n", first.Sql);
        Assert.Equal(2, first.Metadata.Tables);
        Assert.True(first.Metadata.Inserts >= 2);
    }
    [Fact]
    public void MinimumSizeStillContainsRelatedTablesAndCompletePadding()
    {
        var fixture = RepresentativeImport.Generate(1024);
        Assert.Equal(1024, fixture.Sql.Length);
        Assert.Equal(2, fixture.Metadata.Inserts);
        Assert.Equal(new[] { "Parents", "Children" }, fixture.Expected.Select(insert => insert.Table));
        Assert.EndsWith("*/\r\n", fixture.Sql);
    }

    [Theory]
    [InlineData("prepare.parameters")]
    [InlineData("prepare.split")]
    [InlineData("prepare.spans")]
    [InlineData("prepare.execution")]
    [InlineData("prepare.highlighter")]
    public void ScenariosPreserveGeneratedInsertRangesAndReportDialectDifferences(string id)
    {
        var fixture = RepresentativeImport.Generate(4096);
        foreach (var insert in fixture.Expected)
        {
            Assert.StartsWith($"INSERT INTO {insert.Table}", fixture.Sql.Substring(insert.Start, insert.Length));
            Assert.Equal(';', fixture.Sql[insert.Start + insert.Length]);
            if (insert.ParentId is { } parentId)
                Assert.Contains(fixture.Expected, parent => parent.Table == "Parents" && parent.Id == parentId);
        }
        var scenario = new ScriptPreparationScenario(id, fixture);
        scenario.Execute();
        var result = scenario.Validate();
        Assert.Equal(fixture.Metadata.Inserts, result.ExpectedInserts);
        if (id is "prepare.parameters" or "prepare.execution")
            Assert.Equal(0, result.DetectedParameters);
        if (id != "prepare.parameters")
        {
            Assert.Equal(fixture.Metadata.Inserts + 1, result.SpanCount);
            Assert.NotEmpty(result.ExecutionUnitDiscrepancies);
        }
    }

    [Fact]
    public async Task SupervisorCarriesFixtureMetadataAndObservations()
    {
        var run = await new ProcessSupervisor().RunAsync(new("prepare.execution", "representative", 4096),
            1, false, TimeSpan.FromSeconds(20));
        Assert.True(run.Outcome == "completed", run.Failure);
        Assert.Equal(4096, run.Fixture!.EncodedBytes);
        Assert.Equal(0, run.Observations!.DetectedParameters);
        Assert.NotEmpty(run.Observations.ExecutionUnitDiscrepancies);
        Assert.NotNull(run.Metrics);
    }

    [Fact]
    public void PreparationSelectionExpandsAllPrimarySizes()
    {
        var options = RunnerOptions.Parse(["--scenarios", "prepare.all"]);
        Assert.Equal(5, options.Scenarios.Length);
        foreach (var scenario in options.Scenarios)
            Assert.Equal(new long[] { 1_048_576, 16_777_216, 67_108_864 },
                ScenarioSettings.Expand(scenario).Select(settings => settings.SizeBytes));
    }
}
