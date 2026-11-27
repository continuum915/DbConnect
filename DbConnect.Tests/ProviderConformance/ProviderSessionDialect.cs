using System;
using System.Collections.Generic;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// The provider-specific SQL the session contract needs. An unknown provider
/// throws instead of falling back to another provider's SQL, so a new provider
/// cannot pass the contract by accident.
/// </summary>
public sealed record ProviderSessionDialect(
    string TempTablePrefix,
    string CreateTempTableColumns,
    string TempTableKeyword,
    string TableHint,
    string BeginSql,
    SessionSetProbe? SetProbe,
    string? LongRunningSql,
    IReadOnlyList<StatementForm> ResultSetForms)
{
    public string CreateTempTable(string name) =>
        $"CREATE {TempTableKeyword} {name} ({CreateTempTableColumns});";

    /// <summary>Statement forms every provider accepts that the keyword-sniffing detector used to miss.</summary>
    private static readonly StatementForm[] CommonForms =
    [
        new("WITH ... SELECT", "WITH c AS (SELECT 1 AS a) SELECT a FROM c;", 1),
        new("line comment before SELECT", "-- leading comment\nSELECT 1 AS a;", 1),
        new("block comment before SELECT", "/* leading */ SELECT 1 AS a;", 1)
    ];

    /// <summary>
    /// False when the driver's reader has no rows-affected count for DML. DuckDB.NET reports -1, so the
    /// contract expects a negative "unknown" count there instead of a number.
    /// </summary>
    public bool ReportsRowsAffected { get; init; } = true;

    public static ProviderSessionDialect For(string providerName)
    {
        var dialect = Build(providerName);
        return providerName == "DuckDB"
            ? dialect with
            {
                ReportsRowsAffected = false,
                ResultSetForms = [.. dialect.ResultSetForms, new("TABLE shorthand", "TABLE {table};", 1)]
            }
            : dialect;
    }

    private static ProviderSessionDialect Build(string providerName) => providerName switch
    {
        "SQLite" or "DuckDB" => new(
            TempTablePrefix: string.Empty,
            CreateTempTableColumns: "val INTEGER",
            TempTableKeyword: "TEMPORARY TABLE",
            TableHint: string.Empty,
            BeginSql: "BEGIN;",
            SetProbe: null,
            // Embedded engines run in-process and cannot interrupt a statement mid-flight.
            LongRunningSql: null,
            ResultSetForms:
            [
                .. CommonForms,
                new("VALUES list", "VALUES (1),(2);", 2),
                new("INSERT ... RETURNING", "INSERT INTO {temp} (val) VALUES (5) RETURNING val;", 1)
            ]),
        "PostgreSQL" => new(
            TempTablePrefix: string.Empty,
            CreateTempTableColumns: "val INTEGER",
            TempTableKeyword: "TEMPORARY TABLE",
            TableHint: string.Empty,
            BeginSql: "BEGIN;",
            SetProbe: new("SET application_name = 'rain_session';", "SHOW application_name;", "rain_session"),
            LongRunningSql: "SELECT pg_sleep(60);",
            ResultSetForms:
            [
                .. CommonForms,
                new("VALUES list", "VALUES (1),(2);", 2),
                new("INSERT ... RETURNING", "INSERT INTO {temp} (val) VALUES (5) RETURNING val;", 1),
                new("TABLE shorthand", "TABLE {table};", 1),
                new(
                    "CALL with an INOUT parameter",
                    "CALL rain_conformance_proc(1);",
                    1,
                    Setup: "CREATE PROCEDURE rain_conformance_proc(INOUT x integer) LANGUAGE plpgsql AS $$ BEGIN x := x + 1; END $$;",
                    Cleanup: "DROP PROCEDURE IF EXISTS rain_conformance_proc(integer);")
            ]),
        "SQL Server" => new(
            TempTablePrefix: "#",
            CreateTempTableColumns: "val INT",
            TempTableKeyword: "TABLE",
            // A plain read blocks on another session's uncommitted row instead of
            // proving it is invisible; READPAST skips locked rows.
            TableHint: " WITH (READPAST)",
            BeginSql: "BEGIN TRANSACTION;",
            SetProbe: new("SET DATEFIRST 3;", "SELECT @@DATEFIRST AS setting;", "3"),
            LongRunningSql: "WAITFOR DELAY '00:01:00';",
            ResultSetForms:
            [
                .. CommonForms,
                new("INSERT ... OUTPUT", "INSERT INTO {temp} (val) OUTPUT inserted.val VALUES (5);", 1),
                new("EXEC", "EXEC sp_executesql N'SELECT 1 AS a';", 1)
            ]),
        "MySQL" => new(
            TempTablePrefix: string.Empty,
            CreateTempTableColumns: "val INTEGER",
            TempTableKeyword: "TEMPORARY TABLE",
            TableHint: string.Empty,
            BeginSql: "START TRANSACTION;",
            SetProbe: new("SET @rain_session = 5;", "SELECT @rain_session AS setting;", "5"),
            // MySQL returns early from SLEEP() when killed instead of failing, so use
            // a statement that really is interrupted.
            LongRunningSql: "SELECT COUNT(*) FROM information_schema.columns a, information_schema.columns b, information_schema.columns c, information_schema.columns d;",
            ResultSetForms:
            [
                .. CommonForms,
                new("VALUES list", "VALUES ROW(1), ROW(2);", 2),
                new(
                    "CALL",
                    "CALL rain_conformance_proc();",
                    1,
                    Setup: "CREATE PROCEDURE rain_conformance_proc() SELECT 1 AS a;",
                    Cleanup: "DROP PROCEDURE IF EXISTS rain_conformance_proc;")
            ]),
        _ => throw new NotSupportedException($"No session dialect is defined for {providerName}.")
    };
}

/// <summary>
/// A statement that returns rows. <c>{temp}</c> and <c>{table}</c> stand for the session temp table and the
/// seeded table; the optional setup and cleanup run around it for forms that need a routine.
/// </summary>
public sealed record StatementForm(string Label, string Sql, int MinRows, string? Setup = null, string? Cleanup = null);

/// <summary>A SET statement, the query that reads it back, and the value a session that ran the SET must see.</summary>
public sealed record SessionSetProbe(string Set, string Read, string Expected);
