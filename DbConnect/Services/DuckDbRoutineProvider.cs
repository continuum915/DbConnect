using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuckDB.NET.Data;

namespace DbConnect.Services;

internal sealed class DuckDbRoutineProvider(IDatabaseQueryProvider queries) : DatabaseRoutineProvider(queries), IDatabaseRoutineProvider
{
    /// <summary>
    /// The macro as one <c>CREATE MACRO</c> statement with every overload, rebuilt from its catalog entries; DuckDB keeps
    /// only the parameters and body. DuckDB 1.3 does not report parameter defaults, so they are not reproduced.
    /// </summary>
    public async Task<string?> GetFunctionDefinitionAsync(string connectionString, string schema, string functionName)
    {
        try
        {
            await using var connection = DuckDbConnections.Create(connectionString);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT function_type, parameters, macro_definition
                FROM duckdb_functions()
                WHERE schema_name = ? AND function_name = ? AND function_type IN ('macro', 'table_macro') AND NOT internal
                ORDER BY function_oid, len(parameters);";
            cmd.Parameters.Add(new DuckDBParameter { Value = schema });
            cmd.Parameters.Add(new DuckDBParameter { Value = functionName });
            var overloads = new List<string>();
            await using (var reader = await cmd.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                {
                    var parameters = ((IEnumerable)reader.GetValue(1)).Cast<object>().Select(p => Quote(p.ToString()!));
                    var table = reader.GetString(0) == "table_macro" ? "TABLE " : "";
                    overloads.Add($"({string.Join(", ", parameters)}) AS {table}{reader.GetString(2)}");
                }
            return overloads.Count == 0
                ? null
                : $"CREATE OR REPLACE MACRO {Quote(schema)}.{Quote(functionName)}{string.Join(",\n    ", overloads)};";
        }
        catch
        {
            return null;
        }
    }

    private static string Quote(string name) => ProviderCapabilityDeclarations.DuckDb.QuoteIdentifier(name);
}
