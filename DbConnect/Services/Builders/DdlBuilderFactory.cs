using System;
using System.Collections.Generic;

namespace DbConnect.Services.Builders;

public static class DdlBuilderFactory
{
    private static readonly Dictionary<string, IDdlBuilder> _builders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SQLite"] = new SqliteDdlBuilder(),
        ["PostgreSQL"] = new PostgreSqlDdlBuilder(),
        ["SQL Server"] = new SqlServerDdlBuilder(),
        ["MySQL"] = new MySqlDdlBuilder(),
        ["DuckDB"] = new DuckDbDdlBuilder()
    };

    public static IDdlBuilder For(string providerName) =>
        _builders.TryGetValue(providerName, out var b) ? b : new GenericDdlBuilder(providerName);
}
