using System;
using System.IO;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;
using Xunit;

namespace DbConnect.Tests;

/// <summary>
/// milestone-0 ticket 01 — Test Connection reports why a connection failed and which server it reached.
/// The live engines are covered by the provider conformance contract; these need no server.
/// </summary>
public class ConnectionTestResultTests
{
    [Theory]
    [InlineData("PostgreSQL", "Host=127.0.0.1;Port=1;Database=rain;Username=rain;Password=rain")]
    [InlineData("MySQL", "Server=127.0.0.1;Port=1;Database=rain;User ID=rain;Password=rain")]
    [InlineData("SQL Server", "Server=tcp:127.0.0.1,1;Database=rain;User ID=rain;Password=rain;TrustServerCertificate=True")]
    public async Task UnreachablePort_ReportsHostUnreachableOrTimeout(string providerName, string connectionString)
    {
        var result = await new DatabaseService().GetProvider(providerName).TestConnectionAsync(connectionString);

        Assert.False(result.Success);
        Assert.Contains(result.Category, new[] { ConnectionFailureCategory.HostUnreachable, ConnectionFailureCategory.Timeout });
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task LocalDatabase_ReportsServerVersionAndElapsedTime(string providerName)
    {
        var result = await new DatabaseService().GetProvider(providerName).TestConnectionAsync("Data Source=:memory:");

        Assert.True(result.Success, result.Detail);
        Assert.Equal(ConnectionFailureCategory.None, result.Category);
        Assert.Contains(providerName, result.ServerVersion, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task MissingSqliteFileOpenedReadWrite_ReportsDatabaseNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-missing-{Guid.NewGuid():N}.sqlite");

        var result = await new SqliteProvider().TestConnectionAsync($"Data Source={path};Mode=ReadWrite");

        Assert.False(result.Success);
        Assert.Equal(ConnectionFailureCategory.DatabaseNotFound, result.Category);
    }
}
