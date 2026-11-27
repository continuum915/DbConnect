using DbConnect.Models;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class QueryMessagesPaneViewModelTests
{
    [Fact]
    public void MessagesRows_KeepCountsDurationsAndWarningsWithTheirStatements()
    {
        var tab = new QueryTabViewModel();
        var result = new QueryResult
        {
            Statements =
            [
                new QueryStatementExecution { Index = 0, HasResultSet = true, RowCount = 3,
                    Duration = TimeSpan.FromMilliseconds(12), Messages = ["first warning"] },
                new QueryStatementExecution { Index = 1, RecordsAffected = 7,
                    Duration = TimeSpan.FromMilliseconds(24), Messages = ["second warning"] }
            ]
        };

        tab.SetExecutionResults([result]);

        Assert.Collection(tab.MessagesRows,
            first =>
            {
                Assert.Equal(1, first.StatementNumber);
                Assert.Equal("3 returned", first.Rows);
                Assert.Equal("12 ms", first.Duration);
                Assert.Equal("first warning", first.Warnings);
            },
            second =>
            {
                Assert.Equal(2, second.StatementNumber);
                Assert.Equal("7 affected", second.Rows);
                Assert.Equal("24 ms", second.Duration);
                Assert.Equal("second warning", second.Warnings);
            });
    }

    [Fact]
    public void ErrorRow_UsesExistingErrorNavigationForItsStatement()
    {
        const string sql = "SELECT 1; SELECT missing;";
        var error = new QueryResult
        {
            ExecutedSql = "SELECT missing",
            StatementStart = 10,
            ReturnedStatementIndex = 1,
            Error = new QueryErrorInfo { Summary = "no column", Position = new QueryErrorPosition(Offset: 7) },
            Statements =
            [
                new QueryStatementExecution { Index = 0, HasResultSet = true, RowCount = 1 },
                new QueryStatementExecution { Index = 1,
                    Error = new QueryErrorInfo { Summary = "no column", Position = new QueryErrorPosition(Offset: 7) } }
            ]
        };
        var tab = new QueryTabViewModel { QueryText = sql };

        tab.SetExecutionResults([error]);
        tab.NavigateToError(sql, sql, 0, error);
        tab.ErrorSpan = null;
        tab.MessagesRows[1].NavigateErrorCommand.Execute(null);

        Assert.Equal("missing", sql.Substring(tab.ErrorSpan!.Value.Start, tab.ErrorSpan.Value.Length));
        Assert.Equal("no column", tab.MessagesRows[1].Error);
    }

    [Fact]
    public void ErrorWithoutDriverPosition_NavigatesToTheFailedStatementStart()
    {
        const string sql = "SELECT 1; UPDATE broken;";
        var errorInfo = new QueryErrorInfo { Summary = "syntax error" };
        var error = new QueryResult
        {
            ExecutedSql = "UPDATE broken",
            StatementStart = 10,
            ReturnedStatementIndex = 1,
            Error = errorInfo,
            Statements = [new QueryStatementExecution { Index = 1, Error = errorInfo }]
        };
        var tab = new QueryTabViewModel { QueryText = sql };
        tab.SetExecutionResults([error]);
        tab.NavigateToError(sql, sql, 0, error);

        tab.MessagesRows[0].NavigateErrorCommand.Execute(null);

        Assert.Equal(10, tab.ErrorSpan?.Start);
    }
}
