using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using DbConnect.Models;
using static DbConnect.Services.SqlServerTableScripts;

namespace DbConnect.Services;

/// <summary>
/// Reads a SQL Server <see cref="TableDesign"/> from the sys catalog views. Constraints the
/// server named are shown as unnamed, as the CREATE script leaves them unnamed.
/// </summary>
internal sealed class SqlServerTableDesignReader(SqlConnection connection, TableContext table, CancellationToken ct)
{
    private const string Provider = "SQL Server";
    private readonly List<TableDesignGap> _gaps = [];
    private readonly Dictionary<string, string> _columnComments = new(StringComparer.Ordinal);
    private string? _tableComment;

    public static async Task<TableDesign> LoadAsync(string connectionString, DatabaseObjectIdentity table, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        try
        {
            var context = await LoadContextAsync(connection, table, ct, scriptable: false);
            return await new SqlServerTableDesignReader(connection, context, ct).ReadAsync(table);
        }
        catch (SqlException ex)
        {
            throw new TableDefinitionUnavailableException($"Could not read the design of {table.Name}: {ex.Message}", ex);
        }
    }

    private async Task<TableDesign> ReadAsync(DatabaseObjectIdentity requested)
    {
        _gaps.AddRange(table.Unscriptable.Select(b => new TableDesignGap($"This table is {b}.", table.Name)));
        await ExtendedPropertiesAsync();
        var columns = await ColumnsAsync();
        var (primaryKey, uniqueKeys, indexes) = await IndexesAsync();
        await using var current = new SqlCommand("SELECT SCHEMA_NAME()", connection);
        var defaultSchema = await current.ExecuteScalarAsync(ct) as string;
        var options = new List<TableDesignOption>();
        if (table.FileGroup != null) options.Add(new("filegroup", table.FileGroup));
        if (table.Compression != null) options.Add(new("DATA_COMPRESSION", table.Compression));
        if (table.LockEscalation != "TABLE") options.Add(new("LOCK_ESCALATION", table.LockEscalation));
        return new TableDesign(requested with { Catalog = table.Catalog, Schema = table.Schema, Name = table.Name }, Provider)
        {
            Columns = columns,
            PrimaryKey = primaryKey,
            UniqueKeys = uniqueKeys,
            ForeignKeys = await ForeignKeysAsync(),
            ReferencedBy = await ReferencesAsync(),
            Checks = await ChecksAsync(),
            Indexes = indexes,
            Triggers = await TriggersAsync(),
            Comment = _tableComment,
            Options = options,
            Unsupported = new Dictionary<TableDesignFeature, string>
            {
                [TableDesignFeature.IndexExpressions] = TableDesign.NotSupportedBy(Provider)
            },
            Unrepresented = _gaps,
            InDefaultSchema = table.Schema == defaultSchema
        };
    }

    /// <summary>MS_Description is the comment; any other extended property is not represented.</summary>
    private async Task ExtendedPropertiesAsync()
    {
        await using var cmd = Command(connection, """
            SELECT ep.name, CONVERT(nvarchar(max), ep.value), c.name, SQL_VARIANT_PROPERTY(ep.value, 'BaseType')
            FROM sys.extended_properties ep
            LEFT JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
            WHERE ep.class = 1 AND ep.major_id = @id ORDER BY ep.minor_id, ep.name
            """, table.ObjectId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var column = reader.IsDBNull(2) ? null : reader.GetString(2);
            var text = !reader.IsDBNull(3) && reader.GetString(3) is "nvarchar" or "varchar";
            if (reader.GetString(0) == "MS_Description" && text && !reader.IsDBNull(1))
            {
                if (column == null) _tableComment = reader.GetString(1);
                else _columnComments[column] = reader.GetString(1);
            }
            else
                _gaps.Add(new($"Extended property {reader.GetString(0)} on {column ?? table.Name}.", column ?? table.Name));
        }
    }

    private async Task<List<TableDesignColumn>> ColumnsAsync()
    {
        var columns = new List<TableDesignColumn>();
        await using var cmd = Command(connection, """
            SELECT c.name, TYPE_NAME(c.user_type_id), ts.name, tp.is_user_defined, c.max_length, c.precision, c.scale,
                   c.is_nullable, c.collation_name, cc.definition, cc.is_persisted, dc.name, dc.definition, dc.is_system_named,
                   CONVERT(nvarchar(40), ic.seed_value), CONVERT(nvarchar(40), ic.increment_value), ic.is_not_for_replication,
                   c.is_rowguidcol, c.is_sparse, c.is_column_set, mc.masking_function, c.xml_collection_id
            FROM sys.columns c
            JOIN sys.types tp ON tp.user_type_id = c.user_type_id
            JOIN sys.schemas ts ON ts.schema_id = tp.schema_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.masked_columns mc ON mc.object_id = c.object_id AND mc.column_id = c.column_id
            WHERE c.object_id = @id ORDER BY c.column_id
            """, table.ObjectId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var properties = new List<string>();
            if (reader.GetBoolean(19)) properties.Add("COLUMN_SET FOR ALL_SPARSE_COLUMNS");
            if (reader.GetBoolean(18)) properties.Add("SPARSE");
            if (reader.GetBoolean(17)) properties.Add("ROWGUIDCOL");
            if (!reader.IsDBNull(20)) properties.Add($"MASKED WITH (FUNCTION = '{reader.GetString(20)}')");
            if (!reader.IsDBNull(11) && !reader.GetBoolean(13)) properties.Add($"DEFAULT CONSTRAINT {reader.GetString(11)}");
            if (reader.GetInt32(21) != 0) _gaps.Add(new($"Column {name} is typed XML bound to an XML schema collection.", name));
            columns.Add(new TableDesignColumn(name,
                TypeName(reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt16(4), reader.GetByte(5), reader.GetByte(6)),
                reader.GetBoolean(7))
            {
                Default = reader.IsDBNull(12) ? null : reader.GetString(12),
                Collation = !reader.IsDBNull(8) && reader.GetString(8) != table.DefaultCollation ? reader.GetString(8) : null,
                Generated = reader.IsDBNull(9) ? null : new TableDesignGenerated(reader.GetString(9),
                    reader.GetBoolean(10) ? TableDesignGeneratedStorage.Stored : TableDesignGeneratedStorage.Virtual),
                Identity = reader.IsDBNull(14) ? null
                    : new TableDesignIdentity(TableDesignIdentityKind.Identity, reader.GetString(14), reader.GetString(15))
                    {
                        Options = reader.GetBoolean(16) ? "NOT FOR REPLICATION" : null
                    },
                Comment = _columnComments.GetValueOrDefault(name),
                Properties = properties
            });
        }
        return columns;
    }

    private static string Action(string action) => action.Replace('_', ' ');

    private async Task<List<TableDesignForeignKey>> ForeignKeysAsync()
    {
        var keys = new List<(int Id, string Name, bool SystemNamed, DatabaseObjectIdentity Target, string Delete, string Update, string? State)>();
        await using (var cmd = Command(connection, """
            SELECT fk.object_id, fk.name, fk.is_system_named, rs.name, rt.name, fk.delete_referential_action_desc,
                   fk.update_referential_action_desc, fk.is_not_trusted, fk.is_disabled, fk.is_not_for_replication
            FROM sys.foreign_keys fk
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            WHERE fk.parent_object_id = @id ORDER BY fk.name
            """, table.ObjectId))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                keys.Add((reader.GetInt32(0), reader.GetString(1), reader.GetBoolean(2),
                    new DatabaseObjectIdentity(table.Catalog, reader.GetString(3), reader.GetString(4), SchemaNodeType.Table),
                    Action(reader.GetString(5)), Action(reader.GetString(6)),
                    State(reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9))));
        var result = new List<TableDesignForeignKey>();
        foreach (var key in keys)
        {
            var pairs = new List<TableDesignColumnPair>();
            await using var cmd = Command(connection, """
                SELECT pc.name, rc.name FROM sys.foreign_key_columns k
                JOIN sys.columns pc ON pc.object_id = k.parent_object_id AND pc.column_id = k.parent_column_id
                JOIN sys.columns rc ON rc.object_id = k.referenced_object_id AND rc.column_id = k.referenced_column_id
                WHERE k.constraint_object_id = @id ORDER BY k.constraint_column_id
                """, key.Id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) pairs.Add(new(reader.GetString(0), reader.GetString(1)));
            result.Add(new TableDesignForeignKey(key.SystemNamed ? null : key.Name, pairs, key.Target, key.Delete, key.Update)
            {
                State = key.State,
                GeneratedName = key.SystemNamed ? key.Name : null
            });
        }
        return result;
    }

    private async Task<List<TableDesignReference>> ReferencesAsync()
    {
        var rows = new List<(int Id, string Name, bool SystemNamed, string Schema, string Table, string Column)>();
        await using (var cmd = Command(connection, """
            SELECT fk.object_id, fk.name, fk.is_system_named, ps.name, pt.name, rc.name
            FROM sys.foreign_keys fk
            JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
            JOIN sys.foreign_key_columns k ON k.constraint_object_id = fk.object_id
            JOIN sys.columns rc ON rc.object_id = k.referenced_object_id AND rc.column_id = k.referenced_column_id
            WHERE fk.referenced_object_id = @id ORDER BY ps.name, pt.name, fk.name, k.constraint_column_id
            """, table.ObjectId))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetBoolean(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return rows.GroupBy(r => r.Id).Select(g =>
        {
            var first = g.First();
            return new TableDesignReference(first.SystemNamed ? null : first.Name,
                new DatabaseObjectIdentity(table.Catalog, first.Schema, first.Table, SchemaNodeType.Table), g.Select(r => r.Column).ToList());
        }).ToList();
    }

    private static string? State(bool notTrusted, bool disabled, bool notForReplication)
    {
        var state = new List<string>();
        if (disabled) state.Add("DISABLED");
        else if (notTrusted) state.Add("NOT TRUSTED");
        if (notForReplication) state.Add("NOT FOR REPLICATION");
        return state.Count == 0 ? null : string.Join(" ", state);
    }

    private async Task<List<TableDesignCheck>> ChecksAsync()
    {
        var checks = new List<TableDesignCheck>();
        await using var cmd = Command(connection, """
            SELECT cc.name, cc.definition, cc.is_system_named, cc.is_not_trusted, cc.is_disabled, cc.is_not_for_replication, c.name
            FROM sys.check_constraints cc
            LEFT JOIN sys.columns c ON c.object_id = cc.parent_object_id AND c.column_id = cc.parent_column_id
            WHERE cc.parent_object_id = @id ORDER BY cc.name
            """, table.ObjectId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            checks.Add(new TableDesignCheck(reader.GetBoolean(2) ? null : reader.GetString(0), reader.GetString(1))
            {
                GeneratedName = reader.GetBoolean(2) ? reader.GetString(0) : null,
                Column = reader.IsDBNull(6) ? null : reader.GetString(6),
                State = State(reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5))
            });
        return checks.OrderBy(c => c.Name == null).ThenBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Expression, StringComparer.Ordinal).ToList();
    }

    private async Task<(TableDesignKey? PrimaryKey, List<TableDesignKey> UniqueKeys, List<TableDesignIndex> Indexes)> IndexesAsync()
    {
        var rows = new List<(int Id, string Name, byte Type, bool Unique, bool Primary, bool UniqueKey, bool SystemNamed,
            byte FillFactor, bool IgnoreDuplicates, string? Filter, bool Disabled, string? Compression, string? FileGroup)>();
        await using (var cmd = Command(connection, """
            SELECT i.index_id, i.name, i.type, i.is_unique, i.is_primary_key, i.is_unique_constraint, kc.is_system_named,
                   i.fill_factor, i.ignore_dup_key, i.filter_definition, i.is_disabled,
                   (SELECT TOP 1 p.data_compression_desc FROM sys.partitions p
                    WHERE p.object_id = i.object_id AND p.index_id = i.index_id AND p.data_compression <> 0
                      AND p.data_compression_desc NOT IN ('COLUMNSTORE')),
                   CASE WHEN ds.is_default = 0 THEN ds.name END
            FROM sys.indexes i
            LEFT JOIN sys.key_constraints kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
            LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE i.object_id = @id AND i.type > 0 AND i.is_hypothetical = 0
            ORDER BY i.name
            """, table.ObjectId))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetByte(2), reader.GetBoolean(3), reader.GetBoolean(4),
                    reader.GetBoolean(5), !reader.IsDBNull(6) && reader.GetBoolean(6), reader.GetByte(7), reader.GetBoolean(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetBoolean(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12)));

        TableDesignKey? primaryKey = null;
        var uniqueKeys = new List<TableDesignKey>();
        var indexes = new List<TableDesignIndex>();
        foreach (var row in rows)
        {
            if (row.Type is not (1 or 2 or 5 or 6))
            {
                _gaps.Add(new($"Index {row.Name} is an XML, spatial or other special index.", row.Name));
                continue;
            }
            var columns = await IndexColumnsAsync(row.Id);
            var keyColumns = columns.Where(c => !c.Included).Select(c => c.Name).ToList();
            if (row.Primary) primaryKey = new TableDesignKey(row.SystemNamed ? null : row.Name, keyColumns);
            else if (row.UniqueKey) uniqueKeys.Add(new TableDesignKey(row.SystemNamed ? null : row.Name, keyColumns));
            var properties = new List<string>();
            if (row.FillFactor > 0) properties.Add($"FILLFACTOR = {row.FillFactor}");
            if (row.IgnoreDuplicates) properties.Add("IGNORE_DUP_KEY = ON");
            if (row.Compression != null) properties.Add($"DATA_COMPRESSION = {row.Compression}");
            if (row.FileGroup != null) properties.Add($"ON {row.FileGroup}");
            if (row.Disabled) properties.Add("DISABLED");
            var columnstore = row.Type is 5 or 6;
            indexes.Add(new TableDesignIndex(row.Name, row.Unique,
                columnstore ? columns.Select(c => new TableDesignIndexPart(c.Name, null, false)).ToList()
                    : columns.Where(c => !c.Included).Select(c => new TableDesignIndexPart(c.Name, null, c.Descending)).ToList(),
                row.Primary ? TableDesignIndexOrigin.PrimaryKey : row.UniqueKey ? TableDesignIndexOrigin.UniqueKey : TableDesignIndexOrigin.CreateIndex)
            {
                IncludedColumns = columnstore ? [] : columns.Where(c => c.Included).Select(c => c.Name).ToList(),
                Filter = row.Filter,
                Method = (row.Type is 1 or 5 ? "CLUSTERED" : "NONCLUSTERED") + (columnstore ? " COLUMNSTORE" : ""),
                Properties = properties
            });
        }
        uniqueKeys = uniqueKeys.OrderBy(k => k.Name == null).ThenBy(k => k.Name, StringComparer.Ordinal).ToList();
        return (primaryKey, uniqueKeys, indexes);
    }

    private async Task<List<(string Name, bool Descending, bool Included)>> IndexColumnsAsync(int indexId)
    {
        await using var cmd = Command(connection, """
            SELECT c.name, ic.is_descending_key, ic.is_included_column FROM sys.index_columns ic
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = @id AND ic.index_id = @index
            ORDER BY CASE WHEN ic.key_ordinal = 0 THEN 1 ELSE 0 END, ic.key_ordinal, ic.index_column_id
            """, table.ObjectId);
        cmd.Parameters.AddWithValue("@index", indexId);
        var columns = new List<(string, bool, bool)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) columns.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        return columns;
    }

    private async Task<List<TableDesignTrigger>> TriggersAsync()
    {
        var triggers = new List<TableDesignTrigger>();
        await using var cmd = Command(connection, """
            SELECT tr.name, OBJECT_DEFINITION(tr.object_id), tr.is_disabled, tr.is_instead_of_trigger,
                   STUFF((SELECT ' OR ' + te.type_desc FROM sys.trigger_events te WHERE te.object_id = tr.object_id
                          ORDER BY te.type FOR XML PATH('')), 1, 4, '')
            FROM sys.triggers tr WHERE tr.parent_id = @id ORDER BY tr.name
            """, table.ObjectId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (reader.IsDBNull(1))
            {
                _gaps.Add(new($"Trigger {name} is encrypted or not visible to this login.", name));
                continue;
            }
            triggers.Add(new TableDesignTrigger(name, reader.GetBoolean(3) ? "INSTEAD OF" : "AFTER",
                reader.IsDBNull(4) ? "" : reader.GetString(4), reader.GetString(1).Trim())
            {
                State = reader.GetBoolean(2) ? "DISABLED" : null
            });
        }
        return triggers;
    }
}
