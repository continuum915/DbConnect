using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Services;
using Xunit;

namespace DbConnect.Tests;

/// <summary>
/// architecture-discipline ticket 01 — the connection dialog's fields map to and from each provider's
/// connection string through <see cref="ConnectionStringFormats"/>, not provider checks in the view model.
/// </summary>
public class ConnectionStringFormatTests
{
    [Theory]
    [InlineData("PostgreSQL", "Host=db;SSL Mode=VerifyFull;Root Certificate=ca.pem;SSL Certificate=client.pem;SSL Key=client.key", "Root Certificate", "SSL Certificate", "SSL Key")]
    [InlineData("MySQL", "Server=db;SSL Mode=VerifyFull;SSL CA=ca.pem;SSL Cert=client.pem;SSL Key=client.key", "SSL CA", "SSL Cert", "SSL Key")]
    public void CertificateSettings_SurviveEditing(string provider, string original, string ca, string cert, string key)
    {
        var format = ConnectionStringFormats.For(provider)!;
        var rebuilt = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = format.Build(format.Parse(original)) };
        Assert.Equal("ca.pem", rebuilt[ca]);
        Assert.Equal("client.pem", rebuilt[cert]);
        Assert.Equal("client.key", rebuilt[key]);
        Assert.Equal("VerifyFull", format.Parse(rebuilt.ConnectionString).SslMode);
    }

    [Fact]
    public void EveryRegisteredProvider_HasAConnectionStringFormat()
    {
        foreach (var provider in new DatabaseService().GetAvailableProviders())
            Assert.True(ConnectionStringFormats.For(provider) is not null, $"{provider} has no connection string format.");
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    [InlineData("MySQL")]
    public void ServerFields_RoundTrip(string provider)
    {
        var format = ConnectionStringFormats.For(provider)!;
        var fields = new ConnectionFields
        {
            Host = "db.example.com",
            Port = 4000,
            Database = "shop",
            Username = "rain",
            Password = "p;w'd",
            SslMode = provider switch { "PostgreSQL" => "Require", "SQL Server" => "Required", _ => "Preferred" },
            // Only SQL Server has this setting; the others report the default.
            TrustServerCertificate = false
        };

        var parsed = format.Parse(format.Build(fields));

        Assert.Equal(fields, parsed);
    }

    [Theory]
    [InlineData("SQLite", ".sqlite")]
    [InlineData("DuckDB", ".duckdb")]
    public async Task FileFields_RoundTripAndOpenTheDatabase(string provider, string extension)
    {
        var format = ConnectionStringFormats.For(provider)!;
        var path = Path.Combine(Path.GetTempPath(), $"rain-format-{Guid.NewGuid():N}{extension}");
        try
        {
            var connectionString = format.Build(new ConnectionFields { FilePath = path });

            Assert.Equal(path, format.Parse(connectionString).FilePath);
            var result = await new DatabaseService().GetProvider(provider).TestConnectionAsync(connectionString);
            Assert.True(result.Success, result.Detail);
        }
        finally
        {
            // Best effort: SQLite's connection pool may still hold the file.
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("PostgreSQL", "Host=db.example.com;Port=5433;Database=shop;Timeout=42", "Timeout=42")]
    [InlineData("SQL Server", "Server=db.example.com,1444;Database=shop;Connect Timeout=42", "Connect Timeout=42")]
    [InlineData("MySQL", "Server=db.example.com;Port=3307;Database=shop;Connection Timeout=42", "Connection Timeout=42")]
    public void WithServer_PointsAtTheTunnelAndKeepsOtherSettings(string provider, string connectionString, string kept)
    {
        var format = ConnectionStringFormats.For(provider)!;

        var patched = format.WithServer(connectionString, "127.0.0.1", 50000);

        var fields = format.Parse(patched);
        Assert.Equal(("127.0.0.1", 50000), (fields.Host, fields.Port));
        Assert.Equal("shop", fields.Database);
        Assert.Contains(kept, patched, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PostgreSQL", "Host=db.example.com;Database=shop", 5432)]
    [InlineData("SQL Server", "Server=db.example.com;Database=shop", 1433)]
    [InlineData("MySQL", "Server=db.example.com;Database=shop", 3306)]
    public void Parse_WithoutAPort_ReportsTheProvidersDefault(string provider, string connectionString, int expectedPort)
    {
        var fields = ConnectionStringFormats.For(provider)!.Parse(connectionString);

        Assert.Equal(("db.example.com", expectedPort), (fields.Host, fields.Port));
        Assert.Equal(expectedPort, ConnectionStringFormats.For(provider)!.DefaultPort);
    }

    [Fact]
    public void FileDatabases_HaveNoDefaultPort_AndServersDo()
    {
        var service = new DatabaseService();
        var fileBased = service.GetAvailableProviders()
            .Where(name => ConnectionStringFormats.For(name)!.UsesDatabaseFile)
            .Order()
            .ToArray();

        Assert.Equal(["DuckDB", "SQLite"], fileBased);
        foreach (var name in service.GetAvailableProviders())
        {
            var format = ConnectionStringFormats.For(name)!;
            Assert.True(format.UsesDatabaseFile == (format.DefaultPort is null),
                $"{name}: a server database declares a default port; a file database declares none.");
        }
    }
}
