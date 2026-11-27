using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Services;
using Xunit;

namespace DbConnect.Tests.ProviderConformance;

public sealed class ProviderConformanceTests
{
    [Fact]
    public void AllBuiltInProviders_HaveOneSharedConformanceDescriptor()
    {
        var service = new DatabaseService();
        var registered = service.GetAvailableProviders().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var descriptors = ProviderConformanceContract.Descriptors
            .Select(descriptor => descriptor.ProviderName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(descriptors, registered);
        foreach (var providerName in registered)
            ProviderConformanceContract.AssertDeclaration(service.GetProvider(providerName));
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    public void EditableResultGateProviders_ReportResultColumnSources(string providerName)
    {
        // RAIN-P2-15 gates editable query results on these three; the live contract proves each declaration.
        Assert.True(new DatabaseService().GetProvider(providerName).Capabilities.ReportsResultColumnSources);
    }

    [Fact]
    public void ExternalProviderFixtures_UseStableEnvironmentVariableNames()
    {
        Assert.Equal("DBCONNECT_CONFORMANCE_POSTGRESQL_CONNECTION_STRING", ProviderConformanceFixture.GetEnvironmentConnectionStringKey("PostgreSQL"));
        Assert.Equal("DBCONNECT_CONFORMANCE_SQLSERVER_CONNECTION_STRING", ProviderConformanceFixture.GetEnvironmentConnectionStringKey("SQL Server"));
        Assert.Equal("DBCONNECT_CONFORMANCE_MYSQL_CONNECTION_STRING", ProviderConformanceFixture.GetEnvironmentConnectionStringKey("MySQL"));
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    [Trait("Category", "Integration")]
    public async Task LocalProvider_SatisfiesSharedLiveContract(string providerName)
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync(providerName);
        await ProviderConformanceContract.RunAsync(fixture);
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    [Trait("Category", "Integration")]
    public async Task LocalProvider_SatisfiesSessionContract(string providerName)
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync(providerName);
        await ProviderConformanceContract.RunSessionAsync(fixture);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExternalProviders_SatisfySharedLiveContractWhenConfigured()
    {
        foreach (var providerName in ConfiguredExternalProviders())
        {
            var connectionString = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
            await using var fixture = await ProviderConformanceFixture.CreateExternalAsync(providerName, connectionString!);
            await ProviderConformanceContract.RunAsync(fixture);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExternalProviders_SatisfySessionContractWhenConfigured()
    {
        foreach (var providerName in ConfiguredExternalProviders())
        {
            var connectionString = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
            await using var fixture = await ProviderConformanceFixture.CreateExternalAsync(providerName, connectionString!);
            await ProviderConformanceContract.RunSessionAsync(fixture);
        }
    }

    public static IEnumerable<string> ConfiguredExternalProviders()
    {
        foreach (var providerName in new[] { "PostgreSQL", "SQL Server", "MySQL" })
        {
            if (!string.IsNullOrWhiteSpace(ProviderConformanceFixture.GetEnvironmentConnectionString(providerName)))
                yield return providerName;
        }
    }
}
