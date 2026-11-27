using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// CREATE TABLE written from portable columns runs on every engine and reads back as the same portable types, or
/// with the loss noted (data-transfer/05). SQLite and DuckDB run locally; the servers need the container lane's
/// DBCONNECT_CONFORMANCE_*_CONNECTION_STRING variables.
/// </summary>
public sealed class PortableCreateTableTests : DumpTestDatabases
{
    private static PortableTable EveryType => new(new DatabaseObjectIdentity(null, null, "portable", SchemaNodeType.Table),
    [
        new PortableColumn("id", PortableType.Int64, false) { IsIdentity = true },
        new PortableColumn("flag", PortableType.Boolean, true) { Default = new(PortableDefaultKind.Boolean, "false") },
        new PortableColumn("tiny", PortableType.Int8, true),
        new PortableColumn("small", PortableType.Int16, true) { Default = new(PortableDefaultKind.Number, "-3") },
        new PortableColumn("normal", PortableType.Int32, false),
        new PortableColumn("amount", PortableType.Decimal, true) { Precision = 12, Scale = 3, Default = new(PortableDefaultKind.Number, "1.5") },
        new PortableColumn("unbounded", PortableType.Decimal, true),
        new PortableColumn("single", PortableType.Single, true),
        new PortableColumn("double", PortableType.Double, true),
        new PortableColumn("code", PortableType.Char, true) { Length = 3 },
        new PortableColumn("name", PortableType.VarChar, true) { Length = 40, Default = new(PortableDefaultKind.String, "it's") },
        new PortableColumn("body", PortableType.Text, true) { Default = new(PortableDefaultKind.String, "x") },
        new PortableColumn("hash", PortableType.Binary, true) { Length = 4 },
        new PortableColumn("bytes", PortableType.VarBinary, true) { Length = 16 },
        new PortableColumn("blob", PortableType.Blob, true),
        new PortableColumn("born", PortableType.Date, true),
        new PortableColumn("at", PortableType.Time, true),
        new PortableColumn("seen", PortableType.Timestamp, true) { Precision = 6, Default = PortableDefault.CurrentTimestamp },
        new PortableColumn("instant", PortableType.TimestampTz, true),
        new PortableColumn("span", PortableType.Interval, true),
        new PortableColumn("uid", PortableType.Uuid, true),
        new PortableColumn("doc", PortableType.Json, true)
    ], ["id"]);

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQL Server")]
    public async Task EveryPortableTypeCreatesAndReadsBack(string provider)
    {
        ScratchServer? server = null;
        DatabaseSession session;
        if (provider is "SQLite" or "DuckDB") session = await SessionAsync(provider, "portable", null);
        else
        {
            server = ScratchServer.For(provider);
            if (server == null) return;
            session = await server.CreateDatabaseAsync("portable", null);
        }
        try
        {
            var schema = provider switch { "PostgreSQL" => "public", "SQL Server" => "dbo", "SQLite" or "DuckDB" => "main", _ => null };
            var target = new DatabaseObjectIdentity(provider == "MySQL" ? ScratchComparisons.Database(session) : null, schema, "portable", SchemaNodeType.Table);
            var created = PortableTables.CreateTable(provider, session.Capabilities, target, EveryType);

            await using (var db = await SessionSetup.OpenAsync(session))
            {
                var result = await db.ExecuteQueryAsync(created.Sql);
                Assert.False(result.HasError, $"{result.ErrorMessage}\n{created.Sql}");
                // The defaults and the identity fill a row that names only the required column.
                var insert = await db.ExecuteQueryAsync($"INSERT INTO {SqlInsertTarget.ForTable(target, session.Capabilities)} " +
                                                        $"({session.Capabilities.QuoteIdentifier("normal")}) VALUES (7)");
                if (provider != "DuckDB") Assert.False(insert.HasError, $"{insert.ErrorMessage}\n{created.Sql}");
            }

            var back = await PortableTables.ReadAsync(session, provider is "SQLite" or "DuckDB" ? Table("portable") : ScratchComparisons.Identity(session, "portable"));
            Assert.Equal(EveryType.Columns.Select(c => c.Name), back.Columns.Select(c => c.Name));
            Assert.Equal(["id"], back.PrimaryKey);
            var renderedNotes = string.Join("\n", created.Notes);
            foreach (var (expected, actual) in EveryType.Columns.Zip(back.Columns))
            {
                Assert.Equal(expected.IsNullable, actual.IsNullable);
                // A different type comes back only where creating it said so.
                if (actual.Type != expected.Type)
                    Assert.True(renderedNotes.Contains(expected.Name + ":", StringComparison.Ordinal) || IsWider(expected.Type, actual.Type),
                        $"{expected.Name}: {expected.Type} came back as {actual.Type} ({actual.SourceType}) without a note.\n{created.Sql}");
                if (expected.Default != null && actual.Default == null && !actual.Notes.Any())
                    Assert.Fail($"{expected.Name}: the default did not come back.\n{created.Sql}");
            }
            Assert.Equal(provider != "DuckDB", back.Columns[0].IsIdentity);
        }
        finally
        {
            if (server != null) await server.DisposeAsync();
        }
    }

    private static bool IsWider(PortableType from, PortableType to) => (from, to) switch
    {
        (PortableType.Int8, PortableType.Int16 or PortableType.Int32 or PortableType.Int64) => true,
        (PortableType.Int16 or PortableType.Int32, PortableType.Int64) => true,
        (PortableType.Single, PortableType.Double) => true,
        (PortableType.VarChar or PortableType.Char, PortableType.Text) => true,
        (PortableType.Binary or PortableType.VarBinary, PortableType.Blob) => true,
        _ => false
    };
}
