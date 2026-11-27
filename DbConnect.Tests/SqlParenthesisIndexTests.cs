using DbConnect.Services;

namespace DbConnect.Tests;

public class SqlParenthesisIndexTests
{
    [Theory]
    [InlineData("(()", 1, 1, 2)]
    [InlineData(")()", 1, 1, 2)]
    [InlineData("(())()", 4, 0, 3)]
    [InlineData("(())()", 5, 4, 5)]
    public void UnmatchedAndAdjacentParenthesesKeepCaretPreference(string sql, int caret, int opening, int closing)
    {
        Assert.Equal(((int Opening, int Closing)?)(opening, closing),
            SqlEditorStructure.Analyze(sql).FindMatchingParentheses(caret));
    }

    [Fact]
    public void ManyPairsUseCompactStorage()
    {
        var sql = string.Concat(Enumerable.Repeat("()", 100_000));
        _ = SqlEditorStructure.Analyze("()");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var structure = SqlEditorStructure.Analyze(sql);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(((int Opening, int Closing)?)(199_998, 199_999), structure.FindMatchingParentheses(sql.Length));
        // Includes geometric list growth; the former dictionary allocated over 17 MB here.
        Assert.True(allocated < 5 * 1024 * 1024, $"Pair analysis allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void MatchingAgreesWithAStackOracleAtEveryCaret()
    {
        var random = new Random(1729);
        for (var example = 0; example < 100; example++)
        {
            var sql = new string(Enumerable.Range(0, 100).Select(_ => "() x\n"[random.Next(5)]).ToArray());
            var openings = new Stack<int>();
            var expected = new Dictionary<int, (int Opening, int Closing)>();
            for (var offset = 0; offset < sql.Length; offset++)
            {
                if (sql[offset] == '(') openings.Push(offset);
                else if (sql[offset] == ')' && openings.TryPop(out var opening))
                    expected[opening] = expected[offset] = (opening, offset);
            }
            var structure = SqlEditorStructure.Analyze(sql);
            var foldings = expected.Values.Distinct()
                .Where(pair => sql.AsSpan(pair.Opening, pair.Closing - pair.Opening + 1).IndexOf('\n') >= 0)
                .OrderBy(pair => pair.Opening)
                .Select(pair => new StatementSpan(pair.Opening, pair.Closing - pair.Opening + 1));
            Assert.Equal(foldings, structure.Foldings);
            for (var caret = -1; caret <= sql.Length + 1; caret++)
            {
                (int Opening, int Closing)? pair = null;
                if (caret >= 0 && caret <= sql.Length &&
                    (expected.TryGetValue(caret - 1, out var previous) || expected.TryGetValue(caret, out previous)))
                    pair = previous;
                Assert.Equal(pair, structure.FindMatchingParentheses(caret));
            }
        }
    }
}
