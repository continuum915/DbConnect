using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

internal static class MySqlExportEnvironment
{
    public const string MissingService = "Live MySQL export checks require DBCONNECT_CONFORMANCE_MYSQL_CONNECTION_STRING pointing to a disposable database.";
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL"));
    public static Task<IDbSession> OpenAsync() => new MySqlProvider().OpenSessionAsync(new DbConnect.Models.ConnectionInfo
    {
        Provider = "MySQL",
        ConnectionString = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL") ?? throw new InvalidOperationException(MissingService)
    });

    public static QueryExportCapture Capture(IDbSession session, DbConnect.Models.QueryResult result) =>
        Assert.IsAssignableFrom<IQueryExportSession>(session).CaptureQueryExport(result)
        ?? throw new Xunit.Sdk.XunitException("Expected an eligible MySQL session export capture.");
}

public sealed class MySqlExportFactAttribute : FactAttribute
{
    public MySqlExportFactAttribute()
    {
        if (!MySqlExportEnvironment.IsConfigured) Skip = MySqlExportEnvironment.MissingService;
    }
}

public sealed class MySqlExportTheoryAttribute : TheoryAttribute
{
    public MySqlExportTheoryAttribute()
    {
        if (!MySqlExportEnvironment.IsConfigured) Skip = MySqlExportEnvironment.MissingService;
    }
}
