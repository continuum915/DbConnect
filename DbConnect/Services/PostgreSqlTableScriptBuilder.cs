using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using DbConnect.Models;
using static DbConnect.Services.PostgreSqlTableScripts;

namespace DbConnect.Services;

/// <summary>Assembles one PostgreSQL table definition from catalog rows; see <see cref="PostgreSqlTableScripts"/>.</summary>
internal sealed class PostgreSqlTableScriptBuilder(NpgsqlConnection connection, TableContext table, CancellationToken ct)
{
    private readonly List<TableDefinitionPart> _parts = [];
    private readonly SortedSet<TablePrerequisite> _prerequisites =
        new(Comparer<TablePrerequisite>.Create((a, b) => string.CompareOrdinal(a.Text, b.Text)));
    private readonly List<string> _exclusions = ["Table data.", "Ownership, privileges and row-level security policies."];
    private readonly List<string> _body = [];
    private string Q => table.Temporary ? Quote(table.Name) : table.Qualified;

    public async Task<TableDefinitionSnapshot> BuildAsync()
    {
        if (!table.Temporary && table.Schema != "public") _prerequisites.Add($"Schema {Quote(table.Schema)}");
        await ColumnsAsync();
        await OwnedSequencesAsync();
        await ConstraintsAsync();
        _parts.Insert(0, new(TableDefinitionPartKind.Table, table.Name, CreateTable()));
        await IndexesAsync();
        TableOptions();
        await TriggersAsync();
        await NotesAsync();
        if (!string.IsNullOrEmpty(table.Comment))
            _parts.Add(new(TableDefinitionPartKind.Comment, table.Name, $"COMMENT ON TABLE {Q} IS {Literal(table.Comment)}"));
        return new TableDefinitionSnapshot(
            new DatabaseObjectIdentity(null, table.Schema, table.Name, SchemaNodeType.Table), "PostgreSQL", _parts)
        {
            Prerequisites = _prerequisites.ToList(),
            Exclusions = _exclusions
        };
    }

    private string CreateTable()
    {
        var persistence = table.Persistence switch { 'u' => "UNLOGGED ", 't' => "TEMPORARY ", _ => "" };
        var sql = $"CREATE {persistence}TABLE {Q} (\n    {string.Join(",\n    ", _body)}\n)";
        if (table.PartitionKey != null) sql += $"\nPARTITION BY {table.PartitionKey}";
        if (table.AccessMethod != null)
        {
            sql += $"\nUSING {Quote(table.AccessMethod)}";
            _prerequisites.Add($"Table access method {Quote(table.AccessMethod)}");
        }
        if (table.Options is { Length: > 0 }) sql += $"\nWITH ({string.Join(", ", table.Options)})";
        if (table.Tablespace != null)
        {
            sql += $"\nTABLESPACE {Quote(table.Tablespace)}";
            _prerequisites.Add($"Tablespace {Quote(table.Tablespace)}");
        }
        return sql;
    }

    private async Task ColumnsAsync()
    {
        var identities = new List<(string Column, string Kind)>();
        await using (var cmd = Command(connection, """
            SELECT a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull, pg_get_expr(d.adbin, d.adrelid),
                   a.attidentity::text, a.attgenerated::text,
                   CASE WHEN a.attcollation <> 0 AND a.attcollation <> t.typcollation
                        THEN quote_ident(cn.nspname) || '.' || quote_ident(co.collname) END,
                   col_description(a.attrelid, a.attnum),
                   CASE WHEN tn.nspname NOT IN ('pg_catalog', 'information_schema') THEN format_type(a.atttypid, NULL) END,
                   COALESCE(a.attstattarget::int, -1), a.attstorage::text, t.typstorage::text,
                   CASE WHEN cn.nspname NOT IN ('pg_catalog') THEN 1 ELSE 0 END
            FROM pg_attribute a
            JOIN pg_type t ON t.oid = a.atttypid
            JOIN pg_namespace tn ON tn.oid = t.typnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            LEFT JOIN pg_collation co ON co.oid = a.attcollation
            LEFT JOIN pg_namespace cn ON cn.oid = co.collnamespace
            WHERE a.attrelid = $1 AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """, table.Oid))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var column = $"{Quote(name)} {reader.GetString(1)}";
                var collation = reader.IsDBNull(6) ? null : reader.GetString(6);
                if (collation != null)
                {
                    column += $" COLLATE {collation}";
                    if (reader.GetInt32(12) == 1) _prerequisites.Add($"Collation {collation}");
                }
                var expression = reader.IsDBNull(3) ? null : reader.GetString(3);
                var generated = reader.GetString(5);
                var identity = reader.GetString(4);
                if (generated is "s" or "v")
                    column += $" GENERATED ALWAYS AS ({expression}) {(generated == "s" ? "STORED" : "VIRTUAL")}";
                else if (expression != null)
                    column += $" DEFAULT {expression}";
                if (identity is "a" or "d")
                {
                    column += $" {{identity:{name}}}";
                    identities.Add((name, identity));
                }
                if (reader.GetBoolean(2)) column += " NOT NULL";
                _body.Add(column);
                if (!reader.IsDBNull(7))
                    _parts.Add(new(TableDefinitionPartKind.Comment, name,
                        $"COMMENT ON COLUMN {Q}.{Quote(name)} IS {Literal(reader.GetString(7))}"));
                if (!reader.IsDBNull(8)) _prerequisites.Add($"Type {reader.GetString(8).Replace("[]", "")}");
                if (reader.GetInt32(9) >= 0)
                    _parts.Add(new(TableDefinitionPartKind.Option, name,
                        $"ALTER TABLE {Q} ALTER COLUMN {Quote(name)} SET STATISTICS {reader.GetInt32(9)}"));
                if (reader.GetString(10) != reader.GetString(11))
                    _parts.Add(new(TableDefinitionPartKind.Option, name,
                        $"ALTER TABLE {Q} ALTER COLUMN {Quote(name)} SET STORAGE {Storage(reader.GetString(10))}"));
            }
        foreach (var (column, kind) in identities)
        {
            var options = await SequenceOptionsAsync("pg_get_serial_sequence($1, $2)::regclass", table.Qualified, column);
            var clause = $"GENERATED {(kind == "a" ? "ALWAYS" : "BY DEFAULT")} AS IDENTITY ({options.Options})";
            var at = _body.FindIndex(b => b.Contains($"{{identity:{column}}}", StringComparison.Ordinal));
            _body[at] = _body[at].Replace($"{{identity:{column}}}", clause);
        }
        if (identities.Count > 0) _exclusions.Add("Current identity sequence values; new rows start from the START WITH value.");
    }

    private static string Storage(string code) => code switch
    {
        "p" => "PLAIN",
        "e" => "EXTERNAL",
        "m" => "MAIN",
        _ => "EXTENDED"
    };

    private async Task<(string Type, string Options)> SequenceOptionsAsync(string relation, params object[] values)
    {
        await using var cmd = Command(connection, $"""
            SELECT format_type(s.seqtypid, NULL), s.seqstart, s.seqincrement, s.seqmin, s.seqmax, s.seqcache, s.seqcycle
            FROM pg_sequence s WHERE s.seqrelid = {relation}
            """, values);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new TableDefinitionUnavailableException($"Sequence options for {table.Name} could not be read.");
        return (reader.GetString(0), $"START WITH {reader.GetInt64(1)} INCREMENT BY {reader.GetInt64(2)} " +
            $"MINVALUE {reader.GetInt64(3)} MAXVALUE {reader.GetInt64(4)} CACHE {reader.GetInt64(5)}" +
            (reader.GetBoolean(6) ? " CYCLE" : " NO CYCLE"));
    }

    /// <summary>serial columns own a sequence; it is created before the table and re-owned after it.</summary>
    private async Task OwnedSequencesAsync()
    {
        var owned = new List<(uint Oid, string Name, string Column)>();
        await using (var cmd = Command(connection, """
            SELECT s.oid, quote_ident(sn.nspname) || '.' || quote_ident(s.relname), a.attname
            FROM pg_depend d
            JOIN pg_class s ON s.oid = d.objid AND s.relkind = 'S'
            JOIN pg_namespace sn ON sn.oid = s.relnamespace
            JOIN pg_attribute a ON a.attrelid = d.refobjid AND a.attnum = d.refobjsubid
            WHERE d.classid = 'pg_class'::regclass AND d.refobjid = $1 AND d.deptype = 'a'
            ORDER BY a.attnum
            """, table.Oid))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                owned.Add((reader.GetFieldValue<uint>(0), reader.GetString(1), reader.GetString(2)));
        foreach (var (oid, name, column) in owned)
        {
            var (type, options) = await SequenceOptionsAsync("$1", oid);
            _parts.Add(new(TableDefinitionPartKind.Sequence, name, $"CREATE SEQUENCE {name} AS {type} {options}"));
            _parts.Add(new(TableDefinitionPartKind.Option, name, $"ALTER SEQUENCE {name} OWNED BY {Q}.{Quote(column)}"));
        }
        if (owned.Count > 0) _exclusions.Add("Current values of owned sequences; they restart from START WITH.");
    }

    private async Task ConstraintsAsync()
    {
        await using var cmd = Command(connection, """
            SELECT c.conname, c.contype::text, pg_get_constraintdef(c.oid, true), c.convalidated,
                   obj_description(c.oid, 'pg_constraint'),
                   CASE WHEN c.contype = 'f' AND c.confrelid <> c.conrelid
                        THEN quote_ident(fn.nspname) || '.' || quote_ident(fc.relname) END,
                   fn.nspname, fc.relname
            FROM pg_constraint c
            LEFT JOIN pg_class fc ON fc.oid = c.confrelid
            LEFT JOIN pg_namespace fn ON fn.oid = fc.relnamespace
            WHERE c.conrelid = $1 AND c.contype IN ('p', 'u', 'x', 'c', 'f')
            ORDER BY CASE c.contype WHEN 'p' THEN 0 WHEN 'u' THEN 1 WHEN 'x' THEN 2 WHEN 'c' THEN 3 ELSE 4 END, c.conname
            """, table.Oid);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var definition = $"CONSTRAINT {Quote(name)} {reader.GetString(2)}";
            if (reader.GetString(1) == "f" || !reader.GetBoolean(3))
                _parts.Add(new(TableDefinitionPartKind.Constraint, name, $"ALTER TABLE {Q} ADD {definition}"));
            else
                _body.Add(definition);
            if (!reader.IsDBNull(4))
                _parts.Add(new(TableDefinitionPartKind.Comment, name,
                    $"COMMENT ON CONSTRAINT {Quote(name)} ON {Q} IS {Literal(reader.GetString(4))}"));
            if (!reader.IsDBNull(5))
                _prerequisites.Add(new($"Referenced table {reader.GetString(5)} (foreign key target)",
                    new DatabaseObjectIdentity(null, reader.GetString(6), reader.GetString(7), SchemaNodeType.Table)));
            if (reader.GetString(1) == "x") _exclusions.Add($"Exclusion constraint {Quote(name)} may need an extension such as btree_gist.");
        }
    }

    private async Task IndexesAsync()
    {
        await using var cmd = Command(connection, """
            SELECT ic.relname, pg_get_indexdef(i.indexrelid), i.indisclustered, obj_description(i.indexrelid, 'pg_class'),
                   i.indisreplident,
                   EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = i.indexrelid AND k.conrelid = i.indrelid
                           AND k.contype IN ('p', 'u', 'x'))
            FROM pg_index i JOIN pg_class ic ON ic.oid = i.indexrelid
            WHERE i.indrelid = $1 ORDER BY ic.relname
            """, table.Oid);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (!reader.GetBoolean(5))
            {
                var sql = reader.GetString(1);
                if (table.Kind == 'p') sql = sql.Replace(" ON ONLY ", " ON ", StringComparison.Ordinal);
                _parts.Add(new(TableDefinitionPartKind.Index, name, sql));
            }
            if (reader.GetBoolean(2))
                _parts.Add(new(TableDefinitionPartKind.Option, name, $"ALTER TABLE {Q} CLUSTER ON {Quote(name)}"));
            if (reader.GetBoolean(4) && table.ReplicaIdentity == 'i')
                _parts.Add(new(TableDefinitionPartKind.Option, name, $"ALTER TABLE {Q} REPLICA IDENTITY USING INDEX {Quote(name)}"));
            if (!reader.IsDBNull(3))
                _parts.Add(new(TableDefinitionPartKind.Comment, name,
                    $"COMMENT ON INDEX {(table.Temporary ? "" : Quote(table.Schema) + ".")}{Quote(name)} IS {Literal(reader.GetString(3))}"));
        }
    }

    private void TableOptions()
    {
        if (table.ReplicaIdentity == 'f')
            _parts.Add(new(TableDefinitionPartKind.Option, table.Name, $"ALTER TABLE {Q} REPLICA IDENTITY FULL"));
        if (table.ReplicaIdentity == 'n')
            _parts.Add(new(TableDefinitionPartKind.Option, table.Name, $"ALTER TABLE {Q} REPLICA IDENTITY NOTHING"));
        if (table.RowSecurity)
            _parts.Add(new(TableDefinitionPartKind.Option, table.Name, $"ALTER TABLE {Q} ENABLE ROW LEVEL SECURITY"));
        if (table.ForceRowSecurity)
            _parts.Add(new(TableDefinitionPartKind.Option, table.Name, $"ALTER TABLE {Q} FORCE ROW LEVEL SECURITY"));
    }

    private async Task TriggersAsync()
    {
        await using var cmd = Command(connection, """
            SELECT t.tgname, pg_get_triggerdef(t.oid, true), t.tgenabled::text, obj_description(t.oid, 'pg_trigger'),
                   t.tgfoid::regprocedure::text, fn.nspname, f.proname
            FROM pg_trigger t
            JOIN pg_proc f ON f.oid = t.tgfoid
            JOIN pg_namespace fn ON fn.oid = f.pronamespace
            WHERE t.tgrelid = $1 AND NOT t.tgisinternal ORDER BY t.tgname
            """, table.Oid);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            _parts.Add(new(TableDefinitionPartKind.Trigger, name, reader.GetString(1)));
            var state = reader.GetString(2) switch
            {
                "D" => "DISABLE TRIGGER",
                "R" => "ENABLE REPLICA TRIGGER",
                "A" => "ENABLE ALWAYS TRIGGER",
                _ => null
            };
            if (state != null)
                _parts.Add(new(TableDefinitionPartKind.Trigger, name, $"ALTER TABLE {Q} {state} {Quote(name)}"));
            if (!reader.IsDBNull(3))
                _parts.Add(new(TableDefinitionPartKind.Trigger, name,
                    $"COMMENT ON TRIGGER {Quote(name)} ON {Q} IS {Literal(reader.GetString(3))}"));
            _prerequisites.Add(new($"Trigger function {reader.GetString(4)}",
                new DatabaseObjectIdentity(null, reader.GetString(5), reader.GetString(6), SchemaNodeType.Function)));
        }
    }

    private async Task NotesAsync()
    {
        await using var cmd = Command(connection, """
            SELECT (SELECT count(*) FROM pg_policy WHERE polrelid = $1),
                   (SELECT count(*) FROM pg_statistic_ext WHERE stxrelid = $1),
                   (SELECT count(*) FROM pg_inherits WHERE inhparent = $1),
                   (SELECT count(*) FROM pg_rewrite WHERE ev_class = $1 AND rulename <> '_RETURN')
            """, table.Oid);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.GetInt64(0) > 0) _exclusions.Add($"{reader.GetInt64(0)} row-level security policies.");
        if (reader.GetInt64(1) > 0) _exclusions.Add($"{reader.GetInt64(1)} extended statistics objects.");
        if (reader.GetInt64(2) > 0) _exclusions.Add($"{reader.GetInt64(2)} partitions or child tables.");
        if (reader.GetInt64(3) > 0) _exclusions.Add($"{reader.GetInt64(3)} rewrite rules.");
    }
}
