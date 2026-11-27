using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Services;

/// <summary>
/// RAIN-P2-24 — Built-in functions per dialect, offered by completion through
/// <see cref="DatabaseProviderCapabilities.BuiltInFunctions"/>. Each list holds only names the dialect accepts.
/// </summary>
public static class SqlFunctionCatalog
{
    private static readonly string[] Common =
    [
        "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "NULLIF", "CAST", "ABS", "ROUND",
        "LOWER", "UPPER", "TRIM", "LTRIM", "RTRIM", "REPLACE"
    ];

    private static readonly string[] Window =
    [
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE"
    ];

    public static IReadOnlyList<string> PostgreSql { get; } = Build(
    [
        "LENGTH", "SUBSTRING", "POSITION", "CONCAT", "CONCAT_WS", "LEFT", "RIGHT", "LPAD", "RPAD",
        "SPLIT_PART", "INITCAP", "REGEXP_REPLACE", "REGEXP_MATCHES", "MD5", "STRING_AGG", "ARRAY_AGG",
        "JSON_AGG", "JSONB_AGG", "JSON_BUILD_OBJECT", "JSONB_BUILD_OBJECT", "BOOL_AND", "BOOL_OR",
        "TO_CHAR", "TO_DATE", "TO_TIMESTAMP", "TO_NUMBER", "NOW", "DATE_TRUNC", "DATE_PART", "EXTRACT",
        "AGE", "GENERATE_SERIES", "UNNEST", "CEIL", "FLOOR", "POWER", "SQRT", "MOD", "RANDOM",
        "GREATEST", "LEAST", "GEN_RANDOM_UUID"
    ]);

    public static IReadOnlyList<string> SqlServer { get; } = Build(
    [
        "LEN", "SUBSTRING", "CHARINDEX", "CONCAT", "CONCAT_WS", "LEFT", "RIGHT", "STRING_AGG",
        "STRING_SPLIT", "FORMAT", "GETDATE", "GETUTCDATE", "SYSDATETIME", "DATEADD", "DATEDIFF",
        "DATEPART", "DATENAME", "EOMONTH", "ISNULL", "IIF", "CHOOSE", "CONVERT", "TRY_CAST",
        "TRY_CONVERT", "TRY_PARSE", "NEWID", "CEILING", "FLOOR", "POWER", "SQRT", "RAND", "COUNT_BIG",
        "JSON_VALUE", "JSON_QUERY", "OPENJSON", "ISJSON", "SCOPE_IDENTITY", "OBJECT_ID"
    ]);

    public static IReadOnlyList<string> MySql { get; } = Build(
    [
        "LENGTH", "CHAR_LENGTH", "SUBSTRING", "SUBSTRING_INDEX", "LOCATE", "INSTR", "CONCAT", "CONCAT_WS",
        "LEFT", "RIGHT", "LPAD", "RPAD", "GROUP_CONCAT", "IFNULL", "IF", "NOW", "CURDATE", "CURTIME",
        "DATE_FORMAT", "STR_TO_DATE", "DATE_ADD", "DATE_SUB", "DATEDIFF", "TIMESTAMPDIFF",
        "UNIX_TIMESTAMP", "FROM_UNIXTIME", "YEAR", "MONTH", "DAY", "CEIL", "FLOOR", "POW", "SQRT", "MOD",
        "RAND", "GREATEST", "LEAST", "JSON_EXTRACT", "JSON_OBJECT", "JSON_ARRAY", "JSON_UNQUOTE",
        "JSON_CONTAINS", "UUID", "LAST_INSERT_ID", "MD5", "SHA2", "CONVERT"
    ]);

    public static IReadOnlyList<string> Sqlite { get; } = Build(
    [
        "LENGTH", "SUBSTR", "INSTR", "IFNULL", "IIF", "PRINTF", "GROUP_CONCAT", "TOTAL", "DATE", "TIME",
        "DATETIME", "JULIANDAY", "STRFTIME", "UNIXEPOCH", "RANDOM", "RANDOMBLOB", "ZEROBLOB", "HEX",
        "QUOTE", "TYPEOF", "CHAR", "UNICODE", "LAST_INSERT_ROWID", "CHANGES", "JSON", "JSON_EXTRACT",
        "JSON_OBJECT", "JSON_ARRAY", "JSON_GROUP_ARRAY", "JSON_GROUP_OBJECT"
    ]);

    public static IReadOnlyList<string> DuckDb { get; } = Build(
    [
        "LENGTH", "SUBSTRING", "CONCAT", "CONCAT_WS", "LEFT", "RIGHT", "LPAD", "RPAD", "SPLIT_PART",
        "STRING_SPLIT", "STRING_AGG", "ARRAY_AGG", "LIST", "LIST_VALUE", "STRUCT_PACK", "REGEXP_MATCHES",
        "REGEXP_REPLACE", "REGEXP_EXTRACT", "STRFTIME", "STRPTIME", "NOW", "DATE_TRUNC", "DATE_PART",
        "DATE_DIFF", "DATE_ADD", "EPOCH", "MAKE_DATE", "AGE", "GENERATE_SERIES", "RANGE", "UNNEST",
        "READ_CSV", "READ_CSV_AUTO", "READ_PARQUET", "READ_JSON", "READ_JSON_AUTO", "ARG_MAX", "ARG_MIN",
        "MEDIAN", "MODE", "QUANTILE_CONT", "APPROX_COUNT_DISTINCT", "GREATEST", "LEAST", "CEIL", "FLOOR",
        "POW", "SQRT", "RANDOM", "IFNULL", "MD5", "UUID", "GEN_RANDOM_UUID", "JSON_EXTRACT"
    ]);

    private static IReadOnlyList<string> Build(string[] dialect) =>
        Common.Concat(Window).Concat(dialect).Distinct().Order().ToArray();
}
