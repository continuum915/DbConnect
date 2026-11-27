using DbConnect.Models;
using DbConnect.Services;
using static DbConnect.Tests.SqlInsertRoundTrip;

namespace DbConnect.Tests;

/// <summary>
/// Exports a query result as SQL INSERT, runs the script into an empty copy of the table on the same engine and
/// compares what the engine reads back from both tables (ticket sql-dump-and-insert-export/01). The network
/// providers are in <c>ProviderConformance/SqlInsertNetworkRoundTripTests</c>.
/// </summary>
public sealed class SqlInsertRoundTripTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task SqliteValuesSurviveTheScript(int rowsPerStatement)
    {
        await using var session = await new SqliteProvider().OpenSessionAsync(new ConnectionInfo { ConnectionString = "Data Source=:memory:" });
        await ExecuteAsync(session, "CREATE TABLE src (id INTEGER PRIMARY KEY, whole INTEGER, big INTEGER, real_value REAL, text_value TEXT, unicode_value TEXT, blob_value BLOB, flag INTEGER, stamp TEXT, empty_value TEXT)");
        await ExecuteAsync(session, "CREATE TABLE dst (id INTEGER PRIMARY KEY, whole INTEGER, big INTEGER, real_value REAL, text_value TEXT, unicode_value TEXT, blob_value BLOB, flag INTEGER, stamp TEXT, empty_value TEXT)");
        await ExecuteAsync(session, $"INSERT INTO src VALUES (1, -42, 9223372036854775807, 0.1, {Literal(Text)}, {Literal(Unicode)}, X'00FF7F', 1, '2026-11-14 12:34:56.789', NULL)");
        await ExecuteAsync(session, "INSERT INTO src VALUES (2, 0, -9223372036854775808, -1.5e300, '', '', X'', 0, '2026-01-01', NULL)");
        await ExecuteAsync(session, "INSERT INTO src VALUES (3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)");
        await ExecuteAsync(session, "INSERT INTO src VALUES (4, 7, 7, 3.141592653589793, 'x', 'y', X'01', 1, 'z', 'w')");

        await AssertAsync(session, "src", "dst", rowsPerStatement);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task DuckDbValuesSurviveTheScript(int rowsPerStatement)
    {
        await using var session = await new DuckDbProvider().OpenSessionAsync(new ConnectionInfo { ConnectionString = "Data Source=:memory:" });
        const string columns = "id INTEGER PRIMARY KEY, tiny TINYINT, big BIGINT, huge HUGEINT, amount DECIMAL(18,4), wide DECIMAL(38,10), dbl DOUBLE, text_value VARCHAR, unicode_value VARCHAR, blob_value BLOB, flag BOOLEAN, day DATE, clock TIME, stamp TIMESTAMP, stamp_tz TIMESTAMPTZ, uid UUID, doc JSON";
        await ExecuteAsync(session, $"CREATE TABLE src ({columns})");
        await ExecuteAsync(session, $"CREATE TABLE dst ({columns})");
        await ExecuteAsync(session, $"INSERT INTO src VALUES (1, -128, 9223372036854775807, -170141183460469231731687303715884105728, 12345.6700, 12345678901234567.0123456789, 0.1, {Literal(Text)}, {Literal(Unicode)}, '\\x00\\xFF\\x27'::BLOB, true, DATE '2026-11-14', TIME '23:59:59.123456', TIMESTAMP '2026-11-14 12:34:56.123456', TIMESTAMPTZ '2026-11-14 12:34:56.5+05:30', 'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11', '{{\"k\": [1, \"it''s\"]}}')");
        await ExecuteAsync(session, "INSERT INTO src VALUES (2, 0, -9223372036854775808, 0, -0.0001, 0, -1.5e300, '', '', ''::BLOB, false, DATE '0001-01-01', TIME '00:00:00', TIMESTAMP '1970-01-01 00:00:00', TIMESTAMPTZ '2000-01-01 00:00:00+00', '00000000-0000-0000-0000-000000000000', '[]')");
        await ExecuteAsync(session, "INSERT INTO src VALUES (3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)");
        await ExecuteAsync(session, "INSERT INTO src VALUES (4, 1, 1, 170141183460469231731687303715884105727, 1, 1, 3.141592653589793, 'x', 'y', 'z'::BLOB, true, DATE '2024-02-29', TIME '12:00:00', TIMESTAMP '2024-02-29 00:00:00', TIMESTAMPTZ '2024-02-29 00:00:00-08', 'ffffffff-ffff-ffff-ffff-ffffffffffff', 'null')");

        await AssertAsync(session, "src", "dst", rowsPerStatement);
    }
}
