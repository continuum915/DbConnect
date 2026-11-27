using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services.Builders;
using SQLitePCL;

namespace DbConnect.Services;

internal static class SqliteSchemaObjects
{
    public static async Task<IReadOnlyList<SqliteSchemaObject>> LoadAsync(SqliteConnection connection, string tableName)
    {
        var objects = new List<SqliteSchemaObject>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE type IN ('view', 'trigger') ORDER BY name;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                objects.Add(new SqliteSchemaObject(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        }
        var unresolved = new HashSet<SqliteSchemaObject>();
        foreach (var view in objects.Where(o => o.Type == "view"))
        {
            // Preparing a query resolves aliases, quoted names, joins and nested views without reading rows.
            strdelegate_authorizer authorizer = (_, action, name, column, database, source) =>
            {
                if (action == raw.SQLITE_READ && database == "main")
                {
                    ((HashSet<string>)view.ReferencedObjects).Add(name);
                    if (name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                        ((HashSet<string>)view.ReferencedColumns).Add(column);
                }
                return raw.SQLITE_OK;
            };
            try { Inspect(connection, $"SELECT * FROM {Quote(view.Name)};", authorizer); }
            catch (SqliteException) { unresolved.Add(view); }
        }
        var targetColumns = await ColumnsAsync(connection, tableName);
        foreach (var item in objects.Where(o => o.Type == "trigger"))
            ((HashSet<string>)item.ReferencedColumns).UnionWith(
                SqliteTriggerDependencies.InsertColumns(item.CreateSql, tableName, targetColumns));
        var triggers = objects.Where(o => o.Type == "trigger").ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);
        strdelegate_authorizer triggerAuthorizer = (_, action, name, column, database, source) =>
        {
            if (source != null && triggers.TryGetValue(source, out var item) && database == "main"
                && action is raw.SQLITE_READ or raw.SQLITE_UPDATE or raw.SQLITE_INSERT or raw.SQLITE_DELETE)
            {
                ((HashSet<string>)item.ReferencedObjects).Add(name);
                if (name.Equals(tableName, StringComparison.OrdinalIgnoreCase) && column != null)
                    ((HashSet<string>)item.ReferencedColumns).Add(column);
            }
            return raw.SQLITE_OK;
        };
        foreach (var group in triggers.Values.GroupBy(t => t.TableName, StringComparer.OrdinalIgnoreCase))
        {
            var columns = await ColumnsAsync(connection, group.Key);
            var events = new HashSet<string>();
            foreach (var item in group)
            {
                var header = SqliteDefinitionSql.Tokens(item.CreateSql).TakeWhile(t => !t.Is("BEGIN")).ToList();
                var operation = header.First(t => t.Is("INSERT") || t.Is("UPDATE") || t.Is("DELETE"));
                events.Add(operation.Text.ToUpperInvariant());
                var of = header.FindIndex(t => t.Is("OF"));
                if (of >= 0 && group.Key.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    foreach (var token in header.Skip(of + 1).TakeWhile(t => !t.Is("ON")))
                        if (!token.Is(",")) ((HashSet<string>)item.ReferencedColumns).Add(token.Identifier);
            }
            foreach (var operation in events)
            {
                var sql = operation switch
                {
                    "INSERT" => $"INSERT INTO {Quote(group.Key)} DEFAULT VALUES;",
                    "DELETE" => $"DELETE FROM {Quote(group.Key)};",
                    _ => $"UPDATE {Quote(group.Key)} SET {string.Join(", ", columns.Select(c => Quote(c) + "=NULL"))};"
                };
                // EXPLAIN compiles trigger programs but never executes their writes.
                try { Inspect(connection, "EXPLAIN " + sql, triggerAuthorizer); }
                catch (SqliteException) { unresolved.UnionWith(group); }
            }
        }
        var dependentViews = objects.Where(o => o.Type == "view" && o.ReferencedObjects.Contains(tableName)).ToList();
        var relatedNames = dependentViews.Select(v => v.Name).Append(tableName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in unresolved)
        {
            var body = SqliteDefinitionSql.Tokens(item.CreateSql)
                .SkipWhile(t => !t.Is(item.Type == "view" ? "AS" : "ON")).Skip(1);
            if (item.ReferencedObjects.Any(relatedNames.Contains) || body.Any(t =>
                (!t.Quoted || t.Text[0] != '\'') && relatedNames.Contains(t.Identifier)))
                throw new InvalidOperationException($"Cannot resolve dependencies of {item.Type} {item.Name}; repair its stored SQL before rebuilding {tableName}.");
        }
        // SQLite validates the whole schema on rename. Temporarily suspend unrelated invalid
        // objects too, then restore their exact SQL so they do not obstruct a healthy table.
        return dependentViews.Concat(objects.Where(o => o.Type == "trigger" &&
            (o.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase)
                || o.ReferencedObjects.Contains(tableName)
                || dependentViews.Any(v => v.Name.Equals(o.TableName, StringComparison.OrdinalIgnoreCase)
                    || o.ReferencedObjects.Contains(v.Name))))).Concat(unresolved).Distinct().ToList();
    }

    private static async Task<List<string>> ColumnsAsync(SqliteConnection connection, string tableName)
    {
        var columns = new List<string>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_xinfo({Quote(tableName)});";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (reader.GetInt32(6) == 0) columns.Add(reader.GetString(1));
        return columns;
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static void Inspect(SqliteConnection connection, string sql, strdelegate_authorizer authorizer)
    {
        var result = raw.sqlite3_set_authorizer(connection.Handle, authorizer, null);
        if (result != raw.SQLITE_OK) throw new InvalidOperationException("Could not inspect SQLite schema dependencies.");
        try
        {
            result = raw.sqlite3_prepare_v2(connection.Handle, sql, out var statement);
            using (statement)
                if (result != raw.SQLITE_OK)
                    throw new SqliteException($"Could not inspect SQLite schema dependencies: {raw.sqlite3_errmsg(connection.Handle).utf8_to_string()}", result);
        }
        finally
        {
            raw.sqlite3_set_authorizer(connection.Handle, (strdelegate_authorizer)null!, null);
        }
    }
}
