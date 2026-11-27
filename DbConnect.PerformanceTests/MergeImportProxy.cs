using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DbConnect.PerformanceTests;

public sealed record ProxyStatement(int Start, int Length, string Table, string Kind, int SourceRows);

/// <summary>Private-workload shape proxy; SQL contents and row widths are synthetic.</summary>
public static class MergeImportProxy
{
    public const string Profile = "merge-import-proxy";
    public const int StatementCount = 4281;

    public static ImportFixture Generate(int targetBytes)
    {
        if (targetBytes < 8 * RepresentativeImport.MiB || targetBytes > 64 * RepresentativeImport.MiB)
            throw new ArgumentOutOfRangeException(nameof(targetBytes));
        var clock = Stopwatch.StartNew();
        var plan = Plan().ToArray();
        var baseline = plan.Sum(item => Statement(item.Table, item.Kind, item.Index, item.Rows, 0).Length + 6);
        var remaining = targetBytes - baseline;
        if (remaining < 0) throw new ArgumentOutOfRangeException(nameof(targetBytes));
        var rows = plan.Sum(item => item.Rows);
        var sql = new StringBuilder(targetBytes);
        var expected = new List<ProxyStatement>(StatementCount);
        foreach (var item in plan)
        {
            // Spread extra bytes across real VALUES data, rather than inert comment padding.
            var padding = rows == 0 ? 0 : (int)((long)remaining * item.Rows / rows);
            remaining -= padding;
            rows -= item.Rows;
            var statement = Statement(item.Table, item.Kind, item.Index, item.Rows, padding);
            expected.Add(new(sql.Length, statement.Length - 1, item.Table, item.Kind, item.Rows));
            sql.Append(statement).Append("\r\nGO\r\n");
        }
        var text = sql.ToString();
        var lines = text.Split("\r\n");
        var metadata = new ImportMetadata("merge-proxy-v2", RepresentativeImport.Seed, Profile,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), text.Length, text.Length,
            lines.Length, StatementCount, 0, 94, lines.Max(line => line.Length), clock.Elapsed.TotalMilliseconds);
        return new(text, metadata, [], expected);
    }

    private static IEnumerable<(string Table, string Kind, int Index, int Rows)> Plan()
    {
        var index = 0;
        for (var i = 0; i < 3291; i++)
        {
            yield return ("Product", "MERGE", index, 1 + index++ % 32);
        }
        int[] large = [200, 150, 100, 75, 50, 40, 30, 25, 20];
        for (var table = 0; table < 93; table++)
        {
            var count = table < large.Length ? large[table] : table < 81 ? 4 : 1;
            for (var i = 0; i < count; i++)
            {
                var kind = table < 2 && i == 0 ? "DELETE" : "MERGE";
                yield return ($"Table{table + 1:D3}", kind, index, kind == "DELETE" ? 0 : 1 + index % 32);
                index++;
            }
        }
    }

    private static string Statement(string table, string kind, int index, int rows, int padding)
    {
        if (kind == "DELETE") return $"DELETE FROM [dbo].[{table}] WHERE [Id] < 0;";
        var sql = new StringBuilder();
        sql.Append($"MERGE INTO [dbo].[{table}] AS target\r\nUSING (VALUES\r\n");
        for (var row = 0; row < rows; row++)
        {
            var length = padding / (rows - row);
            padding -= length;
            sql.Append('(').Append(index * 100 + row).Append(", 'O''Brien ; GO @fake :fake $1 ? ")
                .Append((char)('a' + index % 26), length).Append("')")
                .Append(row + 1 == rows ? "\r\n" : ",\r\n");
        }
        sql.Append(") AS source ([Id], [Name])\r\nON target.[Id] = source.[Id]\r\nWHEN MATCHED THEN UPDATE SET [Name] = source.[Name]\r\nWHEN NOT MATCHED THEN INSERT ([Id], [Name]) VALUES (source.[Id], source.[Name]);");
        return sql.ToString();
    }
}
