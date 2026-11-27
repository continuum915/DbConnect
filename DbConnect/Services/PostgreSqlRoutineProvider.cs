using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed class PostgreSqlRoutineProvider(IDatabaseQueryProvider queries) : DatabaseRoutineProvider(queries), IDatabaseRoutineProvider
{
    public async Task<string?> GetFunctionDefinitionAsync(string connectionString, string schema, string functionName)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            // pg_get_functiondef returns the full CREATE OR REPLACE FUNCTION DDL
            await using var cmd = new NpgsqlCommand(
                @"SELECT pg_get_functiondef(p.oid)
                  FROM pg_proc p
                  JOIN pg_namespace n ON n.oid = p.pronamespace
                  WHERE n.nspname = @schema AND p.proname = @name
                    AND p.prokind = 'f'
                  LIMIT 1;", connection);
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("name", functionName);
            return await cmd.ExecuteScalarAsync() as string;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GetProcedureDefinitionAsync(string connectionString, string schema, string procedureName)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                @"SELECT pg_get_functiondef(p.oid)
                  FROM pg_proc p
                  JOIN pg_namespace n ON n.oid = p.pronamespace
                  WHERE n.nspname = @schema AND p.proname = @name
                    AND p.prokind = 'p'
                  LIMIT 1;", connection);
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("name", procedureName);
            return await cmd.ExecuteScalarAsync() as string;
        }
        catch
        {
            return null;
        }
    }

    public List<ProcedureParameter> ParseParameters(string routineDefinition) =>
        RoutineDefinitionParser.ParseParameters(routineDefinition);

    public string GetNewProcedureTemplate(string name) =>
        $"CREATE OR REPLACE FUNCTION {name}()\nRETURNS void\nLANGUAGE plpgsql\nAS $$\nBEGIN\n  -- TODO\nEND;\n$$;";

    public async Task<IReadOnlyList<ProcedureParameter>> GetProcedureParametersAsync(
        string connectionString,
        string schema,
        string routineName,
        CancellationToken ct = default)
    {
        var parameters = new List<ProcedureParameter>();
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                @"SELECT parameter_name, data_type, parameter_mode, ordinal_position
                  FROM information_schema.parameters
                  WHERE specific_schema = @schema AND specific_name LIKE @routineName || '%'
                  ORDER BY ordinal_position;", conn);
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("routineName", routineName);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.IsDBNull(0) ? $"p{reader.GetInt32(3)}" : reader.GetString(0);
                var mode = reader.GetString(2).ToUpperInvariant();
                parameters.Add(new ProcedureParameter
                {
                    Name = name,
                    DataType = reader.GetString(1),
                    Direction = mode switch
                    {
                        "IN" => DbConnect.Models.ParameterDirection.In,
                        "OUT" => DbConnect.Models.ParameterDirection.Out,
                        "INOUT" => DbConnect.Models.ParameterDirection.InOut,
                        _ => DbConnect.Models.ParameterDirection.In
                    },
                    OrdinalPosition = reader.GetInt32(3)
                });
            }
        }
        catch { /* ignore and return what we have */ }
        return parameters;
    }

    public async Task<RoutineExecutionResult> ExecuteRoutineAsync(
        string connectionString,
        string schema,
        string routineName,
        string qualifiedRoutineName,
        SchemaNodeType routineType,
        IReadOnlyList<DbParameterValue> parameters,
        CancellationToken ct = default)
    {
        var result = new RoutineExecutionResult();
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            conn.Notice += (_, e) => result.Messages.Add($"[{e.Notice.Severity}] {e.Notice.MessageText}");
            await conn.OpenAsync(ct);

            var targetRoutineName = string.IsNullOrWhiteSpace(qualifiedRoutineName)
                ? string.IsNullOrWhiteSpace(schema)
                    ? routineName
                    : $"{schema}.{routineName}"
                : qualifiedRoutineName;
            var executableParameters = routineType == SchemaNodeType.Function
                ? parameters.Where(parameter => parameter.Direction != DbConnect.Models.ParameterDirection.Out).ToList()
                : parameters.ToList();

            await using var cmd = conn.CreateCommand();
            var placeholders = new List<string>(executableParameters.Count);
            for (var i = 0; i < executableParameters.Count; i++)
            {
                var parameter = executableParameters[i];
                var placeholder = $"@p{i}";
                placeholders.Add(placeholder);
                cmd.Parameters.AddWithValue(placeholder, parameter.Value ?? DBNull.Value);
            }

            var argumentList = string.Join(", ", placeholders);
            cmd.CommandText = routineType == SchemaNodeType.Function
                ? $"SELECT {targetRoutineName}({argumentList});"
                : $"CALL {targetRoutineName}({argumentList});";

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            int resultSetIndex = 0;
            do
            {
                if (reader.FieldCount <= 0)
                    continue;

                var queryResult = new QueryResult
                {
                    TabLabel = $"Result {++resultSetIndex}",
                    ResultIndex = resultSetIndex - 1
                };

                for (int i = 0; i < reader.FieldCount; i++)
                    queryResult.ColumnInfos.Add(new ColumnInfo { Name = reader.GetName(i), Ordinal = i });

                while (await reader.ReadAsync(ct))
                {
                    var values = new object?[reader.FieldCount];
                    for (int i = 0; i < reader.FieldCount; i++)
                        values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    queryResult.ObjectRows.Add(values);
                }

                result.ResultSets.Add(queryResult);
            } while (await reader.NextResultAsync(ct));

            if (routineType == SchemaNodeType.Procedure)
                PopulateCallOutputParameters(result, executableParameters);
        }
        catch (Exception ex)
        {
            result.Messages.Add($"[Error] {ex.Message}");
            throw;
        }

        return result;
    }

    private static void PopulateCallOutputParameters(RoutineExecutionResult result, IReadOnlyList<DbParameterValue> parameters)
    {
        var outputParameters = parameters
            .Where(parameter => parameter.Direction is DbConnect.Models.ParameterDirection.Out or DbConnect.Models.ParameterDirection.InOut)
            .ToList();
        if (outputParameters.Count == 0 || result.ResultSets.Count == 0)
            return;

        var firstResultSet = result.ResultSets[0];
        if (firstResultSet.ObjectRows.Count != 1 || firstResultSet.ColumnInfos.Count != outputParameters.Count)
            return;

        static string NormalizeName(string name) => name.TrimStart('@');

        int IndexOfParameter(string name)
        {
            var index = firstResultSet.IndexOfColumn(name);
            return index >= 0 ? index : firstResultSet.IndexOfColumn(NormalizeName(name));
        }

        var columnNames = new HashSet<string>(firstResultSet.ColumnInfos.Select(column => NormalizeName(column.Name)), StringComparer.OrdinalIgnoreCase);
        if (!outputParameters.All(parameter => columnNames.Contains(NormalizeName(parameter.Name))))
            return;

        var row = firstResultSet.ObjectRows[0];
        foreach (var parameter in outputParameters)
        {
            var index = IndexOfParameter(parameter.Name);
            result.OutputParameters.Add((parameter.Name, index >= 0 && index < row.Length ? row[index] : null));
        }
    }
}
