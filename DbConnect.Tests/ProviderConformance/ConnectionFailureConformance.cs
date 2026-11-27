using System;
using System.Data.Common;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using DbConnect.Models;
using Xunit.Sdk;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// milestone-0 ticket 01 — a failed connection test names its cause. Each case derives a broken
/// connection string from the fixture's working one and checks the category the provider reports.
/// </summary>
public static class ConnectionFailureConformance
{
    public static async Task RunAsync(ProviderConformanceFixture fixture)
    {
        foreach (var (name, connectionString, expected) in Cases(fixture))
        {
            var result = await fixture.Provider.TestConnectionAsync(connectionString);
            if (result.Success || Array.IndexOf(expected, result.Category) < 0)
            {
                throw new XunitException(
                    $"{fixture.ProviderName} {fixture.DatabaseVersion} [connection failure] {name}: expected " +
                    $"{string.Join(" or ", expected)}, got {(result.Success ? "success" : result.Category)} " +
                    $"({result.Summary} {result.Detail}).");
            }
            if (string.IsNullOrWhiteSpace(result.Summary) || string.IsNullOrWhiteSpace(result.Detail))
                throw new XunitException($"{fixture.ProviderName} [connection failure] {name}: no summary or driver detail was reported.");
        }
    }

    private static (string Name, string ConnectionString, ConnectionFailureCategory[] Expected)[] Cases(ProviderConformanceFixture fixture)
    {
        var cs = fixture.ConnectionString;
        var unreachable = new[] { ConnectionFailureCategory.HostUnreachable, ConnectionFailureCategory.Timeout };
        var missingFile = Path.Combine(Path.GetTempPath(), $"rain-conformance-missing-{Guid.NewGuid():N}");
        return fixture.ProviderName switch
        {
            "PostgreSQL" =>
            [
                ("wrong password", With(new NpgsqlConnectionStringBuilder(cs), b => b.Password = WrongPassword), [ConnectionFailureCategory.Authentication]),
                ("missing database", With(new NpgsqlConnectionStringBuilder(cs), b => b.Database = MissingDatabase), [ConnectionFailureCategory.DatabaseNotFound]),
                ("unreachable port", With(new NpgsqlConnectionStringBuilder(cs), b => b.Port = 1), unreachable)
            ],
            "SQL Server" =>
            [
                ("wrong password", With(new SqlConnectionStringBuilder(cs), b => b.Password = WrongPassword), [ConnectionFailureCategory.Authentication]),
                ("missing database", With(new SqlConnectionStringBuilder(cs), b => b.InitialCatalog = MissingDatabase), [ConnectionFailureCategory.DatabaseNotFound]),
                ("unreachable port", With(new SqlConnectionStringBuilder(cs), b => b.DataSource = $"tcp:{SqlServerHost(b.DataSource)},1"), unreachable)
            ],
            "MySQL" =>
            [
                ("wrong password", With(new MySqlConnectionStringBuilder(cs), b => b.Password = WrongPassword), [ConnectionFailureCategory.Authentication]),
                ("missing database", With(new MySqlConnectionStringBuilder(cs), b => b.Database = MissingDatabase), [ConnectionFailureCategory.DatabaseNotFound]),
                ("unreachable port", With(new MySqlConnectionStringBuilder(cs), b => b.Port = 1), unreachable)
            ],
            "SQLite" =>
            [
                ("missing database file", $"Data Source={missingFile}.sqlite;Mode=ReadWrite", [ConnectionFailureCategory.DatabaseNotFound])
            ],
            "DuckDB" =>
            [
                ("missing database file", $"Data Source={missingFile}.duckdb;ACCESS_MODE=READ_ONLY", [ConnectionFailureCategory.DatabaseNotFound])
            ],
            _ => []
        };
    }

    private const string WrongPassword = "rain-conformance-wrong-password";
    private const string MissingDatabase = "rain_conformance_missing_database";

    private static string With<TBuilder>(TBuilder builder, Action<TBuilder> change) where TBuilder : DbConnectionStringBuilder
    {
        change(builder);
        return builder.ConnectionString;
    }

    /// <summary>The host part of a SQL Server data source such as "tcp:localhost,14333" or "localhost\SQLEXPRESS".</summary>
    private static string SqlServerHost(string dataSource)
    {
        var host = dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? dataSource[4..] : dataSource;
        var end = host.IndexOfAny([',', '\\']);
        return end < 0 ? host : host[..end];
    }
}
