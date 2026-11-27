using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public class FindInDataService
{
    public async IAsyncEnumerable<FindInDataMatch> SearchAsync(
        DatabaseSession session,
        string searchTerm,
        FindInDataOptions options,
        IProgress<FindInDataProgress> progress,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 1. Get the schema
        List<SchemaNode> rootNodes;
        bool failedToLoadSchema = false;
        string? schemaError = null;
        try
        {
            rootNodes = await session.GetSchemaAsync();
        }
        catch (Exception ex)
        {
            failedToLoadSchema = true;
            schemaError = ex.Message;
            rootNodes = null!;
        }

        if (failedToLoadSchema)
        {
            yield return FindInDataMatch.Error("Database Schema", $"Failed to load database schema: {schemaError}");
            yield break;
        }

        // 2. Gather all tables
        var tables = ExtractTableNodes(rootNodes);

        for (int i = 0; i < tables.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tableNode = tables[i];
            var tableName = tableNode.RawName ?? tableNode.Name;

            progress.Report(new FindInDataProgress(i + 1, tables.Count, tableName));

            // Get columns of type Column
            var textCols = tableNode.Children
                .Where(c => c.Type == SchemaNodeType.Column)
                .Select(c => new ColumnDefinition { Name = c.RawName ?? c.Name, DataType = c.DataType ?? "TEXT" })
                .Where(c => IsTextCompatible(c.DataType, options.TextColumnsOnly))
                .ToList();

            if (textCols.Count == 0) continue;

            await foreach (var match in SearchTableAsync(session, tableNode, textCols, searchTerm, options, ct))
            {
                yield return match;
            }
        }
    }

    private List<SchemaNode> ExtractTableNodes(List<SchemaNode> rootNodes)
    {
        var tables = new List<SchemaNode>();
        foreach (var node in rootNodes)
        {
            ExtractTableNodesRecursive(node, tables);
        }
        return tables;
    }

    private void ExtractTableNodesRecursive(SchemaNode node, List<SchemaNode> tables)
    {
        if (node.Type is SchemaNodeType.Table or SchemaNodeType.VirtualTable)
        {
            tables.Add(node);
        }
        else if (node.Type is not SchemaNodeType.Column && node.Type is not SchemaNodeType.Index && node.Type is not SchemaNodeType.Trigger)
        {
            foreach (var child in node.Children)
            {
                ExtractTableNodesRecursive(child, tables);
            }
        }
    }

    private async IAsyncEnumerable<FindInDataMatch> SearchTableAsync(
        DatabaseSession session,
        SchemaNode tableNode,
        List<ColumnDefinition> cols,
        string term,
        FindInDataOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var provider = session.Provider;
        var capabilities = session.Capabilities;
        var tableName = tableNode.RawName ?? tableNode.Name;
        var qualifiedTableName = GetQualifiedTableName(tableNode);

        var quotedTable = QuoteQualifiedIdentifier(qualifiedTableName, capabilities);
        var literalTerm = EscapeSqlLiteral(term);
        var likeTerm = EscapeLikePattern(term);
        var whereParts = new List<string>();

        foreach (var col in cols)
        {
            var quotedCol = QuoteQualifiedIdentifier(col.Name, capabilities);
            string clause;
            if (options.ExactMatch)
            {
                if (options.CaseSensitive)
                {
                    clause = $"{quotedCol} = '{literalTerm}'";
                }
                else
                {
                    if (provider.ProviderName == "PostgreSQL")
                        clause = $"LOWER({quotedCol}) = LOWER('{literalTerm}')";
                    else if (provider.ProviderName == "SQLite")
                        clause = $"{quotedCol} = '{literalTerm}' COLLATE NOCASE";
                    else
                        clause = $"{quotedCol} = '{literalTerm}'";
                }
            }
            else
            {
                if (options.CaseSensitive)
                {
                    clause = $"{quotedCol} LIKE '%{likeTerm}%' ESCAPE '\\'";
                }
                else
                {
                    if (provider.ProviderName == "PostgreSQL" || provider.ProviderName == "DuckDB")
                        clause = $"{quotedCol} ILIKE '%{likeTerm}%' ESCAPE '\\'";
                    else
                        clause = $"{quotedCol} LIKE '%{likeTerm}%' ESCAPE '\\'";
                }
            }
            whereParts.Add(clause);
        }

        var baseSql = $"SELECT * FROM {quotedTable} WHERE {string.Join(" OR ", whereParts)}";
        var sql = provider.FormatLimitQuery(baseSql, options.RowLimitPerTable);

        QueryResult? result = null;
        bool failedToExecute = false;
        string? executeError = null;
        try
        {
            result = await provider.ExecuteQueryAsync(
                session.ConnectionString,
                sql,
                new QueryExecutionOptions
                {
                    CancellationToken = ct,
                    MaxResultRows = options.RowLimitPerTable,
                    TimeoutSeconds = 30
                });
        }
        catch (Exception ex)
        {
            failedToExecute = true;
            executeError = ex.Message;
        }

        if (failedToExecute)
        {
            yield return FindInDataMatch.Error(tableName, executeError ?? "Unknown execution error");
            yield break;
        }

        if (result == null || result.HasError)
        {
            yield return FindInDataMatch.Error(tableName, result?.ErrorMessage ?? "Unknown query error");
            yield break;
        }

        foreach (var row in result.ObjectRows)
        {
            var matchedCols = new List<string>();
            foreach (var col in cols)
            {
                var val = result.ValueOrDefault(row, col.Name);
                if (val != null)
                {
                    var valStr = val.ToString();
                    if (valStr != null)
                    {
                        var isMatch = options.ExactMatch
                            ? string.Equals(valStr, term, options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                            : valStr.Contains(term, options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                        if (isMatch)
                        {
                            matchedCols.Add(col.Name);
                        }
                    }
                }
            }

            if (matchedCols.Count == 0)
                continue;

            var rowByName = result.ToRowDictionary(row);

            foreach (var colName in matchedCols)
            {
                yield return new FindInDataMatch
                {
                    TableName = tableName,
                    ColumnName = colName,
                    Row = rowByName,
                    TableNode = tableNode
                };
            }
        }
    }

    private static string GetQualifiedTableName(SchemaNode node)
    {
        var tableName = node.RawName ?? node.Name;
        if (!string.IsNullOrEmpty(node.SchemaName))
        {
            return $"{node.SchemaName}.{tableName}";
        }
        return tableName;
    }

    private static string QuoteQualifiedIdentifier(string identifier, DatabaseProviderCapabilities capabilities)
    {
        return string.Join(".", identifier.Split('.').Select(capabilities.QuoteIdentifier));
    }

    private static bool IsTextCompatible(string dataType, bool textColumnsOnly)
    {
        if (!textColumnsOnly) return true;
        var dt = dataType.ToUpperInvariant();
        return dt.Contains("CHAR") || dt.Contains("TEXT") || dt.Contains("CLOB")
            || dt.Contains("STRING") || dt.Contains("VARCHAR") || dt.Contains("NCHAR");
    }

    private static string EscapeSqlLiteral(string value) =>
        value.Replace("'", "''");

    /// <summary>
    /// Escapes LIKE/ILIKE wildcards so user input is matched literally when ESCAPE '\' is used.
    /// </summary>
    public static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_")
            .Replace("'", "''");
    }
}
