using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>RAIN-P2-09 — Parsing MySQL's information_schema COLUMN_TYPE literal for ENUM/SET.</summary>
public class MySqlEnumSetLiteralParserTests
{
    [Fact]
    public void Parse_SimpleEnum_ReturnsMembersInOrder()
    {
        Assert.Equal(["small", "medium", "large"], MySqlEnumSetLiteralParser.Parse("enum('small','medium','large')"));
    }

    [Fact]
    public void Parse_SimpleSet_ReturnsMembersInOrder()
    {
        Assert.Equal(["read", "write", "execute"], MySqlEnumSetLiteralParser.Parse("set('read','write','execute')"));
    }

    [Fact]
    public void Parse_MemberWithEscapedQuote_UnescapesDoubledQuote()
    {
        Assert.Equal(["it's here"], MySqlEnumSetLiteralParser.Parse("enum('it''s here')"));
    }

    [Fact]
    public void Parse_MemberContainingComma_KeepsCommaInsideTheValue()
    {
        Assert.Equal(["a,b", "c"], MySqlEnumSetLiteralParser.Parse("enum('a,b','c')"));
    }

    [Fact]
    public void Parse_NoParentheses_ReturnsEmpty()
    {
        Assert.Empty(MySqlEnumSetLiteralParser.Parse("ENUM"));
    }

    [Fact]
    public void Parse_EmptyMemberList_ReturnsEmpty()
    {
        Assert.Empty(MySqlEnumSetLiteralParser.Parse("enum()"));
    }
}
