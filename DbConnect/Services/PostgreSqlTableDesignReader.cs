using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using DbConnect.Models;
using static DbConnect.Services.PostgreSqlTableScripts;

namespace DbConnect.Services;

/// <summary>
/// Reads a PostgreSQL <see cref="TableDesign"/> from pg_attribute, pg_constraint, pg_index,
/// pg_trigger and pg_description. Expressions come from the server's own deparsers.
/// </summary>
internal sealed class PostgreSqlTableDesignReader(NpgsqlConnection connection, TableContext table, CancellationToken ct)
{
    private const string Provider = "PostgreSQL";
    private readonly List<TableDesignGap> _gaps = [];
    private readonly List<TableDesignKey> _uniqueKeys = [];
    private readonly List<TableDesignForeignKey> _foreignKeys = [];
    private readonly List<TableDesignCheck> _checks = [];
    private TableDesignKey? _primaryKey;

    public static async Task<TableDesign> LoadAsync(string connectionString, DatabaseObjectIdentity table, CancellationToken ct)
    {
        await using var connection = PostgreSqlConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        try
        {
            var context = await TableContext.LoadAsync(connection, table, ct, scriptable: false);
            return await new PostgreSqlTableDesignReader(connection, context, ct).ReadAsync(table);
        }
        catch (PostgresException ex)
        {
            throw new TableDefinitionUnavailableException($"Could not read the design of {table.Name}: {ex.MessageText}", ex);
        }
    }

    private async Task<TableDesign> ReadAsync(DatabaseObjectIdentity requested)
    {
        TableShape();
        var columns = await ColumnsAsync();
        await ConstraintsAsync();
        var indexes = await IndexesAsync();
        var triggers = await TriggersAsync();
        await OwnedObjectsAsync();
        await using var current = Command(connection, "SELECT current_schema()");
        var defaultSchema = await current.ExecuteScalarAsync(ct) as string;
        var unsupported = new Dictionary<TableDesignFeature, string>();
        if (connection.PostgreSqlVersion.Major < 18)
            unsupported[TableDesignFeature.VirtualGeneratedColumns] = TableDesign.NotSupportedBy($"{Provider} {connection.PostgreSqlVersion.Major}");
        // ALTER COLUMN ... SET EXPRESSION arrived in PostgreSQL 17.
        if (connection.PostgreSqlVersion.Major < 17)
            unsupported[TableDesignFeature.GeneratedExpressionChanges] = TableDesign.NotSupportedBy($"{Provider} {connection.PostgreSqlVersion.Major}");
        return new TableDesign(requested with { Catalog = null, Schema = table.Schema, Name = table.Name }, Provider)
        {
            Columns = columns,
            PrimaryKey = _primaryKey,
            UniqueKeys = _uniqueKeys,
            ForeignKeys = _foreignKeys,
            ReferencedBy = await ReferencesAsync(),
            Checks = _checks,
            Indexes = indexes,
            Triggers = triggers,
            Comment = table.Comment,
            Options = Options(),
            Unsupported = unsupported,
            Unrepresented = _gaps,
            InDefaultSchema = table.Schema == defaultSchema || table.Temporary
        };
    }

    /// <summary>Tables the CREATE script refuses are shown but not saved (issue 03).</summary>
    private void TableShape()
    {
        if (table.IsPartition) _gaps.Add(new("This table is a partition of another table.", table.Name));
        if (table.Inherits) _gaps.Add(new("This table inherits columns from another table.", table.Name));
        if (table.IsTyped) _gaps.Add(new("This is a typed table (CREATE TABLE OF).", table.Name));
        if (table.PartitionKey != null) _gaps.Add(new($"This table is partitioned by {table.PartitionKey}.", table.Name));
    }

    private List<TableDesignOption> Options()
    {
        var options = new List<TableDesignOption>();
        if (table.Persistence == 'u') options.Add(new("persistence", "UNLOGGED"));
        if (table.Persistence == 't') options.Add(new("persistence", "TEMPORARY"));
        foreach (var option in table.Options ?? [])
        {
            var at = option.IndexOf('=');
            options.Add(at < 0 ? new(option, "") : new(option[..at], option[(at + 1)..]));
        }
        if (table.AccessMethod != null) options.Add(new("access method", table.AccessMethod));
        if (table.Tablespace != null) options.Add(new("tablespace", table.Tablespace));
        if (table.ReplicaIdentity == 'f') options.Add(new("replica identity", "FULL"));
        if (table.ReplicaIdentity == 'n') options.Add(new("replica identity", "NOTHING"));
        if (table.RowSecurity) options.Add(new("row level security", "ENABLED"));
        if (table.ForceRowSecurity) options.Add(new("force row level security", "ON"));
        return options;
    }

    private async Task<List<TableDesignColumn>> ColumnsAsync()
    {
        var columns = new List<TableDesignColumn>();
        await using var cmd = Command(connection, """
            SELECT a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull, pg_get_expr(d.adbin, d.adrelid, true),
                   a.attidentity::text, a.attgenerated::text,
                   CASE WHEN a.attcollation <> 0 AND a.attcollation <> t.typcollation THEN
                        CASE WHEN cn.nspname = 'pg_catalog' THEN quote_ident(co.collname)
                             ELSE quote_ident(cn.nspname) || '.' || quote_ident(co.collname) END END,
                   col_description(a.attrelid, a.attnum), COALESCE(a.attstattarget::int, -1), a.attstorage::text,
                   t.typstorage::text, sq.seqname, sq.deptype::text, sq.seqstart, sq.seqincrement, sq.seqmin, sq.seqmax,
                   sq.seqcache, sq.seqcycle, sq.seqtype, us.seqname
            FROM pg_attribute a
            JOIN pg_type t ON t.oid = a.atttypid
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            LEFT JOIN pg_collation co ON co.oid = a.attcollation
            LEFT JOIN pg_namespace cn ON cn.oid = co.collnamespace
            LEFT JOIN LATERAL (
                SELECT quote_ident(sn.nspname) || '.' || quote_ident(sc.relname) AS seqname, dep.deptype, s.seqstart,
                       s.seqincrement, s.seqmin, s.seqmax, s.seqcache, s.seqcycle, format_type(s.seqtypid, NULL) AS seqtype
                FROM pg_depend dep
                JOIN pg_sequence s ON s.seqrelid = dep.objid
                JOIN pg_class sc ON sc.oid = s.seqrelid
                JOIN pg_namespace sn ON sn.oid = sc.relnamespace
                WHERE dep.classid = 'pg_class'::regclass AND dep.refclassid = 'pg_class'::regclass
                  AND dep.refobjid = a.attrelid AND dep.refobjsubid = a.attnum AND dep.deptype IN ('a', 'i')
                LIMIT 1) sq ON true
            LEFT JOIN LATERAL (
                SELECT quote_ident(un.nspname) || '.' || quote_ident(uc.relname) AS seqname
                FROM pg_depend ud
                JOIN pg_class uc ON uc.oid = ud.refobjid AND uc.relkind = 'S'
                JOIN pg_namespace un ON un.oid = uc.relnamespace
                WHERE ud.classid = 'pg_attrdef'::regclass AND ud.objid = d.oid AND ud.refclassid = 'pg_class'::regclass
                ORDER BY 1 LIMIT 1) us ON true
            WHERE a.attrelid = $1 AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """, table.Oid);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var expression = reader.IsDBNull(3) ? null : reader.GetString(3);
            var generated = reader.GetString(5);
            var identity = reader.GetString(4);
            var properties = new List<string>();
            if (reader.GetInt32(8) >= 0) properties.Add($"STATISTICS {reader.GetInt32(8)}");
            if (reader.GetString(9) != reader.GetString(10)) properties.Add($"STORAGE {StorageName(reader.GetString(9))}");
            // The sequence a default draws from: owned (serial, OWNED BY) or external; an identity's own sequence is its identity.
            var sequence = !reader.IsDBNull(11) && reader.GetString(12) == "a" ? new TableDesignSequence(reader.GetString(11), true)
                : !reader.IsDBNull(20) ? new TableDesignSequence(reader.GetString(20), false) : null;
            columns.Add(new TableDesignColumn(reader.GetString(0), reader.GetString(1), !reader.GetBoolean(2))
            {
                Default = generated.Length == 0 ? expression : null,
                Collation = reader.IsDBNull(6) ? null : reader.GetString(6),
                Generated = generated.Length == 0 || expression == null ? null
                    : new TableDesignGenerated(expression, generated == "s" ? TableDesignGeneratedStorage.Stored : TableDesignGeneratedStorage.Virtual),
                Identity = identity.Length == 0 || reader.IsDBNull(11) ? null : Identity(reader, identity),
                Sequence = sequence,
                Comment = reader.IsDBNull(7) ? null : reader.GetString(7),
                Properties = properties
            });
        }
        return columns;
    }

    private static TableDesignIdentity Identity(NpgsqlDataReader reader, string kind)
    {
        var increment = reader.GetInt64(14);
        var (min, max) = (reader.GetInt64(15), reader.GetInt64(16));
        var typeMax = reader.GetString(19) switch { "smallint" => short.MaxValue, "integer" => int.MaxValue, _ => long.MaxValue };
        var options = new List<string>();
        if (increment > 0 ? min != 1 : min != -typeMax - 1) options.Add($"MINVALUE {min}");
        if (increment > 0 ? max != typeMax : max != -1) options.Add($"MAXVALUE {max}");
        if (reader.GetInt64(17) != 1) options.Add($"CACHE {reader.GetInt64(17)}");
        if (reader.GetBoolean(18)) options.Add("CYCLE");
        return new TableDesignIdentity(kind == "a" ? TableDesignIdentityKind.Always : TableDesignIdentityKind.ByDefault,
            reader.GetInt64(13).ToString(), increment.ToString())
        {
            Options = options.Count == 0 ? null : string.Join(" ", options)
        };
    }

    private static string StorageName(string code) => code switch
    {
        "p" => "PLAIN",
        "e" => "EXTERNAL",
        "m" => "MAIN",
        _ => "EXTENDED"
    };

    private static string Action(string code) => code switch
    {
        "r" => "RESTRICT",
        "c" => "CASCADE",
        "n" => "SET NULL",
        "d" => "SET DEFAULT",
        _ => "NO ACTION"
    };

    private async Task<List<TableDesignReference>> ReferencesAsync()
    {
        var references = new List<TableDesignReference>();
        await using var cmd = Command(connection, """
            SELECT c.conname, n.nspname, r.relname,
                   ARRAY(SELECT a.attname::text FROM unnest(c.confkey) WITH ORDINALITY k(n, o)
                         JOIN pg_attribute a ON a.attrelid = c.confrelid AND a.attnum = k.n ORDER BY k.o)
            FROM pg_constraint c
            JOIN pg_class r ON r.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = r.relnamespace
            WHERE c.confrelid = $1 AND c.contype = 'f'
            ORDER BY n.nspname, r.relname, c.conname
            """, table.Oid);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            references.Add(new TableDesignReference(reader.GetString(0),
                new DatabaseObjectIdentity(null, reader.GetString(1), reader.GetString(2), SchemaNodeType.Table), reader.GetFieldValue<string[]>(3)));
        return references;
    }

    private async Task ConstraintsAsync()
    {
        await using var cmd = Command(connection, """
            SELECT c.conname, c.contype::text,
                   ARRAY(SELECT a.attname::text FROM unnest(c.conkey) WITH ORDINALITY k(n, o)
                         JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.n ORDER BY k.o),
                   ARRAY(SELECT a.attname::text FROM unnest(c.confkey) WITH ORDINALITY k(n, o)
                         JOIN pg_attribute a ON a.attrelid = c.confrelid AND a.attnum = k.n ORDER BY k.o),
                   fn.nspname, fc.relname, c.confupdtype::text, c.confdeltype::text, c.confmatchtype::text,
                   c.condeferrable, c.condeferred, c.convalidated, c.connoinherit, pg_get_expr(c.conbin, c.conrelid, true),
                   obj_description(c.oid, 'pg_constraint')
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
            var columns = reader.GetFieldValue<string[]>(2);
            var comment = reader.IsDBNull(14) ? null : reader.GetString(14);
            var deferrable = reader.GetBoolean(9) ? reader.GetBoolean(10) ? "DEFERRABLE INITIALLY DEFERRED" : "DEFERRABLE" : null;
            switch (reader.GetString(1))
            {
                case "p" or "u" when deferrable != null:
                    _gaps.Add(new($"Key {name} is {deferrable}.", name));
                    break;
                case "p":
                    _primaryKey = new TableDesignKey(name, columns) { Comment = comment };
                    break;
                case "u":
                    _uniqueKeys.Add(new TableDesignKey(name, columns) { Comment = comment });
                    break;
                case "x":
                    _gaps.Add(new($"Exclusion constraint {name}.", name));
                    break;
                case "c":
                    var checkState = Join(reader.GetBoolean(11) ? null : "NOT VALID", reader.GetBoolean(12) ? "NO INHERIT" : null);
                    _checks.Add(new TableDesignCheck(name, reader.GetString(13))
                    {
                        Column = columns.Length == 1 ? columns[0] : null,
                        State = checkState,
                        Comment = comment
                    });
                    break;
                default:
                    var referenced = reader.GetFieldValue<string[]>(3);
                    var match = reader.GetString(8) switch { "f" => "MATCH FULL", "p" => "MATCH PARTIAL", _ => null };
                    _foreignKeys.Add(new TableDesignForeignKey(name,
                        columns.Zip(referenced, (from, to) => new TableDesignColumnPair(from, to)).ToList(),
                        new DatabaseObjectIdentity(null, reader.GetString(4), reader.GetString(5), SchemaNodeType.Table),
                        Action(reader.GetString(7)), Action(reader.GetString(6)))
                    {
                        State = Join(match, deferrable, reader.GetBoolean(11) ? null : "NOT VALID"),
                        Comment = comment
                    });
                    break;
            }
        }
    }

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => p != null).ToList();
        return present.Count == 0 ? null : string.Join(" ", present);
    }

    private async Task<List<TableDesignIndex>> IndexesAsync()
    {
        var nullsNotDistinct = connection.PostgreSqlVersion.Major >= 15 ? "i.indnullsnotdistinct" : "false";
        var rows = new List<(uint Oid, string Name, bool Unique, short Keys, short All, string Method, string? Filter,
            bool Clustered, bool ReplicaIdentity, string? Comment, string? Backs, string[]? Options, string? Tablespace,
            bool Valid, bool NullsNotDistinct)>();
        await using (var cmd = Command(connection, $"""
            SELECT i.indexrelid, ic.relname, i.indisunique, i.indnkeyatts, i.indnatts, am.amname,
                   pg_get_expr(i.indpred, i.indrelid, true), i.indisclustered, i.indisreplident,
                   obj_description(i.indexrelid, 'pg_class'),
                   (SELECT k.contype::text FROM pg_constraint k WHERE k.conindid = i.indexrelid AND k.conrelid = i.indrelid
                      AND k.contype IN ('p', 'u', 'x') LIMIT 1),
                   ic.reloptions, ts.spcname, i.indisvalid, {nullsNotDistinct}
            FROM pg_index i
            JOIN pg_class ic ON ic.oid = i.indexrelid
            JOIN pg_am am ON am.oid = ic.relam
            LEFT JOIN pg_tablespace ts ON ts.oid = ic.reltablespace
            WHERE i.indrelid = $1 ORDER BY ic.relname
            """, table.Oid))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetFieldValue<uint>(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt16(3),
                    reader.GetInt16(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetBoolean(7),
                    reader.GetBoolean(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetFieldValue<string[]>(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.GetBoolean(13), reader.GetBoolean(14)));

        var indexes = new List<TableDesignIndex>();
        foreach (var row in rows)
        {
            if (row.Backs == "x") continue;
            var (parts, included) = await IndexPartsAsync(row.Oid, row.Keys, row.All);
            var properties = new List<string>();
            if (row.NullsNotDistinct) properties.Add("NULLS NOT DISTINCT");
            properties.AddRange(row.Options ?? []);
            if (row.Tablespace != null) properties.Add($"TABLESPACE {row.Tablespace}");
            if (row.Clustered) properties.Add("CLUSTER");
            if (row.ReplicaIdentity && table.ReplicaIdentity == 'i') properties.Add("REPLICA IDENTITY");
            if (!row.Valid) properties.Add("INVALID");
            indexes.Add(new TableDesignIndex(row.Name, row.Unique, parts, row.Backs switch
            {
                "p" => TableDesignIndexOrigin.PrimaryKey,
                "u" => TableDesignIndexOrigin.UniqueKey,
                _ => TableDesignIndexOrigin.CreateIndex
            })
            {
                IncludedColumns = included,
                Filter = row.Filter,
                Method = row.Method,
                Properties = properties,
                Comment = row.Comment
            });
        }
        return indexes;
    }

    /// <summary>pg_get_indexdef with a column number returns only that key's column or expression.</summary>
    private async Task<(List<TableDesignIndexPart> Parts, List<string> Included)> IndexPartsAsync(uint index, short keys, short all)
    {
        var parts = new List<TableDesignIndexPart>();
        var included = new List<string>();
        await using var cmd = Command(connection, """
            SELECT k.n, pg_get_indexdef(i.indexrelid, k.n, true), i.indkey[k.n - 1] <> 0, COALESCE(i.indoption[k.n - 1], 0)::int2,
                   CASE WHEN i.indcollation[k.n - 1] <> 0
                         AND i.indcollation[k.n - 1] <> COALESCE(NULLIF(ta.attcollation, 0), ty.typcollation) THEN
                        CASE WHEN cn.nspname = 'pg_catalog' THEN quote_ident(co.collname)
                             ELSE quote_ident(cn.nspname) || '.' || quote_ident(co.collname) END END,
                   CASE WHEN NOT opc.opcdefault THEN opc.opcname::text END
            FROM pg_index i
            CROSS JOIN generate_series(1, i.indnatts) k(n)
            JOIN pg_attribute ia ON ia.attrelid = i.indexrelid AND ia.attnum = k.n
            JOIN pg_type ty ON ty.oid = ia.atttypid
            LEFT JOIN pg_attribute ta ON ta.attrelid = i.indrelid AND ta.attnum = i.indkey[k.n - 1] AND i.indkey[k.n - 1] <> 0
            LEFT JOIN pg_collation co ON co.oid = i.indcollation[k.n - 1]
            LEFT JOIN pg_namespace cn ON cn.oid = co.collnamespace
            LEFT JOIN pg_opclass opc ON opc.oid = i.indclass[k.n - 1]
            WHERE i.indexrelid = $1 ORDER BY k.n
            """, index);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var text = reader.GetString(1);
            if (reader.GetInt32(0) > keys)
            {
                included.Add(Unquote(text));
                continue;
            }
            var option = reader.GetInt16(3);
            var descending = (option & 1) != 0;
            var nullsFirst = (option & 2) != 0;
            var options = Join(reader.IsDBNull(5) ? null : reader.GetString(5),
                descending && !nullsFirst ? "NULLS LAST" : !descending && nullsFirst ? "NULLS FIRST" : null);
            parts.Add(new TableDesignIndexPart(reader.GetBoolean(2) ? Unquote(text) : null, reader.GetBoolean(2) ? null : text, descending)
            {
                Collation = reader.IsDBNull(4) ? null : reader.GetString(4),
                Options = options
            });
        }
        if (parts.Count != keys || parts.Count + included.Count != all)
            throw new TableDefinitionUnavailableException($"The key columns of an index on {table.Name} could not be read.");
        return (parts, included);
    }

    /// <summary>pg_get_indexdef quotes a column name the way quote_ident does.</summary>
    private static string Unquote(string identifier) =>
        identifier.Length > 1 && identifier[0] == '"' && identifier[^1] == '"' ? identifier[1..^1].Replace("\"\"", "\"") : identifier;

    private async Task<List<TableDesignTrigger>> TriggersAsync()
    {
        var triggers = new List<TableDesignTrigger>();
        await using var cmd = Command(connection, """
            SELECT t.tgname, t.tgtype, pg_get_triggerdef(t.oid, true), t.tgenabled::text
            FROM pg_trigger t WHERE t.tgrelid = $1 AND NOT t.tgisinternal ORDER BY t.tgname
            """, table.Oid);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var type = reader.GetInt16(1);
            var timing = (type & 64) != 0 ? "INSTEAD OF" : (type & 2) != 0 ? "BEFORE" : "AFTER";
            var events = new[] { (4, "INSERT"), (16, "UPDATE"), (8, "DELETE"), (32, "TRUNCATE") }
                .Where(e => (type & e.Item1) != 0).Select(e => e.Item2);
            var level = (type & 1) != 0 ? "FOR EACH ROW" : "FOR EACH STATEMENT";
            triggers.Add(new TableDesignTrigger(reader.GetString(0), timing, $"{string.Join(" OR ", events)} {level}", reader.GetString(2))
            {
                State = reader.GetString(3) switch { "D" => "DISABLED", "R" => "ENABLE REPLICA", "A" => "ENABLE ALWAYS", _ => null }
            });
        }
        return triggers;
    }

    /// <summary>Objects attached to the table that the design has no place for.</summary>
    private async Task OwnedObjectsAsync()
    {
        await using var cmd = Command(connection, """
            SELECT (SELECT count(*) FROM pg_policy WHERE polrelid = $1),
                   (SELECT count(*) FROM pg_statistic_ext WHERE stxrelid = $1),
                   (SELECT count(*) FROM pg_inherits WHERE inhparent = $1),
                   (SELECT count(*) FROM pg_rewrite WHERE ev_class = $1 AND rulename <> '_RETURN')
            """, table.Oid);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.GetInt64(0) > 0) _gaps.Add(new($"{reader.GetInt64(0)} row-level security policies."));
        if (reader.GetInt64(1) > 0) _gaps.Add(new($"{reader.GetInt64(1)} extended statistics objects."));
        if (reader.GetInt64(2) > 0) _gaps.Add(new($"{reader.GetInt64(2)} partitions or child tables."));
        if (reader.GetInt64(3) > 0) _gaps.Add(new($"{reader.GetInt64(3)} rewrite rules."));
    }
}
