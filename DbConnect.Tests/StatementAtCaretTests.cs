using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Run-statement-at-caret finds boundaries with the same per-dialect tokenizer as script splitting.</summary>
public sealed class StatementAtCaretTests : MainWindowTestBase
{
    private static string At(string sql, string marker, SqlBatchDialect dialect, int shift = 0)
    {
        var caret = sql.IndexOf(marker, StringComparison.Ordinal) + shift;
        var span = SqlBatchParser.FindStatementAt(sql, caret, dialect);
        Assert.NotNull(span);
        return sql.Substring(span!.Value.Start, span.Value.Length);
    }

    [Fact]
    public void Standard_PicksStatementUnderCaret()
    {
        const string sql = "SELECT 1;\nSELECT 2;\nSELECT 3;";

        Assert.Equal("SELECT 2", At(sql, "2", SqlBatchDialect.Standard));
        Assert.Equal("SELECT 3", At(sql, "SELECT 3", SqlBatchDialect.Standard));
    }

    [Fact]
    public void Standard_CaretRightAfterTerminatorBelongsToThatStatement()
    {
        const string sql = "SELECT 1;\nSELECT 2;";

        Assert.Equal("SELECT 1", At(sql, "SELECT 1;", SqlBatchDialect.Standard, "SELECT 1;".Length));
    }

    [Fact]
    public void Standard_EmptyScriptHasNoStatement()
    {
        Assert.Null(SqlBatchParser.FindStatementAt("  \n ", 1));
    }

    [Fact]
    public void PostgreSql_CaretInsideDollarBodySelectsWholeStatement()
    {
        const string sql = "SELECT 0;\nCREATE FUNCTION f() RETURNS int AS $$ BEGIN PERFORM 1; RETURN 2; END; $$ LANGUAGE plpgsql;\nSELECT 9;";

        var statement = At(sql, "RETURN 2", SqlBatchDialect.PostgreSql);

        Assert.StartsWith("CREATE FUNCTION", statement);
        Assert.EndsWith("LANGUAGE plpgsql", statement);
    }

    [Fact]
    public void MySql_CaretInsideDelimiterRoutineSelectsWholeRoutine()
    {
        var sql = "DELIMITER //\nCREATE PROCEDURE p()\nBEGIN\n  SELECT 1;\n  SELECT 2;\nEND//\nDELIMITER ;\nSELECT 3;";

        var statement = At(sql, "SELECT 2", SqlBatchDialect.MySql);

        Assert.StartsWith("CREATE PROCEDURE p()", statement);
        Assert.EndsWith("END", statement);
        Assert.Equal("SELECT 3", At(sql, "SELECT 3", SqlBatchDialect.MySql));
    }

    [Fact]
    public void SqlServer_CaretInsideGoBatchSelectsWholeBatch()
    {
        var sql = "CREATE TABLE t (id int)\nGO\nINSERT INTO t VALUES (1)\nINSERT INTO t VALUES (2)\nGO\nSELECT * FROM t";

        var statement = At(sql, "VALUES (2)", SqlBatchDialect.SqlServer);

        Assert.Equal("INSERT INTO t VALUES (1)\nINSERT INTO t VALUES (2)", statement.Replace("\r", ""));
    }

    [Fact]
    public void Sqlite_CaretInsideTriggerBodySelectsWholeTrigger()
    {
        const string sql = "SELECT 1;\nCREATE TRIGGER trg AFTER INSERT ON t BEGIN UPDATE t SET a = 1; INSERT INTO log VALUES (1); END;\nSELECT 2;";

        var statement = At(sql, "INSERT INTO log", SqlBatchDialect.Sqlite);

        Assert.StartsWith("CREATE TRIGGER", statement);
        Assert.EndsWith("END", statement);
    }

    [Fact]
    public async Task RunSelection_WithNoSelection_RunsTheStatementAtTheCaret()
    {
        var dbService = new DatabaseService();
        var connection = dbService.CreateSession(new ConnectionInfo
        {
            Name = "Local",
            Provider = "SQLite",
            ConnectionString = "Data Source=:memory:"
        });
        var vm = CreateMainWindowViewModel(dbService);
        var tab = vm.NewQueryTabWithSession();
        vm.ActiveSessionOptions.Add(connection);
        tab.SelectedSession = connection;

        tab.QueryText = "SELECT 1 AS first_col;\nSELECT 2 AS second_col;";
        tab.SelectedQueryText = string.Empty;
        tab.CaretOffset = tab.QueryText.IndexOf("second_col", StringComparison.Ordinal);
        await vm.ExecuteSelectionCommand.ExecuteAsync(null);

        var result = Assert.Single(tab.Results);
        Assert.False(result.HasError, result.Error?.ToString());
        Assert.Equal("SELECT 2 AS second_col", result.ExecutedSql);
    }
}
