namespace DbConnect.Services;

/// <summary>Provider declarations shared by SQL helpers and UI workflows.</summary>
internal static class ProviderCapabilityDeclarations
{
    public static DatabaseProviderCapabilities Sqlite { get; } =
        new("\"", "\"", "LIMIT")
        {
            MaxCommandParameters = 32766,
            SupportsUpdateSetDefault = false,
            CsvImport = CsvImportDialects.Sqlite,
            AsciiCaseInsensitiveObjectNames = true,
            SupportsIndexWhereClause = true,
            IsEmbedded = true,
            SupportsForwardEngineering = true,
            RequiresForeignKeyPragma = true,
            ForeignKeyChecks = new("PRAGMA foreign_keys=OFF;", "PRAGMA foreign_keys=ON;",
                "run it outside a transaction, inside which SQLite ignores the PRAGMA"),
            RequiresTableRebuildForConstraints = true,
            BackupStrategy = BackupStrategy.SqliteOnline,
            BatchDialect = SqlBatchDialect.Sqlite,
            TransactionSupport = TransactionSupportLevel.Guaranteed,
            TransactionalDdl = true,
            SupportsSchemas = false,
            SupportsAnalyzeExplain = false,
            BuiltInFunctions = SqlFunctionCatalog.Sqlite,
            SupportsViews = true,
            SupportsIndexes = true,
            SupportsCreateOrReplaceView = false,
            SupportsDropColumn = true,
            SupportsRenameColumn = true,
            SupportsTableDesignerDropColumn = true,
            SupportsTableDesignerRenameColumn = false,
            SupportsForeignKeys = true,
            SupportsTriggers = true,
            SupportsFunctions = false,
            SupportsProcedures = false,
            SupportsUserWrite = false,
            SupportsTableQuerySpec = true,
            ReportsResultColumnSources = true
        };

    public static DatabaseProviderCapabilities PostgreSql { get; } =
        new("\"", "\"", "LIMIT", BinaryLiteralStyle.ByteaHex)
        {
            MaxCommandParameters = 65535,
            CsvImport = CsvImportDialects.PostgreSql,
            SupportsIndexWhereClause = true,
            SupportsForwardEngineering = true,
            ShowsVerifyFullSslHint = true,
            DefaultSchema = "public",
            BackupStrategy = BackupStrategy.PostgreSqlTools,
            PrivilegeOptions = ["CONNECT", "CREATE", "TEMPORARY", "USAGE", "SELECT", "INSERT", "UPDATE", "DELETE", "TRIGGER", "EXECUTE", "ALL PRIVILEGES"],
            BatchDialect = SqlBatchDialect.PostgreSql,
            SnapshotRead = new("REPEATABLE READ READ ONLY", ["START TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY"], ["ROLLBACK"]),
            // pg_get_serial_sequence finds both identity and serial sequences; is_called = false makes max + 1 the next value.
            IdentityLoading = new()
            {
                InsertOverride = "OVERRIDING SYSTEM VALUE",
                Advance = "SELECT setval(pg_get_serial_sequence({2}, {3}), COALESCE((SELECT max({1}) FROM {0}), 0) + 1, false);"
            },
            TransactionSupport = TransactionSupportLevel.Guaranteed,
            TransactionalDdl = true,
            SupportsSchemas = true,
            SupportsAnalyzeExplain = true,
            UsesShowPlanExplain = false,
            SupportsViews = true,
            SupportsIndexes = true,
            SupportsCreateOrReplaceView = true,
            SupportsDropColumn = true,
            SupportsRenameColumn = true,
            SupportsTableDesignerDropColumn = true,
            SupportsTableDesignerRenameColumn = false,
            SupportsForeignKeys = true,
            SupportsTriggers = true,
            SupportsFunctions = true,
            SupportsProcedures = true,
            SupportsObjectComments = true,
            SupportsUserWrite = true,
            SupportsTableQuerySpec = true,
            BuiltInFunctions = SqlFunctionCatalog.PostgreSql,
            ReportsResultColumnSources = true,
            UsesBooleanKeywords = true,
            // character(n)/bpchar: the driver hands back the blank padding, but comparing the column
            // with a text parameter drops the column's padding only, so "=" never matches.
            NonComparableTypeNames = ["json", "xml", "point", "path", "polygon", "character", "bpchar"],
            LikeOperator = "ILIKE"
        };

    public static DatabaseProviderCapabilities SqlServer { get; } =
        new("[", "]", "TOP", BinaryLiteralStyle.HexPrefix)
        {
            MaxCommandParameters = 2100,
            TypedNull = type => type.Split('(')[0].Trim().ToLowerInvariant() is "binary" or "varbinary" or "image"
                ? (object)System.Data.SqlTypes.SqlBinary.Null : null,
            CsvImport = CsvImportDialects.SqlServer,
            SupportsIncludedIndexColumns = true,
            SupportsForwardEngineering = true,
            UsesEncryptionOptions = true,
            DefaultSchema = "dbo",
            BackupStrategy = BackupStrategy.SqlServerCommands,
            BatchDialect = SqlBatchDialect.SqlServer,
            ScriptRules = new(BatchSeparator: "GO"),
            MaxInsertValuesRows = 1000,
            // Snapshot isolation must be enabled per database (ALLOW_SNAPSHOT_ISOLATION); the isolation level outlives
            // the transaction, so it is put back for whoever reuses the pooled connection.
            SnapshotRead = new("SNAPSHOT isolation", ["SET TRANSACTION ISOLATION LEVEL SNAPSHOT", "BEGIN TRANSACTION"],
                ["ROLLBACK", "SET TRANSACTION ISOLATION LEVEL READ COMMITTED"],
                Probe: "SELECT CASE WHEN snapshot_isolation_state = 1 THEN 1 ELSE 0 END FROM sys.databases WHERE database_id = DB_ID()"),
            // IDENTITY_INSERT moves the identity past the inserted values itself.
            IdentityLoading = new()
            {
                Enable = "SET IDENTITY_INSERT {0} ON;",
                Disable = "SET IDENTITY_INSERT {0} OFF;",
                EngineWrittenTypeNames = ["timestamp", "rowversion"]
            },
            StringLiteralStyle = StringLiteralStyle.National,
            DateTimeLiteralSeparator = 'T',
            TransactionSupport = TransactionSupportLevel.Guaranteed,
            TransactionalDdl = true,
            SupportsSchemas = true,
            SupportsAnalyzeExplain = false,
            UsesShowPlanExplain = true,
            SupportsViews = true,
            SupportsIndexes = true,
            SupportsCreateOrReplaceView = false,
            SupportsDropColumn = true,
            SupportsRenameColumn = true,
            SupportsTableDesignerDropColumn = true,
            SupportsTableDesignerRenameColumn = false,
            SupportsForeignKeys = true,
            SupportsTriggers = true,
            SupportsFunctions = true,
            SupportsProcedures = true,
            SupportsObjectComments = true,
            SupportsUserWrite = false,
            SupportsTableQuerySpec = true,
            BuiltInFunctions = SqlFunctionCatalog.SqlServer,
            ReportsResultColumnSources = true,
            NonComparableTypeNames = ["text", "ntext", "image", "xml", "geography", "geometry"],
            NarrowStringTypeNames = ["char", "varchar", "text"],
            TextCastType = "NVARCHAR(MAX)"
        };

    public static DatabaseProviderCapabilities MySql { get; } =
        new("`", "`", "LIMIT")
        {
            MaxCommandParameters = 65535,
            CsvImport = CsvImportDialects.MySql,
            SupportsForwardEngineering = true,
            PrivilegeOptions = ["SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "INDEX", "EXECUTE", "ALL PRIVILEGES"],
            BatchDialect = SqlBatchDialect.MySql,
            ScriptRules = new(DelimitCompoundBodies: true),
            TransactionSupport = TransactionSupportLevel.Guaranteed,
            BeginTransactionStatement = "START TRANSACTION;",
            SnapshotRead = new("START TRANSACTION WITH CONSISTENT SNAPSHOT; InnoDB tables only",
                ["SET TRANSACTION ISOLATION LEVEL REPEATABLE READ", "START TRANSACTION WITH CONSISTENT SNAPSHOT, READ ONLY"], ["ROLLBACK"]),
            // As mysqldump does: the session's own setting is put back, and CREATE TABLE may reference tables created later.
            ForeignKeyChecks = new("SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0;",
                "SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS;"),
            StringLiteralStyle = StringLiteralStyle.BackslashEscapes,
            TimeLiteralAcceptsDurations = true,
            SupportsSchemas = false,
            SupportsAnalyzeExplain = true,
            SupportsViews = true,
            SupportsTriggers = true,
            SupportsFunctions = true,
            SupportsProcedures = true,
            SupportsDropColumn = true,
            SupportsRenameColumn = true,
            SupportsTableDesignerDropColumn = true,
            SupportsTableDesignerRenameColumn = true,
            SupportsForeignKeys = true,
            SupportsCreateOrReplaceView = false,
            SupportsObjectComments = true,
            SupportsUserWrite = true,
            SupportsTableQuerySpec = true,
            BuiltInFunctions = SqlFunctionCatalog.MySql,
            SupportsInsertDefaultValues = false,
            NonComparableTypeNames = ["JSON", "FLOAT", "GEOMETRY", "POINT", "LINESTRING", "POLYGON"],
            NonSeekableTypeNames = ["ENUM", "SET"],
            BackupStrategy = BackupStrategy.DbConnectDump,
            TextCastType = "CHAR"
        };

    public static DatabaseProviderCapabilities DuckDb { get; } =
        new("\"", "\"", "LIMIT", BinaryLiteralStyle.BlobEscapes)
        {
            CsvImport = CsvImportDialects.DuckDb,
            AsciiCaseInsensitiveObjectNames = true,
            IsEmbedded = true,
            TransactionSupport = TransactionSupportLevel.Guaranteed,
            TransactionalDdl = true,
            SupportsSchemas = true,
            DefaultSchema = "main",
            SupportsAnalyzeExplain = true,
            SupportsViews = true,
            SupportsIndexes = true,
            SupportsFunctions = true,
            SupportsDropColumn = true,
            SupportsRenameColumn = true,
            SupportsTableDesignerDropColumn = true,
            SupportsTableDesignerRenameColumn = true,
            SupportsCreateOrReplaceView = true,
            BuiltInFunctions = SqlFunctionCatalog.DuckDb,
            SupportsProcedures = false,
            SupportsForeignKeys = false,
            SupportsTableQuerySpec = true,
            UsesBooleanKeywords = true,
            UsesPositionalParameters = true,
            // Transactions are MVCC snapshots, so reading in one costs writers nothing.
            SnapshotRead = new("one read transaction", ["BEGIN TRANSACTION"], ["ROLLBACK"]),
            NonSeekableTypeNames = ["ENUM"],
            LikeOperator = "ILIKE",
        };
}
