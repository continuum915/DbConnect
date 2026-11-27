using System.Text;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Reading a script file statement by statement without loading it whole (mysql-duckdb-backup/02).</summary>
public sealed class SqlScriptReaderTests
{
    // DELIMITER changes, a compound body holding ';', and quotes and comments holding the delimiters, so a chunk
    // boundary can fall anywhere that matters.
    private const string MySqlScript = """
        -- DbConnect SQL dump; a comment with ; and $$
        SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0;
        CREATE TABLE `t` (`id` int PRIMARY KEY, `note` text);
        INSERT INTO `t` VALUES (1, 'semi; colon'), (2, 'dollar $$ sign');
        INSERT INTO `t` VALUES (3, 'it''s "quoted" /* not a comment */');
        DELIMITER $$
        CREATE TRIGGER `t_audit` AFTER INSERT ON `t` FOR EACH ROW BEGIN
            INSERT INTO audit VALUES (NEW.id, ';'); -- inner ; stays
        END$$
        CREATE PROCEDURE `p`() BEGIN SELECT 1; SELECT 2; END$$
        DELIMITER ;
        /* block ; comment */ INSERT INTO `t` VALUES (4, NULL);
        SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS;
        """;

    // DELIMITER is a directive only at the start of a line; after a cut the reader's buffer starts with the statement,
    // which must not make a mid-line DELIMITER one.
    private const string MidLineDelimiterScript = "SELECT 1; DELIMITER $$\nSELECT 2;\nSELECT 3; SELECT 4$$ SELECT 5;";

    public static TheoryData<string, int> Scripts()
    {
        var data = new TheoryData<string, int>();
        foreach (var size in new[] { 1, 2, 7, 64, 65536 })
        {
            data.Add(MySqlScript, size);
            data.Add(MidLineDelimiterScript, size);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task StatementsMatchTheWholeScriptsAtAnyChunkSize(string script, int chunkSize)
    {
        var expected = SqlBatchParser.SplitStatements(script, SqlBatchDialect.MySql);

        var read = new List<SqlScriptStatement>();
        await foreach (var statement in SqlScriptReader.ReadAsync(new StringReader(script), SqlBatchDialect.MySql, CancellationToken.None, chunkSize))
            read.Add(statement);

        Assert.Equal(expected, read.Select(s => s.Text));
        Assert.Equal(Enumerable.Range(1, expected.Count), read.Select(s => s.Number));
        Assert.Equal(script == MySqlScript ? 8 : 4, expected.Count);
    }

    [Fact]
    public async Task AStatementLongerThanAChunkIsReadWhole()
    {
        var values = string.Join(", ", Enumerable.Range(1, 20_000).Select(i => $"({i}, 'row {i}')"));
        var script = $"INSERT INTO t VALUES {values};\nSELECT 1;";

        var read = new List<string>();
        await foreach (var statement in SqlScriptReader.ReadAsync(new StringReader(script), SqlBatchDialect.MySql, CancellationToken.None, 1024))
            read.Add(statement.Text);

        Assert.Equal([$"INSERT INTO t VALUES {values}", "SELECT 1"], read);
    }

    [Fact]
    public async Task SqlServerScriptsAreRefusedSinceGoBatchesAreNotStatements()
    {
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in SqlScriptReader.ReadAsync(new StringReader("SELECT 1\nGO"), SqlBatchDialect.SqlServer, CancellationToken.None)) { }
        });
    }
}
