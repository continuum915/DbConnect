using System.Text;
using DbConnect.PerformanceTests;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class MergeImportProxyTests
{
    [Fact]
    public void ProxyPreservesWorkloadCountsAndParserSubmissionRanges()
    {
        var fixture = MergeImportProxy.Generate(8 * RepresentativeImport.MiB);
        var expected = fixture.ProxyStatements!;
        Assert.Equal(4281, expected.Count);
        Assert.Equal(4279, expected.Count(item => item.Kind == "MERGE"));
        Assert.Equal(0, expected.Count(item => item.Kind == "INSERT"));
        Assert.DoesNotContain("INSERT INTO", fixture.Sql);
        Assert.Equal(2, expected.Count(item => item.Kind == "DELETE"));
        Assert.Equal(2, expected.Where(item => item.Kind == "DELETE").Select(item => item.Table).Distinct().Count());
        Assert.Equal(94, expected.Select(item => item.Table).Distinct().Count());
        Assert.Equal(3291, expected.Count(item => item.Table == "Product"));
        var others = expected.Where(item => item.Table != "Product").GroupBy(item => item.Table).Select(group => group.Count());
        Assert.Equal(1, others.Min());
        Assert.Equal(200, others.Max());
        Assert.Equal(8 * RepresentativeImport.MiB, Encoding.UTF8.GetByteCount(fixture.Sql));
        Assert.Equal(4281, fixture.Sql.Split("\r\nGO\r\n").Length - 1);
        Assert.EndsWith(";\r\nGO\r\n", fixture.Sql);
        Assert.DoesNotContain("BEGIN TRAN", fixture.Sql);
        Assert.DoesNotContain("COMMIT", fixture.Sql);
        Assert.DoesNotContain("/*", fixture.Sql);
        var spans = SqlBatchParser.GetStatementSpans(fixture.Sql, SqlBatchDialect.SqlServer);
        Assert.Equal(expected.Count, spans.Count);
        for (var i = 0; i < spans.Count; i++)
        {
            Assert.Equal(expected[i].Start, spans[i].Start);
            Assert.Equal(expected[i].Length, spans[i].Length);
        }
        var second = MergeImportProxy.Generate(fixture.Sql.Length);
        Assert.Equal(fixture.Metadata.Sha256, second.Metadata.Sha256);
        Assert.Equal(fixture.Sql, second.Sql);
    }

    [Fact]
    public async Task IntegratedProxyVerifiesEverySubmissionAndResult()
    {
        var run = await new ProcessSupervisor().RunAsync(
            new(IntegratedRunScenario.RunId, MergeImportProxy.Profile, 8 * RepresentativeImport.MiB),
            1, false, TimeSpan.FromSeconds(45));
        Assert.True(run.Outcome == "completed", run.Failure);
        var execution = run.Editor!.Integrated!.Execution!;
        Assert.Equal(4281, execution.CommandCount);
        Assert.Equal(4281, execution.ResultCount);
        Assert.Equal(0, execution.VerifiedInserts);
        Assert.Equal(4279, execution.VerifiedMerges);
        Assert.Equal(2, execution.VerifiedDeletes);
        Assert.Equal(94, run.Fixture!.Tables);
        Assert.False(Directory.Exists(run.EditorDataDirectory));
    }
}
