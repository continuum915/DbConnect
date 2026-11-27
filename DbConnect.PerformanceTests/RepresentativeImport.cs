using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DbConnect.PerformanceTests;

public sealed record ExpectedInsert(int Start, int Length, string Table, int Id, int? ParentId);
public sealed record ImportMetadata(string GeneratorVersion, uint Seed, string Profile, string Sha256,
    int EncodedBytes, int Utf16Characters, int Lines, int GoDirectives, int Inserts, int Tables,
    int MaximumLineLength, double GenerationMilliseconds, ImportConfiguration? Configuration = null);
public sealed record ImportFixture(string Sql, ImportMetadata Metadata, IReadOnlyList<ExpectedInsert> Expected,
    IReadOnlyList<ProxyStatement>? ProxyStatements = null);

public static class RepresentativeImport
{
    public const string Version = "representative-v1";
    public const uint Seed = 1729;
    public const int MiB = 1_048_576;

    public static ImportFixture Generate(int targetBytes, string profile = "representative")
    {
        if (targetBytes < (profile == "long-lines" ? 8192 : 1024))
            throw new ArgumentOutOfRangeException(nameof(targetBytes));
        var configuration = ImportConfiguration.For(profile);
        var clock = Stopwatch.StartNew();
        var sql = new StringBuilder(targetBytes);
        var expected = new List<ExpectedInsert>();
        uint state = Seed;
        var directives = 0;
        var id = 1;
        var parameterExpression = configuration.ActualParameters ? " + @value" : "";
        var padding = new string('z', configuration.LiteralPadding);
        do
        {
            // Explicit LCG rather than runtime-dependent Random implementation.
            state = unchecked(state * 1664525 + 1013904223);
            var value = state.ToString(CultureInfo.InvariantCulture);
            Add("Parents", id, null, $"INSERT INTO Parents (Id, Name) VALUES ({id}, 'O''Brien {value}; GO @fake :fake $1 ?{padding}'{parameterExpression});");
            Add("Children", id, id, $"INSERT INTO Children (Id, ParentId, Note) VALUES ({id}, {id}, 'line one\r\nGO\r\nline two; @fake :fake $1 ?');");
            if (id % configuration.PairsPerBatch == 0)
                Go();
            id++;
        }
        while (sql.Length < targetBytes - 1024 - configuration.LiteralPadding);
        Go();
        // A bounded-line comment pads to the exact byte size without truncating SQL.
        sql.Append("/*");
        var remaining = targetBytes - sql.Length - 4; // closing comment and CRLF
        while (remaining > 120)
        {
            sql.Append('x', 118).Append("\r\n");
            remaining -= 120;
        }
        sql.Append('x', remaining).Append("*/\r\n");
        var text = sql.ToString();
        var lines = text.Split("\r\n");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var metadata = new ImportMetadata(profile == "representative" ? Version : "variants-v1", Seed, profile, hash, Encoding.UTF8.GetByteCount(text),
            text.Length, lines.Length, directives, expected.Count, 2, lines.Max(line => line.Length), clock.Elapsed.TotalMilliseconds, configuration);
        return new(text, metadata, expected);

        void Add(string table, int rowId, int? parentId, string insert)
        {
            var prefix = "-- GO ; @comment :comment $2 ?\r\n/* misleading\r\nGO\r\n; @comment */\r\n";
            sql.Append(prefix);
            var start = sql.Length;
            sql.Append(insert).Append("\r\n");
            expected.Add(new(start, insert.Length - 1, table, rowId, parentId));
        }

        void Go()
        {
            sql.Append("GO\r\n");
            directives++;
        }
    }
}
