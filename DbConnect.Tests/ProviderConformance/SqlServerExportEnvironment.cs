using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

internal static class SqlServerExportEnvironment
{
    public const string MissingService = "Live SQL Server export checks require DBCONNECT_CONFORMANCE_SQLSERVER_CONNECTION_STRING pointing to a disposable database.";
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server"));
    public static Task<IDbSession> OpenAsync() => new SqlServerProvider().OpenSessionAsync(new DbConnect.Models.ConnectionInfo
    {
        Provider = "SQL Server",
        ConnectionString = ProviderConformanceFixture.GetEnvironmentConnectionString("SQL Server") ?? throw new InvalidOperationException(MissingService)
    });

    public static QueryExportCapture Capture(IDbSession session, DbConnect.Models.QueryResult result) =>
        Assert.IsAssignableFrom<IQueryExportSession>(session).CaptureQueryExport(result)
        ?? throw new Xunit.Sdk.XunitException("Expected an eligible SQL Server session export capture.");
}

public sealed class SqlServerExportFactAttribute : FactAttribute
{
    public SqlServerExportFactAttribute()
    {
        if (!SqlServerExportEnvironment.IsConfigured) Skip = SqlServerExportEnvironment.MissingService;
    }
}

public sealed class SqlServerExportTheoryAttribute : TheoryAttribute
{
    public SqlServerExportTheoryAttribute()
    {
        if (!SqlServerExportEnvironment.IsConfigured) Skip = SqlServerExportEnvironment.MissingService;
    }
}
