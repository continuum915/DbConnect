using System.Globalization;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// The cross-engine matrix (data-transfer/09): a 20-table fixture, described once as portable tables and created on the
/// source engine, is transferred into an empty database of another engine. Each table's rows are compared by count and
/// by a checksum of values normalized by type, and the copy's primary keys, foreign keys and indexes are read back from
/// the target's catalog. SQLite → DuckDB runs locally; pairs with a server need the container lane's
/// DBCONNECT_CONFORMANCE_*_CONNECTION_STRING variables.
/// </summary>
public sealed class DataTransferMatrixTests : DumpTestDatabases
{
    private const int Children = 19;
    private const int ParentRows = 40;
    private const int ChildRows = 12;

    private static readonly PortableColumn[] ParentColumns =
    [
        new("id", PortableType.Int64, false),
        new("name", PortableType.VarChar, false) { Length = 40 },
        new("amount", PortableType.Decimal, true) { Precision = 12, Scale = 2 },
        new("ratio", PortableType.Double, true),
        new("flag", PortableType.Boolean, true),
        new("born", PortableType.Date, true),
        new("seen", PortableType.Timestamp, true) { Precision = 6 },
        new("uid", PortableType.Uuid, true),
        new("data", PortableType.VarBinary, true) { Length = 16 },
        new("note", PortableType.Text, true),
        new("small", PortableType.Int16, true),
        new("code", PortableType.Char, true) { Length = 3 }
    ];

    private static readonly PortableColumn[] ChildColumns =
    [
        new("id", PortableType.Int64, false),
        new("parent_id", PortableType.Int64, false),
        new("label", PortableType.VarChar, true) { Length = 20 },
        new("qty", PortableType.Int32, true)
    ];

    private static string Child(int n) => $"t{n + 1:00}";

    /// <summary>Row values chosen to be exact on every engine: two-decimal amounts, binary fractions, microsecond times.</summary>
    private static object?[] ParentRow(int i) =>
    [
        (long)i, i == 2 ? "it's \"quoted\" é" : $"name {i}", i % 7 == 0 ? null : i * 1.25m - 10m, i % 5 == 0 ? null : i / 4.0,
        i % 3 == 0, i % 6 == 0 ? null : new DateTime(1990, 1, 1).AddDays(i * 37), new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(i * 12_340L),
        i % 4 == 0 ? null : new Guid(i, 2, 3, [4, 5, 6, 7, 8, 9, 10, (byte)i]), i % 8 == 0 ? null : new byte[] { 0, (byte)i, 255 },
        i % 9 == 0 ? "" : $"note {i}", (short)(i - 20), i % 2 == 0 ? "abc" : "xy "
    ];

    public static TheoryData<string, string> Pairs => new()
    {
        { "SQLite", "DuckDB" },
        { "PostgreSQL", "SQL Server" },
        { "MySQL", "PostgreSQL" },
        { "SQL Server", "MySQL" },
        { "SQLite", "PostgreSQL" },
        { "SQLite", "SQL Server" },
        { "SQLite", "MySQL" },
    };

    [Theory]
    [Trait("Category", "Integration")]
    [MemberData(nameof(Pairs))]
    public async Task TwentyTablesCrossEnginesWithTheirValuesKeysAndIndexes(string from, string to)
    {
        var servers = new List<ScratchServer>();
        try
        {
            var source = await DatabaseAsync(from, "src", servers);
            var target = await DatabaseAsync(to, "dst", servers);
            if (source == null || target == null) return;
            await CreateFixtureAsync(source);
            var tables = (await DumpCatalog.ResolveAsync(source, DumpSelection.Database)).Tables.Where(t => t.Kind == SchemaNodeType.Table).ToList();
            Assert.Equal(Children + 1, tables.Count);

            var outcomes = await DataTransferJob.RunAsync(source, target, tables, new DataTransferOptions { BatchSize = 7 });

            Assert.All(outcomes, o => Assert.True(o.Status == DataTransferTableStatus.Created, $"{o.Table.Name}: {o.Status} {o.Diagnostic}"));
            Assert.All(outcomes, o => Assert.DoesNotContain(o.Notes, n => n.Contains("was not created")));
            var copies = (await DumpCatalog.ResolveAsync(target, DumpSelection.Database)).Tables.Where(t => t.Kind == SchemaNodeType.Table).ToList();
            foreach (var table in tables)
            {
                var copy = copies.Single(c => c.Name == table.Name);
                var columns = table.Name == "t01" ? ParentColumns : ChildColumns;
                Assert.Equal(await ChecksumAsync(source, table, columns), await ChecksumAsync(target, copy, columns));
                var design = await ((ITableDesignSource)target.Provider).GetTableDesignAsync(target.ConnectionString, copy);
                Assert.Equal(["id"], design.PrimaryKey?.Columns);
                if (table.Name == "t01") continue;
                var key = Assert.Single(design.ForeignKeys);
                Assert.Equal(table.Name == "t02" || table.Name.EndsWith('0') ? "t01" : Child(int.Parse(table.Name[1..], CultureInfo.InvariantCulture) - 2), key.ReferencedTable.Name);
                Assert.Contains(design.Indexes, i => i.Origin == TableDesignIndexOrigin.CreateIndex && i.Parts.Single().Column == "label");
            }
        }
        finally
        {
            foreach (var server in servers) await server.DisposeAsync();
        }
    }

    private async Task<DatabaseSession?> DatabaseAsync(string provider, string name, List<ScratchServer> servers)
    {
        if (provider is "SQLite" or "DuckDB") return Session(provider, name);
        var server = ScratchServer.For(provider);
        if (server == null) return null;
        servers.Add(server);
        return await server.CreateDatabaseAsync(name, null);
    }

    /// <summary>t01 is the parent; t02 … t20 hang off it or off the table before them, each with an index on its label.</summary>
    private static async Task CreateFixtureAsync(DatabaseSession source)
    {
        var dialect = source.Capabilities;
        var types = PortableTypeDialects.For(source.ProviderName)!;
        var schema = types.DefaultSchema;
        DatabaseObjectIdentity Id(string name) => new(null, schema, name, SchemaNodeType.Table);
        await using var db = await SessionSetup.OpenAsync(source);
        async Task RunAsync(string sql)
        {
            var result = await db.ExecuteTransactionAsync([new SqlCommandSpec(sql, new Dictionary<string, object?>())]);
            Assert.True(result.Succeeded, $"{result.ErrorMessage}\n{sql}");
        }
        async Task InsertAsync(string table, PortableColumn[] columns, IEnumerable<object?[]> rows)
        {
            var convert = columns.Select(c => PortableValueConverter.For(c, source.ProviderName)).ToArray();
            var insert = new DataTransferInsert(dialect, SqlInsertTarget.ForTable(Id(table), dialect), columns.Select(c => c.Name).ToList(), 50,
                columnTypes: columns.Select(c => c.Type is PortableType.VarBinary ? "varbinary" : c.Type.ToString()).ToList());
            var converted = rows.Select(r => (IReadOnlyList<object?>)r.Select((v, i) => convert[i](v)).ToArray()).ToList();
            var result = await db.ExecuteTransactionAsync(insert.Commands(converted));
            Assert.True(result.Succeeded, result.ErrorMessage);
        }

        await RunAsync(PortableTables.CreateTable(source.ProviderName, dialect, Id("t01"), new(Id("t01"), ParentColumns, ["id"])).Sql);
        await InsertAsync("t01", ParentColumns, Enumerable.Range(1, ParentRows).Select(ParentRow));
        for (var n = 1; n <= Children; n++)
        {
            var name = Child(n);
            var parent = n == 1 || (n + 1) % 10 == 0 ? "t01" : Child(n - 1);
            var inline = !types.AddsForeignKeysAfterCreate;
            var referenced = inline ? dialect.QuoteIdentifier(parent) : SqlInsertTarget.ForTable(Id(parent), dialect);
            var key = $"FOREIGN KEY ({dialect.QuoteIdentifier("parent_id")}) REFERENCES {referenced} ({dialect.QuoteIdentifier("id")})";
            await RunAsync(PortableTables.CreateTable(source.ProviderName, dialect, Id(name), new(Id(name), ChildColumns, ["id"]), inline ? [key] : null).Sql);
            if (!inline) await RunAsync($"ALTER TABLE {SqlInsertTarget.ForTable(Id(name), dialect)} ADD CONSTRAINT {dialect.QuoteIdentifier("fk_" + name)} {key}");
            var indexed = types.IndexesNameBareTable ? dialect.QuoteIdentifier(name) : SqlInsertTarget.ForTable(Id(name), dialect);
            await RunAsync($"CREATE INDEX {dialect.QuoteIdentifier("ix_" + name + "_label")} ON {indexed} ({dialect.QuoteIdentifier("label")})");
            var parentRows = parent == "t01" ? ParentRows : ChildRows;
            await InsertAsync(name, ChildColumns, Enumerable.Range(1, ChildRows)
                .Select(i => new object?[] { (long)i, (long)(i % parentRows + 1), i % 5 == 0 ? null : $"{name} label {i}", i * n }));
        }
    }

    /// <summary>The table's rows by id, each value normalized by its portable type, so two engines' copies compare equal.</summary>
    private static async Task<string> ChecksumAsync(DatabaseSession session, DatabaseObjectIdentity table, PortableColumn[] columns)
    {
        await using var db = await SessionSetup.OpenAsync(session);
        var dialect = session.Capabilities;
        var result = await db.ExecuteQueryAsync($"SELECT {string.Join(", ", columns.Select(c => dialect.QuoteIdentifier(c.Name)))} " +
                                                $"FROM {SqlInsertTarget.ForTable(table, dialect)} ORDER BY {dialect.QuoteIdentifier("id")}");
        Assert.False(result.HasError, result.ErrorMessage);
        return $"{result.ObjectRows.Count} rows\n" + string.Join("\n", result.ObjectRows.Select(row =>
            string.Join("|", columns.Select((c, i) => Normalize(row[i], c.Type)))));
    }

    private static string Normalize(object? value, PortableType type)
    {
        if (value is null or DBNull) return "∅";
        var inv = CultureInfo.InvariantCulture;
        return type switch
        {
            PortableType.Boolean => (value is bool b ? b : Convert.ToInt64(value, inv) != 0).ToString(),
            PortableType.Int16 or PortableType.Int32 or PortableType.Int64 => Convert.ToInt64(value, inv).ToString(inv),
            PortableType.Decimal => (Convert.ToDecimal(value, inv) / 1.000000000000m).ToString("0.##########", inv),
            PortableType.Double => Convert.ToDouble(value, inv).ToString("R", inv),
            PortableType.Date => Instant(value).ToString("yyyy-MM-dd", inv),
            PortableType.Timestamp => Instant(value).ToString("yyyy-MM-dd HH:mm:ss.ffffff", inv),
            PortableType.Uuid => (value switch { Guid g => g, byte[] bytes => new Guid(bytes), _ => Guid.Parse(value.ToString()!) }).ToString("D"),
            PortableType.VarBinary => Convert.ToHexString((byte[])value),
            PortableType.Char => value.ToString()!.TrimEnd(),
            _ => value.ToString()!
        };
    }

    private static DateTime Instant(object value) => value switch
    {
        DateTime d => d,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTimeOffset o => o.DateTime,
        _ => DateTime.Parse(value.ToString()!, CultureInfo.InvariantCulture)
    };
}
