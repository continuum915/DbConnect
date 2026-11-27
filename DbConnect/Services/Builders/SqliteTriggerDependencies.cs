using System;
using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Services.Builders;

internal static class SqliteTriggerDependencies
{
    // The SQLite authorizer reports INSERT targets, but not their destination columns.
    public static IEnumerable<string> InsertColumns(string sql, string tableName, IReadOnlyList<string> tableColumns)
    {
        var tokens = SqliteDefinitionSql.Tokens(sql).SkipWhile(t => !t.Is("BEGIN")).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!(tokens[i].Is("INSERT") || tokens[i].Is("REPLACE"))) continue;
            var into = i + 1;
            if (tokens.ElementAtOrDefault(into)?.Is("OR") == true) into += 2;
            if (tokens.ElementAtOrDefault(into)?.Is("INTO") != true) continue;
            var target = into + 1;
            if (tokens.ElementAtOrDefault(target + 1)?.Is(".") == true)
            {
                if (!tokens[target].Identifier.Equals("main", StringComparison.OrdinalIgnoreCase)) continue;
                target += 2;
            }
            if (!tokens[target].Identifier.Equals(tableName, StringComparison.OrdinalIgnoreCase)) continue;
            var next = target + 1;
            if (tokens.ElementAtOrDefault(next)?.Is("DEFAULT") == true) continue;
            if (tokens.ElementAtOrDefault(next)?.Is("(") != true)
            {
                foreach (var column in tableColumns) yield return column;
                continue;
            }
            var depth = tokens[next].Depth;
            foreach (var token in tokens.Skip(next + 1).TakeWhile(t => !(t.Is(")") && t.Depth == depth)))
                if (!token.Is(",")) yield return token.Identifier;
        }
    }
}
