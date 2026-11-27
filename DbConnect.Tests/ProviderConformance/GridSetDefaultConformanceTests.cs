using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>Set Value ▸ Default runs as UPDATE … SET col = DEFAULT where the engine allows it (Tier 2 §2.9).</summary>
public sealed class GridSetDefaultConformanceTests
{
    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQL Server")]
    [InlineData("DuckDB")]
    [Trait("Category", "Integration")]
    public async Task SetDefault_RestoresTheColumnDefault(string providerName)
    {
        var provider = new DatabaseService().GetProvider(providerName);
        var cs = providerName == "DuckDB"
            ? $"Data Source={Path.Combine(Path.GetTempPath(), "rain-default-" + Guid.NewGuid().ToString("N") + ".duckdb")}"
            : ProviderConformanceFixture.GetEnvironmentConnectionString(providerName);
        if (string.IsNullOrWhiteSpace(cs)) return;
        var table = "rain_default_" + Guid.NewGuid().ToString("N")[..8];
        var quote = provider.Capabilities.QuoteIdentifier;
        await provider.ExecuteNonQueryAsync(cs, $"CREATE TABLE {quote(table)} (id int PRIMARY KEY, qty int DEFAULT 7)");
        try
        {
            await provider.ExecuteNonQueryAsync(cs, $"INSERT INTO {quote(table)} (id, qty) VALUES (1, 1)");
            Assert.True(provider.Capabilities.SupportsUpdateSetDefault);
            var tracker = new DataChangeTracker(table, ["id", "qty"], ["id"], provider.Capabilities);
            var row = new EditableRow(new Dictionary<string, object?> { ["id"] = 1, ["qty"] = 1 });
            row.Values["qty"] = ColumnDefault.Value;
            row.State = EditState.Modified;
            var result = await provider.ExecuteTransactionAsync(cs, [tracker.BuildCommand(row)!]);
            Assert.True(result.Succeeded, result.DiagnosticMessage);
            var value = (await provider.ExecuteQueryAsync(cs, $"SELECT qty FROM {quote(table)} WHERE id = 1")).ObjectRows[0][0];
            Assert.Equal(7, Convert.ToInt32(value));
        }
        finally { await provider.ExecuteNonQueryAsync(cs, $"DROP TABLE {quote(table)}"); }
    }
}
