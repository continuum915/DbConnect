using System;
using DbConnect.Services;
using Xunit;

namespace DbConnect.Tests;

public class UserManagementWriteTests
{
    [Fact]
    public void PostgreSqlProvider_GenerateCreateUserSql_ContainsUsernameAndMaskedPassword()
    {
        var provider = new PostgreSqlProvider();
        var sql = provider.GenerateCreateUserSql("alice", "•••");

        Assert.Contains("alice", sql);
        Assert.Contains("CREATE USER", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("•••", sql);
    }

    [Fact]
    public void PostgreSqlProvider_GenerateDropUserSql_ContainsUsername()
    {
        var provider = new PostgreSqlProvider();
        var sql = provider.GenerateDropUserSql("alice");

        Assert.Contains("alice", sql);
        Assert.Contains("DROP USER", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostgreSqlProvider_GenerateGrantSql_WithObject_IncludesOnClause()
    {
        var provider = new PostgreSqlProvider();
        var sql = provider.GenerateGrantSql("alice", "SELECT", "public.orders");

        Assert.Contains("SELECT", sql);
        Assert.Contains("public.orders", sql);
        Assert.Contains("TO", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostgreSqlProvider_GenerateGrantSql_WithoutObject_NoOnClause()
    {
        var provider = new PostgreSqlProvider();
        var sql = provider.GenerateGrantSql("alice", "CONNECT", null);

        Assert.DoesNotContain(" ON ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TO", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySqlProvider_GenerateCreateUserSql_UsesMysqlHostSyntax()
    {
        var provider = new MySqlProvider();
        var sql = provider.GenerateCreateUserSql("bob", "•••");

        Assert.Contains("bob", sql);
        Assert.Contains("@'%'", sql);
        Assert.Contains("IDENTIFIED BY", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqliteProvider_SupportsUserWrite_IsFalse()
    {
        var provider = new SqliteProvider();

        Assert.False(provider.Capabilities.SupportsUserWrite);
    }

    [Fact]
    public void SqlServerProvider_SupportsUserWrite_IsFalse()
    {
        var provider = new SqlServerProvider();

        Assert.False(provider.Capabilities.SupportsUserWrite);
    }

    [Fact]
    public void PostgreSqlProvider_SupportsUserWrite_IsTrue()
    {
        var provider = new PostgreSqlProvider();

        Assert.True(provider.Capabilities.SupportsUserWrite);
    }

    [Fact]
    public void MySqlProvider_SupportsUserWrite_IsTrue()
    {
        var provider = new MySqlProvider();

        Assert.True(provider.Capabilities.SupportsUserWrite);
    }
}
