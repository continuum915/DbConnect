using System;
using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

public enum TransactionSupportLevel
{
    None,
    BestEffort,
    Guaranteed
}

/// <summary>RAIN-P2-17 — A dialect's literal for binary data, rendered by <see cref="DatabaseProviderCapabilities.FormatBinaryLiteral"/>.</summary>
public enum BinaryLiteralStyle
{
    /// <summary><c>X'AB01'</c> (SQLite, MySQL).</summary>
    HexString,
    /// <summary><c>0xAB01</c> (SQL Server).</summary>
    HexPrefix,
    /// <summary><c>'\xAB01'::bytea</c> (PostgreSQL).</summary>
    ByteaHex,
    /// <summary><c>'\xAB\x01'::BLOB</c>, one escape per byte (DuckDB).</summary>
    BlobEscapes
}

/// <summary>How a dialect writes a character string literal, rendered by <see cref="DatabaseProviderCapabilities.FormatStringLiteral"/>.</summary>
public enum StringLiteralStyle
{
    /// <summary><c>'it''s'</c>: quotes doubled, nothing else escaped (SQLite, PostgreSQL, DuckDB).</summary>
    Standard,
    /// <summary><c>'it''s C:\\temp'</c>: backslash is an escape character too, so it is doubled (MySQL).</summary>
    BackslashEscapes,
    /// <summary><c>N'it''s'</c>: national (Unicode) prefix, so text is not narrowed to the code page (SQL Server).</summary>
    National
}

public sealed record DatabaseProviderCapabilities(
    string IdentifierOpenQuote,
    string IdentifierCloseQuote,
    string LimitStyle,
    BinaryLiteralStyle BinaryLiteralStyle = BinaryLiteralStyle.HexString)
{
    public bool SupportsIndexWhereClause { get; init; }
    public bool SupportsIncludedIndexColumns { get; init; }
    public bool IsEmbedded { get; init; }
    public bool SupportsForwardEngineering { get; init; }
    public bool RequiresForeignKeyPragma { get; init; }
    public bool RequiresTableRebuildForConstraints { get; init; }
    public bool ShowsVerifyFullSslHint { get; init; }
    public bool UsesEncryptionOptions { get; init; }
    public string DefaultSchema { get; init; } = string.Empty;
    public bool AsciiCaseInsensitiveObjectNames { get; init; }
    public BackupStrategy BackupStrategy { get; init; }
    public IReadOnlyList<string> PrivilegeOptions { get; init; } = [];

    public string ParameterPrefix { get; init; } = "@";
    public CsvImportDialect? CsvImport { get; init; }
    public TransactionSupportLevel TransactionSupport { get; init; } = TransactionSupportLevel.Guaranteed;

    /// <summary>
    /// Schema changes (CREATE/ALTER/DROP, comments) can run inside a transaction and roll back
    /// together. False where DDL commits implicitly (MySQL), so a multi-step change can be left
    /// partly applied; false by default because that is the safe assumption.
    /// </summary>
    public bool TransactionalDdl { get; init; }
    public bool SupportsSchemas { get; init; }
    public bool SupportsAnalyzeExplain { get; init; }
    public bool UsesShowPlanExplain { get; init; }
    public SqlBatchDialect BatchDialect { get; init; } = SqlBatchDialect.Standard;
    public bool SupportsViews { get; init; } = true;
    public bool SupportsIndexes { get; init; } = true;
    public bool SupportsCreateOrReplaceView { get; init; }
    public bool SupportsDropColumn { get; init; }
    public bool SupportsRenameColumn { get; init; }
    public bool SupportsTableDesignerDropColumn { get; init; }
    public bool SupportsTableDesignerRenameColumn { get; init; }
    public bool SupportsForeignKeys { get; init; }
    public bool SupportsTriggers { get; init; }
    public bool SupportsFunctions { get; init; }
    public bool SupportsProcedures { get; init; }
    public bool SupportsObjectComments { get; init; }
    public bool SupportsUserWrite { get; init; }
    public bool SupportsTableQuerySpec { get; init; }

    /// <summary>RAIN-P2-24 — The dialect's built-in function names, offered by SQL completion (see <see cref="SqlFunctionCatalog"/>).</summary>
    public IReadOnlyList<string> BuiltInFunctions { get; init; } = [];

    /// <summary>
    /// RAIN-P2-15 — Whether query results report each column's source table and column in
    /// <see cref="ColumnInfo"/> (and report neither for an expression), verified by the conformance
    /// contract. Editable query results depend on it.
    /// </summary>
    public bool ReportsResultColumnSources { get; init; }

    /// <summary>Whether the dialect supports "INSERT INTO t DEFAULT VALUES" for a row with no explicit columns (MySQL uses "INSERT INTO t () VALUES ()" instead).</summary>
    public bool SupportsInsertDefaultValues { get; init; } = true;

    /// <summary>The engine accepts <c>UPDATE … SET col = DEFAULT</c>.</summary>
    public bool SupportsUpdateSetDefault { get; init; } = true;

    /// <summary>True when command text uses "?" placeholders bound by position rather than named parameters.</summary>
    public bool UsesPositionalParameters { get; init; }

    /// <summary>The case-insensitive pattern operator ("LIKE" where LIKE already ignores case).</summary>
    public string LikeOperator { get; init; } = "LIKE";

    /// <summary>The type a column is CAST to before a Contains/StartsWith/EndsWith LIKE comparison.</summary>
    public string TextCastType { get; init; } = "TEXT";

    /// <summary>True when BOOLEAN columns take TRUE/FALSE literals and reject a bare 1/0 (PostgreSQL, DuckDB).</summary>
    public bool UsesBooleanKeywords { get; init; }

    /// <summary>How character strings are written as literals; see <see cref="FormatStringLiteral"/>.</summary>
    public StringLiteralStyle StringLiteralStyle { get; init; }

    /// <summary>
    /// Separates date and time in a timestamp literal. SQL Server takes 'T' (ISO 8601), which its datetime and
    /// smalldatetime read the same under every session language; with a space they read year-day-month under DMY ones.
    /// </summary>
    public char DateTimeLiteralSeparator { get; init; } = ' ';

    /// <summary>
    /// TIME holds a signed duration rather than a time of day (MySQL: -838:59:59 to 838:59:59), so a negative span
    /// or one of a day or more has a literal (<c>'-838:59:59'</c>).
    /// </summary>
    public bool TimeLiteralAcceptsDurations { get; init; }

    /// <summary>The most rows one INSERT ... VALUES list may hold (SQL Server: 1,000); null when unlimited.</summary>
    public int? MaxInsertValuesRows { get; init; }

    /// <summary>
    /// The most parameters one command may bind (SQL Server: 2,100; PostgreSQL and MySQL: 65,535; SQLite: 32,766);
    /// null when the driver sets no practical limit.
    /// </summary>
    public int? MaxCommandParameters { get; init; }

    /// <summary>
    /// The value to bind for NULL in a column of this declared type when an untyped NULL would be refused (SQL Server
    /// binds it as nvarchar, which does not convert to varbinary); null when an untyped NULL will do.
    /// </summary>
    public Func<string, object?>? TypedNull { get; init; }

    /// <summary>How generated scripts terminate statements so the engine's clients split them correctly.</summary>
    public SqlScriptRules ScriptRules { get; init; } = SqlScriptRules.Standard;

    /// <summary>The statement that opens a transaction in a generated script; MySQL has no BEGIN TRANSACTION.</summary>
    public string BeginTransactionStatement { get; init; } = "BEGIN TRANSACTION;";

    /// <summary>How a dump reads all its data from one snapshot, where the engine offers one cheaply; null when it does not.</summary>
    public SnapshotReadDialect? SnapshotRead { get; init; }

    /// <summary>How a data script loads identity and serial values as they are; null when explicit values need nothing.</summary>
    public IdentityValueLoading? IdentityLoading { get; init; }

    /// <summary>
    /// How a script turns foreign-key checks off and back on, where foreign keys are part of CREATE TABLE; null when
    /// they are separate statements a dump can add after the data, or when the engine cannot turn them off.
    /// </summary>
    public ForeignKeyCheckSwitch? ForeignKeyChecks { get; init; }

    /// <summary>
    /// RAIN-P2-12 — Column types (provider type names, case-insensitive) that "=" can't compare
    /// reliably against a bound parameter: it either errors (PostgreSQL <c>json</c>, SQL Server
    /// <c>ntext</c>) or never matches (MySQL <c>FLOAT</c>, <c>JSON</c>). An UPDATE's concurrency
    /// check leaves such columns out. Names are listed without a length/precision suffix.
    /// </summary>
    public IReadOnlyCollection<string> NonComparableTypeNames { get; init; } = [];

    /// <summary>Whether <paramref name="providerTypeName"/> compares reliably, ignoring any "(n)" suffix such as <c>character(5)</c>.</summary>
    public bool CanCompareType(string? providerTypeName) =>
        providerTypeName is null || !NonComparableTypeNames.Contains(BaseTypeName(providerTypeName), System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Key column types a read cannot page past by value (<see cref="TableKeySeek"/>), listed without a suffix: MySQL
    /// ENUM and SET, and DuckDB ENUM, sort by their declaration order but compare with a text literal as text.
    /// </summary>
    public IReadOnlyCollection<string> NonSeekableTypeNames { get; init; } = [];

    /// <summary>
    /// Character types that hold code-page text rather than Unicode, listed without a suffix. Compared with a
    /// <see cref="StringLiteralStyle.National"/> literal such a column widens to Unicode, which under SQL Server's SQL
    /// collations sorts differently from the column itself and cannot use its index.
    /// </summary>
    public IReadOnlyCollection<string> NarrowStringTypeNames { get; init; } = [];

    public bool CanSeekType(string providerTypeName) =>
        !NonSeekableTypeNames.Contains(BaseTypeName(providerTypeName), System.StringComparer.OrdinalIgnoreCase);

    public bool IsNarrowStringType(string providerTypeName) =>
        NarrowStringTypeNames.Contains(BaseTypeName(providerTypeName), System.StringComparer.OrdinalIgnoreCase);

    /// <summary>The type name without any "(n)" suffix, such as <c>character</c> for <c>character(5)</c>.</summary>
    private static string BaseTypeName(string providerTypeName)
    {
        var suffix = providerTypeName.IndexOf('(');
        return (suffix < 0 ? providerTypeName : providerTypeName[..suffix]).Trim();
    }

    /// <summary>RAIN-P2-12 — The names of the result columns whose type <see cref="CanCompareType"/> rejects.</summary>
    public IEnumerable<string> NonComparableColumns(IEnumerable<ColumnInfo> columns) =>
        columns.Where(column => !CanCompareType(column.ProviderTypeName)).Select(column => column.Name);

    public string ParameterPlaceholder(int ordinal) => UsesPositionalParameters ? "?" : CreateParameterName(ordinal);

    public bool SupportsTransactionalBatches => TransactionSupport != TransactionSupportLevel.None;
    public bool GuaranteesTransactionalBatches => TransactionSupport == TransactionSupportLevel.Guaranteed;

    public string TransactionCommitSummary => TransactionSupport switch
    {
        TransactionSupportLevel.Guaranteed =>
            "The following SQL will be executed as a single transaction. Review before committing.",
        TransactionSupportLevel.BestEffort =>
            "The following SQL will be executed in a provider-managed batch. Partial changes may remain if one statement fails.",
        _ =>
            "The following SQL will be executed statement-by-statement because this provider does not guarantee transactional commits for this workflow."
    };

    public string TransactionStatusLabel => TransactionSupport switch
    {
        TransactionSupportLevel.Guaranteed => "Transactional commit",
        TransactionSupportLevel.BestEffort => "Best-effort commit",
        _ => "Non-transactional commit"
    };

    public string QuoteIdentifier(string identifier) =>
        $"{IdentifierOpenQuote}{EscapeIdentifier(identifier)}{IdentifierCloseQuote}";

    public bool SupportsObjectType(SchemaNodeType type) => type switch
    {
        SchemaNodeType.View => SupportsViews,
        SchemaNodeType.IndexFolder or SchemaNodeType.Index => SupportsIndexes,
        SchemaNodeType.TriggerFolder or SchemaNodeType.Trigger => SupportsTriggers,
        SchemaNodeType.FunctionFolder or SchemaNodeType.Function => SupportsFunctions,
        SchemaNodeType.ProcedureFolder or SchemaNodeType.Procedure => SupportsProcedures,
        _ => true
    };

    private string EscapeIdentifier(string identifier)
    {
        if (IdentifierCloseQuote == "]")
            return identifier.Replace("]", "]]");

        return identifier.Replace(IdentifierCloseQuote, IdentifierCloseQuote + IdentifierCloseQuote);
    }

    /// <summary>Renders " ORDER BY ..." for the spec's sort keys, or an empty string when there are none.</summary>
    public string BuildOrderByClause(IReadOnlyList<TableSortKey> sort) =>
        sort.Count == 0
            ? string.Empty
            : " ORDER BY " + string.Join(", ", sort.Select(k => $"{QuoteIdentifier(k.Column)} {(k.Descending ? "DESC" : "ASC")}"));

    public string CreateParameterName(int ordinal) => $"{ParameterPrefix}p{ordinal}";

    public string FormatBinaryLiteral(byte[] bytes) => BinaryLiteralStyle switch
    {
        BinaryLiteralStyle.HexPrefix => $"0x{Convert.ToHexString(bytes)}",
        // Assumes standard_conforming_strings (the default since PostgreSQL 9.1), so '\x' is not an escape.
        BinaryLiteralStyle.ByteaHex => $@"'\x{Convert.ToHexString(bytes)}'::bytea",
        BinaryLiteralStyle.BlobEscapes => $"'{string.Concat(bytes.Select(b => $@"\x{b:X2}"))}'::BLOB",
        _ => $"X'{Convert.ToHexString(bytes)}'"
    };

    public string FormatStringLiteral(string text) => StringLiteralStyle switch
    {
        // Assumes the default sql_mode (no NO_BACKSLASH_ESCAPES), where a lone backslash starts an escape sequence.
        StringLiteralStyle.BackslashEscapes => $"'{text.Replace("\\", "\\\\").Replace("'", "''")}'",
        StringLiteralStyle.National => $"N'{text.Replace("'", "''")}'",
        _ => $"'{text.Replace("'", "''")}'"
    };
}
