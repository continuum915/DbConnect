using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

/// <summary>
/// Reads a DuckDB <see cref="TableDesign"/> from duckdb_columns, duckdb_constraints and
/// duckdb_indexes. DuckDB keeps no constraint names, index directions or column collations
/// in its catalog functions; collations and generated expressions come from the stored
/// CREATE TABLE text (<see cref="StoredTableText"/>), cross-checked against the catalog.
/// </summary>
internal sealed class DuckDbTableDesignReader(DuckDBConnection connection, string? catalog, string schema, string table, CancellationToken ct)
{
    private const string Provider = "DuckDB";
    private readonly List<TableDesignGap> _gaps = [];
    private string _catalog = catalog ?? "";

    public static async Task<TableDesign> LoadAsync(string connectionString, DatabaseObjectIdentity table, CancellationToken ct)
    {
        await using var connection = DuckDbConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        try
        {
            return await new DuckDbTableDesignReader(connection, table.Catalog, table.Schema ?? "main", table.Name, ct).ReadAsync(table);
        }
        catch (DuckDBException ex)
        {
            throw new TableDefinitionUnavailableException($"Could not read the design of {table.Name}: {ex.Message}", ex);
        }
    }

    private DuckDBCommand Command(string sql, params object?[] values)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var value in values) cmd.Parameters.Add(new DuckDBParameter { Value = value ?? DBNull.Value });
        return cmd;
    }

    private async Task<TableDesign> ReadAsync(DatabaseObjectIdentity requested)
    {
        string? createSql = null, comment = null;
        var inDefault = false;
        await using (var cmd = Command("""
            SELECT sql, comment, database_name, database_name = current_database() AND schema_name = current_schema() FROM duckdb_tables()
            WHERE database_name = COALESCE(?, current_database()) AND schema_name = ? AND table_name = ?
            """, catalog, schema, table))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
            {
                createSql = reader.IsDBNull(0) ? null : reader.GetString(0);
                comment = reader.IsDBNull(1) || reader.GetString(1).Length == 0 ? null : reader.GetString(1);
                _catalog = reader.GetString(2);
                inDefault = reader.GetBoolean(3);
            }
        if (createSql is null)
            throw new TableDefinitionUnavailableException($"Table {schema}.{table} was not found or has no stored definition.");

        var text = StoredTableText.Parse(createSql);
        _gaps.AddRange(text.Unrecognised.Select(t => new TableDesignGap($"Unrecognised table clause: {t}")));
        var columns = await ColumnsAsync(text);
        var (primaryKey, uniqueKeys, foreignKeys, checks) = await ConstraintsAsync(text);
        var unsupported = new[]
        {
            TableDesignFeature.Identity, TableDesignFeature.StoredGeneratedColumns, TableDesignFeature.ForeignKeyActions, TableDesignFeature.Triggers,
            TableDesignFeature.Options, TableDesignFeature.IndexIncludedColumns, TableDesignFeature.IndexFilter,
            TableDesignFeature.IndexDescending, TableDesignFeature.IndexMethod, TableDesignFeature.ConstraintChanges
        }.ToDictionary(f => f, _ => TableDesign.NotSupportedBy(Provider));
        return new TableDesign(requested with { Schema = schema, Name = table }, Provider)
        {
            Columns = columns,
            PrimaryKey = primaryKey,
            UniqueKeys = uniqueKeys,
            ForeignKeys = foreignKeys,
            ReferencedBy = await ReferencesAsync(),
            Checks = checks,
            Indexes = await IndexesAsync(),
            Comment = comment,
            Unsupported = unsupported,
            Unrepresented = _gaps,
            InDefaultSchema = inDefault
        };
    }

    private async Task<List<TableDesignColumn>> ColumnsAsync(StoredTableText text)
    {
        var columns = new List<TableDesignColumn>();
        await using var cmd = Command("""
            SELECT column_name, data_type, is_nullable, column_default, comment FROM duckdb_columns()
            WHERE database_name = ? AND schema_name = ? AND table_name = ? ORDER BY column_index
            """, _catalog, schema, table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var stored = text.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (stored == null && text.Unrecognised.Count == 0)
                throw new TableDefinitionUnavailableException($"Column {name} of {table} is missing from its stored definition.");
            var generated = stored?.GeneratedExpression;
            columns.Add(new TableDesignColumn(name, reader.GetString(1), reader.GetBoolean(2))
            {
                Default = generated == null && !reader.IsDBNull(3) ? reader.GetString(3) : null,
                Collation = stored?.Collation,
                Generated = generated == null ? null : new TableDesignGenerated(generated, TableDesignGeneratedStorage.Virtual),
                Comment = reader.IsDBNull(4) || reader.GetString(4).Length == 0 ? null : reader.GetString(4)
            });
        }
        if (columns.Count != text.Columns.Count && text.Unrecognised.Count == 0)
            throw new TableDefinitionUnavailableException($"The stored definition of {table} does not match its columns.");
        return columns;
    }

    private async Task<(TableDesignKey?, List<TableDesignKey>, List<TableDesignForeignKey>, List<TableDesignCheck>)> ConstraintsAsync(
        StoredTableText text)
    {
        TableDesignKey? primaryKey = null;
        var uniqueKeys = new List<TableDesignKey>();
        var foreignKeys = new List<TableDesignForeignKey>();
        var checks = new List<TableDesignCheck>();
        await using var cmd = Command("""
            SELECT constraint_type, constraint_column_names, expression, referenced_table, referenced_column_names
            FROM duckdb_constraints()
            WHERE database_name = ? AND schema_name = ? AND table_name = ? AND constraint_type <> 'NOT NULL'
            ORDER BY constraint_index
            """, _catalog, schema, table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var columns = Names(reader.GetValue(1));
            switch (reader.GetString(0))
            {
                case "PRIMARY KEY":
                    primaryKey = new TableDesignKey(null, columns);
                    break;
                case "UNIQUE":
                    uniqueKeys.Add(new TableDesignKey(null, columns));
                    break;
                case "CHECK":
                    checks.Add(new TableDesignCheck(null, reader.GetString(2)) { Column = columns.Count == 1 ? columns[0] : null });
                    break;
                case "FOREIGN KEY":
                    var referenced = Names(reader.GetValue(4));
                    foreignKeys.Add(new TableDesignForeignKey(null,
                        columns.Zip(referenced, (from, to) => new TableDesignColumnPair(from, to)).ToList(),
                        new DatabaseObjectIdentity(catalog, schema, reader.GetString(3), SchemaNodeType.Table), "NO ACTION", "NO ACTION"));
                    break;
                default:
                    _gaps.Add(new($"{reader.GetString(0)} constraint on {string.Join(", ", columns)}."));
                    break;
            }
        }
        if (text.Unrecognised.Count == 0 && text.Constraints.Count(c => c.Kind == StoredConstraintKind.Check) != checks.Count)
            throw new TableDefinitionUnavailableException($"The checks of {table} disagree with its stored definition.");
        return (primaryKey, uniqueKeys, foreignKeys, checks);
    }

    private async Task<List<TableDesignReference>> ReferencesAsync()
    {
        var references = new List<TableDesignReference>();
        await using var cmd = Command("""
            SELECT table_name, referenced_column_names FROM duckdb_constraints()
            WHERE database_name = ? AND schema_name = ? AND constraint_type = 'FOREIGN KEY' AND referenced_table = ?
            ORDER BY table_name, constraint_index
            """, _catalog, schema, table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            references.Add(new TableDesignReference(null, new DatabaseObjectIdentity(catalog, schema, reader.GetString(0), SchemaNodeType.Table),
                Names(reader.GetValue(1))));
        return references;
    }

    private async Task<List<TableDesignIndex>> IndexesAsync()
    {
        var indexes = new List<TableDesignIndex>();
        await using var cmd = Command("""
            SELECT index_name, is_unique, sql, comment FROM duckdb_indexes()
            WHERE database_name = ? AND schema_name = ? AND table_name = ? ORDER BY index_name
            """, _catalog, schema, table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (reader.IsDBNull(2))
                throw new TableDefinitionUnavailableException($"Index {name} has no stored definition.");
            var parts = SqliteDefinitionSql.BodyTerms(reader.GetString(2)).Select(term =>
            {
                var tokens = SqliteDefinitionSql.Tokens(term);
                return tokens.Count == 1
                    ? new TableDesignIndexPart(tokens[0].Identifier, null, false)
                    : new TableDesignIndexPart(null, term, false);
            }).ToList();
            indexes.Add(new TableDesignIndex(name, reader.GetBoolean(1), parts, TableDesignIndexOrigin.CreateIndex)
            {
                Comment = reader.IsDBNull(3) || reader.GetString(3).Length == 0 ? null : reader.GetString(3)
            });
        }
        return indexes;
    }

    private static List<string> Names(object value) => value switch
    {
        System.Collections.IEnumerable list and not string => list.Cast<object>().Select(v => v.ToString()!).ToList(),
        _ => throw new TableDefinitionUnavailableException("Constraint column names are unavailable in this DuckDB version.")
    };
}
