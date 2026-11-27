using DuckDB.NET.Data;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Script as CREATE recreates DuckDB tables with keys, indexes and comments (§1.6, issue 02).</summary>
public sealed class TableScriptingDuckDbTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-duck-script-" + Guid.NewGuid().ToString("N"));

    public TableScriptingDuckDbTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private const string Prerequisites = """
        CREATE SCHEMA "s.x";
        CREATE SEQUENCE seq1;
        CREATE TABLE "s.x"."Parent.T" (id INTEGER PRIMARY KEY, code VARCHAR COLLATE NOCASE UNIQUE);
        """;

    private const string Fixture = Prerequisites + """
        CREATE TABLE "s.x"."Child Items" (
            id INTEGER DEFAULT nextval('seq1') PRIMARY KEY,
            pid INTEGER REFERENCES "s.x"."Parent.T"(id),
            a INTEGER NOT NULL CHECK (a > 0),
            b DECIMAL(10,2) DEFAULT 1.5,
            g INTEGER GENERATED ALWAYS AS (a * 2) VIRTUAL,
            l INTEGER[],
            st STRUCT(x INTEGER, y VARCHAR),
            "Name" VARCHAR COLLATE NOCASE,
            UNIQUE (a, b)
        );
        CREATE INDEX "ix.a" ON "s.x"."Child Items" (a, (b + 1));
        COMMENT ON TABLE "s.x"."Child Items" IS 'it''s the child';
        COMMENT ON COLUMN "s.x"."Child Items".a IS 'quantity';
        COMMENT ON INDEX "s.x"."ix.a" IS 'index note';
        CREATE TABLE main."Child Items" (other INTEGER);
        """;

    private string NewDatabase(string name, string? sql = null)
    {
        var cs = $"Data Source={Path.Combine(_directory, name + ".duckdb")}";
        if (sql != null) Execute(cs, sql);
        return cs;
    }

    private static void Execute(string cs, string sql)
    {
        using var connection = new DuckDBConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<string> Rows(string cs, string sql)
    {
        using var connection = new DuckDBConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "<null>" : reader.GetValue(i) is System.Collections.IEnumerable e and not string ? string.Join(",", e.Cast<object>()) : reader.GetValue(i).ToString())));
        return rows;
    }

    private static List<string> Definition(string cs) =>
    [
        .. Rows(cs, "SELECT sql, comment FROM duckdb_tables() WHERE schema_name='s.x' AND table_name='Child Items'"),
        .. Rows(cs, "SELECT sql, comment FROM duckdb_indexes() WHERE table_name='Child Items'"),
        .. Rows(cs, "SELECT column_name, data_type, column_default, is_nullable, comment FROM duckdb_columns() WHERE schema_name='s.x' AND table_name='Child Items' ORDER BY column_index"),
        .. Rows(cs, "SELECT constraint_type, constraint_column_names, referenced_table, referenced_column_names FROM duckdb_constraints() WHERE schema_name='s.x' AND table_name='Child Items' ORDER BY constraint_type, constraint_text")
    ];

    [Fact]
    public async Task ScriptedTable_RecreatesDefinitionAndBehavior()
    {
        var source = NewDatabase("source", Fixture);
        var session = new DatabaseSession(new ConnectionInfo { Name = "d", Provider = "DuckDB", ConnectionString = source }, new DuckDbProvider());
        var result = await TableScripting.ScriptCreateAsync(session,
            new DatabaseObjectIdentity(null, "s.x", "Child Items", SchemaNodeType.Table));
        Assert.Null(result.Error);
        var sql = result.Sql!;
        Assert.Contains("REFERENCES \"s.x\".\"Parent.T\"(", sql);
        Assert.Contains("Sequence seq1", sql);
        Assert.Contains("Referenced table \"s.x\".\"Parent.T\"", sql);
        Assert.DoesNotContain("other", sql);

        var target = NewDatabase("target", Prerequisites);
        Execute(target, sql);
        Assert.Equal(Definition(source), Definition(target));

        Execute(target, "INSERT INTO \"s.x\".\"Parent.T\" VALUES (1, 'x'); INSERT INTO \"s.x\".\"Child Items\" (pid, a) VALUES (1, 3);");
        Assert.Equal(["6"], Rows(target, "SELECT g FROM \"s.x\".\"Child Items\""));
        Assert.ThrowsAny<Exception>(() => Execute(target, "INSERT INTO \"s.x\".\"Child Items\" (a) VALUES (0)"));
        Assert.ThrowsAny<Exception>(() => Execute(target, "INSERT INTO \"s.x\".\"Child Items\" (pid, a) VALUES (99, 1)"));
    }

    [Fact]
    public async Task SelfReferencingKeys_AreScriptedInPlace()
    {
        // DuckDB stores each self-referencing key as an empty term with no constraint text (sql-dump-and-insert-export/06).
        const string other = "CREATE TABLE o (id INTEGER PRIMARY KEY);";
        var source = NewDatabase("self", other + """
            CREATE TABLE "s.x" (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES "s.x"(id), o_id INTEGER REFERENCES o(id),
                prev_id INTEGER, FOREIGN KEY (prev_id) REFERENCES "s.x"(id), CHECK (id > 0));
            """);
        var session = new DatabaseSession(new ConnectionInfo { Name = "d", Provider = "DuckDB", ConnectionString = source }, new DuckDbProvider());
        var result = await TableScripting.ScriptCreateAsync(session, new DatabaseObjectIdentity(null, "main", "s.x", SchemaNodeType.Table));
        Assert.Null(result.Error);
        Assert.Contains("""
            prev_id INTEGER, FOREIGN KEY ("parent_id") REFERENCES "main"."s.x"("id"), FOREIGN KEY ("o_id") REFERENCES "main"."o"("id"), FOREIGN KEY ("prev_id") REFERENCES "main"."s.x"("id"), CHECK
            """, result.Sql);

        var target = NewDatabase("self-target", other);
        Execute(target, result.Sql!);
        const string constraints = "SELECT constraint_type, constraint_column_names, referenced_table FROM duckdb_constraints() WHERE table_name = 's.x' ORDER BY constraint_index";
        Assert.Equal(Rows(source, constraints), Rows(target, constraints));
        Assert.ThrowsAny<Exception>(() => Execute(target, "INSERT INTO \"s.x\" (id, parent_id) VALUES (1, 2)"));
    }

    [Fact]
    public async Task MissingTable_ReportsErrorInsteadOfScript()
    {
        var source = NewDatabase("missing", "CREATE TABLE t (id INTEGER);");
        var session = new DatabaseSession(new ConnectionInfo { Name = "d", Provider = "DuckDB", ConnectionString = source }, new DuckDbProvider());
        var result = await TableScripting.ScriptCreateAsync(session, new DatabaseObjectIdentity(null, "main", "nope", SchemaNodeType.Table));
        Assert.Null(result.Sql);
        Assert.Contains("nope", result.Error);
    }
}
