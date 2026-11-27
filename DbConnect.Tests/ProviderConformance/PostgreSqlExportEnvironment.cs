using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

internal static class PostgreSqlExportEnvironment
{
    public const string MissingService = "Live PostgreSQL export checks require DBCONNECT_CONFORMANCE_POSTGRESQL_CONNECTION_STRING pointing to a disposable database.";
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL"));
    public static Task<IDbSession> OpenAsync() => new PostgreSqlProvider().OpenSessionAsync(new DbConnect.Models.ConnectionInfo
    {
        Provider = "PostgreSQL",
        ConnectionString = ProviderConformanceFixture.GetEnvironmentConnectionString("PostgreSQL") ?? throw new InvalidOperationException(MissingService)
    });

    public static QueryExportCapture Capture(IDbSession session, DbConnect.Models.QueryResult result) =>
        Assert.IsAssignableFrom<IQueryExportSession>(session).CaptureQueryExport(result)
        ?? throw new Xunit.Sdk.XunitException("Expected an eligible PostgreSQL session export capture.");
}

public sealed class PostgreSqlExportFactAttribute : FactAttribute
{
    public PostgreSqlExportFactAttribute()
    {
        if (!PostgreSqlExportEnvironment.IsConfigured) Skip = PostgreSqlExportEnvironment.MissingService;
    }
}

public sealed class PostgreSqlExportTheoryAttribute : TheoryAttribute
{
    public PostgreSqlExportTheoryAttribute()
    {
        if (!PostgreSqlExportEnvironment.IsConfigured) Skip = PostgreSqlExportEnvironment.MissingService;
    }
}
