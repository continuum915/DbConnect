using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Reading each engine's column types as portable types and writing them for every other (data-transfer/05).</summary>
public sealed class PortableTypeMapTests
{
    private static readonly string[] Providers = ["SQLite", "PostgreSQL", "SQL Server", "MySQL", "DuckDB"];

    private static PortableColumn Read(string provider, string type, string? defaultValue = null) =>
        PortableTypeReader.Read(provider, new TableDesignColumn("c", type, true) { Default = defaultValue });

    public static TheoryData<string, string, PortableType, int?, int?, int?, string?> Spellings => new()
    {
        // provider, spelled type (as the table design reports it), portable type, length, precision, scale, note fragment
        { "SQLite", "INTEGER", PortableType.Int64, null, null, null, null },
        { "SQLite", "INT", PortableType.Int64, null, null, null, null },
        { "SQLite", "smallint", PortableType.Int16, null, null, null, null },
        { "SQLite", "varchar(20)", PortableType.VarChar, 20, null, null, null },
        { "SQLite", "NUMERIC(10,2)", PortableType.Decimal, null, 10, 2, null },
        { "SQLite", "REAL", PortableType.Double, null, null, null, null },
        { "SQLite", "BLOB", PortableType.Blob, null, null, null, null },
        { "SQLite", "BOOLEAN", PortableType.Boolean, null, null, null, null },
        { "SQLite", "DATETIME", PortableType.Timestamp, null, null, null, null },
        { "SQLite", "", PortableType.Text, null, null, null, "without a declared type" },
        { "PostgreSQL", "smallint", PortableType.Int16, null, null, null, null },
        { "PostgreSQL", "integer", PortableType.Int32, null, null, null, null },
        { "PostgreSQL", "bigint", PortableType.Int64, null, null, null, null },
        { "PostgreSQL", "character varying(20)", PortableType.VarChar, 20, null, null, null },
        { "PostgreSQL", "character varying", PortableType.Text, null, null, null, null },
        { "PostgreSQL", "character(3)", PortableType.Char, 3, null, null, null },
        { "PostgreSQL", "numeric(10,2)", PortableType.Decimal, null, 10, 2, null },
        { "PostgreSQL", "numeric", PortableType.Decimal, null, null, null, null },
        { "PostgreSQL", "real", PortableType.Single, null, null, null, null },
        { "PostgreSQL", "double precision", PortableType.Double, null, null, null, null },
        { "PostgreSQL", "bytea", PortableType.Blob, null, null, null, null },
        { "PostgreSQL", "boolean", PortableType.Boolean, null, null, null, null },
        { "PostgreSQL", "timestamp(3) without time zone", PortableType.Timestamp, null, 3, null, null },
        { "PostgreSQL", "timestamp with time zone", PortableType.TimestampTz, null, null, null, null },
        { "PostgreSQL", "time without time zone", PortableType.Time, null, null, null, null },
        { "PostgreSQL", "time with time zone", PortableType.Time, null, null, null, "offset is dropped" },
        { "PostgreSQL", "interval", PortableType.Interval, null, null, null, null },
        { "PostgreSQL", "uuid", PortableType.Uuid, null, null, null, null },
        { "PostgreSQL", "jsonb", PortableType.Json, null, null, null, null },
        { "PostgreSQL", "integer[]", PortableType.Json, null, null, null, "written as JSON" },
        { "PostgreSQL", "mood", PortableType.Text, null, null, null, "no equivalent" },
        { "PostgreSQL", "money", PortableType.Decimal, null, 19, 4, "decimal(19,4)" },
        { "PostgreSQL", "xml", PortableType.Text, null, null, null, "no equivalent" },
        { "SQL Server", "tinyint", PortableType.Int16, null, null, null, null },
        { "SQL Server", "int", PortableType.Int32, null, null, null, null },
        { "SQL Server", "bit", PortableType.Boolean, null, null, null, null },
        { "SQL Server", "nvarchar(20)", PortableType.VarChar, 20, null, null, null },
        { "SQL Server", "varchar(max)", PortableType.Text, null, null, null, null },
        { "SQL Server", "nchar(3)", PortableType.Char, 3, null, null, null },
        { "SQL Server", "decimal(10, 2)", PortableType.Decimal, null, 10, 2, null },
        { "SQL Server", "real", PortableType.Single, null, null, null, null },
        { "SQL Server", "float(53)", PortableType.Double, null, null, null, null },
        { "SQL Server", "varbinary(16)", PortableType.VarBinary, 16, null, null, null },
        { "SQL Server", "varbinary(max)", PortableType.Blob, null, null, null, null },
        { "SQL Server", "binary(4)", PortableType.Binary, 4, null, null, null },
        { "SQL Server", "datetime2(3)", PortableType.Timestamp, null, 3, null, null },
        { "SQL Server", "datetime", PortableType.Timestamp, null, null, null, null },
        { "SQL Server", "datetimeoffset(7)", PortableType.TimestampTz, null, 7, null, null },
        { "SQL Server", "time(7)", PortableType.Time, null, 7, null, null },
        { "SQL Server", "uniqueidentifier", PortableType.Uuid, null, null, null, null },
        { "SQL Server", "money", PortableType.Decimal, null, 19, 4, "decimal(19,4)" },
        { "SQL Server", "xml", PortableType.Text, null, null, null, "no equivalent" },
        { "MySQL", "tinyint", PortableType.Int8, null, null, null, null },
        { "MySQL", "tinyint(1)", PortableType.Boolean, null, null, null, null },
        { "MySQL", "tinyint unsigned", PortableType.Int16, null, null, null, null },
        { "MySQL", "smallint unsigned", PortableType.Int32, null, null, null, null },
        { "MySQL", "mediumint", PortableType.Int32, null, null, null, null },
        { "MySQL", "int", PortableType.Int32, null, null, null, null },
        { "MySQL", "int unsigned", PortableType.Int64, null, null, null, null },
        { "MySQL", "bigint unsigned", PortableType.Decimal, null, 20, 0, null },
        { "MySQL", "decimal(10,2)", PortableType.Decimal, null, 10, 2, null },
        { "MySQL", "float", PortableType.Single, null, null, null, null },
        { "MySQL", "double", PortableType.Double, null, null, null, null },
        { "MySQL", "varchar(20)", PortableType.VarChar, 20, null, null, null },
        { "MySQL", "char(36)", PortableType.Char, 36, null, null, null },
        { "MySQL", "longtext", PortableType.Text, null, null, null, null },
        { "MySQL", "varbinary(16)", PortableType.VarBinary, 16, null, null, null },
        { "MySQL", "longblob", PortableType.Blob, null, null, null, null },
        { "MySQL", "datetime(6)", PortableType.Timestamp, null, 6, null, null },
        { "MySQL", "timestamp(6)", PortableType.Timestamp, null, 6, null, "UTC" },
        { "MySQL", "time", PortableType.Time, null, null, null, "durations" },
        { "MySQL", "year", PortableType.Int16, null, null, null, null },
        { "MySQL", "json", PortableType.Json, null, null, null, null },
        { "MySQL", "enum('a','bcd')", PortableType.VarChar, 3, null, null, "not enforced" },
        { "MySQL", "set('p','q')", PortableType.VarChar, 4, null, null, "not enforced" },
        { "MySQL", "bit(1)", PortableType.Boolean, null, null, null, null },
        { "MySQL", "bit(12)", PortableType.VarBinary, 2, null, null, "bytes" },
        { "DuckDB", "TINYINT", PortableType.Int8, null, null, null, null },
        { "DuckDB", "UTINYINT", PortableType.Int16, null, null, null, null },
        { "DuckDB", "INTEGER", PortableType.Int32, null, null, null, null },
        { "DuckDB", "UBIGINT", PortableType.Decimal, null, 20, 0, null },
        { "DuckDB", "HUGEINT", PortableType.Decimal, null, 38, 0, "38 digits" },
        { "DuckDB", "DECIMAL(10,2)", PortableType.Decimal, null, 10, 2, null },
        { "DuckDB", "FLOAT", PortableType.Single, null, null, null, null },
        { "DuckDB", "DOUBLE", PortableType.Double, null, null, null, null },
        { "DuckDB", "VARCHAR", PortableType.Text, null, null, null, null },
        { "DuckDB", "BLOB", PortableType.Blob, null, null, null, null },
        { "DuckDB", "TIMESTAMP WITH TIME ZONE", PortableType.TimestampTz, null, null, null, null },
        { "DuckDB", "INTERVAL", PortableType.Interval, null, null, null, null },
        { "DuckDB", "UUID", PortableType.Uuid, null, null, null, null },
        { "DuckDB", "JSON", PortableType.Json, null, null, null, null },
        { "DuckDB", "INTEGER[]", PortableType.Json, null, null, null, "written as JSON" },
        { "DuckDB", "STRUCT(x INTEGER)", PortableType.Json, null, null, null, "written as JSON" },
        { "DuckDB", "ENUM('a', 'b')", PortableType.VarChar, 1, null, null, "not enforced" },
    };

    [Theory]
    [MemberData(nameof(Spellings))]
    public void EachEnginesSpellingIsReadAsAPortableType(string provider, string spelled, PortableType type, int? length,
        int? precision, int? scale, string? note)
    {
        var column = Read(provider, spelled);

        Assert.Equal(type, column.Type);
        Assert.Equal(length, column.Length);
        Assert.Equal(precision, column.Precision);
        Assert.Equal(scale, column.Scale);
        if (note == null) Assert.Empty(column.Notes);
        else Assert.Contains(column.Notes, n => n.Contains(note, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SQL Server", "smallint", "((0))", PortableDefaultKind.Number, "0")]
    [InlineData("SQL Server", "nvarchar(10)", "(N'it''s')", PortableDefaultKind.String, "it's")]
    [InlineData("SQL Server", "bit", "((1))", PortableDefaultKind.Boolean, "true")]
    [InlineData("SQL Server", "datetime2(7)", "(getdate())", PortableDefaultKind.CurrentTimestamp, null)]
    [InlineData("PostgreSQL", "text", "'x'::text", PortableDefaultKind.String, "x")]
    [InlineData("PostgreSQL", "boolean", "true", PortableDefaultKind.Boolean, "true")]
    [InlineData("PostgreSQL", "timestamp without time zone", "now()", PortableDefaultKind.CurrentTimestamp, null)]
    [InlineData("PostgreSQL", "numeric(5,1)", "'-1.5'::numeric", PortableDefaultKind.String, "-1.5")]
    [InlineData("MySQL", "tinyint(1)", "1", PortableDefaultKind.Boolean, "true")]
    [InlineData("MySQL", "datetime(6)", "CURRENT_TIMESTAMP(6)", PortableDefaultKind.CurrentTimestamp, null)]
    [InlineData("DuckDB", "DOUBLE", "1.5", PortableDefaultKind.Number, "1.5")]
    [InlineData("SQLite", "INTEGER", "-5", PortableDefaultKind.Number, "-5")]
    public void DefaultsEveryEngineCanWriteAreRead(string provider, string type, string text, PortableDefaultKind kind, string? value)
    {
        var column = Read(provider, type, text);

        Assert.Equal(new PortableDefault(kind, value), column.Default);
        Assert.Empty(column.Notes);
    }

    [Theory]
    [InlineData("PostgreSQL", "integer", "nextval('t_w_seq'::regclass)")]
    [InlineData("SQL Server", "int", "(abs(checksum(newid())))")]
    [InlineData("MySQL", "varchar(36)", "(uuid())")]
    public void AnExpressionDefaultIsLeftOutAndSaysSo(string provider, string type, string text)
    {
        var column = Read(provider, type, text);

        Assert.Null(column.Default);
        Assert.Contains(column.Notes, n => n.Contains("not carried over"));
    }

    public static TheoryData<string, PortableType, string> Renderings => new()
    {
        { "PostgreSQL", PortableType.Int8, "smallint" }, { "PostgreSQL", PortableType.TimestampTz, "timestamptz" },
        { "PostgreSQL", PortableType.Json, "jsonb" }, { "PostgreSQL", PortableType.Blob, "bytea" },
        { "SQL Server", PortableType.Boolean, "bit" }, { "SQL Server", PortableType.Text, "nvarchar(max)" },
        { "SQL Server", PortableType.TimestampTz, "datetimeoffset(7)" }, { "SQL Server", PortableType.Uuid, "uniqueidentifier" },
        { "MySQL", PortableType.Boolean, "tinyint(1)" }, { "MySQL", PortableType.Text, "longtext" },
        { "MySQL", PortableType.Timestamp, "datetime(6)" }, { "MySQL", PortableType.Json, "json" },
        { "DuckDB", PortableType.Int8, "TINYINT" }, { "DuckDB", PortableType.TimestampTz, "TIMESTAMPTZ" },
        { "DuckDB", PortableType.Interval, "INTERVAL" }, { "SQLite", PortableType.Int32, "INTEGER" },
        { "SQLite", PortableType.Double, "REAL" }, { "SQLite", PortableType.Blob, "BLOB" },
    };

    [Theory]
    [MemberData(nameof(Renderings))]
    public void EachEngineWritesAPortableType(string provider, PortableType type, string expected) =>
        Assert.Equal(expected, PortableTypeDialects.For(provider)!.Type(new PortableColumn("c", type, true), false).Type);

    /// <summary>
    /// For every ordered pair of engines and every type the source can declare, the target's type reads back as the
    /// same portable type, or the loss is noted on the way (reading the source or writing the target).
    /// </summary>
    [Fact]
    public void EveryPairKeepsTheTypeOrNotesTheLoss()
    {
        var silentChanges = new List<string>();
        foreach (var (source, spelled) in Spellings.Select(row => ((string)row[0], (string)row[1])))
            foreach (var target in Providers)
            {
                var column = Read(source, spelled);
                var rendered = PortableTypeDialects.For(target)!.Type(column, false);
                var back = Read(target, rendered.Type);
                if (back.Type == column.Type) continue;
                // Widening to a type that holds every value of the source loses nothing.
                if (Widens(column.Type, back.Type)) continue;
                if (column.Notes.Count > 0 || rendered.Note != null) continue;
                silentChanges.Add($"{source} {spelled} → {target} {rendered.Type} reads back as {back.Type}, not {column.Type}");
            }
        Assert.True(silentChanges.Count == 0, string.Join("\n", silentChanges));
    }

    private static bool Widens(PortableType from, PortableType to) => (from, to) switch
    {
        (PortableType.Int8, PortableType.Int16 or PortableType.Int32 or PortableType.Int64) => true,
        (PortableType.Int16, PortableType.Int32 or PortableType.Int64) => true,
        (PortableType.Int32, PortableType.Int64) => true,
        (PortableType.Single, PortableType.Double) => true,
        (PortableType.VarChar or PortableType.Char, PortableType.Text) => true,
        (PortableType.Binary or PortableType.VarBinary, PortableType.Blob) => true,
        _ => false
    };

    [Fact]
    public void CreateTableWritesKeysIdentityDefaultsAndNotes()
    {
        var table = new PortableTable(new DatabaseObjectIdentity(null, "dbo", "orders", SchemaNodeType.Table),
        [
            new PortableColumn("id", PortableType.Int32, false) { IsIdentity = true },
            new PortableColumn("note", PortableType.Text, true) { Default = new(PortableDefaultKind.String, "it's") },
            new PortableColumn("placed", PortableType.Timestamp, false) { Precision = 6, Default = PortableDefault.CurrentTimestamp },
            new PortableColumn("open", PortableType.Boolean, true) { Default = new(PortableDefaultKind.Boolean, "true") }
        ], ["id"]);
        var target = new DatabaseObjectIdentity(null, null, "orders", SchemaNodeType.Table);

        var mysql = PortableTables.CreateTable("MySQL", ProviderCapabilityDeclarations.MySql, target, table);
        Assert.Equal("""
            CREATE TABLE `orders` (
                `id` int NOT NULL AUTO_INCREMENT,
                `note` longtext DEFAULT ('it''s'),
                `placed` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                `open` tinyint(1) DEFAULT 1,
                PRIMARY KEY (`id`)
            )
            """.ReplaceLineEndings("\n"), mysql.Sql);
        Assert.Contains(mysql.Notes, n => n.Contains("booleans as 0 and 1"));

        var sqlite = PortableTables.CreateTable("SQLite", ProviderCapabilityDeclarations.Sqlite, target, table);
        Assert.StartsWith("CREATE TABLE \"orders\" (\n    \"id\" INTEGER NOT NULL PRIMARY KEY,", sqlite.Sql);
        Assert.DoesNotContain("PRIMARY KEY (", sqlite.Sql);

        var duck = PortableTables.CreateTable("DuckDB", ProviderCapabilityDeclarations.DuckDb, target, table);
        Assert.Contains(duck.Notes, n => n.Contains("DuckDB has no identity columns"));
        Assert.Contains("PRIMARY KEY (\"id\")", duck.Sql);
    }
}
