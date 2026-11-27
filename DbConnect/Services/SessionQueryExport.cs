using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Owns immutable execution evidence, independent of mutable result/editor properties.</summary>
internal sealed class SessionQueryExport
{
    internal delegate IAsyncEnumerable<IReadOnlyList<object?>> Reader(string sql, QueryExecutionOptions options,
        IReadOnlyList<ExportColumn> columns, Func<bool> isCurrent, CancellationToken cancellationToken);

    private readonly ConditionalWeakTable<QueryResult, QueryExportCapture> _captures = new();
    private readonly Reader _reader;
    private readonly Func<bool> _isDisposed;
    private readonly SqlBatchDialect _dialect;
    private long _version;

    public SessionQueryExport(Reader reader, Func<bool> isDisposed, SqlBatchDialect dialect)
    {
        _reader = reader;
        _isDisposed = isDisposed;
        _dialect = dialect;
    }

    public void Invalidate() => Interlocked.Increment(ref _version);

    public QueryExportCapture? Capture(QueryResult result) =>
        _captures.TryGetValue(result, out var capture) && capture.IsCurrent() ? capture : null;

    public void Record(string sql, QueryExecutionOptions options, IReadOnlyList<QueryResult> results)
    {
        if (!IsEligibleStatement(sql)) { Invalidate(); return; }
        if (options.Parameters?.Any(value => !QueryExportParameters.CanCapture(value)) == true) return;
        if (results.Count != 1 || results[0].HasError || !results[0].HasResultSet) return;
        var result = results[0];
        var version = Interlocked.Read(ref _version);
        bool IsCurrent() => !_isDisposed() && version == Interlocked.Read(ref _version);
        var columns = Array.AsReadOnly(result.ColumnInfos.Select(c => new ExportColumn(c.Name, c.Ordinal, c.ProviderTypeName, c.ClrType)).ToArray());
        var source = new Source(_reader, sql, Snapshot(options), columns, IsCurrent);
        _captures.Add(result, new(source, Guid.NewGuid().ToString("N"), IsCurrent));
    }

    public static QueryExecutionOptions Snapshot(QueryExecutionOptions? options) =>
        (options ?? new QueryExecutionOptions()) with
        {
            CancellationToken = default,
            Continuation = null,
            Parameters = options?.Parameters?.Select(QueryExportParameters.Copy).ToArray()
        };

    // Intentionally narrow: no WITH, EXPLAIN, PRAGMA, procedures or batches. Unknown functions,
    // including table-valued functions/extensions, are excluded. This is eligibility, not a proof
    // that arbitrary SQL or views/virtual tables have no external effects.
    private bool IsEligibleStatement(string sql)
    {
        if (SqlBatchParser.SplitStatements(sql, _dialect).Count != 1) return false;
        var allTokens = SqlTokenizer.Tokenize(sql, _dialect);
        // MySQL/MariaDB execute version comments and allow assignments inside SELECT.
        // The shared tokenizer accepts -- without trailing whitespace as a comment;
        // MySQL can execute that text as arithmetic, so exclude it conservatively.
        if (_dialect == SqlBatchDialect.MySql && allTokens.Any(t =>
            t.Text.StartsWith("/*!", StringComparison.Ordinal) || t.Text.StartsWith("/*M!", StringComparison.OrdinalIgnoreCase)
            || t.Text is ":" or ":="
            || t.Kind == SqlTokenKind.Comment && t.Text.StartsWith("--", StringComparison.Ordinal)
                && t.Text.Length > 2 && !char.IsWhiteSpace(t.Text[2]))) return false;
        var tokens = allTokens.Where(t => t.Kind != SqlTokenKind.Comment).ToArray();
        if (tokens.Length == 0 || !tokens[0].Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase)) return false;
        // T-SQL permits adjacent statements without semicolons, and SELECT can assign
        // session variables. Neither is a replayable read, even if the first result was capped.
        if (_dialect == SqlBatchDialect.SqlServer && !SqlServerQueryExportEligibility.IsRead(tokens)) return false;
        var allowedBeforeParenthesis = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "WHERE", "ON", "AND", "OR", "NOT", "IN", "EXISTS", "AS", "BY", "HAVING",
            "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "IFNULL", "NULLIF", "TYPEOF", "HEX", "LENGTH",
            "ABS", "ROUND", "LOWER", "UPPER", "SUBSTR", "SUBSTRING", "TRIM", "LTRIM", "RTRIM", "CAST"
        };
        if (_dialect == SqlBatchDialect.SqlServer) allowedBeforeParenthesis.Add("TOP");
        for (var i = 1; i < tokens.Length; i++)
        {
            if (tokens[i].Kind == SqlTokenKind.Directive || !tokens[i].IsTerminated) return false;
            // SELECT INTO writes a table; locking SELECTs affect other sessions.
            if (tokens[i].Kind == SqlTokenKind.Word &&
                (tokens[i].Text.Equals("INTO", StringComparison.OrdinalIgnoreCase) || tokens[i].Text.Equals("FOR", StringComparison.OrdinalIgnoreCase)
                || tokens[i].Text.Equals("LOCK", StringComparison.OrdinalIgnoreCase))) return false;
            if (tokens[i].Text == "(" && tokens[i - 1].Kind is SqlTokenKind.Word or SqlTokenKind.Quoted &&
                !allowedBeforeParenthesis.Contains(tokens[i - 1].Text)) return false;
        }
        return true;
    }

    private sealed class Source(Reader reader, string sql, QueryExecutionOptions options,
        IReadOnlyList<ExportColumn> columns, Func<bool> isCurrent) : IExportSource
    {
        public IReadOnlyList<ExportColumn> Columns => columns;
        public async IAsyncEnumerable<IReadOnlyList<object?>> ReadRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using var rows = reader(sql, options, columns, isCurrent, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool hasRow;
                try { hasRow = await rows.MoveNextAsync(); }
                catch (Exception error) when (cancellationToken.IsCancellationRequested && error is not OperationCanceledException)
                {
                    throw new OperationCanceledException("Export canceled.", error, cancellationToken);
                }
                if (!hasRow) yield break;
                yield return rows.Current;
            }
        }
    }
}
