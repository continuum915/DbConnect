using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;
using Xunit;

namespace DbConnect.Tests;

public class DdlTranslationServiceTests
{
    private readonly DdlTranslationService _svc = new();

    [Fact]
    public void SqliteInteger_ToPostgres_MappedToBigint()
    {
        var (ddl, _) = _svc.TranslateDdl(
            "CREATE TABLE t (id INTEGER, name TEXT);",
            "SQLite", "PostgreSQL");
        Assert.Contains("BIGINT", ddl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServerNvarchar_ToSqlite_MappedToText()
    {
        var (ddl, _) = _svc.TranslateDdl(
            "CREATE TABLE t (name NVARCHAR(100));",
            "SQL Server", "SQLite");
        Assert.Contains("TEXT", ddl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoubleQuote_ToSqlServer_ConvertedToBrackets()
    {
        var (ddl, _) = _svc.TranslateDdl(
            "SELECT \"id\" FROM \"users\";",
            "PostgreSQL", "SQL Server");
        Assert.Contains("[id]", ddl);
        Assert.Contains("[users]", ddl);
    }

    [Fact]
    public void CompatibilityReport_FlagsSerial_ForSqlServer()
    {
        var issues = _svc.GetCompatibilityReport(
            "CREATE TABLE t (id SERIAL PRIMARY KEY);",
            "SQL Server");
        Assert.Contains(issues, i => i.Contains("SERIAL", StringComparison.OrdinalIgnoreCase));
    }
}

public class DataMaskingServiceTests
{
    private readonly DataMaskingService _svc = new();
    private static readonly Dictionary<string, string?> EmptyRow = new();

    [Fact]
    public void NullOut_ReturnsNull()
    {
        var rule = new MaskingRule { ColumnName = "email", Method = MaskingMethod.NullOut };
        Assert.Null(_svc.ApplyMask(rule, "test@example.com", EmptyRow));
    }

    [Fact]
    public void ConstantValue_ReturnsConstant()
    {
        var rule = new MaskingRule
        {
            ColumnName = "password",
            Method = MaskingMethod.ConstantValue,
            ConstantValue = "***"
        };
        Assert.Equal("***", _svc.ApplyMask(rule, "secret", EmptyRow));
    }

    [Fact]
    public void HashSha256_Returns64CharHex()
    {
        var rule = new MaskingRule { ColumnName = "ssn", Method = MaskingMethod.HashSha256 };
        var result = _svc.ApplyMask(rule, "123-45-6789", EmptyRow);
        Assert.NotNull(result);
        Assert.Equal(64, result!.Length);
        Assert.Matches("^[0-9a-f]+$", result);
    }

    [Fact]
    public void RegexReplace_SubstitutesPattern()
    {
        var rule = new MaskingRule
        {
            ColumnName = "phone",
            Method = MaskingMethod.RegexReplace,
            RegexPattern = @"\d",
            RegexReplacement = "X"
        };
        var result = _svc.ApplyMask(rule, "555-1234", EmptyRow);
        Assert.Equal("XXX-XXXX", result);
    }
}

public class AutomationServiceTests
{
    private readonly AutomationService _svc = new();

    [Fact]
    public async Task SaveJob_And_LoadJobs_RoundTrip()
    {
        var job = new AutomationJob
        {
            Name = "TestJob_" + Guid.NewGuid().ToString("N"),
            Type = JobType.RunQuery,
            SqlOrFilePath = "SELECT 1",
            IsEnabled = true
        };

        await _svc.SaveJobAsync(job);
        var jobs = await _svc.LoadJobsAsync();
        Assert.Contains(jobs, j => j.Id == job.Id && j.Name == job.Name);

        // Clean up
        await _svc.DeleteJobAsync(job.Id);
        var afterDelete = await _svc.LoadJobsAsync();
        Assert.DoesNotContain(afterDelete, j => j.Id == job.Id);
    }
}

public class ErDiagramLayoutServiceTests
{
    private readonly ErDiagramLayoutService _svc = new();

    [Fact]
    public void ComputeLayout_PlacesTables_NoOverlapsForSmallCount()
    {
        var tables = new[]
        {
            new ErTable { TableName = "users" },
            new ErTable { TableName = "orders" },
            new ErTable { TableName = "products" }
        };

        var layout = _svc.ComputeLayout(tables);

        Assert.Equal(3, layout.Count);

        // Verify all positions are distinct
        var positions = new HashSet<(double, double)>();
        foreach (var (_, pos) in layout)
            Assert.True(positions.Add(pos), "Duplicate position found.");
    }

    [Fact]
    public void ComputeLayout_EmptyList_ReturnsEmptyDict()
    {
        var layout = _svc.ComputeLayout(Array.Empty<ErTable>());
        Assert.Empty(layout);
    }
}
