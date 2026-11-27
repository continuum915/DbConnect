using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Which statements end the transaction a session has open, and how.</summary>
public sealed class TransactionControlTests
{
    [Theory]
    [InlineData("COMMIT")]
    [InlineData("commit;")]
    [InlineData("  COMMIT  ;  ")]
    [InlineData("COMMIT TRANSACTION")]
    [InlineData("COMMIT TRAN")]
    [InlineData("COMMIT WORK;")]
    [InlineData("-- finish up\nCOMMIT;")]
    [InlineData("/* done */ COMMIT;")]
    [InlineData("\r\n\tCOMMIT")]
    public void Classify_IsCommit_ForStatementsThatCommit(string sql) =>
        Assert.Equal(TransactionEnd.Commit, TransactionControl.Classify(sql));

    [Theory]
    [InlineData("ROLLBACK")]
    [InlineData("rollback;")]
    [InlineData("ROLLBACK TRANSACTION")]
    [InlineData("ROLLBACK WORK")]
    [InlineData("/* undo */ ROLLBACK;")]
    public void Classify_IsRollback_ForStatementsThatRollBack(string sql) =>
        Assert.Equal(TransactionEnd.Rollback, TransactionControl.Classify(sql));

    [Theory]
    [InlineData("ROLLBACK TO SAVEPOINT s1")]
    [InlineData("rollback to s1;")]
    [InlineData("SAVEPOINT s1")]
    [InlineData("RELEASE SAVEPOINT s1")]
    [InlineData("BEGIN")]
    [InlineData("SELECT 1")]
    [InlineData("SELECT 'COMMIT'")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE commits SET n = 1")]
    [InlineData("COMMITTED")]
    [InlineData("COMMIT PREPARED 'tx1'")]
    [InlineData("ENDING")]
    [InlineData("END")]
    [InlineData("END TRANSACTION;")]
    [InlineData("END;")]
    [InlineData("END IF")]
    [InlineData("-- COMMIT\nSELECT 1")]
    [InlineData("/* COMMIT */ SELECT 1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";")]
    public void Classify_IsNone_ForEverythingElse(string sql) =>
        Assert.Equal(TransactionEnd.None, TransactionControl.Classify(sql));
}
