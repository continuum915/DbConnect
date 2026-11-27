using Xunit;
using DbConnect.Services;

namespace DbConnect.Tests;

public class SqlKeywordProviderTests
{
    [Fact]
    public void GetKeywords_NullProvider_ReturnsOnlyGenericKeywords()
    {
        var result = SqlKeywordProvider.GetKeywords("UNKNOWN ", null);
        Assert.Contains("SELECT", result);
        Assert.DoesNotContain("RETURNING", result);
        Assert.DoesNotContain("PRAGMA", result);
    }

    [Fact]
    public void GetKeywords_PostgreSQL_IncludesReturning()
    {
        var result = SqlKeywordProvider.GetKeywords("UNKNOWN ", "PostgreSQL");
        Assert.Contains("RETURNING", result);
    }

    [Fact]
    public void GetKeywords_SqlServer_IncludesTop()
    {
        var result = SqlKeywordProvider.GetKeywords("UNKNOWN ", "SQL Server");
        Assert.Contains("TOP", result);
    }

    [Fact]
    public void GetKeywords_MySQL_IncludesOnDuplicateKey()
    {
        var result = SqlKeywordProvider.GetKeywords("UNKNOWN ", "MySQL");
        Assert.Contains("ON DUPLICATE KEY UPDATE", result);
    }

    [Fact]
    public void GetKeywords_SQLite_IncludesPragma()
    {
        var result = SqlKeywordProvider.GetKeywords("", "SQLite");
        Assert.Contains("PRAGMA", result);
    }

    [Fact]
    public void GetKeywords_DuckDB_IncludesSummarize()
    {
        var result = SqlKeywordProvider.GetKeywords("", "DuckDB");
        Assert.Contains("SUMMARIZE", result);
    }
}
