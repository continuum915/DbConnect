using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

/// <summary>Statements that copy a table under a new name in the same schema.</summary>
public sealed record TableDuplicatePlan(DatabaseObjectIdentity Target, IReadOnlyList<string> Statements,
    IReadOnlyList<string> NotCopied);

/// <summary>Plans Duplicate Table from the provider's own definition; nothing runs while planning.</summary>
public interface ITableDuplicateSource
{
    /// <exception cref="TableDefinitionUnavailableException">The table cannot be copied faithfully.</exception>
    Task<TableDuplicatePlan> PlanDuplicateAsync(string connectionString, DatabaseObjectIdentity table, string newName,
        bool includeData, CancellationToken cancellationToken = default);
}

/// <summary>Renames the objects in stored CREATE TABLE and CREATE INDEX text by token position, never by text search.</summary>
internal static class DefinitionNames
{
    private static SqliteDefinitionSql.Token LastBefore(string sql, Func<SqliteDefinitionSql.Token, bool> stop, int from = 0) =>
        SqliteDefinitionSql.Tokens(sql).Where(t => t.Start >= from).TakeWhile(t => !stop(t)).Last();

    private static string Replace(string sql, SqliteDefinitionSql.Token token, string replacement) =>
        sql[..token.Start] + replacement + sql[token.End..];

    /// <summary>Replaces the table name in CREATE TABLE [schema.]name(...).</summary>
    public static string RenameTable(string createTable, string quotedName) =>
        Replace(createTable, LastBefore(createTable, t => t.Is("(") || t.Is("AS")), quotedName);

    /// <summary>Renames CREATE INDEX name ON [schema.]table(...) to a new index name and target table.</summary>
    public static string RenameIndex(string createIndex, string quotedIndex, string quotedTable)
    {
        var on = SqliteDefinitionSql.Tokens(createIndex).First(t => t.Is("ON"));
        var table = LastBefore(createIndex, t => t.Is("(") || t.Is("USING"), on.End);
        var renamed = Replace(createIndex, table, quotedTable);
        return Replace(renamed, LastBefore(renamed, t => t.Is("ON")), quotedIndex);
    }

    /// <summary>Column names whose stored definition is GENERATED, from CREATE TABLE body terms.</summary>
    public static HashSet<string> GeneratedColumns(string createTable) =>
        SqliteDefinitionSql.BodyTerms(createTable)
            .Select(SqliteDefinitionSql.Tokens)
            .Where(tokens => tokens.Count > 1 && tokens.Any(t => t.Is("GENERATED")))
            .Select(tokens => tokens[0].Identifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>A per-schema object name derived from the original and the new table name.</summary>
    public static string Suffixed(string name, string newTable, int max = 63)
    {
        var candidate = $"{name}_{newTable}";
        return candidate.Length <= max ? candidate : candidate[..max];
    }

    public static string CopyRows(Func<string, string> quote, string source, string target, IEnumerable<string> columns,
        string? overriding = null)
    {
        var list = string.Join(", ", columns.Select(quote));
        return $"INSERT INTO {target} ({list}){(overriding == null ? "" : " " + overriding)} SELECT {list} FROM {source}";
    }
}
