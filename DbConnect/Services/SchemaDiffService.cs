using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public class SchemaDiffService
{
    public async Task<SchemaDiffResult> CompareAsync(DatabaseSession source, DatabaseSession target)
    {
        var result = new SchemaDiffResult
        {
            SourceConnectionName = source.Name,
            TargetConnectionName = target.Name
        };

        try
        {
            var sourceObjects = await GetObjectsAsync(source);
            var targetObjects = await GetObjectsAsync(target);

            var sourceMap = sourceObjects.ToDictionary(BuildKey, o => o, StringComparer.OrdinalIgnoreCase);
            var targetMap = targetObjects.ToDictionary(BuildKey, o => o, StringComparer.OrdinalIgnoreCase);

            var allKeys = sourceMap.Keys
                .Union(targetMap.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var key in allKeys)
            {
                var inSource = sourceMap.TryGetValue(key, out var srcObj);
                var inTarget = targetMap.TryGetValue(key, out var tgtObj);
                var canonical = srcObj ?? tgtObj!;

                var item = new SchemaDiffItem
                {
                    ObjectName = canonical.Name,
                    SchemaName = canonical.SchemaName,
                    ObjectKind = canonical.ObjectType,
                    Status = inSource && !inTarget ? DiffStatus.OnlyInSource
                        : !inSource && inTarget ? DiffStatus.OnlyInTarget
                        : DiffStatus.InBoth,
                    ChangeType = inSource && !inTarget ? SchemaDiffChangeType.Add
                        : !inSource && inTarget ? SchemaDiffChangeType.Remove
                        : SchemaDiffChangeType.None,
                    IsSelected = true
                };

                if (inSource && inTarget)
                    await PopulateModifyDetailsAsync(item, source, srcObj!, target, tgtObj!);

                result.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            result.Items.Add(new SchemaDiffItem
            {
                ObjectName = "Error",
                ObjectType = "error",
                ObjectKind = SchemaDiffObjectType.Unknown,
                Status = DiffStatus.OnlyInSource,
                ChangeType = SchemaDiffChangeType.Add,
                SourceDdl = ex.Message
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<SchemaDiffItem>> DiffVirtualSchemaAsync(
        VirtualSchema virtualSchema,
        DatabaseSession targetSession)
    {
        var liveSchema = await targetSession.GetSchemaAsync();
        var liveTables = FlattenSchemaNodes(liveSchema)
            .Where(node => node.Type is SchemaNodeType.Table or SchemaNodeType.VirtualTable)
            .ToDictionary(node => node.RawName ?? node.Name, node => node, StringComparer.OrdinalIgnoreCase);
        var liveForeignKeys = await GetForeignKeysAsync(targetSession, liveTables.Keys.ToList());

        var items = new List<SchemaDiffItem>();

        foreach (var virtualTable in virtualSchema.Tables)
        {
            if (!liveTables.ContainsKey(virtualTable.Name))
            {
                items.Add(new SchemaDiffItem
                {
                    ObjectName = virtualTable.Name,
                    ObjectType = "table",
                    ObjectKind = SchemaDiffObjectType.Table,
                    Status = DiffStatus.OnlyInSource,
                    ChangeType = SchemaDiffChangeType.Add,
                    SourceDdl = $"Create table with {virtualTable.Columns.Count} column(s).",
                    GeneratedScript = DdlGenerator.GenerateCreateTable(virtualTable.Name, virtualTable.Columns, targetSession.ProviderName),
                    IsSelected = true
                });
                continue;
            }

            var liveColumns = await targetSession.GetColumnDefinitionsAsync(virtualTable.Name);
            if (ColumnsDiffer(liveColumns, virtualTable.Columns))
            {
                items.Add(new SchemaDiffItem
                {
                    ObjectName = virtualTable.Name,
                    ObjectType = "table",
                    ObjectKind = SchemaDiffObjectType.Table,
                    Status = DiffStatus.InBoth,
                    ChangeType = SchemaDiffChangeType.Modify,
                    SourceDdl = $"Columns differ: model=[{string.Join(", ", virtualTable.Columns.Select(c => $"{c.Name} {c.DataType}"))}] target=[{string.Join(", ", liveColumns.Select(c => $"{c.Name} {c.DataType}"))}]",
                    GeneratedScript = BuildAlterTableScript(virtualTable.Name, liveColumns, virtualTable.Columns, targetSession.ProviderName),
                    IsSelected = true
                });
            }
        }

        foreach (var relationship in virtualSchema.Relationships)
        {
            var exists = liveForeignKeys.Any(foreignKey =>
                string.Equals(foreignKey.FromTable, relationship.FromTable, StringComparison.OrdinalIgnoreCase)
                && string.Equals(foreignKey.FromColumn, relationship.FromColumn, StringComparison.OrdinalIgnoreCase)
                && string.Equals(foreignKey.ToTable, relationship.ToTable, StringComparison.OrdinalIgnoreCase)
                && string.Equals(foreignKey.ToColumn, relationship.ToColumn, StringComparison.OrdinalIgnoreCase));

            if (exists)
                continue;

            items.Add(new SchemaDiffItem
            {
                ObjectName = string.IsNullOrWhiteSpace(relationship.Name) ? $"{relationship.FromTable}.{relationship.FromColumn}" : relationship.Name,
                ObjectType = "foreign key",
                Status = DiffStatus.OnlyInSource,
                ChangeType = SchemaDiffChangeType.Add,
                SourceDdl = $"Create foreign key {relationship.FromTable}.{relationship.FromColumn} → {relationship.ToTable}.{relationship.ToColumn}",
                GeneratedScript = new VirtualSchemaService().GenerateAddForeignKey(relationship, targetSession.ProviderName),
                IsSelected = true
            });
        }

        return items
            .OrderBy(item => item.ObjectType.Equals("table", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task PopulateModifyDetailsAsync(
        SchemaDiffItem item,
        DatabaseSession source,
        SchemaObjectInfo sourceObject,
        DatabaseSession target,
        SchemaObjectInfo targetObject)
    {
        switch (item.ObjectKind)
        {
            case SchemaDiffObjectType.Table:
                await PopulateTableDiffAsync(item, source, sourceObject, target, targetObject);
                break;

            case SchemaDiffObjectType.View:
            case SchemaDiffObjectType.Function:
                await PopulateBodyDiffAsync(item, source, sourceObject, target, targetObject);
                break;

            case SchemaDiffObjectType.Procedure:
            case SchemaDiffObjectType.Trigger:
            case SchemaDiffObjectType.Index:
            case SchemaDiffObjectType.Sequence:
                await PopulateDefinitionDiffAsync(item, source, sourceObject, target, targetObject);
                break;
        }
    }

    private static async Task PopulateTableDiffAsync(
        SchemaDiffItem item,
        DatabaseSession source,
        SchemaObjectInfo sourceObject,
        DatabaseSession target,
        SchemaObjectInfo targetObject)
    {
        try
        {
            var srcColumns = await source.GetColumnDefinitionsAsync(sourceObject.Name);
            var tgtColumns = await target.GetColumnDefinitionsAsync(targetObject.Name);

            var srcColNames = srcColumns.Select(c => $"{c.Name} {c.DataType}").OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            var tgtColNames = tgtColumns.Select(c => $"{c.Name} {c.DataType}").OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

            if (!srcColNames.SequenceEqual(tgtColNames, StringComparer.OrdinalIgnoreCase))
            {
                item.ChangeType = SchemaDiffChangeType.Modify;
                item.SourceDdl = $"Columns differ: source=[{string.Join(", ", srcColNames)}] target=[{string.Join(", ", tgtColNames)}]";
            }
        }
        catch
        {
            // Best-effort comparison only.
        }
    }

    private static async Task PopulateBodyDiffAsync(
        SchemaDiffItem item,
        DatabaseSession source,
        SchemaObjectInfo sourceObject,
        DatabaseSession target,
        SchemaObjectInfo targetObject)
    {
        var sourceDdl = await GetDefinitionAsync(source, sourceObject);
        var targetDdl = await GetDefinitionAsync(target, targetObject);

        item.SourceDdl = sourceDdl;
        item.TargetDdl = targetDdl;

        if (!string.Equals(
                DependencyAnalysisService.NormalizeBody(sourceDdl ?? string.Empty),
                DependencyAnalysisService.NormalizeBody(targetDdl ?? string.Empty),
                StringComparison.Ordinal))
        {
            item.ChangeType = SchemaDiffChangeType.Modify;
        }
    }

    private static async Task PopulateDefinitionDiffAsync(
        SchemaDiffItem item,
        DatabaseSession source,
        SchemaObjectInfo sourceObject,
        DatabaseSession target,
        SchemaObjectInfo targetObject)
    {
        var sourceDdl = await GetDefinitionAsync(source, sourceObject);
        var targetDdl = await GetDefinitionAsync(target, targetObject);

        item.SourceDdl = sourceDdl;
        item.TargetDdl = targetDdl;

        if (!string.Equals(
                DependencyAnalysisService.NormalizeBody(sourceDdl ?? string.Empty),
                DependencyAnalysisService.NormalizeBody(targetDdl ?? string.Empty),
                StringComparison.Ordinal))
        {
            item.ChangeType = SchemaDiffChangeType.Modify;
        }
    }

    private static async Task<string?> GetDefinitionAsync(DatabaseSession session, SchemaObjectInfo obj)
    {
        var schema = obj.SchemaName ?? string.Empty;
        return obj.ObjectType switch
        {
            SchemaDiffObjectType.View => await session.GetViewDefinitionAsync(obj.QualifiedName),
            SchemaDiffObjectType.Function => await session.GetFunctionDefinitionAsync(schema, obj.Name),
            SchemaDiffObjectType.Procedure => await session.GetProcedureDefinitionAsync(schema, obj.Name),
            SchemaDiffObjectType.Trigger => await GetTriggerDefinitionAsync(session, obj),
            _ => null
        };
    }

    private static Task<string?> GetTriggerDefinitionAsync(DatabaseSession session, SchemaObjectInfo obj) =>
        session.ProviderName == "SQLite"
            ? session.GetViewDefinitionAsync(obj.Name)
            : session.GetTriggerDefinitionAsync(obj.SchemaName ?? string.Empty, obj.Name);

    private static async Task<List<SchemaObjectInfo>> GetObjectsAsync(DatabaseSession session)
    {
        var result = await session.ExecuteQueryAsync(GetMetadataSql(session.ProviderName));
        var objects = new List<SchemaObjectInfo>();

        if (result.HasError)
            return objects;

        foreach (var row in result.ObjectRows)
        {
            var name = GetValue(result, row, "name", "table_name", "routine_name", "trigger_name", "sequence_name", "index_name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var schemaName = GetValue(result, row, "schema_name", "table_schema", "routine_schema", "trigger_schema", "sequence_schema");
            var type = GetValue(result, row, "type", "table_type", "object_type", "routine_type");
            var objectType = SchemaDiffObjectTypeExtensions.Parse(type);
            if (objectType == SchemaDiffObjectType.Unknown)
                continue;

            objects.Add(new SchemaObjectInfo(name, schemaName, objectType));
        }

        return objects
            .GroupBy(BuildKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static string GetMetadataSql(string providerName) => providerName switch
    {
        "SQLite" => @"
            SELECT name, '' AS schema_name, type
            FROM sqlite_master
            WHERE type IN ('table', 'view', 'trigger', 'index')
              AND name NOT LIKE 'sqlite_%'
              AND COALESCE(sql, '') <> ''",

        "PostgreSQL" => @"
            SELECT table_name AS name,
                   table_schema AS schema_name,
                   CASE WHEN table_type = 'VIEW' THEN 'VIEW' ELSE 'TABLE' END AS type
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
            UNION ALL
            SELECT routine_name AS name,
                   routine_schema AS schema_name,
                   routine_type AS type
            FROM information_schema.routines
            WHERE routine_schema NOT IN ('pg_catalog', 'information_schema')
            UNION ALL
            SELECT DISTINCT trigger_name AS name,
                   trigger_schema AS schema_name,
                   'TRIGGER' AS type
            FROM information_schema.triggers
            WHERE trigger_schema NOT IN ('pg_catalog', 'information_schema')
            UNION ALL
            SELECT sequence_name AS name,
                   sequence_schema AS schema_name,
                   'SEQUENCE' AS type
            FROM information_schema.sequences
            WHERE sequence_schema NOT IN ('pg_catalog', 'information_schema')
            UNION ALL
            SELECT indexname AS name,
                   schemaname AS schema_name,
                   'INDEX' AS type
            FROM pg_indexes
            WHERE schemaname NOT IN ('pg_catalog', 'information_schema')",

        "SQL Server" => @"
            SELECT t.name AS name, SCHEMA_NAME(t.schema_id) AS schema_name, 'TABLE' AS type
            FROM sys.tables t
            WHERE t.is_ms_shipped = 0
            UNION ALL
            SELECT v.name AS name, SCHEMA_NAME(v.schema_id) AS schema_name, 'VIEW' AS type
            FROM sys.views v
            WHERE v.is_ms_shipped = 0
            UNION ALL
            SELECT o.name AS name, SCHEMA_NAME(o.schema_id) AS schema_name,
                   CASE WHEN o.type = 'P' THEN 'PROCEDURE' ELSE 'FUNCTION' END AS type
            FROM sys.objects o
            WHERE o.type IN ('P', 'FN', 'TF', 'IF')
              AND o.is_ms_shipped = 0
            UNION ALL
            SELECT tr.name AS name, SCHEMA_NAME(tab.schema_id) AS schema_name, 'TRIGGER' AS type
            FROM sys.triggers tr
            JOIN sys.tables tab ON tr.parent_id = tab.object_id
            WHERE tr.is_ms_shipped = 0
            UNION ALL
            SELECT s.name AS name, SCHEMA_NAME(s.schema_id) AS schema_name, 'SEQUENCE' AS type
            FROM sys.sequences s
            UNION ALL
            SELECT i.name AS name, SCHEMA_NAME(t.schema_id) AS schema_name, 'INDEX' AS type
            FROM sys.indexes i
            JOIN sys.tables t ON i.object_id = t.object_id
            WHERE i.name IS NOT NULL
              AND i.is_hypothetical = 0
              AND t.is_ms_shipped = 0",

        "MySQL" => @"
            SELECT table_name AS name,
                   '' AS schema_name,
                   CASE WHEN table_type = 'VIEW' THEN 'VIEW' ELSE 'TABLE' END AS type
            FROM information_schema.tables
            WHERE table_schema = DATABASE()
            UNION ALL
            SELECT routine_name AS name,
                   '' AS schema_name,
                   routine_type AS type
            FROM information_schema.routines
            WHERE routine_schema = DATABASE()
            UNION ALL
            SELECT trigger_name AS name,
                   '' AS schema_name,
                   'TRIGGER' AS type
            FROM information_schema.triggers
            WHERE trigger_schema = DATABASE()
            UNION ALL
            SELECT DISTINCT index_name AS name,
                   '' AS schema_name,
                   'INDEX' AS type
            FROM information_schema.statistics
            WHERE table_schema = DATABASE()
              AND index_name <> 'PRIMARY'",

        // DuckDB has no information_schema.sequences; its own catalog functions list every kind.
        "DuckDB" => @"
            SELECT table_name AS name, schema_name, 'TABLE' AS type FROM duckdb_tables() WHERE NOT internal
            UNION ALL SELECT view_name, schema_name, 'VIEW' FROM duckdb_views() WHERE NOT internal
            UNION ALL SELECT sequence_name, schema_name, 'SEQUENCE' FROM duckdb_sequences()
            UNION ALL SELECT index_name, schema_name, 'INDEX' FROM duckdb_indexes()",

        _ => @"
            SELECT table_name AS name,
                   table_schema AS schema_name,
                   CASE WHEN table_type = 'VIEW' THEN 'VIEW' ELSE 'TABLE' END AS type
            FROM information_schema.tables"
    };

    public static SchemaDiffResult CompareFromNodes(
        string sourceConnectionName,
        string targetConnectionName,
        List<SchemaNode> sourceNodes,
        List<SchemaNode> targetNodes)
    {
        var result = new SchemaDiffResult
        {
            SourceConnectionName = sourceConnectionName,
            TargetConnectionName = targetConnectionName
        };

        var sourceObjects = FlattenNodes(sourceNodes);
        var targetObjects = FlattenNodes(targetNodes);

        var sourceMap = sourceObjects.ToDictionary(BuildKey, o => o, StringComparer.OrdinalIgnoreCase);
        var targetMap = targetObjects.ToDictionary(BuildKey, o => o, StringComparer.OrdinalIgnoreCase);

        var allKeys = sourceMap.Keys.Union(targetMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var key in allKeys)
        {
            var inSource = sourceMap.TryGetValue(key, out var srcObj);
            var inTarget = targetMap.TryGetValue(key, out var tgtObj);
            var canonical = srcObj ?? tgtObj!;

            result.Items.Add(new SchemaDiffItem
            {
                ObjectName = canonical.Name,
                SchemaName = canonical.SchemaName,
                ObjectKind = canonical.ObjectType,
                Status = inSource && !inTarget ? DiffStatus.OnlyInSource
                    : !inSource && inTarget ? DiffStatus.OnlyInTarget
                    : DiffStatus.InBoth,
                ChangeType = inSource && !inTarget ? SchemaDiffChangeType.Add
                    : !inSource && inTarget ? SchemaDiffChangeType.Remove
                    : SchemaDiffChangeType.None,
                IsSelected = true
            });
        }

        return result;
    }

    private static List<SchemaObjectInfo> FlattenNodes(List<SchemaNode> nodes)
    {
        var results = new List<SchemaObjectInfo>();

        foreach (var node in nodes)
            FlattenNode(node, results);

        return results;
    }

    private static void FlattenNode(SchemaNode node, List<SchemaObjectInfo> results)
    {
        var kind = node.Type switch
        {
            SchemaNodeType.Table or SchemaNodeType.VirtualTable => SchemaDiffObjectType.Table,
            SchemaNodeType.View or SchemaNodeType.MaterializedView => SchemaDiffObjectType.View,
            SchemaNodeType.Function => SchemaDiffObjectType.Function,
            SchemaNodeType.Procedure => SchemaDiffObjectType.Procedure,
            SchemaNodeType.Trigger => SchemaDiffObjectType.Trigger,
            SchemaNodeType.Index => SchemaDiffObjectType.Index,
            SchemaNodeType.Sequence => SchemaDiffObjectType.Sequence,
            _ => SchemaDiffObjectType.Unknown
        };

        if (kind != SchemaDiffObjectType.Unknown)
            results.Add(new SchemaObjectInfo(node.RawName ?? node.Name, node.SchemaPath ?? node.SchemaName, kind));

        foreach (var child in node.Children)
            FlattenNode(child, results);
    }

    private static List<SchemaNode> FlattenSchemaNodes(IEnumerable<SchemaNode> nodes)
    {
        var results = new List<SchemaNode>();
        foreach (var node in nodes)
            FlattenSchemaNode(node, results);
        return results;
    }

    private static void FlattenSchemaNode(SchemaNode node, List<SchemaNode> results)
    {
        results.Add(node);
        foreach (var child in node.Children)
            FlattenSchemaNode(child, results);
    }

    private static bool ColumnsDiffer(IReadOnlyList<ColumnDefinition> liveColumns, IReadOnlyList<ColumnDefinition> virtualColumns)
    {
        if (liveColumns.Count != virtualColumns.Count)
            return true;

        for (var index = 0; index < liveColumns.Count; index++)
        {
            var live = liveColumns[index];
            var model = virtualColumns[index];
            if (!string.Equals(live.Name, model.Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(live.DataType, model.DataType, StringComparison.OrdinalIgnoreCase)
                || live.IsNullable != model.IsNullable
                || live.IsPrimaryKey != model.IsPrimaryKey)
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildAlterTableScript(string tableName, IReadOnlyList<ColumnDefinition> liveColumns, IReadOnlyList<ColumnDefinition> virtualColumns, string providerName)
    {
        try
        {
            return DdlGenerator.GenerateAlterTable(tableName, liveColumns.ToList(), virtualColumns.ToList(), providerName);
        }
        catch
        {
            return $"-- TABLE CHANGE: {tableName}{Environment.NewLine}-- Review manually in the table designer.";
        }
    }

    private static async Task<List<ForeignKeyInfo>> GetForeignKeysAsync(DatabaseSession session, IReadOnlyList<string> tableNames)
    {
        if (session.ProviderName == "SQLite")
        {
            var sqliteForeignKeys = new List<ForeignKeyInfo>();
            foreach (var tableName in tableNames)
            {
                var quotedTable = tableName.Replace("'", "''");
                var pragmaResult = await session.ExecuteQueryAsync($"PRAGMA foreign_key_list('{quotedTable}')");
                foreach (var row in pragmaResult.ObjectRows)
                {
                    sqliteForeignKeys.Add(new ForeignKeyInfo(
                        GetValue(pragmaResult, row, "id"),
                        tableName,
                        GetValue(pragmaResult, row, "from"),
                        GetValue(pragmaResult, row, "table"),
                        GetValue(pragmaResult, row, "to")));
                }
            }

            return sqliteForeignKeys;
        }

        var result = await session.ExecuteQueryAsync(GetForeignKeySql(session.ProviderName));
        return result.ObjectRows
            .Select(row => new ForeignKeyInfo(
                GetValue(result, row, "constraint_name", "name"),
                GetValue(result, row, "from_table", "table_name"),
                GetValue(result, row, "from_column", "column_name"),
                GetValue(result, row, "to_table", "referenced_table_name"),
                GetValue(result, row, "to_column", "referenced_column_name")))
            .Where(foreignKey => !string.IsNullOrWhiteSpace(foreignKey.FromTable))
            .ToList();
    }

    private static string GetForeignKeySql(string providerName) => providerName switch
    {
        "PostgreSQL" => @"
            SELECT tc.constraint_name,
                   tc.table_name AS from_table,
                   kcu.column_name AS from_column,
                   ccu.table_name AS to_table,
                   ccu.column_name AS to_column
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name
             AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage ccu
              ON ccu.constraint_name = tc.constraint_name
             AND ccu.constraint_schema = tc.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_schema NOT IN ('pg_catalog', 'information_schema')",
        "SQL Server" => @"
            SELECT fk.name AS constraint_name,
                   tp.name AS from_table,
                   cp.name AS from_column,
                   tr.name AS to_table,
                   cr.name AS to_column
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
            JOIN sys.tables tp ON fkc.parent_object_id = tp.object_id
            JOIN sys.columns cp ON fkc.parent_object_id = cp.object_id AND fkc.parent_column_id = cp.column_id
            JOIN sys.tables tr ON fkc.referenced_object_id = tr.object_id
            JOIN sys.columns cr ON fkc.referenced_object_id = cr.object_id AND fkc.referenced_column_id = cr.column_id",
        "MySQL" => @"
            SELECT kcu.constraint_name,
                   kcu.table_name AS from_table,
                   kcu.column_name AS from_column,
                   kcu.referenced_table_name AS to_table,
                   kcu.referenced_column_name AS to_column
            FROM information_schema.key_column_usage kcu
            WHERE kcu.table_schema = DATABASE()
              AND kcu.referenced_table_name IS NOT NULL",
        _ => string.Empty
    };

    private static string BuildKey(SchemaObjectInfo obj) => $"{obj.SchemaName ?? string.Empty}|{obj.Name}|{obj.ObjectType}";

    private static string GetValue(QueryResult result, object?[] row, params string[] keys) =>
        result.ValueOrDefault(row, keys)?.ToString() ?? string.Empty;

    private sealed record SchemaObjectInfo(string Name, string? SchemaName, SchemaDiffObjectType ObjectType)
    {
        public string QualifiedName => string.IsNullOrWhiteSpace(SchemaName) ? Name : $"{SchemaName}.{Name}";
    }

    private sealed record ForeignKeyInfo(string Name, string FromTable, string FromColumn, string ToTable, string ToColumn);
}
