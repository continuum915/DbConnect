using DbConnect.Services;

namespace DbConnect.PerformanceTests;

/// <summary>Lets the test project reach the internal per-statement schema check.</summary>
public static class SchemaChangeProbe
{
    public static bool MayChangeSchema(string sql, SqlBatchDialect dialect) => SchemaChangeExecution.MayChangeSchema(sql, dialect);
}
