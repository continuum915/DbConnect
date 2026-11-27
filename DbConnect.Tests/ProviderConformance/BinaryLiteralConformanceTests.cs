using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// RAIN-P2-17 — A binary value copied as a SQL literal must read back as the same bytes on the
/// provider it was copied from; this proves each provider's declared binary literal style live.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BinaryLiteralConformanceTests
{
    private static readonly byte[] Bytes = [0x00, 0xAB, 0x27, 0xFF];

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task LocalProvider_ReadsItsBinaryLiteralBackAsTheSameBytes(string providerName)
    {
        await using var fixture = await ProviderConformanceFixture.CreateLocalAsync(providerName);
        await AssertRoundTripAsync(fixture);
    }

    [Fact]
    public async Task ExternalProviders_ReadTheirBinaryLiteralBackAsTheSameBytesWhenConfigured()
    {
        foreach (var providerName in ProviderConformanceTests.ConfiguredExternalProviders())
        {
            var connectionString = ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
            await using var fixture = await ProviderConformanceFixture.CreateExternalAsync(providerName, connectionString!);
            await AssertRoundTripAsync(fixture);
        }
    }

    private static async Task AssertRoundTripAsync(ProviderConformanceFixture fixture)
    {
        var literal = fixture.Provider.Capabilities.FormatBinaryLiteral(Bytes);

        var result = await fixture.Provider.ExecuteQueryAsync(fixture.ConnectionString, $"SELECT {literal} AS b");

        Assert.False(result.HasError, $"{fixture.ProviderName}: {literal} -> {result.ErrorMessage}");
        Assert.Equal<IEnumerable<byte>>(Bytes, Assert.IsType<byte[]>(result.ObjectRows[0][0]));
    }
}
