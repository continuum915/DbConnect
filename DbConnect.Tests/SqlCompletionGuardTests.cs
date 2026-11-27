using DbConnect.Services;

namespace DbConnect.Tests;

public class SqlCompletionGuardTests
{
    [Fact]
    public void CaretAfterDoubleHyphen_IsInsideLineComment()
    {
        // "SELECT -- foo" is 13 chars; caret at end is inside the line comment
        Assert.True(SqlCompletionGuard.IsInsideStringOrComment("SELECT -- foo", 13));
    }

    [Fact]
    public void CaretAfterNewlineInLineComment_IsNotSuppressed()
    {
        // "-- foo\nSELECT" — newline ends the comment; caret at 13 is in SELECT
        Assert.False(SqlCompletionGuard.IsInsideStringOrComment("-- foo\nSELECT", 13));
    }

    [Fact]
    public void CaretInsideBlockComment_IsSuppressed()
    {
        // "/* comment */SELECT" — caret at 8 is inside the block comment
        Assert.True(SqlCompletionGuard.IsInsideStringOrComment("/* comment */SELECT", 8));
    }

    [Fact]
    public void CaretAfterClosedBlockComment_IsNotSuppressed()
    {
        // "/* c */SELECT" — caret at 13 is after the closing */
        Assert.False(SqlCompletionGuard.IsInsideStringOrComment("/* c */SELECT", 13));
    }

    [Fact]
    public void CaretInsideSingleQuoteString_IsSuppressed()
    {
        // "SELECT 'foo'" — caret at 10 is inside the string literal
        Assert.True(SqlCompletionGuard.IsInsideStringOrComment("SELECT 'foo'", 10));
    }

    [Fact]
    public void CaretAfterClosedSingleQuoteString_IsNotSuppressed()
    {
        // "SELECT 'foo' " (trailing space) — caret at 13 is after the closed string
        Assert.False(SqlCompletionGuard.IsInsideStringOrComment("SELECT 'foo' ", 13));
    }

    [Fact]
    public void EscapedSingleQuoteInsideString_DoesNotCloseString()
    {
        // "SELECT 'O''Brien'" — caret at 14 is inside the string after the '' escape
        // prefix = "SELECT 'O''Bri" — still in single-quote context
        Assert.True(SqlCompletionGuard.IsInsideStringOrComment("SELECT 'O''Brien'", 14));
    }

    [Fact]
    public void CaretInsideDoubleQuoteIdentifier_IsSuppressed()
    {
        // SELECT "column" — caret at 10 is inside the double-quoted identifier
        Assert.True(SqlCompletionGuard.IsInsideStringOrComment("SELECT \"column\"", 10));
    }

    [Fact]
    public void EscapedDoubleQuoteInsideIdentifier_DoesNotCloseIdentifier()
    {
        // SELECT "col""x" — caret at 13 is after the "" escape, still inside identifier
        // prefix length 13 = `SELECT "col""` — the "" is consumed as an escape
        Assert.True(SqlCompletionGuard.IsInsideStringOrComment("SELECT \"col\"\"x\"", 13));
    }

    [Fact]
    public void EmptyString_IsNotSuppressed()
    {
        Assert.False(SqlCompletionGuard.IsInsideStringOrComment("", 0));
    }

    [Fact]
    public void CaretBeyondTextLength_IsClamped_IsNotSuppressed()
    {
        // caretOffset=999 is clamped to text.Length; "SELECT 1" has nothing to suppress
        Assert.False(SqlCompletionGuard.IsInsideStringOrComment("SELECT 1", 999));
    }
}
