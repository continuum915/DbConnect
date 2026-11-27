namespace DbConnect.Services;

internal static class CsvImportDialects
{
    public static CsvImportDialect Sqlite { get; } = new("TEXT", "INTEGER", "REAL", "TEXT", "INTEGER", true,
        name => $"SELECT CASE WHEN sql NOT LIKE 'CREATE VIRTUAL TABLE%' THEN 1 ELSE 0 END, (SELECT count(*) FROM sqlite_master WHERE type='trigger' AND tbl_name={name}) FROM sqlite_master WHERE type='table' AND name={name}");
    public static CsvImportDialect DuckDb { get; } = new("VARCHAR", "INTEGER", "DOUBLE", "TIMESTAMP", "BOOLEAN", true,
        name => $"SELECT 1, 0 FROM duckdb_tables() WHERE database_name=current_database() AND schema_name=current_schema() AND table_name={name}");
    public static CsvImportDialect PostgreSql { get; } = new("TEXT", "INTEGER", "DOUBLE PRECISION", "TIMESTAMP", "BOOLEAN", true,
        name => $"SELECT CASE WHEN c.relkind IN ('r','p') THEN 1 ELSE 0 END, (SELECT count(*) FROM pg_trigger t WHERE t.tgrelid=c.oid AND NOT t.tgisinternal) FROM pg_class c WHERE c.oid=to_regclass(quote_ident({name}))");
    public static CsvImportDialect SqlServer { get; } = new("NVARCHAR(MAX)", "INTEGER", "FLOAT", "DATETIME2", "BIT", true,
        name => $"SELECT 1, (SELECT count(*) FROM sys.triggers t WHERE t.parent_id=o.object_id AND t.is_disabled=0) FROM sys.tables o WHERE o.object_id=OBJECT_ID(QUOTENAME({name})) AND o.is_memory_optimized=0")
    { NameLiteral = name => "N'" + name.Replace("'", "''") + "'", DirectInsertCounts = false };
    public static CsvImportDialect MySql { get; } = new("LONGTEXT", "INTEGER", "DOUBLE", "DATETIME", "BOOLEAN", false,
        MySqlAssessment)
    {
        NameLiteral = name => "CONVERT(0x" + System.Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(name)) + " USING utf8mb4)",
        CreateTableSuffix = " ENGINE=InnoDB"
    };

    private static string MySqlAssessment(string name)
    {
        // TRIGGERS silently hides objects without TRIGGER privilege. Verify a direct table/schema grant first.
        // Global-only grants, partial revokes and role-derived privileges remain conservatively unverified.
        const string grantee = "CONCAT(CHAR(39), REPLACE(CURRENT_USER(), '@', CONCAT(CHAR(39), '@', CHAR(39))), CHAR(39))";
        var visibility = $"EXISTS(SELECT 1 FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE={grantee} AND (TABLE_SCHEMA=DATABASE() OR (@@partial_revokes=0 AND DATABASE() LIKE TABLE_SCHEMA)) AND PRIVILEGE_TYPE='TRIGGER') OR EXISTS(SELECT 1 FROM information_schema.TABLE_PRIVILEGES WHERE GRANTEE={grantee} AND TABLE_SCHEMA=DATABASE() AND TABLE_NAME={name} AND PRIVILEGE_TYPE='TRIGGER')";
        return $"SELECT CASE WHEN ENGINE='InnoDB' THEN 1 WHEN ENGINE='MyISAM' THEN -1 ELSE 0 END, CASE WHEN {visibility} THEN (SELECT count(*) FROM information_schema.TRIGGERS t WHERE t.TRIGGER_SCHEMA=DATABASE() AND t.EVENT_OBJECT_TABLE={name}) ELSE -1 END FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME={name} AND TABLE_TYPE='BASE TABLE'";
    }
}
