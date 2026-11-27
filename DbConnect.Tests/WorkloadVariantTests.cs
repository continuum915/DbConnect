using System.Text;
using DbConnect.PerformanceTests;

namespace DbConnect.Tests;

public class WorkloadVariantTests
{
    [Theory]
    [InlineData("many-small-batches", 1)]
    [InlineData("few-large-batches", 512)]
    [InlineData("long-lines", 32)]
    [InlineData("actual-parameters", 32)]
    public void ProfilesAreDeterministicExactAndExposeTheirConfiguration(string profile, int pairs)
    {
        var fixture = RepresentativeImport.Generate(16384, profile);
        Assert.Equal(fixture.Sql, RepresentativeImport.Generate(16384, profile).Sql);
        Assert.Equal(16384, Encoding.UTF8.GetByteCount(fixture.Sql));
        Assert.Equal(pairs, fixture.Metadata.Configuration!.PairsPerBatch);
        Assert.Equal(fixture.Expected.Count / 2 / pairs + 1, fixture.Metadata.GoDirectives);
        Assert.All(fixture.Expected, insert =>
        {
            Assert.StartsWith($"INSERT INTO {insert.Table}", fixture.Sql.Substring(insert.Start, insert.Length));
            Assert.Equal(';', fixture.Sql[insert.Start + insert.Length]);
            if (insert.ParentId is { } parent)
                Assert.Contains(fixture.Expected, other => other.Table == "Parents" && other.Id == parent);
        });
        Assert.Contains("O''Brien", fixture.Sql);
        Assert.EndsWith("*/\r\n", fixture.Sql);
        if (profile == "long-lines") Assert.True(fixture.Metadata.MaximumLineLength > 4000);
        var scenario = new ScriptPreparationScenario(profile == "actual-parameters" ? "parameter.detect" : "prepare.execution", fixture);
        scenario.Execute();
        Assert.Equal(profile == "actual-parameters" ? 1 : 0, scenario.Validate().DetectedParameters);
    }
    [Fact]
    public void SubstitutionValidatesEveryOutputCharacterAndItsOriginalOffset()
    {
        var fixture = RepresentativeImport.Generate(4096, "actual-parameters");
        Assert.Contains("' + @value);", fixture.Sql);
        var scenario = new ScriptPreparationScenario("parameter.substitute", fixture);
        scenario.Execute();
        scenario.Validate();
        Assert.Throws<ArgumentException>(() => new ScriptPreparationScenario("prepare.execution", fixture));
        Assert.Throws<ArgumentException>(() => new ScriptPreparationScenario("parameter.detect", RepresentativeImport.Generate(4096)));
    }

    [Theory]
    [InlineData("many-small-batches", 1024)]
    [InlineData("few-large-batches", 1024)]
    [InlineData("long-lines", 8192)]
    [InlineData("actual-parameters", 1024)]
    public void MinimumProfilesHaveBalancedSqlAndOnlyCountActualDirectives(string profile, int bytes)
    {
        var fixture = RepresentativeImport.Generate(bytes, profile);
        Assert.Equal(bytes, fixture.Sql.Length);
        Assert.Equal(fixture.Sql.Length, fixture.Metadata.Utf16Characters);
        Assert.Equal(fixture.Sql.Split("\r\n").Length, fixture.Metadata.Lines);
        Assert.Equal(fixture.Sql.Split("\r\n").Max(line => line.Length), fixture.Metadata.MaximumLineLength);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Sql))), fixture.Metadata.Sha256);
        // Independent lexical oracle: consume complete strings and comments, then inspect SQL tokens.
        var lexical = System.Text.RegularExpressions.Regex.Replace(fixture.Sql,
            @"'(?:''|[^'])*'|--[^\r\n]*|/\*[\s\S]*?\*/", " ");
        Assert.DoesNotContain("'", lexical);
        Assert.DoesNotContain("/*", lexical);
        Assert.Equal(fixture.Metadata.GoDirectives,
            System.Text.RegularExpressions.Regex.Matches(lexical, @"(?m)^GO\r?$").Count);
        Assert.Equal(fixture.Metadata.Inserts, System.Text.RegularExpressions.Regex.Matches(lexical, @"\bINSERT INTO\b").Count);
        Assert.Equal(profile == "actual-parameters" ? fixture.Metadata.Inserts / 2 : 0,
            System.Text.RegularExpressions.Regex.Matches(lexical, @"@value\b").Count);
        Assert.DoesNotContain("@fake", lexical);
        Assert.All(fixture.Sql, character => Assert.InRange((int)character, 0, 127));
        Assert.DoesNotContain("\n", fixture.Sql.Replace("\r\n", ""));
    }

    [Fact]
    public async Task VariantsExpandAndRunThroughTheSupervisor()
    {
        var settings = ScenarioSettings.Expand("variants.all").ToArray();
        Assert.Equal(51, settings.Length);
        Assert.Equal(51, settings.Distinct().Count());
        Assert.All(settings, ScenarioSettings.Validate);
        foreach (var setting in new[] { new RunSettings("prepare.spans", "long-lines", 8192),
                     new RunSettings("parameter.substitute", "actual-parameters", 4096) })
        {
            var run = await new ProcessSupervisor().RunAsync(setting, 1, false, TimeSpan.FromSeconds(20));
            Assert.True(run.Outcome == "completed", run.Failure);
            Assert.Equal(setting.Profile, run.Fixture!.Profile);
            Assert.NotNull(run.Fixture.Configuration);
            Assert.NotNull(run.Observations);
            var directory = Path.Combine(Path.GetTempPath(), "rain-variants-" + Guid.NewGuid());
            try
            {
                await ReportWriter.WriteAsync(directory, [run], new(directory, [setting.ScenarioId], 1, 20, 20));
                var markdown = await File.ReadAllTextAsync(Path.Combine(directory, "report.md"));
                Assert.Contains(setting.Profile, markdown);
                Assert.Contains("Pairs per batch / literal padding / actual parameters", markdown);
                var json = await File.ReadAllTextAsync(Path.Combine(directory, "report.json"));
                Assert.Contains("\"Configuration\"", json);
                Assert.Contains(run.Fixture.Sha256, json);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
