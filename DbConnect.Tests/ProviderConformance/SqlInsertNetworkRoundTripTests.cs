using DbConnect.Models;
using DbConnect.Services;
using static DbConnect.Tests.SqlInsertRoundTrip;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// SQL INSERT exports from PostgreSQL, MySQL and SQL Server re-run unchanged on the same provider for every common
/// type (ticket sql-dump-and-insert-export/02). Container lane: <c>build/databases/run-provider-integration.ps1</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqlInsertNetworkRoundTripTests
{
    [PostgreSqlExportTheory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task PostgreSqlValuesSurviveTheScript(int rowsPerStatement)
    {
        await using var session = await PostgreSqlExportEnvironment.OpenAsync();
        const string columns = "id integer PRIMARY KEY, small smallint, whole integer, big bigint, amount numeric(20,6), unscaled numeric, " +
            "single real, dbl double precision, text_value text, fixed varchar(200), unicode_value text, blob_value bytea, flag boolean, " +
            "day date, clock time, clock_tz timetz, stamp timestamp, stamp_tz timestamptz, uid uuid, doc jsonb, raw_doc json";
        await RoundTripAsync(session, columns, rowsPerStatement,
            [
                $"1, -32768, -2147483648, 9223372036854775807, 12345678901234.567890, 0.000000000000000000000000001, 0.1, 0.1, {Literal(Text)}, {Literal(Text)}, {Literal(Unicode)}, '\\x00ff27'::bytea, true, '2026-11-14', '23:59:59.123456', '12:34:56.5+05:30', '2026-11-14 12:34:56.123456', '2026-11-14 12:34:56.5+05:30', 'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11', {Literal("{\"k\": [1, \"it's\"], \"u\": \"日本\"}")}, {Literal("{ \"spaced\" :  1 }")}",
                "2, 0, 0, -9223372036854775808, -0.000001, -1e20, -3.4e38, -1.5e300, '', '', '', ''::bytea, false, '0001-01-01', '00:00:00', '00:00:00-08', '1970-01-01 00:00:00', '2000-01-01 00:00:00+00', '00000000-0000-0000-0000-000000000000', '[]', '[]'",
                "3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL",
                "4, 1, 1, 1, 1, 1, 3.4028235e38, 3.141592653589793, 'x', 'y', 'z', 'z'::bytea, true, '2024-02-29', '12:00:00', '23:59:59.999999-12', '2024-02-29 00:00:00', '2024-02-29 00:00:00-08', 'ffffffff-ffff-ffff-ffff-ffffffffffff', 'null', 'null'"
            ],
            // json has no equality operator; its text is compared instead.
            exceptSelect: "id, small, whole, big, amount, unscaled, single, dbl, text_value, fixed, unicode_value, blob_value, flag, day, clock, clock_tz, stamp, stamp_tz, uid, doc, raw_doc::text");
    }

    [MySqlExportTheory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task MySqlValuesSurviveTheScript(int rowsPerStatement)
    {
        await using var session = await MySqlExportEnvironment.OpenAsync();
        const string columns = "id INT PRIMARY KEY, tiny TINYINT, small SMALLINT, whole INT, big BIGINT, ubig BIGINT UNSIGNED, amount DECIMAL(20,6), " +
            "single FLOAT, dbl DOUBLE, text_value TEXT, fixed VARCHAR(200), unicode_value VARCHAR(100) CHARACTER SET utf8mb4, blob_value BLOB, " +
            "fixed_binary VARBINARY(16), flag BOOLEAN, bits BIT(8), day DATE, clock TIME(6), stamp DATETIME(6), stamp_tz TIMESTAMP(6) NULL, " +
            "yr YEAR, uid CHAR(36), doc JSON, choice ENUM('a','b''c'), shape GEOMETRY";
        // Text is escaped here by hand (backslashes doubled) as an oracle independent of StringLiteralStyle.
        await RoundTripAsync(session, columns, rowsPerStatement,
            [
                $"1, -128, -32768, -2147483648, 9223372036854775807, 18446744073709551615, 12345678901234.567890, 0.1, 0.1, {Literal(Text).Replace("\\", "\\\\")}, {Literal(Text).Replace("\\", "\\\\")}, {Literal(Unicode)}, X'00FF27', X'5C27', TRUE, b'10100101', '2026-11-14', '23:59:59.123456', '2026-11-14 12:34:56.123456', '2026-11-14 12:34:56.5', 2026, 'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11', {Literal("{\"k\": [1, \"it's \\\" \\\\ \\n\"], \"u\": \"日本\"}").Replace("\\", "\\\\")}, 'b''c', ST_GeomFromText('POINT(1 2)', 4326)",
                "2, 0, 0, 0, -9223372036854775808, 0, -0.000001, -3.4e38, -1.5e300, '', '', '', X'', X'', FALSE, b'0', '1000-01-01', '-838:59:59', '1000-01-01 00:00:00', '1970-01-01 00:00:01', 1901, '00000000-0000-0000-0000-000000000000', '[]', 'a', ST_GeomFromText('LINESTRING(0 0, 1.5 -2)')",
                "3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL",
                "4, 1, 1, 1, 1, 1, 1, 3.40282e38, 3.141592653589793, 'x', 'y', 'z', X'7A', X'7A', TRUE, b'1', '2024-02-29', '838:59:58.999999', '2024-02-29 00:00:00', '2038-01-19 03:14:07.999999', 2155, 'ffffffff-ffff-ffff-ffff-ffffffffffff', 'null', 'a', ST_GeomFromText('POLYGON((0 0, 1 0, 1 1, 0 0))')"
            ]);
    }

    [SqlServerExportTheory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task SqlServerValuesSurviveTheScript(int rowsPerStatement)
    {
        await using var session = await SqlServerExportEnvironment.OpenAsync();
        // datetime/smalldatetime read 'yyyy-MM-dd hh:mm:ss' as year-day-month under DMY languages; the script must not depend on it.
        await ExecuteAsync(session, "SET LANGUAGE british");
        const string columns = "id int PRIMARY KEY, tiny tinyint, small smallint, whole int, big bigint, amount decimal(20,6), cash money, " +
            "single real, dbl float, text_value varchar(200), unicode_value nvarchar(100), max_value nvarchar(max), blob_value varbinary(max), " +
            "flag bit, day date, clock time(7), legacy_stamp datetime, small_stamp smalldatetime, stamp datetime2(7), stamp_offset datetimeoffset(7), " +
            "uid uniqueidentifier, doc nvarchar(max)";
        await RoundTripAsync(session, columns, rowsPerStatement,
            [
                $"1, 255, -32768, -2147483648, 9223372036854775807, 12345678901234.567890, 922337203685477.5807, 0.1, 0.1, {Literal(Text)}, N{Literal(Unicode)}, N{Literal(Unicode + Text)}, 0x00FF27, 1, '2026-11-14', '23:59:59.1234567', '2026-11-14T12:34:56.123', '2026-11-14T12:34:00', '2026-11-14T12:34:56.1234567', '2026-11-14T12:34:56.1234567+05:30', 'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11', N{Literal("{\"k\": [1, \"it's\"], \"u\": \"日本\"}")}",
                "2, 0, 0, 0, -9223372036854775808, -0.000001, -922337203685477.5808, -3.4e38, -1.5e300, '', N'', N'', 0x, 0, '0001-01-01', '00:00:00', '1753-01-01T00:00:00', '1900-01-01T00:00:00', '0001-01-01T00:00:00', '0001-01-01T00:00:00-14:00', '00000000-0000-0000-0000-000000000000', N'[]'",
                "3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL",
                "4, 1, 1, 1, 1, 1, 1, 3.4028235e38, 3.141592653589793, 'x', N'y', N'z', 0x7A, 1, '9999-12-31', '12:00:00', '9999-12-31T23:59:59.997', '2079-06-06T23:59:00', '9999-12-31T23:59:59.9999999', '9999-12-31T23:59:59.9999999+14:00', 'ffffffff-ffff-ffff-ffff-ffffffffffff', N'null'"
            ]);
    }

    [PostgreSqlExportTheory]
    [InlineData("ARRAY['a','b']::text[]", "tags")]
    [InlineData("INTERVAL '2 days 03:00'", "span")]
    public async Task PostgreSqlValueWithNoLiteralFormFailsNamingTheColumn(string expression, string column)
    {
        await using var session = await PostgreSqlExportEnvironment.OpenAsync();

        var (outcome, script, _) = await ExportAsync(session, $"SELECT 1 AS id, {expression} AS {column}", "t");

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Contains($"\"{column}\"", outcome.Diagnostic);
        Assert.Null(script);
    }

    private static async Task RoundTripAsync(IDbSession session, string columns, int rowsPerStatement, string[] rows, string? exceptSelect = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var source = $"rain_sqlinsert_src_{suffix}";
        var target = $"rain_sqlinsert_dst_{suffix}";
        var caps = session.Provider.Capabilities;
        try
        {
            await ExecuteAsync(session, $"CREATE TABLE {caps.QuoteIdentifier(source)} ({columns})");
            await ExecuteAsync(session, $"CREATE TABLE {caps.QuoteIdentifier(target)} ({columns})");
            foreach (var row in rows)
                await ExecuteAsync(session, $"INSERT INTO {caps.QuoteIdentifier(source)} VALUES ({row})");

            await AssertAsync(session, source, target, rowsPerStatement, exceptSelect);
        }
        finally
        {
            await session.ExecuteQueryAsync($"DROP TABLE IF EXISTS {caps.QuoteIdentifier(source)}");
            await session.ExecuteQueryAsync($"DROP TABLE IF EXISTS {caps.QuoteIdentifier(target)}");
        }
    }
}
