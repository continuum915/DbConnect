using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public class DependencyAnalysisService
{
    private static readonly Dictionary<SchemaDiffObjectType, int> TypeOrder = new()
    {
        [SchemaDiffObjectType.Table] = 0,
        [SchemaDiffObjectType.Sequence] = 1,
        [SchemaDiffObjectType.Index] = 2,
        [SchemaDiffObjectType.View] = 3,
        [SchemaDiffObjectType.Function] = 4,
        [SchemaDiffObjectType.Trigger] = 5,
        [SchemaDiffObjectType.Procedure] = 6,
        [SchemaDiffObjectType.Unknown] = 7
    };

    public SortedDiffResult Sort(IReadOnlyList<SchemaDiffItem> items)
    {
        var actionableItems = items
            .Where(i => i.ChangeType != SchemaDiffChangeType.None)
            .ToList();

        var addsAndModifies = actionableItems
            .Where(i => i.ChangeType != SchemaDiffChangeType.Remove)
            .ToList();
        var removes = actionableItems
            .Where(i => i.ChangeType == SchemaDiffChangeType.Remove)
            .ToList();

        var (sortedAdds, addCycles) = TopologicalSort(addsAndModifies);
        var (sortedRemovesForward, removeCycles) = TopologicalSort(removes);
        var sortedRemoves = sortedRemovesForward.AsEnumerable().Reverse().ToList();

        return new SortedDiffResult
        {
            SortedItems = sortedAdds.Concat(sortedRemoves).ToList(),
            CircularGroups = addCycles.Concat(removeCycles).ToList(),
            ImpactSummary = BuildImpactSummary(actionableItems)
        };
    }

    public static List<string> ExtractReferencedObjects(string sql)
    {
        var refs = new List<string>();
        foreach (Match match in Regex.Matches(
                     sql,
                     @"\b(?:FROM|JOIN|UPDATE|INTO|MERGE\s+INTO|DELETE\s+FROM|EXEC(?:UTE)?|CALL)\s+([a-z_][a-z0-9_]*(?:\.[a-z_][a-z0-9_]*)?)",
                     RegexOptions.IgnoreCase))
        {
            refs.Add(match.Groups[1].Value.Split('.').Last());
        }

        return refs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string NormalizeBody(string sql)
    {
        sql = Regex.Replace(sql, @"--[^\r\n]*", " ");
        sql = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        sql = Regex.Replace(sql, @"\s+", " ").Trim();
        return sql.ToUpperInvariant();
    }

    /// <summary>Returns objects that depend ON the given object (things that would break if dropped).</summary>
    public async Task<List<ObjectDependency>> GetDependenciesAsync(
        DatabaseSession session, string objectName, string objectType)
    {
        try
        {
            return session.ProviderName switch
            {
                "SQLite" => await GetDependenciesSqliteAsync(session, objectName, objectType),
                "PostgreSQL" => await GetDependenciesPostgreSqlAsync(session, objectName, objectType),
                "SQL Server" => await GetDependenciesSqlServerAsync(session, objectName, objectType),
                _ => new List<ObjectDependency>()
            };
        }
        catch
        {
            return new List<ObjectDependency>();
        }
    }

    /// <summary>Returns objects the given object depends on.</summary>
    public async Task<List<ObjectDependency>> GetDependsOnAsync(
        DatabaseSession session, string objectName, string objectType)
    {
        try
        {
            return session.ProviderName switch
            {
                "SQLite" => await GetDependsOnSqliteAsync(session, objectName, objectType),
                "PostgreSQL" => await GetDependsOnPostgreSqlAsync(session, objectName, objectType),
                "SQL Server" => await GetDependsOnSqlServerAsync(session, objectName, objectType),
                _ => new List<ObjectDependency>()
            };
        }
        catch
        {
            return new List<ObjectDependency>();
        }
    }

    // ── SQLite ────────────────────────────────────────────────────────────────

    private static async Task<List<ObjectDependency>> GetDependenciesSqliteAsync(
        DatabaseSession session, string objectName, string objectType)
    {
        var result = new List<ObjectDependency>();
        try
        {
            // Find views/triggers that reference objectName in their SQL
            var masterResult = await session.ExecuteQueryAsync(
                "SELECT name, type, sql FROM sqlite_master WHERE type IN ('view','trigger') AND sql IS NOT NULL");

            if (!masterResult.HasError)
            {
                foreach (var row in masterResult.ObjectRows)
                {
                    var name = masterResult.ValueOrDefault(row, "name")?.ToString() ?? string.Empty;
                    var type = masterResult.ValueOrDefault(row, "type")?.ToString() ?? string.Empty;
                    var sql = masterResult.ValueOrDefault(row, "sql")?.ToString() ?? string.Empty;

                    if (ContainsIdentifier(sql, objectName))
                    {
                        result.Add(new ObjectDependency
                        {
                            ObjectName = name,
                            ObjectType = type,
                            DependsOnName = objectName,
                            DependsOnType = objectType,
                            DependencyKind = "references"
                        });
                    }
                }
            }

            // Find other tables that have FK pointing to objectName
            if (objectType.Equals("Table", StringComparison.OrdinalIgnoreCase) ||
                objectType.Equals("table", StringComparison.OrdinalIgnoreCase))
            {
                var tablesResult = await session.ExecuteQueryAsync(
                    "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");

                if (!tablesResult.HasError)
                {
                    foreach (var row in tablesResult.ObjectRows)
                    {
                        var tableName = tablesResult.ValueOrDefault(row, "name")?.ToString() ?? string.Empty;
                        if (string.Equals(tableName, objectName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            var fkResult = await session.ExecuteQueryAsync(
                                $"PRAGMA foreign_key_list('{tableName.Replace("'", "''")}')");

                            if (!fkResult.HasError)
                            {
                                foreach (var fkRow in fkResult.ObjectRows)
                                {
                                    var refTable = fkResult.ValueOrDefault(fkRow, "table")?.ToString() ?? "";
                                    if (string.Equals(refTable, objectName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        result.Add(new ObjectDependency
                                        {
                                            ObjectName = tableName,
                                            ObjectType = "table",
                                            DependsOnName = objectName,
                                            DependsOnType = objectType,
                                            DependencyKind = "foreign key"
                                        });
                                        break;
                                    }
                                }
                            }
                        }
                        catch { /* ignore per-table errors */ }
                    }
                }
            }
        }
        catch { /* return what we have */ }

        return result;
    }

    private static async Task<List<ObjectDependency>> GetDependsOnSqliteAsync(
        DatabaseSession session, string objectName, string objectType)
    {
        var result = new List<ObjectDependency>();
        try
        {
            if (objectType.Equals("View", StringComparison.OrdinalIgnoreCase) ||
                objectType.Equals("view", StringComparison.OrdinalIgnoreCase))
            {
                // Parse the view's CREATE statement for table references
                var defResult = await session.ExecuteQueryAsync(
                    $"SELECT sql FROM sqlite_master WHERE type='view' AND name='{objectName.Replace("'", "''")}'");

                if (!defResult.HasError && defResult.ObjectRows.Count > 0)
                {
                    var sql = defResult.ValueOrDefault(defResult.ObjectRows[0], "sql")?.ToString() ?? "";
                    var tableNames = ExtractTableNames(sql);

                    // Look up each referenced name's type
                    var allObjects = await session.ExecuteQueryAsync(
                        "SELECT name, type FROM sqlite_master WHERE type IN ('table','view')");
                    var typeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!allObjects.HasError)
                        foreach (var row in allObjects.ObjectRows)
                        {
                            var n = allObjects.ValueOrDefault(row, "name")?.ToString() ?? "";
                            var ty = allObjects.ValueOrDefault(row, "type")?.ToString() ?? "table";
                            typeMap[n] = ty;
                        }

                    foreach (var tbl in tableNames)
                    {
                        if (string.Equals(tbl, objectName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        typeMap.TryGetValue(tbl, out var refType);
                        result.Add(new ObjectDependency
                        {
                            ObjectName = objectName,
                            ObjectType = "view",
                            DependsOnName = tbl,
                            DependsOnType = refType ?? "table",
                            DependencyKind = "references"
                        });
                    }
                }
            }
            else if (objectType.Equals("Table", StringComparison.OrdinalIgnoreCase) ||
                     objectType.Equals("table", StringComparison.OrdinalIgnoreCase))
            {
                // FK references from this table
                var fkResult = await session.ExecuteQueryAsync(
                    $"PRAGMA foreign_key_list('{objectName.Replace("'", "''")}')");

                if (!fkResult.HasError)
                {
                    foreach (var row in fkResult.ObjectRows)
                    {
                        var refTable = fkResult.ValueOrDefault(row, "table")?.ToString() ?? "";
                        if (string.IsNullOrEmpty(refTable)) continue;
                        result.Add(new ObjectDependency
                        {
                            ObjectName = objectName,
                            ObjectType = "table",
                            DependsOnName = refTable,
                            DependsOnType = "table",
                            DependencyKind = "foreign key"
                        });
                    }
                }
            }
        }
        catch { /* return what we have */ }

        return result;
    }

    // ── PostgreSQL ────────────────────────────────────────────────────────────

    private static async Task<List<ObjectDependency>> GetDependenciesPostgreSqlAsync(
        DatabaseSession session, string objectName, string objectType)
    {
        var result = new List<ObjectDependency>();
        try
        {
            // Views/procs depending on this object
            var sql = $"""
                SELECT dependent_ns.nspname || '.' || dependent_view.relname AS name,
                       dependent_view.relkind AS kind
                FROM pg_depend
                JOIN pg_rewrite ON pg_depend.objid = pg_rewrite.oid
                JOIN pg_class dependent_view ON pg_rewrite.ev_class = dependent_view.oid
                JOIN pg_class source_table ON pg_depend.refobjid = source_table.oid
                JOIN pg_namespace dependent_ns ON dependent_ns.oid = dependent_view.relnamespace
                WHERE source_table.relname = '{objectName.Replace("'", "''")}'
                  AND source_table.oid <> dependent_view.oid
                """;
            var queryResult = await session.ExecuteQueryAsync(sql);
            if (!queryResult.HasError)
            {
                foreach (var row in queryResult.ObjectRows)
                {
                    result.Add(new ObjectDependency
                    {
                        ObjectName = queryResult.ValueOrDefault(row, "name")?.ToString() ?? "",
                        ObjectType = queryResult.ValueOrDefault(row, "kind")?.ToString() == "v" ? "view" : "object",
                        DependsOnName = objectName,
                        DependsOnType = objectType,
                        DependencyKind = "references"
                    });
                }
            }

            // FK constraints from other tables referencing this table
            var fkSql = $"""
                SELECT tc.table_name, tc.constraint_name
                FROM information_schema.referential_constraints rc
                JOIN information_schema.table_constraints tc ON rc.constraint_name = tc.constraint_name
                JOIN information_schema.table_constraints fc ON rc.unique_constraint_name = fc.constraint_name
                WHERE fc.table_name = '{objectName.Replace("'", "''")}' AND tc.table_schema = 'public'
                """;
            var fkResult = await session.ExecuteQueryAsync(fkSql);
            if (!fkResult.HasError)
            {
                foreach (var row in fkResult.ObjectRows)
                {
                    result.Add(new ObjectDependency
                    {
                        ObjectName = fkResult.ValueOrDefault(row, "table_name")?.ToString() ?? "",
                        ObjectType = "table",
                        DependsOnName = objectName,
                        DependsOnType = objectType,
                        DependencyKind = "foreign key"
                    });
                }
            }
        }
        catch { /* return what we have */ }
        return result;
    }

    private static async Task<List<ObjectDependency>> GetDependsOnPostgreSqlAsync(
        DatabaseSession session, string objectName, string objectType)
    {
        var result = new List<ObjectDependency>();
        try
        {
            var sql = $"""
                SELECT source_ns.nspname || '.' || source.relname AS dep_name, source.relkind AS kind
                FROM pg_depend
                JOIN pg_rewrite ON pg_depend.objid = pg_rewrite.oid
                JOIN pg_class dependent_view ON pg_rewrite.ev_class = dependent_view.oid
                JOIN pg_class source ON pg_depend.refobjid = source.oid
                JOIN pg_namespace source_ns ON source_ns.oid = source.relnamespace
                WHERE dependent_view.relname = '{objectName.Replace("'", "''")}'
                  AND source.oid <> dependent_view.oid
                """;
            var queryResult = await session.ExecuteQueryAsync(sql);
            if (!queryResult.HasError)
            {
                foreach (var row in queryResult.ObjectRows)
                {
                    result.Add(new ObjectDependency
                    {
                        ObjectName = objectName,
                        ObjectType = objectType,
                        DependsOnName = queryResult.ValueOrDefault(row, "dep_name")?.ToString() ?? "",
                        DependsOnType = queryResult.ValueOrDefault(row, "kind")?.ToString() == "v" ? "view" : "table",
                        DependencyKind = "references"
                    });
                }
            }
        }
        catch { /* return what we have */ }
        return result;
    }

    // ── SQL Server ────────────────────────────────────────────────────────────

    private static async Task<List<ObjectDependency>> GetDependenciesSqlServerAsync(
        DatabaseSession session, string objectName, string objectType)
    {
        var result = new List<ObjectDependency>();
        try
        {
            var sql = $"""
                SELECT OBJECT_NAME(sed.referencing_id) AS name,
                       o.type_desc AS type
                FROM sys.sql_expression_dependencies sed
                JOIN sys.objects o ON o.object_id = sed.referencing_id
                WHERE sed.referenced_entity_name = '{objectName.Replace("'", "''")}'
                """;
            var queryResult = await session.ExecuteQueryAsync(sql);
            if (!queryResult.HasError)
            {
                foreach (var row in queryResult.ObjectRows)
                {
                    result.Add(new ObjectDependency
                    {
                        ObjectName = queryResult.ValueOrDefault(row, "name")?.ToString() ?? "",
                        ObjectType = queryResult.ValueOrDefault(row, "type")?.ToString() ?? "object",
                        DependsOnName = objectName,
                        DependsOnType = objectType,
                        DependencyKind = "references"
                    });
                }
            }

            // FK constraints
            var fkSql = $"""
                SELECT OBJECT_NAME(fk.parent_object_id) AS table_name
                FROM sys.foreign_keys fk
                JOIN sys.tables t ON t.object_id = fk.referenced_object_id
                WHERE t.name = '{objectName.Replace("'", "''")}'
                """;
            var fkResult = await session.ExecuteQueryAsync(fkSql);
            if (!fkResult.HasError)
            {
                foreach (var row in fkResult.ObjectRows)
                {
                    result.Add(new ObjectDependency
                    {
                        ObjectName = fkResult.ValueOrDefault(row, "table_name")?.ToString() ?? "",
                        ObjectType = "table",
                        DependsOnName = objectName,
                        DependsOnType = objectType,
                        DependencyKind = "foreign key"
                    });
                }
            }
        }
        catch { /* return what we have */ }
        return result;
    }

    private static async Task<List<ObjectDependency>> GetDependsOnSqlServerAsync(
        DatabaseSession session, string objectName, string objectType)
    {
        var result = new List<ObjectDependency>();
        try
        {
            var sql = $"""
                SELECT sed.referenced_entity_name AS dep_name, 'object' AS type
                FROM sys.sql_expression_dependencies sed
                WHERE OBJECT_NAME(sed.referencing_id) = '{objectName.Replace("'", "''")}'
                """;
            var queryResult = await session.ExecuteQueryAsync(sql);
            if (!queryResult.HasError)
            {
                foreach (var row in queryResult.ObjectRows)
                {
                    result.Add(new ObjectDependency
                    {
                        ObjectName = objectName,
                        ObjectType = objectType,
                        DependsOnName = queryResult.ValueOrDefault(row, "dep_name")?.ToString() ?? "",
                        DependsOnType = "object",
                        DependencyKind = "references"
                    });
                }
            }
        }
        catch { /* return what we have */ }
        return result;
    }

    private static (List<SchemaDiffItem> sorted, List<List<string>> cycles) TopologicalSort(List<SchemaDiffItem> items)
    {
        if (items.Count == 0)
            return (new List<SchemaDiffItem>(), new List<List<string>>());

        var keyToItem = items.ToDictionary(GetItemKey, i => i, StringComparer.OrdinalIgnoreCase);
        var nameLookup = items
            .GroupBy(i => i.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(GetItemKey).ToList(), StringComparer.OrdinalIgnoreCase);
        var inDegree = items.ToDictionary(GetItemKey, _ => 0, StringComparer.OrdinalIgnoreCase);
        var adjacency = items.ToDictionary(GetItemKey, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            foreach (var dependencyKey in ResolveDependencyKeys(item, keyToItem, nameLookup))
            {
                adjacency[dependencyKey].Add(GetItemKey(item));
                inDegree[GetItemKey(item)]++;
            }
        }

        var ready = items
            .Where(i => inDegree[GetItemKey(i)] == 0)
            .OrderBy(i => TypeOrder.GetValueOrDefault(i.ObjectKind, 99))
            .ThenBy(GetItemKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sorted = new List<SchemaDiffItem>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (ready.Count > 0)
        {
            var next = ready[0];
            ready.RemoveAt(0);

            var nextKey = GetItemKey(next);
            if (!visited.Add(nextKey))
                continue;

            sorted.Add(next);

            foreach (var neighborKey in adjacency[nextKey]
                         .OrderBy(k => TypeOrder.GetValueOrDefault(keyToItem[k].ObjectKind, 99))
                         .ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                inDegree[neighborKey]--;
                if (inDegree[neighborKey] == 0)
                    ready.Add(keyToItem[neighborKey]);
            }

            ready = ready
                .OrderBy(i => TypeOrder.GetValueOrDefault(i.ObjectKind, 99))
                .ThenBy(GetItemKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var cycleItems = items
            .Where(i => !visited.Contains(GetItemKey(i)))
            .OrderBy(i => TypeOrder.GetValueOrDefault(i.ObjectKind, 99))
            .ThenBy(GetItemKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cycles = cycleItems.Count == 0
            ? new List<List<string>>()
            : new List<List<string>> { cycleItems.Select(GetItemKey).ToList() };

        sorted.AddRange(cycleItems);
        return (sorted, cycles);
    }

    private static ImpactSummary BuildImpactSummary(IReadOnlyList<SchemaDiffItem> items)
    {
        var affectedViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var affectedRoutines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items.Where(i => i.ChangeType == SchemaDiffChangeType.Modify))
        {
            if (item.ObjectKind == SchemaDiffObjectType.View)
                affectedViews.Add(GetItemKey(item));
            else if (item.ObjectKind is SchemaDiffObjectType.Procedure or SchemaDiffObjectType.Function)
                affectedRoutines.Add(GetItemKey(item));
        }

        var changedKeys = items
            .Where(i => i.ChangeType != SchemaDiffChangeType.None)
            .Select(GetItemKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items.Where(i => i.ChangeType != SchemaDiffChangeType.Remove))
        {
            if (!item.DependsOn.Any(dep => changedKeys.Contains(dep) || changedKeys.Contains(dep.Split('.').Last())))
                continue;

            if (item.ObjectKind == SchemaDiffObjectType.View)
                affectedViews.Add(GetItemKey(item));
            else if (item.ObjectKind is SchemaDiffObjectType.Procedure or SchemaDiffObjectType.Function)
                affectedRoutines.Add(GetItemKey(item));
        }

        return new ImpactSummary
        {
            AffectedViewCount = affectedViews.Count,
            AffectedProcedureCount = affectedRoutines.Count
        };
    }

    private static IEnumerable<string> ResolveDependencyKeys(
        SchemaDiffItem item,
        IReadOnlyDictionary<string, SchemaDiffItem> keyToItem,
        IReadOnlyDictionary<string, List<string>> nameLookup)
    {
        foreach (var dependency in item.DependsOn.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (keyToItem.ContainsKey(dependency))
            {
                yield return dependency;
                continue;
            }

            if (nameLookup.TryGetValue(dependency, out var keys))
            {
                foreach (var key in keys)
                    yield return key;
                continue;
            }

            var shortName = dependency.Split('.').Last();
            if (nameLookup.TryGetValue(shortName, out keys))
            {
                foreach (var key in keys)
                    yield return key;
            }
        }
    }

    private static string GetItemKey(SchemaDiffItem item) => item.QualifiedName;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool ContainsIdentifier(string sql, string identifier)
    {
        // Match whole-word occurrences (handles quoted or unquoted names)
        return Regex.IsMatch(sql,
            $@"(?<![""'\w]){Regex.Escape(identifier)}(?![""'\w])",
            RegexOptions.IgnoreCase);
    }

    private static List<string> ExtractTableNames(string sql)
    {
        var names = new List<string>();
        // Match FROM/JOIN clauses: FROM tablename, JOIN tablename
        var matches = Regex.Matches(sql,
            @"(?:FROM|JOIN)\s+[""']?(\w+)[""']?",
            RegexOptions.IgnoreCase);
        foreach (Match m in matches)
            names.Add(m.Groups[1].Value);
        return names;
    }
}
