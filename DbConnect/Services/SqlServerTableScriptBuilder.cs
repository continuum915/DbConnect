using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using DbConnect.Models;
using static DbConnect.Services.SqlServerTableScripts;

namespace DbConnect.Services;

/// <summary>Assembles one SQL Server table definition; see <see cref="SqlServerTableScripts"/>.</summary>
internal sealed class SqlServerTableScriptBuilder(SqlConnection connection, TableContext table, CancellationToken ct)
{
    private readonly List<TableDefinitionPart> _parts = [];
    private readonly SortedSet<TablePrerequisite> _prerequisites =
        new(Comparer<TablePrerequisite>.Create((a, b) => string.CompareOrdinal(a.Text, b.Text)));
    private readonly List<string> _exclusions = ["Table data.", "Ownership, permissions and security policies."];
    private readonly List<string> _body = [];

    public async Task<TableDefinitionSnapshot> BuildAsync()
    {
        if (table.Schema != "dbo") _prerequisites.Add($"Schema {Quote(table.Schema)}");
        await ColumnsAsync();
        await KeysAsync();
        await ChecksAsync();
        var create = $"CREATE TABLE {table.Qualified} (\n    {string.Join(",\n    ", _body)}\n)";
        if (table.FileGroup != null)
        {
            create += $" ON {Quote(table.FileGroup)}";
            _prerequisites.Add($"Filegroup {Quote(table.FileGroup)}");
        }
        if (table.Compression != null) create += $"\nWITH (DATA_COMPRESSION = {table.Compression})";
        _parts.Insert(0, new(TableDefinitionPartKind.Table, table.Name, create));
        if (table.LockEscalation != "TABLE")
            _parts.Add(new(TableDefinitionPartKind.Option, table.Name,
                $"ALTER TABLE {table.Qualified} SET (LOCK_ESCALATION = {table.LockEscalation})"));
        if (!table.IsDuplicate) await ForeignKeysAsync();
        await IndexesAsync();
        await ExtendedPropertiesAsync();
        if (!table.IsDuplicate) await TriggersAsync();
        return new TableDefinitionSnapshot(table.Identity, "SQL Server", _parts)
        {
            Prerequisites = _prerequisites.ToList(),
            Exclusions = _exclusions,
            ScriptRules = ProviderCapabilityDeclarations.SqlServer.ScriptRules
        };
    }

    private string Named(string name, bool systemNamed) => systemNamed ? ""
        : $"CONSTRAINT {Quote(table.IsDuplicate ? DefinitionNames.Suffixed(name, table.Name, 128) : name)} ";

    private async Task ColumnsAsync()
    {
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
            var name = Quote(reader.GetString(0));
            if (!reader.IsDBNull(9))
            {
                var computed = $"{name} AS {reader.GetString(9)}";
                if (reader.GetBoolean(10)) computed += " PERSISTED" + (reader.GetBoolean(7) ? "" : " NOT NULL");
                _body.Add(computed);
                continue;
            }
            var userDefined = reader.GetBoolean(3);
            var type = TypeName(reader.GetString(1), reader.GetString(2), userDefined, reader.GetInt16(4), reader.GetByte(5), reader.GetByte(6));
            if (userDefined) _prerequisites.Add($"Type {type}");
            if (reader.GetInt32(21) != 0)
                throw new TableDefinitionUnavailableException($"Column {name} is typed XML; XML schema collections cannot be scripted yet.");
            var column = $"{name} {type}";
            if (reader.GetBoolean(19)) column += " COLUMN_SET FOR ALL_SPARSE_COLUMNS";
            if (reader.GetBoolean(18)) column += " SPARSE";
            if (!reader.IsDBNull(8) && reader.GetString(8) != table.DefaultCollation) column += $" COLLATE {reader.GetString(8)}";
            if (!reader.IsDBNull(14))
                column += $" IDENTITY({reader.GetString(14)}, {reader.GetString(15)})" + (reader.GetBoolean(16) ? " NOT FOR REPLICATION" : "");
            if (reader.GetBoolean(17)) column += " ROWGUIDCOL";
            if (!reader.IsDBNull(20)) column += $" MASKED WITH (FUNCTION = {Literal(reader.GetString(20))})";
            if (!reader.GetBoolean(19)) column += reader.GetBoolean(7) ? " NULL" : " NOT NULL";
            if (!reader.IsDBNull(12)) column += $" {Named(reader.GetString(11), reader.GetBoolean(13))}DEFAULT {reader.GetString(12)}";
            _body.Add(column);
        }
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

    private static string IndexOptions(byte fillFactor, bool ignoreDuplicates, string? compression)
    {
        var options = new List<string>();
        if (fillFactor > 0) options.Add($"FILLFACTOR = {fillFactor}");
        if (ignoreDuplicates) options.Add("IGNORE_DUP_KEY = ON");
        if (compression != null) options.Add($"DATA_COMPRESSION = {compression}");
        return options.Count == 0 ? "" : $" WITH ({string.Join(", ", options)})";
    }

    private const string IndexQuery = """
        SELECT i.index_id, i.name, i.type, i.is_unique, i.is_primary_key, i.is_unique_constraint, kc.name, kc.is_system_named,
               i.fill_factor, i.ignore_dup_key, i.has_filter, i.filter_definition, i.is_disabled,
               (SELECT TOP 1 p.data_compression_desc FROM sys.partitions p
                WHERE p.object_id = i.object_id AND p.index_id = i.index_id AND p.data_compression <> 0
                  AND p.data_compression_desc NOT IN ('COLUMNSTORE')),
               CASE WHEN ds.is_default = 0 THEN ds.name END
        FROM sys.indexes i
        LEFT JOIN sys.key_constraints kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
        LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
        WHERE i.object_id = @id AND i.type > 0 AND i.is_hypothetical = 0
        ORDER BY i.index_id
        """;

    private async Task<List<object[]>> IndexRowsAsync()
    {
        await using var cmd = Command(connection, IndexQuery, table.ObjectId);
        var rows = new List<object[]>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    private static string? Text(object value) => value is DBNull ? null : (string)value;

    private async Task KeysAsync()
    {
        foreach (var row in (await IndexRowsAsync()).Where(r => (bool)r[4] || (bool)r[5]))
        {
            var columns = await IndexColumnsAsync((int)row[0]);
            var kind = (bool)row[4] ? "PRIMARY KEY" : "UNIQUE";
            var clustered = (byte)row[2] == 1 ? "CLUSTERED" : "NONCLUSTERED";
            _body.Add($"{Named((string)row[6], (bool)row[7])}{kind} {clustered} ({Columns(columns.Select(c => (c.Name, c.Descending)))})" +
                      IndexOptions((byte)row[8], (bool)row[9], Text(row[13])));
        }
    }

    private async Task IndexesAsync()
    {
        foreach (var row in await IndexRowsAsync())
        {
            var name = (string)row[1];
            if (!(bool)row[4] && !(bool)row[5])
            {
                var type = (byte)row[2];
                var columns = await IndexColumnsAsync((int)row[0]);
                string sql;
                if (type is 5 or 6)
                    sql = type == 5
                        ? $"CREATE CLUSTERED COLUMNSTORE INDEX {Quote(name)} ON {table.Qualified}"
                        : $"CREATE NONCLUSTERED COLUMNSTORE INDEX {Quote(name)} ON {table.Qualified} ({string.Join(", ", columns.Select(c => Quote(c.Name)))})";
                else if (type is 1 or 2)
                {
                    var keys = columns.Where(c => !c.Included).Select(c => (c.Name, c.Descending));
                    var included = columns.Where(c => c.Included).Select(c => Quote(c.Name)).ToList();
                    sql = $"CREATE {((bool)row[3] ? "UNIQUE " : "")}{(type == 1 ? "CLUSTERED" : "NONCLUSTERED")} INDEX {Quote(name)} " +
                          $"ON {table.Qualified} ({Columns(keys)})";
                    if (included.Count > 0) sql += $" INCLUDE ({string.Join(", ", included)})";
                    if ((bool)row[10]) sql += $" WHERE {Text(row[11])}";
                }
                else
                    throw new TableDefinitionUnavailableException($"Index {name} is an XML, spatial or other special index that DbConnect cannot script yet.");
                sql += IndexOptions((byte)row[8], (bool)row[9], Text(row[13]));
                if (Text(row[14]) is { } fileGroup)
                {
                    sql += $" ON {Quote(fileGroup)}";
                    _prerequisites.Add($"Filegroup {Quote(fileGroup)}");
                }
                _parts.Add(new(TableDefinitionPartKind.Index, name, sql));
            }
            if ((bool)row[12])
                _parts.Add(new(TableDefinitionPartKind.Option, name, $"ALTER INDEX {Quote(name)} ON {table.Qualified} DISABLE"));
        }
    }

    private async Task ChecksAsync()
    {
        await using var cmd = Command(connection, """
            SELECT name, definition, is_system_named, is_not_trusted, is_disabled, is_not_for_replication
            FROM sys.check_constraints WHERE parent_object_id = @id ORDER BY name
            """, table.ObjectId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var definition = $"{Named(reader.GetString(0), reader.GetBoolean(2))}CHECK {(reader.GetBoolean(5) ? "NOT FOR REPLICATION " : "")}{reader.GetString(1)}";
            if (!reader.GetBoolean(3) && !reader.GetBoolean(4)) _body.Add(definition);
            else AddUntrusted(reader.GetString(0), reader.GetBoolean(2), definition, reader.GetBoolean(4));
        }
    }

    private void AddUntrusted(string name, bool systemNamed, string definition, bool disabled)
    {
        if (systemNamed)
            throw new TableDefinitionUnavailableException($"Untrusted constraint {name} has a generated name, so it cannot be scripted faithfully.");
        _parts.Add(new(TableDefinitionPartKind.Constraint, name, $"ALTER TABLE {table.Qualified} WITH NOCHECK ADD {definition}"));
        if (disabled)
            _parts.Add(new(TableDefinitionPartKind.Constraint, name, $"ALTER TABLE {table.Qualified} NOCHECK CONSTRAINT {Quote(name)}"));
    }

    private async Task ForeignKeysAsync()
    {
        var keys = new List<(int Id, string Name, bool SystemNamed, string Target, string Delete, string Update, bool NotTrusted, bool Disabled, bool NotForReplication, bool Self,
            DatabaseObjectIdentity Referenced)>();
        await using (var cmd = Command(connection, """
            SELECT fk.object_id, fk.name, fk.is_system_named, QUOTENAME(rs.name) + '.' + QUOTENAME(rt.name),
                   fk.delete_referential_action_desc, fk.update_referential_action_desc, fk.is_not_trusted, fk.is_disabled,
                   fk.is_not_for_replication, CASE WHEN fk.referenced_object_id = fk.parent_object_id THEN 1 ELSE 0 END,
                   rs.name, rt.name
            FROM sys.foreign_keys fk
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            WHERE fk.parent_object_id = @id ORDER BY fk.name
            """, table.ObjectId))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                keys.Add((reader.GetInt32(0), reader.GetString(1), reader.GetBoolean(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetInt32(9) == 1,
                    new DatabaseObjectIdentity(null, reader.GetString(10), reader.GetString(11), SchemaNodeType.Table)));
        foreach (var key in keys)
        {
            var pairs = new List<(string From, string To)>();
            await using (var cmd = Command(connection, """
                SELECT pc.name, rc.name FROM sys.foreign_key_columns k
                JOIN sys.columns pc ON pc.object_id = k.parent_object_id AND pc.column_id = k.parent_column_id
                JOIN sys.columns rc ON rc.object_id = k.referenced_object_id AND rc.column_id = k.referenced_column_id
                WHERE k.constraint_object_id = @id ORDER BY k.constraint_column_id
                """, key.Id))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) pairs.Add((reader.GetString(0), reader.GetString(1)));
            var definition = $"{Named(key.Name, key.SystemNamed)}FOREIGN KEY ({string.Join(", ", pairs.Select(p => Quote(p.From)))}) " +
                             $"REFERENCES {key.Target} ({string.Join(", ", pairs.Select(p => Quote(p.To)))})";
            if (key.Delete != "NO_ACTION") definition += $" ON DELETE {key.Delete.Replace('_', ' ')}";
            if (key.Update != "NO_ACTION") definition += $" ON UPDATE {key.Update.Replace('_', ' ')}";
            if (key.NotForReplication) definition += " NOT FOR REPLICATION";
            if (key.NotTrusted || key.Disabled) AddUntrusted(key.Name, key.SystemNamed, definition, key.Disabled);
            else _parts.Add(new(TableDefinitionPartKind.Constraint, key.Name, $"ALTER TABLE {table.Qualified} ADD {definition}"));
            if (!key.Self) _prerequisites.Add(new($"Referenced table {key.Target} (foreign key target)", key.Referenced));
        }
    }

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
            if (!reader.IsDBNull(3) && reader.GetString(3) is not ("nvarchar" or "varchar"))
            {
                _exclusions.Add($"Extended property {reader.GetString(0)} (non-text value).");
                continue;
            }
            var sql = $"EXEC sys.sp_addextendedproperty @name = {Literal(reader.GetString(0))}, " +
                      $"@value = {(reader.IsDBNull(1) ? "NULL" : Literal(reader.GetString(1)))}, " +
                      $"@level0type = N'SCHEMA', @level0name = {Literal(table.Schema)}, @level1type = N'TABLE', @level1name = {Literal(table.Name)}";
            if (column != null) sql += $", @level2type = N'COLUMN', @level2name = {Literal(column)}";
            _parts.Add(new(TableDefinitionPartKind.Comment, column ?? table.Name, sql));
        }
    }

    private async Task TriggersAsync()
    {
        await using var cmd = Command(connection, """
            SELECT tr.name, OBJECT_DEFINITION(tr.object_id), tr.is_disabled FROM sys.triggers tr
            WHERE tr.parent_id = @id ORDER BY tr.name
            """, table.ObjectId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (reader.IsDBNull(1))
                throw new TableDefinitionUnavailableException($"Trigger {name} is encrypted or not visible to this login.");
            _parts.Add(new(TableDefinitionPartKind.Trigger, name, reader.GetString(1).Trim()));
            if (reader.GetBoolean(2))
                _parts.Add(new(TableDefinitionPartKind.Trigger, name, $"DISABLE TRIGGER {Quote(table.Schema)}.{Quote(name)} ON {table.Qualified}"));
        }
        if (_parts.Any(p => p.Kind == TableDefinitionPartKind.Trigger))
            _exclusions.Add("Trigger order set with sp_settriggerorder.");
    }
}
