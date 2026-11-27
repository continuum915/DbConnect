using DbConnect.Services;

namespace DbConnect.Tests;

public class IncrementalSqlStructureTests
{
    [Theory]
    [InlineData(SqlBatchDialect.Standard)]
    [InlineData(SqlBatchDialect.PostgreSql)]
    [InlineData(SqlBatchDialect.SqlServer)]
    [InlineData(SqlBatchDialect.Sqlite)]
    public void InteriorWordEditsReuseTheStructureWithoutReadingTheFullDocument(SqlBatchDialect dialect)
    {
        var sql = "/* header\n */\nSELECT (\nCustomerName, 12345\n);";
        var tracker = new SqlEditorStructureTracker(sql, dialect);
        var original = tracker.Structure;
        Edit(sql.IndexOf("Name", StringComparison.Ordinal), 1, "XYZ");
        Edit(sql.IndexOf("234", StringComparison.Ordinal), 2, "");
        Edit(sql.IndexOf("Customer", StringComparison.Ordinal) + 2, 1, "q");
        Assert.Same(original, tracker.Structure);

        void Edit(int offset, int removed, string insertion)
        {
            Assert.True(tracker.PrepareEdit(offset, removed, insertion, (start, length) => sql.Substring(start, length)));
            sql = sql.Remove(offset, removed).Insert(offset, insertion);
            Assert.True(tracker.CompleteEdit(() => throw new Exception("Unexpected full read"), dialect));
            Equivalent(sql, tracker.Structure, dialect);
        }
    }

    [Theory]
    [InlineData("'", SqlBatchDialect.Standard)]
    [InlineData("/*", SqlBatchDialect.Standard)]
    [InlineData("--", SqlBatchDialect.Standard)]
    [InlineData("(", SqlBatchDialect.Standard)]
    [InlineData("\n", SqlBatchDialect.Standard)]
    [InlineData("$tag$", SqlBatchDialect.PostgreSql)]
    [InlineData("DELIMITER //", SqlBatchDialect.MySql)]
    public void SyntaxChangesFallBackToTheFullAnalyzer(string insertion, SqlBatchDialect dialect)
    {
        var sql = "SELECT (\nCustomerName\n);";
        var tracker = new SqlEditorStructureTracker(sql, dialect);
        var offset = sql.IndexOf("Name", StringComparison.Ordinal);
        Assert.False(tracker.PrepareEdit(offset, 0, insertion, (start, length) => sql.Substring(start, length)));
        sql = sql.Insert(offset, insertion);
        Assert.False(tracker.CompleteEdit(() => sql, dialect));
        Equivalent(sql, tracker.Structure, dialect);
    }

    [Fact]
    public void MultipleChangesAndDialectChangesFallBack()
    {
        var sql = "SELECT CustomerName;";
        var tracker = new SqlEditorStructureTracker(sql);
        Assert.True(tracker.PrepareEdit(9, 0, "x", Read));
        sql = sql.Insert(9, "x");
        Assert.False(tracker.PrepareEdit(10, 0, "y", Read));
        sql = sql.Insert(10, "y");
        Assert.False(tracker.CompleteEdit(() => sql, SqlBatchDialect.Standard));
        Equivalent(sql, tracker.Structure, SqlBatchDialect.Standard);
        Assert.True(tracker.PrepareEdit(9, 0, "z", Read));
        sql = sql.Insert(9, "z");
        Assert.False(tracker.CompleteEdit(() => sql, SqlBatchDialect.PostgreSql));
        Equivalent(sql, tracker.Structure, SqlBatchDialect.PostgreSql);
        string Read(int start, int length) => sql.Substring(start, length);
    }

    [Theory]
    [InlineData(SqlBatchDialect.Standard)]
    [InlineData(SqlBatchDialect.PostgreSql)]
    [InlineData(SqlBatchDialect.SqlServer)]
    [InlineData(SqlBatchDialect.Sqlite)]
    [InlineData(SqlBatchDialect.MySql)]
    public void MixedInsertDeleteReplaceAndUndoSequencesMatchFullAnalysis(SqlBatchDialect dialect)
    {
        var sql = "SELECT (\nCustomerName, 'quoted ( text)', /* block\n */ 12345\n);\n";
        var tracker = new SqlEditorStructureTracker(sql, dialect);
        var random = new Random(1729);
        string[] insertions = ["x", "E", "_", "12", "", "'", "/*", "*/", "--", "\n", "(", ")", "$tag$"];
        for (var step = 0; step < 150; step++)
        {
            var offset = random.Next(sql.Length + 1);
            var removed = Math.Min(random.Next(3), sql.Length - offset);
            var insertion = insertions[random.Next(insertions.Length)];
            var previous = sql;
            Apply(offset, removed, insertion);
            Apply(offset, insertion.Length, previous.Substring(offset, removed));
        }
        void Apply(int offset, int removed, string insertion)
        {
            tracker.PrepareEdit(offset, removed, insertion, (start, length) => sql.Substring(start, length));
            sql = sql.Remove(offset, removed).Insert(offset, insertion);
            tracker.CompleteEdit(() => sql, dialect);
            Equivalent(sql, tracker.Structure, dialect);
        }
    }

    [Fact]
    public void ParenthesisAnchorsStayValidAfterShiftsAndDoNotStartInsideMultilineQuotes()
    {
        var sql = string.Concat(Enumerable.Repeat("SELECT (CustomerName);\n", 1000)) +
            "SELECT 'long quote\n" + new string('x', 10000) + "\nCustomerName';\nSELECT (CustomerName);";
        var tracker = new SqlEditorStructureTracker(sql, SqlBatchDialect.PostgreSql);
        for (var step = 0; step < 20; step++)
        {
            var offset = step % 2 == 0 ? 10 : sql.LastIndexOf("SELECT ", StringComparison.Ordinal) + 10;
            Assert.True(tracker.PrepareEdit(offset, 0, "x", Read));
            sql = sql.Insert(offset, "x");
            Assert.True(tracker.CompleteEdit(() => throw new Exception("Unexpected full read"), SqlBatchDialect.PostgreSql));
            Equivalent(sql, tracker.Structure, SqlBatchDialect.PostgreSql);
        }
        var quotedOffset = sql.IndexOf("\nCustomerName'", StringComparison.Ordinal) + 4;
        Assert.False(tracker.PrepareEdit(quotedOffset, 0, "z", Read));
        sql = sql.Insert(quotedOffset, "z");
        Assert.False(tracker.CompleteEdit(() => sql, SqlBatchDialect.PostgreSql));
        Equivalent(sql, tracker.Structure, SqlBatchDialect.PostgreSql);
        string Read(int start, int length) => sql.Substring(start, length);
    }

    private static void Equivalent(string sql, SqlEditorStructure actual, SqlBatchDialect dialect)
    {
        var expected = SqlEditorStructure.Analyze(sql, dialect);
        Assert.Equal(expected.Foldings, actual.Foldings);
        for (var caret = -1; caret <= sql.Length + 1; caret++)
            Assert.Equal(expected.FindMatchingParentheses(caret), actual.FindMatchingParentheses(caret));
    }
}
