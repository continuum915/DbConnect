using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Ticket 04 (S7): a session's name shows its provider once.</summary>
public sealed class ConnectionDisplayNameTests
{
    [Theory]
    [InlineData("Shop", "SQLite", null, "Shop (SQLite)")]
    [InlineData("Shop (SQLite)", "SQLite", null, "Shop (SQLite)")]
    [InlineData("shop sqlite", "SQLite", null, "shop sqlite")]
    [InlineData("Prod", "PostgreSQL", "sales", "Prod / sales (PostgreSQL)")]
    [InlineData("Prod PostgreSQL", "PostgreSQL", "sales", "Prod PostgreSQL / sales")]
    [InlineData("Shop", null, null, "Shop")]
    public void Format_AddsTheProviderOnlyWhenTheNameLacksIt(string name, string? provider, string? database, string expected) =>
        Assert.Equal(expected, ConnectionDisplayName.Format(name, provider, database));
}
