using System;
using System.Data.SqlTypes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Identifier comparison belongs to the connected engine, including its configured collation.</summary>
internal sealed class ObjectNameSemantics(DatabaseSession session)
{
    private Func<string, string, bool>? _compare;
    private bool _loaded;

    public bool Equal(string? left, string? right)
    {
        if (left is null || right is null) return left == right;
        if (_compare != null) return _compare(left, right);
        return session.Capabilities.AsciiCaseInsensitiveObjectNames ? FoldAscii(left) == FoldAscii(right) : left == right;
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        switch (session.Capabilities.BatchDialect)
        {
            case SqlBatchDialect.MySql:
                {
                    var result = await session.ExecuteQueryAsync("SELECT @@lower_case_table_names;",
                        new QueryExecutionOptions { CancellationToken = cancellationToken });
                    if (!result.HasError && result.ObjectRows.Count > 0 && Convert.ToInt32(result.ObjectRows[0][0]) != 0)
                        _compare = (left, right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
                    break;
                }
            case SqlBatchDialect.SqlServer:
                {
                    var result = await session.ExecuteQueryAsync("""
                    SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')),
                        CONVERT(int, COLLATIONPROPERTY(CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')), 'LCID')),
                        CONVERT(int, COLLATIONPROPERTY(CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')), 'ComparisonStyle'));
                    """, new QueryExecutionOptions { CancellationToken = cancellationToken });
                    if (!result.HasError && result.ObjectRows.Count > 0 && result.ObjectRows[0].Length >= 3)
                    {
                        var row = result.ObjectRows[0];
                        var collation = Convert.ToString(row[0]) ?? string.Empty;
                        if (!collation.Contains("_BIN", StringComparison.OrdinalIgnoreCase))
                        {
                            var lcid = Convert.ToInt32(row[1]);
                            var style = Convert.ToInt32(row[2]);
                            var options = SqlCompareOptions.None;
                            if ((style & 1) != 0) options |= SqlCompareOptions.IgnoreCase;
                            if ((style & 2) != 0) options |= SqlCompareOptions.IgnoreNonSpace;
                            if ((style & 65536) != 0) options |= SqlCompareOptions.IgnoreKanaType;
                            if ((style & 131072) != 0) options |= SqlCompareOptions.IgnoreWidth;
                            _compare = (left, right) => (new SqlString(left, lcid, options) == new SqlString(right, lcid, options)).Value;
                        }
                    }
                    break;
                }
        }
        cancellationToken.ThrowIfCancellationRequested();
        _loaded = true;
    }

    private static string FoldAscii(string value) =>
        string.Concat(value.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c));
}
